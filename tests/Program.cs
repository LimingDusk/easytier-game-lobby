using GameLobby;
using GameLobby.Games;

var game = GameProfile.All.Single(g => g.Id == "CIV6");
var host = Room.Create(game);
var guest = Room.Parse(game, "  " + host.Invite.ToLowerInvariant() + " ");
if (guest.Network != host.Network || guest.Secret != host.Secret || !game.RelayBroadcast)
    throw new Exception("Invite round trip or discovery profile failed.");
if (Room.Create(game).Network == host.Network || host.Network.Contains(host.Secret))
    throw new Exception("Room isolation failed.");
foreach (string bad in new[] { "", host.Invite + "|extra", host.Invite.Replace("CIV6", "OTHER") })
{
    try { Room.Parse(game, bad); }
    catch (FormatException) { continue; }
    throw new Exception("Accepted an invalid invitation.");
}
Console.WriteLine("Room checks passed.");
var localOnly = """[{"hostname":"self","cost":"Local","ipv4":"10.0.0.1"},{"hostname":"PublicServer_test","cost":"p2p","ipv4":""}]""";
if (!NetworkReport.Peers(localOnly).Contains("尚未发现队友") ||
    !NetworkReport.Peers("[]").Contains("尚未返回本机")) throw new Exception("Waiting state missing");
var pair = """[{"hostname":"self","cost":"Local","ipv4":"10.0.0.1"},{"hostname":"other","cost":"p2p","ipv4":"10.0.0.2"}]""";
if (!NetworkReport.Peers(pair).Contains("已发现 1") || !NetworkReport.Peers(pair).Contains("10.0.0.2"))
    throw new Exception("Remote peer missing");
if (!NetworkReport.Broadcast("""[{"name":"udp_broadcast_relay_packets_forwarded","value":3}]""").Contains("转发 3"))
    throw new Exception("Missing broadcast metric");
var partialStats = """[{"name":"unrelated","value":42},{"name":"udp_broadcast_relay_packets_forwarded","value":0},{"name":"udp_broadcast_relay_packets_forwarded","value":7}]""";
if (NetworkReport.ReadBroadcast(partialStats) != new NetworkReport.BroadcastCounts("—", "7", "—") ||
    NetworkReport.ReadBroadcast("""[{"name":"unrelated","value":42}]""") != null)
    throw new Exception("Partial or absent broadcast counters are reported incorrectly.");
if (args.Length == 3) // Accept captured v2.6.4 results during integration checks.
{
    Console.WriteLine(NetworkReport.Peers(File.ReadAllText(args[0])));
    Console.WriteLine(NetworkReport.Connectors(File.ReadAllText(args[1])));
    Console.WriteLine(NetworkReport.Broadcast(File.ReadAllText(args[2])));
}
var folder = Directory.CreateTempSubdirectory();
try
{
    var log = new SessionLog(folder.FullName);
    Parallel.For(0, 8, i => log.Write($"检查 {i} {host.Invite} {host.Secret.ToLowerInvariant()}"));
    var snapshot = log.Snapshot();
    var saved = File.ReadAllText(folder.GetFiles().Single().FullName);
    if (log.DiskError != null || !snapshot.Contains("[已隐藏]") || !saved.Contains("检查 7") ||
        saved.Contains(host.Secret, StringComparison.OrdinalIgnoreCase) ||
        snapshot.Contains(host.Secret, StringComparison.OrdinalIgnoreCase))
        throw new Exception("Session log persistence or secret redaction failed");
}
finally { folder.Delete(true); }
Console.WriteLine("Diagnostics checks passed.");

if (HostNetwork.Choose([]).ToString() != "10.126.126.1" ||
    HostNetwork.Choose([(System.Net.IPAddress.Parse("10.126.126.200"), 25)]).ToString() == "10.126.126.1" ||
    !HostNetwork.Choose([(System.Net.IPAddress.Parse("10.99.0.1"), 8)]).ToString().StartsWith("172."))
    throw new Exception("Host subnet conflict detection failed");
if (NetworkReport.Peers(localOnly).Contains("等待虚拟 IP"))
    throw new Exception("Bootstrap is incorrectly waiting for a player IP");

if (guest.Bootstrap != "tcp://150.158.142.253:11010" || host.Bootstrap != guest.Bootstrap)
    throw new Exception("Default node mismatch");
var custom = Room.Create(game, "udp://[::1]:12345");
var customGuest = Room.Parse(game, custom.Invite, "tcp://other.example:1122");
if (customGuest.Bootstrap != custom.Bootstrap || customGuest.Network != custom.Network)
    throw new Exception("Host bootstrap should win over guest settings");
var legacy = $"GL1-{game.Id}-{host.Secret}";
var oldRoom = Room.Parse(game, legacy, "tcp://legacy.example:11010");
var legacyNetwork = "gl-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
    System.Text.Encoding.ASCII.GetBytes(legacy)))[..24];
if (oldRoom.Bootstrap != "tcp://legacy.example:11010" || oldRoom.Network != legacyNetwork ||
    Room.Parse(game, oldRoom.Invite).Network != legacyNetwork)
    throw new Exception("Legacy join or reshare failed");
foreach (var invalid in new[] { "", "http://example.org:80", "tcp://example.org:0",
    "tcp://example.org:12345/path", "tcp://u:p@example.org:12", "tcp://example.org:99999" })
{
    try { Room.Parse(game, $"GL2-{game.Id}-{host.Secret}|{invalid}"); }
    catch (FormatException) { continue; }
    throw new Exception("Malformed invite endpoint accepted");
}
Console.WriteLine("Default, custom, legacy and invalid bootstrap invitation checks passed.");

var waitingRoster = NetworkReport.ReadPeers(localOnly);
if (waitingRoster.Nodes.Count(n => !n.Shared) != 1 ||
    waitingRoster.Nodes.Single(n => n.Local).Route != "本机" ||
    NetworkReport.ReadPeers(pair).Nodes.Single(n => !n.Local).Route != "直连" ||
    NetworkReport.ReadPeers(pair.Replace("p2p", "relay(2)")).Nodes.Last().Route != "中转")
    throw new Exception("Roster presentation lost player identity or connection type");
Console.WriteLine("Room members and connection labels passed.");
