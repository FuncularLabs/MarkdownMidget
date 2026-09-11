namespace MarkdownMidget;

/// <summary>
/// Which spelling of the document the Markdown source view (Ctrl+E) shows, and what
/// counts as an edit once it is showing it.
///
/// The docs promise that a document opened and saved only in the source view is
/// written back as typed. Entering the source view used to ask the editor for the
/// document — <c>window.MDM.getMarkdown()</c>, Milkdown's serialisation — so a
/// setext-heading or reference-link file arrived in the box already rewritten into
/// Markdown Midget's conventions, and a save from there wrote the rewrite without
/// ever showing the user the file they opened. The promise was false.
///
/// The window already holds the missing spelling. As <see cref="ExternalChange"/>
/// describes, a document has two baselines for the same saved state:
/// <c>_cleanMarkdown</c>, the editor's serialisation of it, and
/// <c>_diskBaseline</c>, the file as it was last read from or written to disk,
/// folded to LF. They differ for any file the editor normalises, and both are that
/// one saved state — so while the document is untouched, showing the file's own
/// spelling loses nothing and keeps the promise. The moment the formatted view has
/// changed the document, the file's spelling is stale and the editor's copy is the
/// only one holding the user's work; then it wins, and nothing here is in play.
///
/// Leaving the source view is unaffected: the box's text goes back through the
/// editor, which parses it into the document it was loaded from in the first place
/// (the file's text is what <c>LoadDocumentAsync</c> handed it), so a Ctrl+E there
/// and back is the same document and is not an edit.
/// </summary>
internal static class SourceText
{
    /// <summary>
    /// What to put in the source box on the way in. <paramref name="editorMarkdown"/>
    /// is what the editor just handed over; <paramref name="diskBaseline"/> is the
    /// file's own spelling, null or empty when there is no file behind the document
    /// (untitled, dropped text, nothing open) and the editor's copy is all there is.
    /// </summary>
    public static string For(string editorMarkdown, string cleanMarkdown, string? diskBaseline) =>
        !string.IsNullOrEmpty(diskBaseline)
        && string.Equals(editorMarkdown, cleanMarkdown, StringComparison.Ordinal)
            ? diskBaseline
            : editorMarkdown;

    /// <summary>
    /// True when <paramref name="sourceText"/> — what the box holds now — is still
    /// the document as last opened or saved. Dirty tracking has to agree with
    /// <see cref="For"/> or the title gains a `*`, a crash snapshot is written and
    /// the close prompt fires the instant Ctrl+E is pressed on an untouched
    /// normalised file. Either baseline counts, for the same reason
    /// <see cref="ExternalChange.IsRealChange"/> consults both: they are two
    /// spellings of one saved state, and saving either writes bytes the file
    /// already has.
    ///
    /// The empty <paramref name="diskBaseline"/> is excluded deliberately and not
    /// as a null-guard: for an untitled document it would make "delete everything
    /// you have typed" read as "back to the file", and there is no file.
    /// </summary>
    public static bool IsUnmodified(string sourceText, string cleanMarkdown, string? diskBaseline) =>
        string.Equals(sourceText, cleanMarkdown, StringComparison.Ordinal)
        || (!string.IsNullOrEmpty(diskBaseline)
            && string.Equals(sourceText, diskBaseline, StringComparison.Ordinal));
}
