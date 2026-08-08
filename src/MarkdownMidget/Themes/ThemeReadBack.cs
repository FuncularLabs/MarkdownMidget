using System.Text.Json;
using System.Windows.Media;

namespace MarkdownMidget.Themes;

/// <summary>
/// What the page says a theme resolved to.
///
/// The host can't work these out for itself. Parsing the stylesheet in C# breaks on
/// <c>var()</c> indirection, on several <c>:root</c> blocks, on <c>html {}</c>
/// instead of <c>:root</c>, on a declaration inside <c>@media</c> — and, fatally, on
/// colour syntax: WPF's ColorConverter takes hex and named colours and nothing else,
/// while a theme may reasonably be written in <c>rgb()</c>, <c>hsl()</c>,
/// <c>oklch()</c> or <c>color-mix()</c>. So the engine is asked, and the page hands
/// back plain sRGB bytes it has already flattened over an opaque base.
/// </summary>
internal static class ThemeReadBack
{
    /// <summary>
    /// Parse <c>{"background":{"r":..,"g":..,"b":..},"foreground":{…},…}</c>.
    ///
    /// Null for anything that isn't exactly that — including the literal "null"
    /// ExecuteScriptAsync returns when the script threw, and including a channel out
    /// of range. Half-parsed colours are worse than none: the caller can put the
    /// original back, but it cannot notice that one of the two it just applied was
    /// nonsense.
    /// </summary>
    public static (Color Background, Color Foreground)? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            return ReadColor(doc.RootElement, "background") is { } bg
                && ReadColor(doc.RootElement, "foreground") is { } fg
                ? (bg, fg)
                : null;
        }
        catch (JsonException) { return null; }
    }

    private static Color? ReadColor(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Object) return null;
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
