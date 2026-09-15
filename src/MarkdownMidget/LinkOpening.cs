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
        try
        {
            if (!Uri.TryCreate(text, UriKind.Absolute, out var u) || u.Host.Length == 0 || u.Scheme is not ("http" or "https")) return false;
            // user@ before the host is refused: https://good.com@evil.com/ goes to evil.com.
            if (u.UserInfo.Length > 0 || !text.StartsWith(u.Scheme + "://", StringComparison.OrdinalIgnoreCase)) return false;
            // Only a DNS name or an IP address: a host Uri types as Basic (-a.test, a 64-character label) would be shown in Unicode.
            if (u.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6)) return false;
            var address = u.HostNameType == UriHostNameType.Dns ? u.AbsoluteUri.Replace(u.Host, u.IdnHost, StringComparison.Ordinal) : u.AbsoluteUri;
            // What the prompt shows is what opens: it parses back to itself as the same kind of host (not a name IDN made 0127.0.0.1), nothing hidden.
            if (address.Any(Hidden) || !Uri.TryCreate(address, UriKind.Absolute, out var shown) || shown.HostNameType != u.HostNameType || shown.AbsoluteUri != address) return false;
            // A name ending in a number (0127.0.0.1., 0x) is DNS to Uri but an IPv4 address to a browser, which would open elsewhere (WHATWG "ends in a number").
            var labels = shown.IdnHost.Split('.'); var last = labels.Length > 1 && labels[^1].Length == 0 ? labels[^2] : labels[^1];
            if (shown.HostNameType == UriHostNameType.Dns && ((last.Length > 0 && last.All(char.IsAsciiDigit)) || (last.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && last[2..].All(char.IsAsciiHexDigit)))) return false;
            display = address; uri = shown; return true;
        }
        // IdnHost throws for a name Uri parses but IDN rejects (https://ü-.test/, or a name too long once in punycode): refused like any other.
        catch (UriFormatException) { display = string.Empty; return false; }
    }
    // Whitespace, control characters, and invisible formatting such as a right-to-left override, which reverses how a path reads.
    private static bool Hidden(char c) => char.IsWhiteSpace(c) || char.IsControl(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format;
}
