using MarkdownMidget;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>
/// What Ctrl+E puts in the source box, and what counts as an edit once it is there.
///
/// The promise the docs make — "a document opened and saved only in the source view
/// is written back as typed" — was false, and this class is where it becomes true.
/// Entering the source view asked the editor for the document, so a setext-heading
/// or reference-link file arrived in the box already rewritten into Markdown
/// Midget's conventions; saving from there wrote the rewrite, having never shown the
/// user the file they opened.
///
/// The window holds two baselines for the same saved state (see ExternalChange):
/// <c>_cleanMarkdown</c>, the editor's serialisation of it, and <c>_diskBaseline</c>,
/// the file's own spelling folded to LF. When the document has not been touched
/// since it was opened or saved, those describe the same document and the file's
/// spelling is the honest one to show. The moment the formatted view has changed the
/// document, the file's spelling is stale and the editor's is the only copy of the
/// user's work — so it wins, always.
/// </summary>
public class SourceTextTests
{
    // A file as it was written, and the same document as the editor re-serialises it.
    private const string OnDisk = "Title\n=====\n\nSee [docs][d].\n\n[d]: https://example.invalid\n";
    private const string Serialised = "# Title\n\nSee [docs](https://example.invalid).\n";

    // ===== which text the source box is given =====

    [Fact]
    public void AnUnmodifiedDocumentShowsTheFileAsItIsOnDisk()
        // The finding: press Ctrl+E straight after opening and you should see YOUR
        // file. Saving from there then writes back what you typed, because what you
        // typed is what is in the box.
        => Assert.Equal(OnDisk, SourceText.For(Serialised, Serialised, OnDisk));

    [Fact]
    public void ADocumentTheFormattedViewHasChangedShowsWhatTheEditorHolds()
    {
        // The disk text is now stale — it is missing the edit — and showing it would
        // silently discard work. The editor's copy is the document.
        var edited = Serialised + "\nA new paragraph.\n";
        Assert.Equal(edited, SourceText.For(edited, Serialised, OnDisk));
    }

    [Fact]
    public void WithNoFileBehindTheDocumentTheEditorsTextIsAllThereIs()
    {
        // An untitled document, and a window with nothing open: the field is empty
        // rather than null in the app, so both spellings of "no baseline" are pinned.
        Assert.Equal(Serialised, SourceText.For(Serialised, Serialised, null));
        Assert.Equal(Serialised, SourceText.For(Serialised, Serialised, ""));
    }

    [Fact]
    public void AfterASaveTheTwoBaselinesCoincideSoTheChoiceIsInvisible()
        // Save writes the editor's own text and sets both baselines from it, so
        // there is nothing for this decision to prefer. Pinned because it is the
        // state the app spends most of its time in: the change must be a no-op here.
        => Assert.Equal(Serialised, SourceText.For(Serialised, Serialised, Serialised));

    // ===== what counts as an edit while the box is showing the file =====

    [Fact]
    public void TheFilesOwnSpellingIsNotAnEditSoTheSourceViewDoesNotOpenDirty()
        // The half of this that dirty tracking has to agree with. Without it the
        // title gains a `*` the instant Ctrl+E is pressed, a crash snapshot is
        // written for a document with nothing unsaved in it, and closing prompts.
        => Assert.True(SourceText.IsUnmodified(OnDisk, Serialised, OnDisk));

    [Fact]
    public void TheEditorsOwnSerialisationIsNotAnEditEither()
        // The other text the box can legitimately hold: the source view entered on a
        // document the formatted view had already changed and then undone back to
        // clean, and the save-from-source case where both baselines are this one.
        => Assert.True(SourceText.IsUnmodified(Serialised, Serialised, OnDisk));

    [Fact]
    public void TypingInTheSourceViewIsAnEdit()
    {
        Assert.False(SourceText.IsUnmodified(OnDisk + "typed\n", Serialised, OnDisk));
        Assert.False(SourceText.IsUnmodified(Serialised + "typed\n", Serialised, OnDisk));
    }

    [Fact]
    public void WithNoFileBehindTheDocumentOnlyTheEditorsBaselineCounts()
    {
        // The trap in treating "equal to the disk baseline" as unmodified: for an
        // untitled document that baseline is empty, and clearing the box would
        // otherwise read as "back to the file" — there is no file. Emptied work
        // stays dirty, and the close prompt still happens.
        Assert.False(SourceText.IsUnmodified("", "typed but never saved\n", ""));
        Assert.False(SourceText.IsUnmodified("", "typed but never saved\n", null));
        Assert.True(SourceText.IsUnmodified("typed but never saved\n", "typed but never saved\n", ""));
    }
}
