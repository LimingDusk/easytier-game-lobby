namespace GameLobby.Games;

internal record GameProfile(string Id, string Name, bool RelayBroadcast, string HostGuide, string GuestGuide,
    string? HostSteps = null, string? GuestSteps = null)
{
    public override string ToString() => Name;
    public static readonly GameProfile[] All =
    [
        new(
            "CIV6", "文明 VI", true,
            "打开《文明 VI》 → 多人游戏 → 本地网络 → 创建游戏。保持本窗口开启，等好友加入后开始。",
            "打开《文明 VI》 → 多人游戏 → 本地网络，选择房主的对局并加入。双方使用相同游戏版本及兼容的模组。",
            "多人游戏 → 本地网络 → 创建游戏", "多人游戏 → 本地网络 → 选择房主的对局"),
        new("MCLAN", "我的世界局域网", false,
            "在 Java 版单人世界按 Esc → 对局域网开放 → 创建一个局域网世界。把大厅中的本机虚拟 IP 和游戏聊天栏显示的端口发给好友。",
            "使用兼容的 Java 版和模组，在多人游戏 → 直接连接中输入房主虚拟 IP:开放端口。每次重新开放世界后，请向房主确认端口。",
            "单人世界 → 对局域网开放 → 分享虚拟 IP 和端口", "多人游戏 → 直接连接 → 输入房主虚拟 IP:开放端口"),
        new("CUSTOM", "自定义联机", true,
            "在支持局域网或 IP 直连的游戏中创建对局或启动服务器，把大厅中的本机虚拟 IP 和游戏使用的端口发给好友。",
            "在游戏的局域网列表选择房主的对局。若游戏支持 IP 直连，可输入房主虚拟 IP，并按游戏要求填写端口。",
            "游戏内建房 → 分享虚拟 IP 和游戏端口", "游戏内搜房，或使用房主虚拟 IP 和游戏端口直连")
    ];
}
