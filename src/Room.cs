using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using GameLobby.Games;

namespace GameLobby;

internal sealed record Room
{
    private Room(GameProfile game, string token) { Game = game; Token = token; }
    public GameProfile Game { get; }
    private string Token { get; }
    public string Invite => $"GL1-{Game.Id}-{Token}";
    // Only the hash-derived identifier is advertised. The invitation carries the secret.
    public string Network => "gl-" + Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(Invite)))[..24];
    public string Secret => Token;
    public static Room Create(GameProfile game) => new(game, Convert.ToHexString(RandomNumberGenerator.GetBytes(16)));
    public static Room Parse(GameProfile game, string input)
    {
        string[] parts = input.Trim().ToUpperInvariant().Split('-');
        if (parts.Length != 3 || parts[0] != "GL1" || parts[1] != game.Id ||
            !Regex.IsMatch(parts[2], @"\A[0-9A-F]{32}\z"))
            throw new FormatException("邀请码无效，请复制当前游戏的完整邀请码。");
        return new(game, parts[2]);
    }
    public override string ToString() => Network;
}
