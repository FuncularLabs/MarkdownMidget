using System;
using System.Text.Json;
using System.Windows.Media;

namespace MarkdownMidget.Source;

/// <summary>
/// The page-level colours the source view's markdown highlighting is drawn from,
/// read back from the document theme by the editor page (see <c>readThemeBack</c> in
/// main.js). These are the SAME colours the formatted view uses — heading, link,
/// quote, body text — so the source view captures the theme's vibe rather than the
/// dark code-block palette, which is tuned for a panel and fails on a light page.
///
/// Every colour arrives already flattened to opaque 8-bit sRGB by the page, because
/// a theme may write <c>oklch()</c> or <c>color-mix()</c> that WPF cannot parse. A
/// shape that is not exactly right yields null rather than a half-palette — the same
/// fail-closed posture as <see cref="Themes.ThemeReadBack"/>.
///
/// <see cref="Strong"/> is bold's colour (<c>--mdm-strong</c>). The page resolves an
/// unset one to the body text colour, so it is normally present; null means the
/// read-back carried none (<c>null</c>, or a bundle older than the role), and bold
/// then stays body text. <see cref="Mark"/> is the formatting marks' colour
/// (<c>--mdm-mark</c>), optional in the same way.
/// </summary>
public sealed record SourcePalette(
    Color Background,
    Color Text,
    Color Heading,
    Color Link,
    Color Accent,
    Color Quote,
    Color? Strong = null,
    Color? Mark = null)
{
    /// <summary>Parse the <c>source</c> block of the theme read-back, plus the shared
    /// background/foreground. Null for anything that is not exactly the expected
    /// shape, including a channel out of range.</summary>
    public static SourcePalette? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("source", out var src) || src.ValueKind != JsonValueKind.Object)
                return null;

            var bg = ReadColor(root, "background");
            var text = ReadColor(root, "foreground");
            var heading = ReadColor(src, "heading");
            var link = ReadColor(src, "link");
            var accent = ReadColor(src, "accent");
            var quote = ReadColor(src, "quote");
            if (bg is null || text is null || heading is null || link is null
                || accent is null || quote is null)
                return null;

            // Optional, but not lenient: absent or null is "no bold colour", while a
            // value that is there and malformed refuses the palette like any other role.
            // The same for the formatting marks' colour (--mdm-mark), which FormattingMarks uses.
            if (!Optional("strong", out var strong) || !Optional("mark", out var mark)) return null;

            return new SourcePalette(bg.Value, text.Value, heading.Value, link.Value,
                accent.Value, quote.Value, strong, mark);

            bool Optional(string role, out Color? colour)
            {
                colour = null;
                if (!src.TryGetProperty(role, out var value) || value.ValueKind == JsonValueKind.Null) return true;
                colour = ReadColor(src, role);
                return colour is not null;
            }
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// A role colour that is guaranteed legible on the page background: the mapped
    /// colour when it clears the WCAG AA ratio for normal text (4.5:1), otherwise the
    /// body text colour, which is always legible because the theme is built on it.
    ///
    /// This is why the source view stays readable even in a theme whose heading or
    /// accent is a low-contrast large-text colour (Solarized Light's heading is
    /// 3.41:1 on its page) — small monospace source text needs the 4.5 floor, not the
    /// 3:1 large-text allowance, so a role that cannot meet it falls back to text.
    /// </summary>
    public Color Legible(Color role) =>
        ContrastRatio(role, Background) >= 4.5 ? role : Text;

    /// <summary>WCAG 2.x contrast ratio between two opaque colours (1.0 .. 21.0).</summary>
    public static double ContrastRatio(Color a, Color b)
    {
        var la = RelativeLuminance(a);
        var lb = RelativeLuminance(b);
        var hi = Math.Max(la, lb);
        var lo = Math.Min(la, lb);
        return (hi + 0.05) / (lo + 0.05);
    }

    private static double RelativeLuminance(Color c)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    private static Color? ReadColor(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Object)
            return null;
        return Channel(el, "r") is { } r && Channel(el, "g") is { } g && Channel(el, "b") is { } b
            ? Color.FromRgb(r, g, b)
            : null;

        static byte? Channel(JsonElement el, string channel) =>
            el.TryGetProperty(channel, out var v)
            && v.ValueKind == JsonValueKind.Number
            && v.TryGetInt32(out var n)
            && n is >= 0 and <= 255
                ? (byte)n
                : null;
    }
}
