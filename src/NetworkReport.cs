using System.Text.Json;

namespace GameLobby;

internal static class NetworkReport
{
    private static string Field(JsonElement row, string key) =>
        row.TryGetProperty(key, out var value) ? value.ToString() : "";

    public static string Peers(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var rows = doc.RootElement.EnumerateArray().ToArray();
        if (!rows.Any(row => Field(row, "cost") == "Local"))
            return "内核尚未返回本机节点，正在等待组网。";
        var lines = new List<string>();
        int players = 0;
        bool localReady = false;
        foreach (var row in rows)
        {
            var name = Field(row, "hostname");
            var cost = Field(row, "cost");
            var ip = Field(row, "ipv4");
            var local = cost == "Local";
            var shared = name.StartsWith("PublicServer_", StringComparison.Ordinal);
            if (local) localReady = ip.Length > 0;
            if (!local && !shared && ip.Length > 0) players++;
            var role = local ? "本机" : shared ? "共享节点" : "远端节点";
            lines.Add($"{role}   {name}   {(ip.Length > 0 ? ip : "等待虚拟 IP")}   {cost}");
        }
        lines.Insert(0, !localReady ? "正在等待本机虚拟 IP。" : players == 0
            ? "尚未发现队友。请核对房间标识和引导节点。"
            : $"已发现 {players} 个带虚拟 IP 的远端节点。请双方重新进入文明 VI 的本地网络大厅。");
        return string.Join(Environment.NewLine, lines);
    }

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
