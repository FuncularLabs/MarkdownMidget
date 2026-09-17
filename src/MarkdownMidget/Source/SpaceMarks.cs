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
/// blocks, only trailing spaces. The padding in a row that starts with | and the spaces
/// after a list marker are not runs.
///
/// Reads one line at a time in the <see cref="State"/> the lines above left, so the view
/// can keep the state per line and read only the lines on screen.
/// </summary>
internal static class SpaceMarks
{
    /// <summary>The block context a line is read in: an open code fence, whether an
    /// indented code block may start or goes on here, and the open list items as
    /// (marker column, text column), innermost last. A tab counts to the next column of 4.</summary>
    internal sealed record State(char Fence, int FenceLength, bool CodeMayStart, bool InCode, (int Marker, int Content)[] Items)
    {
        internal static readonly State Start = new('\0', 0, true, false, []);
    }

    /// <summary>The offsets in <paramref name="line"/> (without its line ending) to mark,
    /// in order, and the state the next line is read in.</summary>
    internal static (int[] Marks, State Next) Step(string line, State before)
    {
        var end = line.Length;
        while (end > 0 && line[end - 1] is ' ' or '\t') end--;
        int lead = 0, col = 0;
        for (; lead < end && line[lead] is ' ' or '\t'; lead++) col = line[lead] == '\t' ? col + 4 - col % 4 : col + 1;
        var trailing = Enumerable.Range(end, line.Length - end).Where(i => line[i] == ' ');
        var text = line.AsSpan(lead, end - lead);

        if (before.Fence != '\0')   // in a fence: trailing spaces only, until a run of its character at least as long
        {
            var closes = text.Length >= before.FenceLength && !text.ContainsAnyExcept(before.Fence);
            return (trailing.ToArray(), closes ? before with { Fence = '\0', FenceLength = 0, CodeMayStart = true } : before);
        }
        if (text.IsEmpty) return (trailing.ToArray(), before with { CodeMayStart = true });

        var items = before.Items;
        var suspect = (line.AsSpan(0, lead).Contains(' ') && line.AsSpan(0, lead).Contains('\t'))
            || items.Any(item => item.Marker < col && col < item.Content);
        var open = items.Length;
        while (open > 0 && items[open - 1].Content > col) open--;   // drop the items this line is not inside
        var inside = open == items.Length ? items : items[..open];
        var container = open > 0 ? items[open - 1].Content : 0;
        if (col >= container + 4 && (before.CodeMayStart || before.InCode))   // indented code
            return (trailing.ToArray(), before with { Items = inside, CodeMayStart = false, InCode = true });

        var marks = suspect ? Enumerable.Range(0, lead).Where(i => line[i] == ' ').ToList() : [];
        var next = before with { Items = inside, CodeMayStart = text[0] == '#', InCode = false };
        var fence = text[0] is '`' or '~' ? text.Length - text.TrimStart(text[0]).Length : 0;
        if (fence >= 3 && col < container + 4 && !(text[0] == '`' && text[fence..].Contains('`')))
            return ([.. marks, .. trailing], next with { Fence = text[0], FenceLength = fence });

        var skip = 0;
        if (ListMarker(text) is var marker and > 0)   // a list item: its marker and the spaces after it are not a run
        {
            var spaces = text[marker..].Length - text[marker..].TrimStart(' ').Length;
            skip = marker + spaces;
            next = next with { Items = [.. inside, (col, col + marker + (spaces is >= 1 and <= 4 ? spaces : 1))] };
        }
        if (text[0] != '|')   // a table row's padding is alignment, not a run
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
