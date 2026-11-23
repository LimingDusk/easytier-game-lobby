using System.Text.Json;

namespace GameLobby;

internal static class NetworkReport
{
    private static string Field(JsonElement row, string key) =>
        row.TryGetProperty(key, out var value) ? value.ToString() : "";

    internal sealed record Member(string Name, string Ip, string Cost)
    {
        public bool Local => Cost == "Local";
        public bool Shared => !Local && Name.StartsWith("PublicServer_", StringComparison.Ordinal);
        public string Route => Local ? "本机" : Cost switch
        {
            "p2p" => "直连", var value when value.StartsWith("relay") => "中转", _ => Cost
        };
        public string Address => Ip.Length > 0 ? Ip : Shared ? "—" : "等待虚拟 IP";
    }

    internal sealed record PeerState(string Summary, Member[] Nodes)
    {
        public string Details => string.Join(Environment.NewLine, Nodes.Select(n =>
            $"{(n.Local ? "本机" : n.Shared ? "共享节点（引导）" : "远端节点")}   {n.Name}   {n.Address}   {n.Cost}").Prepend(Summary));
    }

    public static PeerState ReadPeers(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var nodes = doc.RootElement.EnumerateArray().Select(r =>
            new Member(Field(r, "hostname"), Field(r, "ipv4"), Field(r, "cost"))).ToArray();
        var local = nodes.FirstOrDefault(n => n.Local);
        int players = nodes.Count(n => !n.Local && !n.Shared && n.Ip.Length > 0);
        string summary = local == null ? "内核尚未返回本机节点，正在等待组网。"
            : local.Ip.Length == 0 ? "正在等待本机虚拟 IP。"
            : players == 0 ? "尚未发现队友。请确认好友已加入同一房间。"
            : $"已发现 {players} 位队友，可进入游戏的局域网大厅。";
        return new PeerState(summary, nodes);
    }

    public static string Peers(string json) => ReadPeers(json).Details;

    public static string Connectors(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var rows = doc.RootElement.EnumerateArray().Select(row =>
        {
            var address = row.GetProperty("url").GetProperty("url").GetString();
            var state = Field(row, "status");
            return $"{address} {(state == "0" ? "已连接" : $"未就绪（{state}）")}";
        }).ToArray();
        return rows.Length == 0 ? "暂无引导连接" : string.Join("  ", rows);
    }

    public static string Broadcast(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var counts = new Dictionary<string, string>();
        foreach (var row in doc.RootElement.EnumerateArray())
            counts[Field(row, "name")] = Field(row, "value");
        const string prefix = "udp_broadcast_relay_packets_";
        if (!counts.Keys.Any(k => k.StartsWith(prefix, StringComparison.Ordinal)))
            return "内核尚未报告广播计数。";
        string Count(string suffix) => counts.GetValueOrDefault(prefix + suffix, "—");
        return $"捕获 {Count("captured")}，转发 {Count("forwarded")}，转发失败 {Count("forward_failed")}（本机全部 UDP 广播）";
    }
}
