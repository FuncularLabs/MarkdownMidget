using System.Globalization;
using System.Text;

namespace MarkdownMidget;

/// <summary>
/// The caret's line and column in the status bar, and Go to Line's reading of what was
/// typed (#10). Pure, so the rules are tested without a window. The formatted view counts
/// its column by the same rule, in editor-src/src/line-map.js.
/// </summary>
internal static class LineColumn
{
    public const string FormattedColumnTip =
        "In the formatted view, Col counts the characters of text before the cursor on its line; " +
        "markdown symbols such as #, **, list markers, backslashes and link addresses are not counted.";

    /// <summary>1 + the characters before the caret on its line, counted as a reader counts
    /// them: a tab, an emoji, a letter with its combining accent is one. All-ASCII text (a
    /// pasted picture's base64 line) is the same count by its length, without the walk.</summary>
    public static int Column(string beforeCaret) =>
        1 + (Ascii.IsValid(beforeCaret) ? beforeCaret.Length : new StringInfo(beforeCaret).LengthInTextElements);

    public static string StatusText(int line, int column) =>
        string.Create(CultureInfo.InvariantCulture, $"Ln {line}, Col {column}");

    /// <summary>A whole number clamped to the document — past the end is the last line, below
    /// 1 is line 1 — or null for anything that is not a number.</summary>
    public static int? ParseLine(string? typed, int lineCount) =>
        long.TryParse(typed?.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n)
            ? (int)Math.Clamp(n, 1, Math.Max(1, lineCount))
            : null;
}
