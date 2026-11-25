using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace GameLobby;

internal sealed class ReleaseVersion(Version core, string[] identifiers) : IComparable<ReleaseVersion>
{
    private static readonly Regex Syntax = new(
        @"\Av?(?<core>(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*))(?:-(?<pre>[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private readonly Version core = core;
    private readonly string[] identifiers = identifiers;

    public bool IsPrerelease => identifiers.Length != 0;

    public static bool TryParse(string? value, [NotNullWhen(true)] out ReleaseVersion? version)
    {
        version = null;
        if (value is null || value.Length > 80) return false;
        var match = Syntax.Match(value);
        if (!match.Success || !Version.TryParse(match.Groups["core"].Value, out var core)) return false;
        string[] identifiers = match.Groups["pre"].Success ? match.Groups["pre"].Value.Split('.') : [];
        if (identifiers.Any(id => id.Length > 1 && id[0] == '0' && Numeric(id))) return false;
        version = new(core, identifiers);
        return true;
    }

    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null) return 1;
        int comparison = core.CompareTo(other.core);
        if (comparison != 0) return comparison;
        if (IsPrerelease != other.IsPrerelease) return IsPrerelease ? -1 : 1;

        for (int i = 0; i < Math.Min(identifiers.Length, other.identifiers.Length); i++)
        {
            string left = identifiers[i], right = other.identifiers[i];
            bool leftNumeric = Numeric(left), rightNumeric = Numeric(right);
            if (leftNumeric != rightNumeric) return leftNumeric ? -1 : 1;
            if (leftNumeric && left.Length != right.Length) return left.Length.CompareTo(right.Length);
            comparison = string.CompareOrdinal(left, right);
            if (comparison != 0) return comparison;
        }
        return identifiers.Length.CompareTo(other.identifiers.Length);
    }

    private static bool Numeric(string identifier) => identifier.All(char.IsAsciiDigit);
}
