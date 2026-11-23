using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using GameLobby.Games;

namespace GameLobby;

internal sealed record Room
{
    public const string DefaultBootstrap = "tcp://150.158.142.253:11010";
    private Room(GameProfile game, string token, string bootstrap)
    {
        Game = game;
        Secret = token;
        Bootstrap = NormalizeBootstrap(bootstrap);
    }

    public GameProfile Game { get; }
    public string Secret { get; }
    public string Bootstrap { get; }
    public string Invite => $"GL2-{Game.Id}-{Secret}|{Bootstrap}";
    // Network identity stays compatible with GL1 and independent of the bootstrap address.
    public string Network => "gl-" + Convert.ToHexString(SHA256.HashData(
        Encoding.ASCII.GetBytes($"GL1-{Game.Id}-{Secret}")))[..24];

    public static Room Create(GameProfile game, string bootstrap = "") =>
        new(game, Convert.ToHexString(RandomNumberGenerator.GetBytes(16)), bootstrap);

    public static Room Parse(GameProfile game, string input, string legacyBootstrap = "")
    {
        var fields = input.Trim().Split('|', 2);
        var parts = fields[0].ToUpperInvariant().Split('-');
        if (parts.Length != 3 || parts[1] != game.Id ||
            !Regex.IsMatch(parts[2], @"\A[0-9A-F]{32}\z"))
            throw new FormatException("邀请码无效，请复制当前游戏的完整邀请码。");
        return (parts[0], fields.Length) switch
        {
            ("GL2", 2) when !string.IsNullOrWhiteSpace(fields[1]) => new(game, parts[2], fields[1]),
            ("GL1", 1) => new(game, parts[2], legacyBootstrap),
            _ => throw new FormatException("邀请码格式不完整或版本不受支持，请重新复制。")
        };
    }

    private static string NormalizeBootstrap(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return DefaultBootstrap;
        string address = input.Trim();
        if (address.Length <= 320 && Regex.IsMatch(address, @"\A(?:tcp|udp)://(?:[a-z0-9.-]+|\[[0-9a-f:.]+\]):[0-9]{1,5}/?\z", RegexOptions.IgnoreCase) &&
            Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Port > 0)
            return uri.GetLeftPart(UriPartial.Authority);
        throw new FormatException("引导地址格式应为 tcp://主机:端口 或 udp://主机:端口。");
    }

    public override string ToString() => Network;
}
