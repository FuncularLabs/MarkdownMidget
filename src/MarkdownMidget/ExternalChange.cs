namespace MarkdownMidget;

/// <summary>
/// The one decision in external-change handling that can be got wrong without
/// anyone noticing for months (issue #4): is what the file now holds actually
/// different from what we have?
///
/// A window keeps two baselines for its document. <c>_cleanMarkdown</c> is the
/// EDITOR's serialisation of the last opened/saved state - the thing dirty
/// tracking compares against, and rightly so, because it is what the editor will
/// hand back for an untouched document. But it is not what is on disk: the
/// serialiser normalises (a setext heading comes back ATX, a reference link
/// inlined), so for most files the two differ from the moment they are opened.
/// Comparing the disk against the editor's baseline meant that a tool rewriting
/// the file with IDENTICAL bytes - a formatter with nothing to do, a sync client,
/// git touching a checkout - raised a change event, failed the comparison, and
/// prompted the user about a file that had not changed. The second baseline,
/// <c>_diskBaseline</c>, is the file as it was last read from or written to disk,
/// folded to LF like everything in memory. This decision consults BOTH: a file
/// equal to either baseline has nothing to report.
/// </summary>
internal static class ExternalChange
{
    /// <summary>
    /// True when <paramref name="diskText"/> differs from both baselines.
    /// <paramref name="diskText"/> is the file as it reads now, folded
    /// (<see cref="DocumentText.Detect(byte[])"/>), so a rewrite that changed only
    /// the line endings or the byte-order mark is not a change either.
    /// <paramref name="cleanMarkdown"/> still counts: a file that now holds exactly
    /// what the editor considers clean has nothing to reload, which is the check
    /// that existed before <paramref name="diskBaseline"/> did.
    /// </summary>
    public static bool IsRealChange(string diskText, string cleanMarkdown, string diskBaseline) =>
        !string.Equals(diskText, diskBaseline, StringComparison.Ordinal) &&
        !string.Equals(diskText, cleanMarkdown, StringComparison.Ordinal);
}
