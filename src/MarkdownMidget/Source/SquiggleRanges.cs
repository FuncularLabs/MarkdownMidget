using System.Collections.Generic;

namespace MarkdownMidget.Source;

/// <summary>
/// The offset arithmetic that keeps misspelling underlines glued to their words
/// between spell passes. Pure and WPF-free so it can be tested directly; the drawing
/// lives in <see cref="SquiggleRenderer"/>.
/// </summary>
internal static class SquiggleRanges
{
    /// <summary>
    /// Shift ranges through one document edit at <paramref name="offset"/> that
    /// inserted <paramref name="added"/> characters and removed
    /// <paramref name="removed"/>. Ranges entirely before the edit are untouched;
    /// ranges entirely after slide by the net delta; a range the edit overlapped is
    /// dropped, because the word under it may no longer be the word that was flagged —
    /// the next (debounced) spell pass will re-flag it if it is still wrong.
    /// </summary>
    public static IReadOnlyList<(int Start, int Length)> Shift(
        IReadOnlyList<(int Start, int Length)> ranges, int offset, int added, int removed)
    {
        if (ranges.Count == 0) return ranges;
        var delta = added - removed;
        var updated = new List<(int, int)>(ranges.Count);
        foreach (var (start, len) in ranges)
        {
            if (start + len <= offset) { updated.Add((start, len)); continue; }          // wholly before
            if (start >= offset + removed) { updated.Add((start + delta, len)); continue; } // wholly after
            // overlapped by the edit — stale, drop it
        }
        return updated;
    }
}
