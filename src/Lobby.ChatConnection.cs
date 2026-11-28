using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace GameLobby;

public partial class Lobby
{
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
                connection = new RoomChat(address, room.Secret, message =>
                {
                    _ = Dispatcher.InvokeAsync(() =>
                    {
                        if (current == generation && connection != null && ReferenceEquals(roomChat, connection))
                            ShowChatMessage(message, false);
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
        SetChatStatus(chatPeerCount > 0 ? "可向房间成员发送消息" : "正在等待队友加入房间…", chatPeerCount > 0);
    }

    private static IPAddress? ParseChatAddress(string? text) =>
        IPAddress.TryParse(text, out var address) && address.AddressFamily == AddressFamily.InterNetwork &&
        !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.Broadcast) && !IPAddress.IsLoopback(address)
            ? address : null;

    private async Task<bool> SendChatAsync(string text)
    {
        if (roomChat is not { } connection || chatPeerCount < 1) return false;
        int recipients = chatPeerCount;
        var message = new ChatMessage(chatName, text, DateTimeOffset.Now);
        int delivered = await connection.SendAsync(text);
        if (!ReferenceEquals(roomChat, connection)) return false;
        bool canSend = chatPeerCount > 0;
        if (delivered == 0)
        {
            SetChatStatus("发送未获确认，请确认队友已更新并连接后重试。", canSend);
            return false;
        }

        ShowChatMessage(message, true);
        SetChatStatus($"已送达 {delivered}/{recipients} 位队友", canSend);
        return true;
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
