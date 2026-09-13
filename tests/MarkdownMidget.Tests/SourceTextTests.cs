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

    // ===== a document loaded while the source view is already showing =====
    //
    // File ▸ Open, Open Recent, a drop on the source view, both reloads after the
    // file changed on disk, the Save As that follows an external change, and crash
    // recovery into a window launched with --source all install the new document
    // through LoadDocumentAsync without leaving the source view. The install fills
    // the box with the editor's settled serialisation — the rewrite Ctrl+E stopped
    // showing — and the document reads as unmodified, so a save from there wrote
    // Markdown Midget's conventions over a file the user never saw in the formatted
    // view. AfterLoad is the second write, made once the load's baselines are
    // current; null means the box already holds the right text.

    [Fact]
    public void AFileOpenedWhileTheSourceViewIsShowingIsShownAsItIsOnDisk()
        // What LoadDocumentAsync holds straight after the install: the box and the
        // clean baseline are both the editor's serialisation, and the disk baseline
        // is the file.
        => Assert.Equal(OnDisk, SourceText.AfterLoad(sourceViewShowing: true, Serialised, Serialised, OnDisk));

    [Theory]
    [InlineData(Serialised, OnDisk)]       // a file the editor normalises
    [InlineData(Serialised, Serialised)]   // one it does not
    [InlineData(Serialised, "")]           // no file behind the document
    [InlineData(Serialised, null)]
    [InlineData("", "")]                   // File ▸ New's empty document
    public void ALoadInTheSourceViewShowsWhatCtrlEWouldShowAfterTheSameLoad(string settled, string? disk)
    {
        // One rule, two ways in. Loaded in the formatted view and then Ctrl+E, the
        // box gets For(editor, clean, disk) with the editor's text as the clean
        // baseline; loaded with the source view already showing, it has to end up
        // holding the same text, or the view you happened to be in when you opened
        // the file decides what you save.
        var viaCtrlE = SourceText.For(settled, settled, disk);
        var inPlace = SourceText.AfterLoad(sourceViewShowing: true, settled, settled, disk) ?? settled;
        Assert.Equal(viaCtrlE, inPlace);
    }

    [Fact]
    public void ALoadInTheFormattedViewLeavesTheBoxForCtrlEToFill()
        // The box is hidden there, and Ctrl+E fills it from the editor when it
        // opens; writing it now would be a whole-text replace nobody sees.
        => Assert.Null(SourceText.AfterLoad(sourceViewShowing: false, Serialised, Serialised, OnDisk));

    [Fact]
    public void WithNoFileBehindTheLoadTheEditorsTextStays()
    {
        // File ▸ New's document, and anything else with no file behind it, has no
        // disk spelling to prefer — For's own rule, applied on load.
        Assert.Null(SourceText.AfterLoad(sourceViewShowing: true, Serialised, Serialised, ""));
        Assert.Null(SourceText.AfterLoad(sourceViewShowing: true, Serialised, Serialised, null));
    }

    [Fact]
    public void AFileTheEditorDoesNotRewriteIsNotWrittenASecondTime()
        // The install already put that exact text in the box. Replacing it again
        // would move the caret to the top and re-run the spell pass for no change.
        => Assert.Null(SourceText.AfterLoad(sourceViewShowing: true, Serialised, Serialised, Serialised));

    [Fact]
    public void TextInTheBoxThatIsNotTheCleanBaselineIsNeverReplaced()
    {
        // The decision reads the box at the moment it writes. If anything but the
        // freshly installed document is in it — a Ctrl+E that landed in the middle
        // of the load, then typing — that text is the user's, and the file's
        // spelling would overwrite it. For's rule hands the box's own text back, so
        // there is nothing to write.
        var typed = OnDisk + "typed while the load was finishing\n";
        Assert.Null(SourceText.AfterLoad(sourceViewShowing: true, typed, Serialised, OnDisk));
    }

    [Fact]
    public void ALoadInTheSourceViewOpensCleanAndLeavesTheViewClean()
    {
        // No `*` after the load, and back to the formatted view with no edit is not
        // an edit: both ask IsUnmodifiedText of the box, which in the source view is
        // IsUnmodified — and the file's spelling is one of the two baselines it takes.
        var shown = SourceText.AfterLoad(sourceViewShowing: true, Serialised, Serialised, OnDisk);
        Assert.NotNull(shown);
        Assert.True(SourceText.IsUnmodified(shown!, Serialised, OnDisk));
    }

    [Fact]
    public void AFileOpenedAndSavedOnlyInTheSourceViewIsWrittenBackByteForByte()
    {
        // The promise end to end, #3 included: a CRLF file with a UTF-8 byte-order
        // mark and the constructs the editor rewrites, decoded as Open decodes it,
        // given the disk baseline LoadDocumentAsync gives it, shown as a load in the
        // source view shows it, and encoded as Save encodes the box. The bytes that
        // come out are the bytes that went in.
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }
            .Concat(System.Text.Encoding.UTF8.GetBytes(OnDisk.Replace("\n", "\r\n", StringComparison.Ordinal)))
            .ToArray();
        var opened = DocumentText.Detect(bytes);

        var box = SourceText.AfterLoad(sourceViewShowing: true, Serialised, Serialised, DocumentText.Fold(opened.Text));

        Assert.NotNull(box);
        Assert.Equal(bytes, DocumentText.Encode(box!, opened.Ending, opened.HadBom));
    }

    [Fact]
    public void ARecoveredDocumentInTheSourceViewShowsTheRecoveredWorkNeverTheFile()
    {
        // LoadRecoveredAsync (and its encrypted twin) loads the crash snapshot
        // through LoadDocumentAsync, so at the moment of this decision the disk
        // baseline is the snapshot's text; recovery then points both baselines at
        // the file. The box must hold the recovered work — never the file, which is
        // missing it — and still read as modified against the file afterwards, as
        // the title's `*` says. A snapshot taken in the source view is what the user
        // typed, so it comes back in their spelling, not the editor's rewrite of it.
        var snapshot = OnDisk + "\nUnsaved paragraph.\n";
        var settled = Serialised + "\nUnsaved paragraph.\n";

        var shown = SourceText.AfterLoad(sourceViewShowing: true, settled, settled, snapshot);

        Assert.Equal(snapshot, shown);
        Assert.False(SourceText.IsUnmodified(shown!, cleanMarkdown: OnDisk, diskBaseline: OnDisk));
    }

    [Fact]
    public void ARecoveredSnapshotThatMatchesTheFileReadsUnmodifiedAsRecoveryDecided()
    {
        // Recovery computes `*` as "the snapshot differs from the file". When it does
        // not, the box has to agree — showing the editor's rewrite here would put the
        // `*` back on the next dirty check, for a document with nothing unsaved.
        var shown = SourceText.AfterLoad(sourceViewShowing: true, Serialised, Serialised, OnDisk);

        Assert.Equal(OnDisk, shown);
        Assert.True(SourceText.IsUnmodified(shown!, cleanMarkdown: OnDisk, diskBaseline: OnDisk));
    }

    // ===== the wiring, read from the source =====

    [Fact]
    public void LoadDocumentAsyncAsksTheRuleOnceTheBaselinesAreCurrent()
    {
        // LoadDocumentAsync is the window's, and no test can run it, so this is the
        // one check that every route through it — Open, Open Recent, a drop on the
        // source view, both reloads, both Save As branches, both recoveries — asks
        // AfterLoad with the arguments the tests above give it, and asks it where the
        // answer is right: after _diskBaseline is the new file's, and after
        // SetCleanBaselineAsync has taken the editor's serialisation. Asked before
        // that, in the source view the clean baseline would be read off the box —
        // the file's spelling — and #4's external-change check would lose the
        // editor's spelling of the same saved state. A scan proves the call is there,
        // with these arguments and in this order; the tests above prove what it does.
        var body = RepoSources.MethodBody(
            RepoSources.Read("src", "MarkdownMidget", "MainWindow.xaml.cs"),
            "private async Task LoadDocumentAsync(");

        const string decision = "SourceText.AfterLoad(_sourceMode, SourceBox.Text, _cleanMarkdown, _diskBaseline) is { } ownSpelling";
        const string write = "SourceBox.Text = ownSpelling;";
        var disk = body.IndexOf("_diskBaseline = DocumentText.Fold(doc.Text);", StringComparison.Ordinal);
        var clean = body.IndexOf("await SetCleanBaselineAsync();", StringComparison.Ordinal);
        var asked = body.IndexOf(decision, StringComparison.Ordinal);
        var written = body.IndexOf(write, StringComparison.Ordinal);

        Assert.True(disk >= 0, "LoadDocumentAsync no longer sets _diskBaseline from the loaded text");
        Assert.True(clean > disk, "SetCleanBaselineAsync no longer follows the disk baseline in LoadDocumentAsync");
        Assert.True(asked > clean, $"no \"{decision}\" after SetCleanBaselineAsync in LoadDocumentAsync");
        Assert.True(written > asked, $"no \"{write}\" after the decision in LoadDocumentAsync");
    }

    [Fact]
    public void CtrlEAppliesTheRuleTheLoadDelegatesTo()
    {
        // The other half of "the same rule": AfterLoad is For, and Ctrl+E is For
        // with the editor's answer and the window's two baselines.
        var body = RepoSources.MethodBody(
            RepoSources.Read("src", "MarkdownMidget", "MainWindow.xaml.cs"),
            "private async Task SetSourceModeAsync(");
        Assert.Contains("SourceBox.Text = SourceText.For(latest, _cleanMarkdown, _diskBaseline);", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheInstallHelperNoLongerDescribesTheLoadCaseAsAnAcceptedGap()
    {
        // Comment truth: SetDocumentMarkdownAsync said a document loaded while the
        // source view is showing keeps the editor's serialisation "until the next
        // Ctrl+E round trip". It no longer does, and the comment has to say where
        // the case is decided instead.
        var body = RepoSources.MethodBody(
            RepoSources.Read("src", "MarkdownMidget", "MainWindow.xaml.cs"),
            "private async Task<string?> SetDocumentMarkdownAsync(");
        Assert.DoesNotContain("Accepted gap", body, StringComparison.Ordinal);
        Assert.Contains("LoadDocumentAsync", body, StringComparison.Ordinal);
    }
}
