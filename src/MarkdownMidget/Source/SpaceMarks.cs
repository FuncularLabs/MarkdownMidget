using System;
using System.Collections.Generic;
using System.Linq;

namespace MarkdownMidget.Source;

/// <summary>
/// Which spaces the source view marks with · while the ¶ toggle is on: the ones that are
/// hard to see and change what Markdown does. Trailing spaces (two make a line break),
/// runs of two or more inside a line, and every space on a whitespace-only line. Leading
/// indentation only when it looks wrong: tabs and spaces mixed, or indented past an open
/// list item's marker but short of its text, so it lines up with neither. Inside code
/// blocks, front matter and HTML comments, only trailing spaces. A blockquote's markers,
/// the padding in a row that starts with | and the spaces after a list marker are not runs;
/// indentation is measured from after the blockquote markers.
///
/// Reads one line at a time in the <see cref="State"/> the lines above left, so the view
/// can keep the state per line and read only the lines on screen.
/// </summary>
internal static class SpaceMarks
{
    /// <summary>The block context a line is read in: an open code fence (<c>`</c> or <c>~</c>,
    /// its length and the text column of the list item it opened in), front matter
    /// (<c>-</c>) or HTML comment (<c>&lt;</c>); whether an indented code block may start or
    /// goes on here; the blockquote depth; the open list items as (marker column, text
    /// column), innermost last, counted from after the blockquote markers with a tab to the
    /// next column of 4; and whether this is the first line.</summary>
    internal sealed record State(char Fence, int FenceLength, int FenceColumn, bool CodeMayStart, bool InCode,
        int Quote, (int Marker, int Content)[] Items, bool Top)
    {
        internal static readonly State Start = new('\0', 0, 0, true, false, 0, [], true);
    }

    /// <summary>The offsets in <paramref name="line"/> (without its line ending) to mark,
    /// in order, and the state the next line is read in.</summary>
    internal static (int[] Marks, State Next) Step(string line, State before)
    {
        int quote = 0, from = 0;   // the blockquote markers: up to 3 spaces, '>' and one optional space, repeated
        for (var p = 0; ; quote++)
        {
            while (p < line.Length && p - from < 3 && line[p] == ' ') p++;
            if (p == line.Length || line[p] != '>') break;
            p = from = p + 1 < line.Length && line[p + 1] == ' ' ? p + 2 : p + 1;
        }
        var end = line.Length;
        while (end > from && line[end - 1] is ' ' or '\t') end--;
        int lead = from, col = 0;
        for (; lead < end && line[lead] is ' ' or '\t'; lead++) col = line[lead] == '\t' ? col + 4 - col % 4 : col + 1;
        int[] trailing = [.. Enumerable.Range(end, line.Length - end).Where(i => line[i] == ' ')];
        var text = line.AsSpan(lead, end - lead);

        if (before.Fence != '\0')
        {
            // A code fence ends with the blockquote or list item it opened in; the line is then read like any other.
            var inside = before.Fence is '<' or '-' || quote > before.Quote
                || (quote == before.Quote && (text.IsEmpty || col >= before.FenceColumn));
            if (inside)
            {
                var closes = before.Fence switch
                {
                    '<' => line.Contains("-->", StringComparison.Ordinal),
                    '-' => line.AsSpan(0, end) is "---",   // not "...": the formatted view's front-matter parser closes only on ---
                    _ => quote == before.Quote && col < before.FenceColumn + 4 && text.Length >= before.FenceLength
                         && !text.ContainsAnyExcept(before.Fence),
                };
                return (trailing, closes ? before with { Fence = '\0', CodeMayStart = true } : before);
            }
            before = before with { Fence = '\0' };
        }
        if (before.Top)
        {
            if (line.AsSpan(0, end) is "---") return (trailing, before with { Fence = '-', Top = false });
            before = before with { Top = false };
        }
        if (quote != before.Quote)   // into or out of a blockquote: the list items stay behind
            before = before with { Quote = quote, Items = [], CodeMayStart = quote > before.Quote || before.CodeMayStart };
        if (text.IsEmpty) return (trailing, before with { CodeMayStart = true });

        var items = before.Items;
        var indent = line.AsSpan(from, lead - from);
        var suspect = (indent.Contains(' ') && indent.Contains('\t')) || items.Any(item => item.Marker < col && col < item.Content);
        var open = items.Length;
        while (open > 0 && items[open - 1].Content > col) open--;   // drop the items this line is not inside
        var within = open == items.Length ? items : items[..open];
        var container = open > 0 ? items[open - 1].Content : 0;
        if (col >= container + 4 && (before.CodeMayStart || before.InCode))   // indented code
            return (trailing, before with { Items = within, CodeMayStart = false, InCode = true });

        List<int> marks = suspect ? [.. Enumerable.Range(from, lead - from).Where(i => line[i] == ' ')] : [];
        var breakOrUnderline = text[0] is '=' or '-' or '*' or '_' && !text.ContainsAnyExcept(text[0], ' ');
        var next = before with { Items = within, CodeMayStart = text[0] == '#' || breakOrUnderline, InCode = false };
        var skip = 0;
        if (ListMarker(text) is var marker and > 0)   // a list item: its marker and the spaces after it are not a run
        {
            var spaces = text[marker..].Length - text[marker..].TrimStart(' ').Length;
            skip = marker + spaces;
            container = col + marker + (spaces is >= 1 and <= 4 ? spaces : 1);
            next = next with { Items = [.. within, (col, container)] };
            text = text[skip..];   // a fence may start right after the marker
        }
        var fence = !text.IsEmpty && text[0] is '`' or '~' ? text.Length - text.TrimStart(text[0]).Length : 0;
        if (fence >= 3 && col < container + 4 && !(text[0] == '`' && text[fence..].Contains('`')))
            return ([.. marks, .. trailing], next with { Fence = text[0], FenceLength = fence, FenceColumn = container });
        if (text.StartsWith("<!--") && !text[4..].Contains("-->", StringComparison.Ordinal))
            return ([.. marks, .. trailing], next with { Fence = '<' });

        if (line[lead] != '|')   // a table row's padding is alignment, not a run
            for (var i = lead + skip; i < end; i++)
            {
                var run = i;
                while (line[run] == ' ') run++;
                if (run - i >= 2) marks.AddRange(Enumerable.Range(i, run - i));
                i = run;
            }
        return ([.. marks, .. trailing], next);
    }

    /// <summary>The length of the list marker <paramref name="text"/> starts with (<c>-</c>,
    /// <c>*</c>, <c>+</c>, or up to 9 digits and <c>.</c> or <c>)</c>, then a space, a tab or
    /// the end), or 0. A thematic break such as <c>* * *</c> is not one.</summary>
    private static int ListMarker(ReadOnlySpan<char> text)
    {
        var digits = text.Length - text.TrimStart("0123456789").Length;
        var length = text[0] is '-' or '*' or '+' ? 1
            : digits is >= 1 and <= 9 && digits < text.Length && text[digits] is '.' or ')' ? digits + 1 : 0;
        if (length == 0 || (length < text.Length && text[length] is not (' ' or '\t'))) return 0;
        var thematicBreak = text[0] is '-' or '*' && !text.ContainsAnyExcept(text[0], ' ', '\t') && text.Count(text[0]) >= 3;
        return thematicBreak ? 0 : length;
    }
}
