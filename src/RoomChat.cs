using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace GameLobby;

internal sealed record ChatMessage(string Name, string Text, DateTimeOffset SentAt);

internal sealed class RoomChat : IAsyncDisposable
{
    public const int DefaultPort = 46371;
    public const int MaxMessageLength = 500;

    private const int MaxMessageBytes = 3 * MaxMessageLength;
    private const int WorkerCount = 8;
    private const int NonceLength = 32;
    private const int ProofLength = 32;
    // One TCP exchange carries a hello, a listener proof, a signed text frame, and a delivery proof.
    private const byte ListenerProof = 1, MessageProof = 2, DeliveryProof = 3;
    private static ReadOnlySpan<byte> Protocol => "GLCHAT\u0001"u8;
    private static readonly UTF8Encoding WireEncoding = new(false, true);
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(6);

    private sealed record PeerSnapshot(IReadOnlyDictionary<IPAddress, string> Members)
    {
        public TaskCompletionSource Updated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly IPAddress localAddress;
    private readonly int port;
    private readonly byte[] key;
    private readonly Action<ChatMessage> received;
    private readonly TcpListener listener;
    private readonly CancellationTokenSource shutdown = new();
    private readonly Task[] workers;
    private readonly object lifetime = new();
    private readonly HashSet<Task<int>> sends = [];
    private PeerSnapshot peers = new(new Dictionary<IPAddress, string>());
    private Task? disposal;

    public RoomChat(IPAddress localAddress, string roomSecret, Action<ChatMessage> received, int port = DefaultPort)
    {
        if (!IsPeerAddress(localAddress)) throw new ArgumentException("聊天需要本机虚拟 IPv4 地址。", nameof(localAddress));
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, IPEndPoint.MaxPort);
        ArgumentException.ThrowIfNullOrEmpty(roomSecret);
        ArgumentNullException.ThrowIfNull(received);

        this.localAddress = localAddress;
        this.port = port;
        this.received = received;
        key = HMACSHA256.HashData(Encoding.UTF8.GetBytes(roomSecret), "EasyTier Game Lobby chat v1"u8);
        listener = new TcpListener(localAddress, port);
        listener.Start(2 * WorkerCount);
        CancellationToken stopping = shutdown.Token;
        workers = Enumerable.Range(0, WorkerCount).Select(_ => Task.Run(() => ListenAsync(stopping))).ToArray();
    }

    public void UpdatePeers(IReadOnlyDictionary<IPAddress, string> eligiblePeers)
    {
        var members = eligiblePeers.Where(peer => IsPeerAddress(peer.Key) && !peer.Key.Equals(localAddress))
            .ToDictionary(peer => peer.Key, peer => peer.Value);
        Interlocked.Exchange(ref peers, new PeerSnapshot(members)).Updated.TrySetResult();
    }

