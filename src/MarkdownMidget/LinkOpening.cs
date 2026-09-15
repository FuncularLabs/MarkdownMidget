using System.Diagnostics.CodeAnalysis;

namespace MarkdownMidget;

/// <summary>Which links open outside the app: absolute http and https only (email links are copy-only), checked here because the page
/// that asks is not trusted. The address shown in the prompt is the whole of it, with its host in punycode, so a lookalike name shows as what it is.</summary>
public static class LinkOpening
{
    public const string MessageType = "openLink", RefusedMessageType = "linkRefused";
    public const string RefusedNote = "Only web links (http, https) open from here. Right-click a link to copy it.";
    public const int MaxLength = 2048;
    public static bool TryValidate(string? text, [NotNullWhen(true)] out Uri? uri, out string display)
    {
        uri = null; display = string.Empty;
        if (string.IsNullOrEmpty(text) || text.Length > MaxLength || text.Any(Hidden)) return false;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var u) || u.Host.Length == 0 || u.Scheme is not ("http" or "https")) return false;
        // user@ before the host is refused: https://good.com@evil.com/ goes to evil.com.
        if (u.UserInfo.Length > 0 || !text.StartsWith(u.Scheme + "://", StringComparison.OrdinalIgnoreCase)) return false;
        display = u.HostNameType == UriHostNameType.Dns ? u.AbsoluteUri.Replace(u.Host, u.IdnHost, StringComparison.Ordinal) : u.AbsoluteUri;
        uri = u; return true;
    }
    // Whitespace, control characters, and invisible formatting such as a right-to-left override, which reverses how a path reads.
    private static bool Hidden(char c) => char.IsWhiteSpace(c) || char.IsControl(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format;
}
