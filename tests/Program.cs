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