    public Task<int> SendAsync(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        text = text.Trim();
        if (text.Length is 0 or > MaxMessageLength)
            throw new ArgumentException($"聊天消息须为 1 至 {MaxMessageLength} 个字符。", nameof(text));
        byte[] payload = WireEncoding.GetBytes(text);

        lock (lifetime)
        {
            var members = Volatile.Read(ref peers).Members;
            if (disposal != null || members.Count == 0) return Task.FromResult(0);

            var send = SendToPeersAsync(members.Keys, payload, shutdown.Token);
            sends.Add(send);
            _ = send.ContinueWith(finished =>
            {
                lock (lifetime) sends.Remove(finished);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return send;
        }
    }

    private async Task<int> SendToPeersAsync(IEnumerable<IPAddress> addresses, byte[] payload, CancellationToken stopping)
    {
        int delivered = 0;
        try
        {
            await Parallel.ForEachAsync(addresses, new ParallelOptions
            {
                CancellationToken = stopping,
                MaxDegreeOfParallelism = WorkerCount
            }, async (address, token) =>
            {
                if (await TrySendAsync(address, payload, token).ConfigureAwait(false)) Interlocked.Increment(ref delivered);
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        return delivered;
    }

    private async Task<bool> TrySendAsync(IPAddress address, byte[] payload, CancellationToken stopping)
    {
        using var deadline = CreateDeadline(stopping);
        CancellationToken token = deadline.Token;
        try
        {
            using var client = new TcpClient(new IPEndPoint(localAddress, 0)) { NoDelay = true };
            await client.ConnectAsync(address, port, token).ConfigureAwait(false);
            using var stream = client.GetStream();

            byte[] hello = new byte[Protocol.Length + NonceLength];
            Protocol.CopyTo(hello);
            RandomNumberGenerator.Fill(hello.AsSpan(Protocol.Length));
            await stream.WriteAsync(hello, token).ConfigureAwait(false);

            byte[] proof = new byte[NonceLength + ProofLength];
            await stream.ReadExactlyAsync(proof, token).ConfigureAwait(false);
            byte[] transcript = MakeTranscript(localAddress, address, hello.AsSpan(Protocol.Length), proof.AsSpan(0, NonceLength));
            if (!CryptographicOperations.FixedTimeEquals(proof.AsSpan(NonceLength), Sign(ListenerProof, transcript))) return false;

            byte[] frame = new byte[sizeof(ushort) + payload.Length + ProofLength];
            BinaryPrimitives.WriteUInt16BigEndian(frame, (ushort)payload.Length);
            payload.CopyTo(frame, sizeof(ushort));
            Sign(MessageProof, transcript, payload).CopyTo(frame, sizeof(ushort) + payload.Length);
            await stream.WriteAsync(frame, token).ConfigureAwait(false);

            byte[] acknowledgment = new byte[ProofLength];
            await stream.ReadExactlyAsync(acknowledgment, token).ConfigureAwait(false);
            return CryptographicOperations.FixedTimeEquals(acknowledgment, Sign(DeliveryProof, transcript, payload));
        }
        catch (Exception error) when (IsConnectionError(error)) { return false; }
    }

    private async Task ListenAsync(CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(stopping).ConfigureAwait(false); }
            catch (Exception error) when (stopping.IsCancellationRequested &&
                error is OperationCanceledException or SocketException or InvalidOperationException) { return; }
            catch (SocketException)
            {
                try { await Task.Delay(100, stopping).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                continue;
            }

            using (client)
            using (var deadline = CreateDeadline(stopping))
            {
                try { await ReceiveAsync(client, deadline.Token).ConfigureAwait(false); }
                catch (Exception error) when (IsConnectionError(error) || error is DecoderFallbackException) { }
            }
        }
    }

    private async Task ReceiveAsync(TcpClient client, CancellationToken token)
    {
        if (client.Client.RemoteEndPoint is not IPEndPoint remote || !IsPeerAddress(remote.Address)) return;
        client.NoDelay = true;
        using var stream = client.GetStream();

        byte[] hello = new byte[Protocol.Length + NonceLength];
        await stream.ReadExactlyAsync(hello, token).ConfigureAwait(false);
        if (!hello.AsSpan(0, Protocol.Length).SequenceEqual(Protocol)) return;

        byte[] proof = new byte[NonceLength + ProofLength];
        RandomNumberGenerator.Fill(proof.AsSpan(0, NonceLength));
        byte[] transcript = MakeTranscript(remote.Address, localAddress, hello.AsSpan(Protocol.Length), proof.AsSpan(0, NonceLength));
        Sign(ListenerProof, transcript).CopyTo(proof, NonceLength);
        await stream.WriteAsync(proof, token).ConfigureAwait(false);

        byte[] length = new byte[sizeof(ushort)];
        await stream.ReadExactlyAsync(length, token).ConfigureAwait(false);
        int size = BinaryPrimitives.ReadUInt16BigEndian(length);
        if (size is 0 or > MaxMessageBytes) return;
        byte[] payload = new byte[size];
        byte[] signature = new byte[ProofLength];
        await stream.ReadExactlyAsync(payload, token).ConfigureAwait(false);
        await stream.ReadExactlyAsync(signature, token).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(signature, Sign(MessageProof, transcript, payload))) return;

        string text = WireEncoding.GetString(payload);
        if (text.Length > MaxMessageLength || string.IsNullOrWhiteSpace(text)) return;
        string name = await WaitForPeerAsync(remote.Address, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        received(new ChatMessage(name, text, DateTimeOffset.Now));
        await stream.WriteAsync(Sign(DeliveryProof, transcript, payload), token).ConfigureAwait(false);
    }

    private async Task<string> WaitForPeerAsync(IPAddress address, CancellationToken token)
    {
        // Peer discovery runs independently on each client. Only an authenticated sender
        // can wait for the next snapshot to confirm its source address.
        while (true)
        {
            var snapshot = Volatile.Read(ref peers);
            if (snapshot.Members.TryGetValue(address, out string? name)) return name;
            await snapshot.Updated.Task.WaitAsync(token).ConfigureAwait(false);
        }
    }

    private static byte[] MakeTranscript(IPAddress sender, IPAddress recipient, ReadOnlySpan<byte> senderNonce, ReadOnlySpan<byte> recipientNonce)
    {
        const int ipv4Length = 4;
        const int addressBytes = 2 * ipv4Length;
        byte[] transcript = new byte[addressBytes + 2 * NonceLength];
        sender.TryWriteBytes(transcript.AsSpan(0, ipv4Length), out _);
        recipient.TryWriteBytes(transcript.AsSpan(ipv4Length, ipv4Length), out _);
        senderNonce.CopyTo(transcript.AsSpan(addressBytes, NonceLength));
        recipientNonce.CopyTo(transcript.AsSpan(addressBytes + NonceLength));
        return transcript;
    }

    private byte[] Sign(byte kind, byte[] transcript, ReadOnlySpan<byte> payload = default)
    {
        byte[] bytes = new byte[1 + transcript.Length + payload.Length];
        bytes[0] = kind;
        transcript.CopyTo(bytes, 1);
        payload.CopyTo(bytes.AsSpan(1 + transcript.Length));
        return HMACSHA256.HashData(key, bytes);
    }

    private static bool IsPeerAddress(IPAddress address) => address.AddressFamily == AddressFamily.InterNetwork &&
        !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.Broadcast);

    private static bool IsConnectionError(Exception error) => error is IOException or SocketException or OperationCanceledException;

    private static CancellationTokenSource CreateDeadline(CancellationToken stopping)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        deadline.CancelAfter(ConnectionTimeout);
        return deadline;
    }

    public ValueTask DisposeAsync()
    {
        lock (lifetime)
        {
            if (disposal == null)
            {
                shutdown.Cancel();
                listener.Stop();
                disposal = FinishAsync(Task.WhenAll(workers.Concat(sends)));
            }
            return new ValueTask(disposal);
        }
    }

    private async Task FinishAsync(Task pending)
    {
        try { await pending.ConfigureAwait(false); }
        finally
        {
            shutdown.Dispose();
            CryptographicOperations.ZeroMemory(key);
        }
    }
}
