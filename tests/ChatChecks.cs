using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using GameLobby;

internal static class ChatChecks
{
    public static async Task RunAsync(string roomSecret, string otherRoomSecret)
    {
        var first = IPAddress.Loopback;
        // macOS only binds 127.0.0.1 by default. A configured local IPv4 can make the same round trip.
        var second = OperatingSystem.IsMacOS() ? LocalNetworkAddress() : IPAddress.Parse("127.0.0.2");
        if (second == null)
        {
            Console.WriteLine("Room chat network checks skipped because a second local IPv4 is unavailable.");
            return;
        }

        var atFirst = new ConcurrentQueue<ChatMessage>();
        var atSecond = new ConcurrentQueue<ChatMessage>();
        var rosterRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int port = FreePort(first);
        await using (var sender = new RoomChat(first, roomSecret, atFirst.Enqueue, port))
        await using (var receiver = new RoomChat(second, roomSecret, atSecond.Enqueue, port,
            refreshPeers: () => rosterRefresh.TrySetResult()))
        {
            Check(await sender.SendAsync("房间还没有其他成员") == 0, "Chat without peers reports no delivery");
            sender.UpdatePeers(new Dictionary<IPAddress, string> { [second] = "Nova" });
            var delivery = sender.SendAsync("  可以进游戏了！  ");
            await rosterRefresh.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Check(atSecond.IsEmpty, "Authenticated chat requests a refresh but waits for the sending address in the receiver's roster");
            receiver.UpdatePeers(new Dictionary<IPAddress, string> { [first] = "Ming-PC" });
            Check(await delivery == 1 && atSecond.TryDequeue(out var firstMessage) &&
                  firstMessage is { Name: "Ming-PC", Text: "可以进游戏了！" }, "Chat delivery uses the receiver's roster name and trims input");

            string longest = new('联', RoomChat.MaxMessageLength);
            Check(await receiver.SendAsync(longest) == 1 && atFirst.TryDequeue(out var response) &&
                  response.Name == "Nova" && response.Text == longest, "Chat sends the longest valid Unicode message in either direction");
            foreach (string invalid in new[] { " \r\n ", longest + "游" })
            {
                bool rejected = false;
                try { await sender.SendAsync(invalid); }
                catch (ArgumentException) { rejected = true; }
                Check(rejected, "Chat rejects blank and oversized messages");
            }
        }

        port = FreePort(first);
        await using (var sender = new RoomChat(first, roomSecret, atFirst.Enqueue, port))
        await using (var otherRoom = new RoomChat(second, otherRoomSecret, atSecond.Enqueue, port))
        {
            sender.UpdatePeers(new Dictionary<IPAddress, string> { [second] = "Nova" });
            otherRoom.UpdatePeers(new Dictionary<IPAddress, string> { [first] = "Ming-PC" });
            Check(await sender.SendAsync("这条消息只属于当前房间") == 0 && atSecond.IsEmpty,
                "A peer with another room secret receives no chat or delivery acknowledgment");
        }
        await CheckOrderedDeliveryAsync(first, second, roomSecret);
        if (!OperatingSystem.IsMacOS())
            await CheckSlowPeerIsolationAsync(first, second, IPAddress.Parse("127.0.0.3"), roomSecret);
        else Console.WriteLine("The three-address slow-peer scenario is skipped on macOS without extra loopback aliases.");
        Console.WriteLine("Room chat delivery, roster discovery, room isolation and sequential sends passed.");
    }

    private static async Task CheckOrderedDeliveryAsync(IPAddress first, IPAddress second, string roomSecret)
    {
        const string firstText = "第一条", secondText = "第二条";
        var firstReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var confirmFirst = new ManualResetEventSlim();
        int port = FreePort(first);
        await using var sender = new RoomChat(first, roomSecret, _ => { }, port);
        await using var receiver = new RoomChat(second, roomSecret, message =>
        {
            if (message.Text == firstText)
            {
                firstReceived.TrySetResult();
                confirmFirst.Wait(TimeSpan.FromSeconds(4));
            }
            if (message.Text == secondText) secondReceived.TrySetResult();
        }, port);
        sender.UpdatePeers(new Dictionary<IPAddress, string> { [second] = "Nova" });
        receiver.UpdatePeers(new Dictionary<IPAddress, string> { [first] = "Ming-PC" });

        var firstSend = sender.SendAsync(firstText);
        try
        {
            await firstReceived.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var secondSend = sender.SendAsync(secondText);
            await Task.WhenAny(secondReceived.Task, Task.Delay(200));
            Check(!secondReceived.Task.IsCompleted, "A second send to the same peer waits for the first acknowledgment");
            confirmFirst.Set();
            Check(await firstSend == 1 && await secondSend == 1 && secondReceived.Task.IsCompleted,
                "Consecutive sends are acknowledged in the recipient's order");
        }
        finally { confirmFirst.Set(); }
    }

    private static async Task CheckSlowPeerIsolationAsync(IPAddress first, IPAddress second, IPAddress slow, string roomSecret)
    {
        var received = new ConcurrentQueue<string>();
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int port = FreePort(first);
        using var slowListener = new TcpListener(slow, port);
        slowListener.Start();
        await using var sender = new RoomChat(first, roomSecret, _ => { }, port);
        await using var receiver = new RoomChat(second, roomSecret, message =>
        {
            received.Enqueue(message.Text);
            if (received.Count == 2) arrived.TrySetResult();
        }, port);
        sender.UpdatePeers(new Dictionary<IPAddress, string> { [second] = "Nova", [slow] = "Waiting-PC" });
        receiver.UpdatePeers(new Dictionary<IPAddress, string> { [first] = "Ming-PC" });

        var firstSend = sender.SendAsync("第一条");
        using var connectionTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var stalledConnection = await slowListener.AcceptTcpClientAsync(connectionTimeout.Token);
        var secondSend = sender.SendAsync("第二条");
        await arrived.Task.WaitAsync(connectionTimeout.Token);
        Check(!firstSend.IsCompleted && !secondSend.IsCompleted && received.SequenceEqual(["第一条", "第二条"]),
            "A peer that stalls the chat handshake does not delay consecutive sends to healthy peers");

        slowListener.Stop();
        stalledConnection.Dispose();
        Check(await firstSend == 1 && await secondSend == 1, "Only the healthy peer confirms delivery of both messages");
    }

    private static int FreePort(IPAddress address)
    {
        using var listener = new TcpListener(address, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static IPAddress? LocalNetworkAddress() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(network => network.OperationalStatus == OperationalStatus.Up)
        .SelectMany(network => network.GetIPProperties().UnicastAddresses)
        .Select(address => address.Address)
        .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address));

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
    }
}
