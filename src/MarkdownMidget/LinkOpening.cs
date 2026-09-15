using System.Diagnostics.CodeAnalysis;

namespace MarkdownMidget;

/// <summary>Which links open outside the app (absolute http, https, mailto), checked here because the page that asks is not trusted.
/// The address shown in the prompt has its host in punycode, so a lookalike name shows as what it is, and is cut at MaxShown.</summary>
public static class LinkOpening
{
    public const string MessageType = "openLink", RefusedMessageType = "linkRefused";
    public const string RefusedNote = "Only web (http, https) and email (mailto) links open from here.";
    public const int MaxLength = 2048, MaxShown = 300;
    public static bool TryValidate(string? text, [NotNullWhen(true)] out Uri? uri, out string display)
    {
        uri = null; display = string.Empty;
        if (string.IsNullOrEmpty(text) || text.Length > MaxLength || text.Any(Hidden)) return false;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var u) || u.Host.Length == 0) return false;
        if (u.Scheme is "http" or "https" ? !text.StartsWith(u.Scheme + "://", StringComparison.OrdinalIgnoreCase) : u.Scheme != "mailto") return false;
        var shown = u.HostNameType == UriHostNameType.Dns ? u.AbsoluteUri.Replace(u.Host, u.IdnHost, StringComparison.Ordinal) : u.AbsoluteUri;
        display = shown.Length <= MaxShown ? shown : string.Concat(shown.AsSpan(0, MaxShown - 1), "…");
        uri = u; return true;
    }
    // Whitespace, control characters, and invisible formatting such as a right-to-left override, which reverses how a path reads.
    private static bool Hidden(char c) => char.IsWhiteSpace(c) || char.IsControl(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format;
}
