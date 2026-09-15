using System.Diagnostics.CodeAnalysis;

namespace MarkdownMidget;

/// <summary>Which links open outside the app (absolute http, https, mailto), checked here because the page that asks is not trusted.
/// The address shown in the prompt is the whole of it, with its host in punycode, so a lookalike name shows as what it is.</summary>
public static class LinkOpening
{
    public const string MessageType = "openLink", RefusedMessageType = "linkRefused";
    public const string RefusedNote = "Only web (http, https) and email (mailto) links open from here.";
    public const int MaxLength = 2048;
    public static bool TryValidate(string? text, [NotNullWhen(true)] out Uri? uri, out string display)
    {
        uri = null; display = string.Empty;
        if (string.IsNullOrEmpty(text) || text.Length > MaxLength || text.Any(Hidden)) return false;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var u) || u.Host.Length == 0) return false;
        // user@ before a web host is refused: https://good.com@evil.com/ goes to evil.com.
        if (u.Scheme is "http" or "https" ? u.UserInfo.Length > 0 || !text.StartsWith(u.Scheme + "://", StringComparison.OrdinalIgnoreCase) : u.Scheme != "mailto" || !IsPlainMail(u.Query)) return false;
        display = u.HostNameType == UriHostNameType.Dns ? u.AbsoluteUri.Replace(u.Host, u.IdnHost, StringComparison.Ordinal) : u.AbsoluteUri;
        uri = u; return true;
    }
    // Whitespace, control characters, and invisible formatting such as a right-to-left override, which reverses how a path reads.
    private static bool Hidden(char c) => char.IsWhiteSpace(c) || char.IsControl(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format;

    // A mail link may fill in a subject, a body and copies, nothing else: no ?attach=C:/…, no other recipient, no name written encoded.
    // `;` splits too, as some mail apps read it. Only the body may carry a line break once decoded; a header value may not.
    private static bool IsPlainMail(string query) => query.TrimStart('?').Split(new[] { '&', ';' }, StringSplitOptions.RemoveEmptyEntries).All(pair =>
    {
        var name = pair.Split('=', 2)[0].ToLowerInvariant();
        return name is "subject" or "body" or "cc" or "bcc" && (name == "body" || !Uri.UnescapeDataString(pair[name.Length..]).Any(char.IsControl));
    });
}
