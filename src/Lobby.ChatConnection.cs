using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace GameLobby;

public partial class Lobby
{
    private sealed record ChatSubmission(ChatMessage Message, int Recipients, Task<int> Delivery);

    private RoomChat? roomChat;
    private IPAddress? chatAddress;
    private string chatName = "";
    private int chatPeerCount;

    private void RefreshChatConnection(NetworkReport.PeerState snapshot)
    {
        if (currentRoom is not { } room) return;
        var local = snapshot.Nodes.FirstOrDefault(member => member.Local);
        var address = ParseChatAddress(local?.Ip);
        try
        {
            if (local == null || address == null || !HostNetwork.IsAssigned(address))
            {
                CloseChatConnection();
                SetChatStatus("正在等待本机虚拟 IP…", false);
                return;
            }
        }
        catch (NetworkInformationException)
        {
            if (roomChat == null) SetChatStatus("正在确认本机虚拟 IP…", false);
            return;
        }

        var peers = new Dictionary<IPAddress, string>();
        foreach (var member in snapshot.Nodes)
        {
            if (member.Local || member.Shared || ParseChatAddress(member.Ip) is not { } peer || peer.Equals(address))
                continue;
            peers.TryAdd(peer, string.IsNullOrWhiteSpace(member.Name) ? peer.ToString() : member.Name);
        }

        if (!address.Equals(chatAddress))
        {
            CloseChatConnection();
            int current = generation;
            string name = string.IsNullOrWhiteSpace(local.Name) ? "本机" : local.Name;
            RoomChat? connection = null;
            try
            {
                connection = new RoomChat(address, room.Secret,
                    message =>
                    {
                        _ = Dispatcher.InvokeAsync(() =>
                        {
                            if (current == generation && connection != null && ReferenceEquals(roomChat, connection))
                                ShowChatMessage(message, false);
                        });
                    },
                    refreshPeers: () =>
                    {
                        _ = Dispatcher.InvokeAsync(() =>
                        {
                            if (current == generation && connection != null && ReferenceEquals(roomChat, connection))
                                _ = RunActionAsync(RefreshPeers, "刷新房间成员失败");
                        });
                    });
            }
            catch (SocketException)
            {
                SetChatStatus("暂时无法启用房间聊天，正在重试…", false);
                return;
            }
            roomChat = connection;
            chatAddress = address;
            chatName = name;
            chatPeerCount = -1;
        }

        roomChat!.UpdatePeers(peers);
        if (chatPeerCount == peers.Count) return;
        chatPeerCount = peers.Count;
        SetChatStatus(chatPeerCount > 0 ? "可向房间成员发送消息" : "房间里只有你", true);
    }

    private static IPAddress? ParseChatAddress(string? text) =>
        IPAddress.TryParse(text, out var address) && address.AddressFamily == AddressFamily.InterNetwork &&
        !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.Broadcast) && !IPAddress.IsLoopback(address)
            ? address : null;

    private ChatSubmission? SubmitChat(string text)
    {
        if (currentRoom == null) return null;
        var connection = roomChat;
        int recipients = chatPeerCount;
        var message = new ChatMessage(chatName.Length == 0 ? "本机" : chatName, text, DateTimeOffset.Now);
        return new(message, recipients, connection != null && recipients > 0 ? connection.SendAsync(text) : Task.FromResult(0));
    }

    private void CloseChatConnection()
    {
        var previous = roomChat;
        roomChat = null;
        chatAddress = null;
        chatName = "";
        chatPeerCount = 0;
        if (previous != null) _ = DisposeChatConnectionAsync(previous);
    }

    private async Task DisposeChatConnectionAsync(RoomChat connection)
    {
        try { await connection.DisposeAsync(); }
        catch (Exception error) { log.Write($"房间聊天关闭时出错（{error.GetType().Name}）"); }
    }
}
