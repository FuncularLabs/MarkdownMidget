using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace MarkdownMidget.Spelling;

/// <summary>
/// Finds the code regions of a markdown string — fenced blocks and inline code —
/// so the spell checker can skip them. This is the source-view twin of the
/// WYSIWYG extraction (spell-extract.js), which gets the same information for
/// free from the ProseMirror node types.
/// </summary>
internal static class MarkdownCodeRanges
{
    // Closed fences: ``` or ~~~ openers (up to 3 leading spaces per CommonMark),
    // everything through the matching closer line.
    private static readonly Regex FenceRx = new(
        @"^\s{0,3}(`{3,}|~{3,}).*?$.*?^\s{0,3}\1`*\s*$",
        RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.Compiled);

    // An opener with no closer (the user is mid-typing a code block): everything
    // from it to the end of the document is code.
    private static readonly Regex OpenFenceRx = new(
        @"^\s{0,3}(?:`{3,}|~{3,})", RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex InlineRx = new(@"`[^`\r\n]+`", RegexOptions.Compiled);

    /// <summary>Sorted, non-overlapping [start, end) code ranges.</summary>
    public static List<(int Start, int End)> Find(string markdown)
    {
        var ranges = new List<(int, int)>();
        if (string.IsNullOrEmpty(markdown)) return ranges;

        foreach (Match m in FenceRx.Matches(markdown))
            ranges.Add((m.Index, m.Index + m.Length));

        // A dangling opener after the last closed fence exempts the rest of the doc.
        var tail = ranges.Count == 0 ? 0 : ranges[^1].Item2;
        var open = OpenFenceRx.Match(markdown, tail);
        while (open.Success && InsideAny(ranges, open.Index))
            open = open.NextMatch();
        if (open.Success)
            ranges.Add((open.Index, markdown.Length));

        foreach (Match m in InlineRx.Matches(markdown))
            if (!InsideAny(ranges, m.Index))
                ranges.Add((m.Index, m.Index + m.Length));

        ranges.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return ranges;
    }

    /// <summary>The complement of <see cref="Find"/>: [start, end) spans safe to spell-check.</summary>
    public static IEnumerable<(int Start, int End)> ProseSpans(string markdown, List<(int Start, int End)> code)
    {
        var pos = 0;
        foreach (var (s, e) in code)
        {
            if (s > pos) yield return (pos, s);
            pos = Math.Max(pos, e);
        }
        if (pos < markdown.Length) yield return (pos, markdown.Length);
    }

    private static bool InsideAny(List<(int Start, int End)> ranges, int index)
    {
        foreach (var (s, e) in ranges)
            if (index >= s && index < e) return true;
        return false;
    }
}
