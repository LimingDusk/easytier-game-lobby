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
        int port = FreePort(first);
        await using (var sender = new RoomChat(first, roomSecret, atFirst.Enqueue, port))
        await using (var receiver = new RoomChat(second, roomSecret, atSecond.Enqueue, port))
        {
            Check(await sender.SendAsync("房间还没有其他成员") == 0, "Chat without peers reports no delivery");
            sender.UpdatePeers(new Dictionary<IPAddress, string> { [second] = "Nova" });
            var delivery = sender.SendAsync("  可以进游戏了！  ");
            await Task.Delay(50);
            Check(atSecond.IsEmpty, "Chat waits for the sending address to appear in the receiver's roster");
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
        Console.WriteLine("Room chat delivery, roster discovery and room isolation checks passed.");
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
