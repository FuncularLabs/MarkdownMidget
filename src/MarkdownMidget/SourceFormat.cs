using System.Text.RegularExpressions;
using MarkdownMidget.Source;

namespace MarkdownMidget;

/// <summary>
/// Applies the same formatting/style commands to the raw-markdown TextBox that the
/// WYSIWYG editor applies to the document — inline wraps, block-line prefixes, etc.
/// </summary>
internal static partial class SourceFormat
{
    [GeneratedRegex(@"^(#{1,6}\s+|>\s+|[-*+]\s+|\d+\.\s+)")]
    private static partial Regex BlockPrefix();

    public static void Apply(SourceEditor tb, string name)
    {
        switch (name)
        {
            case "bold": Wrap(tb, "**", "**"); break;
            case "italic": Wrap(tb, "*", "*"); break;
            case "underline": Wrap(tb, "<u>", "</u>"); break;
            case "strike": Wrap(tb, "~~", "~~"); break;
            case "code": Wrap(tb, "`", "`"); break;

            case "paragraph": Prefix(tb, ""); break;
            case "h1": Prefix(tb, "# "); break;
            case "h2": Prefix(tb, "## "); break;
            case "h3": Prefix(tb, "### "); break;
            case "h4": Prefix(tb, "#### "); break;
            case "h5": Prefix(tb, "##### "); break;
            case "quote": Prefix(tb, "> "); break;
            case "bullet": Prefix(tb, "- "); break;
            case "ordered": Prefix(tb, "1. "); break;

            case "hr": InsertAtCaret(tb, "\n---\n"); break;
        }
    }

    public static void InsertCodeBlock(SourceEditor tb, string language)
    {
        var body = tb.SelectedText;
        var fence = "```" + language;
        var start = tb.SelectionStart;
        tb.SelectedText = $"{fence}\n{body}\n```\n";
        if (body.Length == 0) tb.SelectionStart = start + fence.Length + 1; // caret inside the block
        tb.Focus();
    }

    /// <summary>Wraps the selection (or the caret) with markers.</summary>
    private static void Wrap(SourceEditor tb, string left, string right)
    {
        var start = tb.SelectionStart;
        var sel = tb.SelectedText;
        tb.SelectedText = left + sel + right;
        if (sel.Length == 0)
            tb.SelectionStart = start + left.Length; // caret between the markers
        else
        {
            tb.SelectionStart = start;
            tb.SelectionLength = left.Length + sel.Length + right.Length;
        }
        tb.Focus();
    }

    /// <summary>Replaces any block marker on the caret's line with <paramref name="prefix"/>.</summary>
    private static void Prefix(SourceEditor tb, string prefix)
    {
        var caret = tb.SelectionStart;
        var line = tb.GetLineIndexFromCharacterIndex(caret);
        if (line < 0) line = 0;
        var lineStart = tb.GetCharacterIndexFromLineIndex(line);
        var content = (tb.GetLineText(line) ?? string.Empty).TrimEnd('\r', '\n');
        var stripped = BlockPrefix().Replace(content, string.Empty);
        var newLine = prefix + stripped;

        tb.Select(lineStart, content.Length);
        tb.SelectedText = newLine;
        tb.SelectionStart = lineStart + newLine.Length;
        tb.Focus();
    }

    private static void InsertAtCaret(SourceEditor tb, string text)
    {
        tb.SelectedText = text;
        tb.SelectionStart += text.Length;
        tb.Focus();
    }
}
