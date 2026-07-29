using System.Collections.Generic;

namespace MarkdownMidget.Spelling;

/// <summary>
/// Maps a caret/click offset onto the misspelling drawn there.
/// </summary>
internal static class SpellHitTest
{
    /// <summary>
    /// The flagged range containing <paramref name="index"/>, or null.
    ///
    /// Ranges come from checking the WHOLE document, and the menu must use them
    /// verbatim: re-deriving the word at the click point disagrees with the engine
    /// (hyphens/apostrophes) and re-checking that word alone loses context-only
    /// errors such as a repeated word — which silently removed the entire spell
    /// block, Add to Dictionary included, from a visibly squiggled word.
    ///
    /// The bounds are inclusive of the range end so a click on the trailing edge of
    /// the last character still counts as being on the word.
    /// </summary>
    public static (int Start, int Length)? RangeAt(
        IReadOnlyList<(int Start, int Length)> ranges, int index, int textLength)
    {
        if (index < 0) return null;
        foreach (var r in ranges)
        {
            if (index < r.Start || index > r.Start + r.Length) continue;
            // Ranges are shifted through edits and can briefly outrun the live text;
            // acting on one that no longer fits would read past the end.
            if (r.Start < 0 || r.Start + r.Length > textLength) return null;
            return r;
        }
        return null;
    }
}
