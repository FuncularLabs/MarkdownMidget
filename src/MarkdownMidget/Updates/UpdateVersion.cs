using System;
using System.Linq;

namespace MarkdownMidget.Updates;

/// <summary>
/// The app's version scheme: <c>major.minor.patch[-prereleaseN]</c> (tags carry a
/// leading <c>v</c>). Ordering follows SemVer's core rule — numeric first, and a
/// prerelease sorts BELOW its own stable (0.6.0-beta1 &lt; 0.6.0) — with the
/// simple prerelease tail this repo actually uses (beta1 &lt; beta2 &lt; rc1
/// compared as label + number, labels ordinal).
/// </summary>
internal sealed record UpdateVersion(Version Numeric, string? Prerelease) : IComparable<UpdateVersion>
{
    public bool IsPrerelease => Prerelease is not null;

    public static UpdateVersion? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.Trim();
        if (s.StartsWith('v') || s.StartsWith('V')) s = s[1..];
        var dash = s.IndexOf('-');
        var numericPart = dash < 0 ? s : s[..dash];
        var pre = dash < 0 ? null : s[(dash + 1)..];
        if (string.IsNullOrWhiteSpace(pre)) pre = null;
        if (!Version.TryParse(numericPart, out var v)) return null;
        // Normalize to 3 components so 0.6 == 0.6.0.
        v = new Version(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0));
        return new UpdateVersion(v, pre);
    }

    public int CompareTo(UpdateVersion? other)
    {
        if (other is null) return 1;
        var n = Numeric.CompareTo(other.Numeric);
        if (n != 0) return n;
        // Same numeric: stable outranks any prerelease.
        if (Prerelease is null && other.Prerelease is null) return 0;
        if (Prerelease is null) return 1;
        if (other.Prerelease is null) return -1;
        return ComparePre(Prerelease, other.Prerelease);
    }

    // "beta1" vs "beta2" vs "rc1": label ordinal, then trailing number.
    private static int ComparePre(string a, string b)
    {
        var (la, na) = SplitPre(a);
        var (lb, nb) = SplitPre(b);
        var l = string.Compare(la, lb, StringComparison.OrdinalIgnoreCase);
        return l != 0 ? l : na.CompareTo(nb);
    }

    private static (string Label, int Num) SplitPre(string p)
    {
        var digits = new string(p.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray());
        var label = p[..(p.Length - digits.Length)];
        return (label, digits.Length > 0 && int.TryParse(digits, out var n) ? n : 0);
    }

    public override string ToString() => Prerelease is null ? Numeric.ToString() : $"{Numeric}-{Prerelease}";
}
