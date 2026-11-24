namespace GameLobby.Games;

internal record GameProfile(string Id, string Name, bool RelayBroadcast, string HostGuide, string GuestGuide,
    string? HostSteps = null, string? GuestSteps = null)
{
    public override string ToString() => Name;
    public static readonly GameProfile[] All = [new(
        "CIV6", "文明 VI", true,
        "打开《文明 VI》 → 多人游戏 → 本地网络 → 创建游戏。保持本窗口开启，等好友加入后开始。",
        "打开《文明 VI》 → 多人游戏 → 本地网络，选择房主的对局并加入。双方使用相同游戏版本及兼容的模组。",
        "多人游戏 → 本地网络 → 创建游戏", "多人游戏 → 本地网络 → 选择房主的对局")];
}
