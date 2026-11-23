using GameLobby;
using GameLobby.Games;

var game = GameProfile.All.Single(g => g.Id == "CIV6");
var host = Room.Create(game);
var guest = Room.Parse(game, "  " + host.Invite.ToLowerInvariant() + " ");
if (guest.Network != host.Network || guest.Secret != host.Secret || !game.RelayBroadcast)
    throw new Exception("Invite round trip or discovery profile failed.");
if (Room.Create(game).Network == host.Network || host.Network.Contains(host.Secret))
    throw new Exception("Room isolation failed.");
foreach (string bad in new[] { "", host.Invite + "x", host.Invite.Replace("CIV6", "OTHER") })
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
