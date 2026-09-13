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
/// A document can also reach the source view without a Ctrl+E: opened, reloaded or
/// recovered while that view is already showing. <see cref="AfterLoad"/> applies the
/// same rule on that path, once the load has made both baselines the new document's.
///
/// Leaving the source view is unaffected: the box's text goes back through the
/// editor, which parses it into the document it was loaded from in the first place
/// (the file's text is what <c>LoadDocumentAsync</c> handed it), so a Ctrl+E there
/// and back is the same document and is not an edit. That last part is not the
/// serialiser's to guarantee, so it is not left to it: a box that was still
/// unmodified on the way out adopts the editor's spelling as the new clean baseline
/// (<c>SetSourceModeAsync</c>), and a parse that does not come back to the same text
/// therefore cannot mark an untouched document modified.
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
    /// What the source box is given once a document has been loaded into the window
    /// — opened, reopened from Open Recent, dropped on the source view, reloaded
    /// after a change on disk, recovered — or null to leave the box as the load left
    /// it.
    ///
    /// The load installs the editor's settled serialisation in both surfaces
    /// (<c>SetDocumentMarkdownAsync</c>), because that is the text the clean baseline
    /// is taken from. With the source view already showing, that puts the rewrite in
    /// front of the user and no Ctrl+E ever runs <see cref="For"/> over it. This is
    /// For, run where Ctrl+E would run it: once <c>LoadDocumentAsync</c> has made
    /// <paramref name="diskBaseline"/> the loaded document's own text and
    /// <paramref name="cleanMarkdown"/> the editor's serialisation of it. A file
    /// loaded in the formatted view and then Ctrl+E, or loaded with the source view
    /// showing, leaves the box holding the same text.
    ///
    /// A crash recovery is the one load where the two part, and the load that reaches
    /// here most deliberately: a snapshot taken in the source view is recovered into
    /// that view (<c>RecoveryPlan.EntersSourceView</c>, applied before the load), so
    /// this is what decides what the user sees of their rescued work. Its disk
    /// baseline at this point is still the snapshot the recovery handed over (it
    /// points both baselines at the file only afterwards), so the box shows the
    /// recovered work as it was captured; a Ctrl+E after a recovery that landed in
    /// the formatted view — a snapshot taken there, or one from a build that recorded
    /// no view — shows the editor's copy of that work instead. When the snapshot
    /// holds unsaved work,
    /// neither is the file and both read as modified against it; a snapshot that
    /// matches the file shows the file's text and reads unmodified, as the recovery
    /// itself decided.
    ///
    /// Null while the formatted view is showing (Ctrl+E fills the box when it opens),
    /// and whenever For's answer is what <paramref name="boxText"/> already holds —
    /// no file behind the document, text the editor does not rewrite, or text in the
    /// box that is not the load's own install — so the box is only ever replaced to
    /// turn the editor's serialisation of the loaded text back into that text.
    /// </summary>
    public static string? AfterLoad(bool sourceViewShowing, string boxText, string cleanMarkdown, string? diskBaseline)
    {
        if (!sourceViewShowing) return null;
        var shown = For(boxText, cleanMarkdown, diskBaseline);
        return string.Equals(shown, boxText, StringComparison.Ordinal) ? null : shown;
    }

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
