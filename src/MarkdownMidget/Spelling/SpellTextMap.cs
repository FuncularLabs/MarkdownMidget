using System;
using System.Collections.Generic;

namespace MarkdownMidget.Spelling;

/// <summary>One run of checkable text: where it starts in the extracted plain
/// string, where it starts in the ProseMirror document, and its length.</summary>
internal sealed record SpellSegment(int PlainStart, int PmPos, int Len);

/// <summary>
/// Maps offsets in the editor-extracted plain text back to ProseMirror positions.
/// The extraction (spell-extract.js) walks the document's text nodes, skipping
/// code, and records a segment per run; inline positions are contiguous within a
/// block, so start + length stays valid as long as the word doesn't cross the
/// "\n" gap we insert between blocks — which no real word does.
/// </summary>
internal static class SpellTextMap
{
    /// <summary>ProseMirror position for a plain-text offset, or -1 when the offset
    /// falls in a gap (block boundary / skipped code).</summary>
    public static int ToPm(IReadOnlyList<SpellSegment> segments, int plainOffset)
    {
        // Segments are in ascending PlainStart order — binary search.
        int lo = 0, hi = segments.Count - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            var s = segments[mid];
            if (plainOffset < s.PlainStart) hi = mid - 1;
            else if (plainOffset >= s.PlainStart + s.Len) lo = mid + 1;
            else return s.PmPos + (plainOffset - s.PlainStart);
        }
        return -1;
    }

    /// <summary>
    /// Translate checker ranges (plain-text offsets) into ProseMirror ranges,
    /// dropping anything that doesn't map cleanly inside one segment run.
    /// </summary>
    public static List<(int From, int To)> MapRanges(
        IReadOnlyList<SpellSegment> segments, IEnumerable<(int Start, int Length)> ranges)
    {
        var result = new List<(int, int)>();
        foreach (var (start, length) in ranges)
        {
            if (length <= 0) continue;
            var from = ToPm(segments, start);
            if (from < 0) continue;
            var lastChar = ToPm(segments, start + length - 1);
            if (lastChar < 0 || lastChar + 1 - from != length) continue; // crossed a gap — not a real word
            result.Add((from, lastChar + 1));
        }
        return result;
    }
}
