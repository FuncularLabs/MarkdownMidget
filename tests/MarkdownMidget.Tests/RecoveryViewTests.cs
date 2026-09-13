using System;
using System.IO;
using System.Linq;
using MarkdownMidget.Backup;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>
/// The view a crash recovery hands unsaved work back in.
///
/// Work being written in the Markdown source view (Ctrl+E) that died with the app
/// came back in the formatted view, and Ctrl+E there showed the editor's
/// re-serialisation of the recovered text — setext headings as `#`, reference links
/// inlined — which a save from either view then wrote to the file. The user never
/// saw their own words again.
///
/// The view is now part of the snapshot, written with every tick's metadata, and
/// recovery enters it BEFORE the work is loaded: the source view's own text is
/// chosen inside LoadDocumentAsync (SourceText.AfterLoad), where the snapshot is
/// still the disk baseline, so a switch made afterwards would show the rewrite
/// again. A snapshot written before the field existed says false and recovers in the
/// formatted view, exactly as it did then.
/// </summary>
public class RecoveredViewTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "mm-recovered-view-tests-" + Guid.NewGuid().ToString("N"));

    private BackupStore New(string id) => new(_dir, id);

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static byte[] Sealed(string markdown) =>
        Secure.SecureMarkdownFormat.Encrypt(markdown, "pw",
            Secure.SecureMarkdownFormat.KdfProfile.FastForTests);

    /// <summary>The metadata a later launch reads for one session, through the same
    /// scans the app uses — the plaintext one, or the encrypted one a password prompt
    /// goes through.</summary>
    private BackupSnapshot Meta(string id)
    {
        var next = New(Guid.NewGuid().ToString("N"));
        next.Start();
        var found = next.FindOrphan(id)?.Meta ?? next.FindEncryptedOrphan(id)?.Meta;
        next.Dispose();
        Assert.NotNull(found);
        return found!;
    }

    /// <summary>Metadata exactly as 0.10.0 and every earlier build wrote it: the seven
    /// fields BackupSnapshot had before the view was recorded, in the order it declares
    /// them, which is the order the serialiser writes.</summary>
    private const string MetadataBeforeTheViewWasRecorded =
        "{\"SessionId\":\"old\",\"Path\":\"C:\\\\docs\\\\notes.md\",\"DisplayName\":null," +
        "\"SavedUtc\":\"2026-09-10T12:00:00Z\",\"RecoveryAttempts\":0,\"Encrypted\":false," +
        "\"GiveUpReported\":false}";

    private const string EncryptedMetadataBeforeTheViewWasRecorded =
        "{\"SessionId\":\"olde\",\"Path\":\"C:\\\\docs\\\\notes.mdenc\",\"DisplayName\":null," +
        "\"SavedUtc\":\"2026-09-10T12:00:00Z\",\"RecoveryAttempts\":0,\"Encrypted\":true," +
        "\"GiveUpReported\":false}";

    // ===== what a snapshot records =====

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ASnapshotRecordsTheViewItWasTakenIn(bool sourceView)
    {
        var crashed = New("plain");
        crashed.Start();
        crashed.Save("unsaved work", @"C:\docs\notes.md", null, sourceView);
        crashed.Dispose();

        Assert.Equal(sourceView, Meta("plain").SourceView);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnEncryptedSnapshotRecordsTheViewItWasTakenIn(bool sourceView)
    {
        // The sealed flavour goes through its own scan and its own password prompt, so
        // it needs its own proof: an encrypted document written in the source view is
        // the case where the rewrite is least recoverable, since the file itself can
        // only be read with the password.
        var crashed = New("enc");
        crashed.Start();
        crashed.SaveEncrypted(Sealed("locked work"), @"C:\docs\notes.mdenc", null, sourceView);
        crashed.Dispose();

        Assert.Equal(sourceView, Meta("enc").SourceView);

        var next = New("next");
        next.Start();
        Assert.Equal(sourceView, Assert.Single(next.FindEncryptedOrphans()).Meta.SourceView);
    }

    [Fact]
    public void TheLatestSnapshotsViewIsTheOneRecovered()
    {
        // A view switch on a modified document arms the next tick (the window's
        // UpdateDirtyAsync, pinned in RecoveredViewWiringTests), so the view the user
        // ended up in is the view the next snapshot records. A field written only on
        // the first save, or OR-ed with what was already there, would hand the work
        // back in the view they left.
        var forward = New("forward");
        forward.Start();
        forward.Save("typed in the formatted view", null, "notes.md", sourceView: false);
        forward.Save("then typed in the source view", null, "notes.md", sourceView: true);
        forward.Dispose();
        Assert.True(Meta("forward").SourceView);

        var back = New("back");
        back.Start();
        back.Save("typed in the source view", null, "notes.md", sourceView: true);
        back.Save("then typed in the formatted view", null, "notes.md", sourceView: false);
        back.Dispose();
        Assert.False(Meta("back").SourceView);
    }

    [Fact]
    public void EncryptingADocumentCarriesTheViewOntoTheEncryptedSnapshot()
    {
        // File ▸ Encrypt swaps which of the two writers the tick uses. The view has to
        // survive that swap, in both directions, or encrypting a document loses it.
        var sealing = New("sealing");
        sealing.Start();
        sealing.Save("plaintext era", @"C:\docs\a.md", null, sourceView: false);
        sealing.SaveEncrypted(Sealed("encrypted era"), @"C:\docs\a.mdenc", null, sourceView: true);
        sealing.Dispose();
        Assert.True(Meta("sealing").SourceView);

        var opening = New("opening");
        opening.Start();
        opening.SaveEncrypted(Sealed("encrypted era"), @"C:\docs\b.mdenc", null, sourceView: true);
        opening.Save("plaintext era", @"C:\docs\b.md", null, sourceView: false);
        opening.Dispose();
        Assert.False(Meta("opening").SourceView);
    }

    // ===== metadata written by other builds =====

    [Fact]
    public void ASnapshotFromBeforeTheViewWasRecordedComesBackInTheFormattedView()
    {
        // The compatibility promise: copies waiting on disk from 0.10.0 or earlier
        // recover exactly as they did then. A field that defaulted to true would send
        // every one of them into a view their user was never in.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "old.json"), MetadataBeforeTheViewWasRecorded);
        File.WriteAllText(Path.Combine(_dir, "old.md"), "work from an older build");
        File.WriteAllText(Path.Combine(_dir, "olde.json"), EncryptedMetadataBeforeTheViewWasRecorded);
        File.WriteAllBytes(Path.Combine(_dir, "olde.mdenc"), Sealed("locked work from an older build"));

        foreach (var id in new[] { "old", "olde" })
        {
            var meta = Meta(id);
            Assert.False(meta.SourceView);
            Assert.False(RecoveryPlan.EntersSourceView(meta, sourceViewShowing: false));
        }
    }

    [Fact]
    public void AFieldThisBuildDoesNotKnowNeverHidesTheWork()
    {
        // Forwards compatibility is not a nicety here: a snapshot this build cannot
        // parse is deliberately left alone and never offered back (FindOrphans swallows
        // and skips it), so a field some later version adds would hide the very
        // document it was written to protect.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "newer.json"),
            "{\"SessionId\":\"newer\",\"Path\":null,\"DisplayName\":\"notes.md\"," +
            "\"SavedUtc\":\"2026-09-13T12:00:00Z\",\"RecoveryAttempts\":0,\"Encrypted\":false," +
            "\"GiveUpReported\":false,\"SourceView\":true,\"SplitRatio\":0.4," +
            "\"Panes\":{\"Left\":\"outline\"}}");
        File.WriteAllText(Path.Combine(_dir, "newer.md"), "work from a later build");

        Assert.True(Meta("newer").SourceView);
    }

    [Fact]
    public void CountingAnAttemptOrGivingUpKeepsTheView()
    {
        // Both rewrite the metadata in place, before the work is handed to a window.
        // If either dropped the field, the second launch after a crash would recover
        // into the wrong view — and the third would be the one the user saw.
        var crashed = New("counted");
        crashed.Start();
        crashed.Save("unsaved work", null, "notes.md", sourceView: true);
        crashed.Dispose();

        var live = New("live");
        live.Start();
        var (meta, _) = Assert.Single(live.FindOrphans());
        live.RecordAttempt(meta);
        Assert.True(Meta("counted").SourceView);
        Assert.Equal(1, Meta("counted").RecoveryAttempts);

        live.MarkGiveUpReported(Meta("counted"));
        Assert.True(Meta("counted").SourceView);
        Assert.True(Meta("counted").GiveUpReported);
        Assert.Equal(1, Meta("counted").RecoveryAttempts);
    }

    // ===== taking a snapshot over =====

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void AdoptionRecordsTheViewTheRecoveringWindowShows(bool snapshotView, bool windowView)
    {
        // The recovering window's own view, never the orphan's. Entering the source
        // view can fail — the editor may not answer — and the snapshot this window now
        // owns has to say what it is really showing, or the NEXT crash hands the work
        // back somewhere the user has not been.
        var crashed = New("donor");
        crashed.Start();
        crashed.Save("rescued work", null, "notes.md", snapshotView);
        crashed.Dispose();

        var heir = New("heir");
        heir.Start();
        var (meta, markdown) = Assert.Single(heir.FindOrphans());
        Assert.True(heir.Adopt(meta, markdown, windowView));
        heir.Dispose();

        Assert.Equal(windowView, Meta("heir").SourceView);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void EncryptedAdoptionRecordsTheViewTheRecoveringWindowShows(bool snapshotView, bool windowView)
    {
        var crashed = New("encdonor");
        crashed.Start();
        crashed.SaveEncrypted(Sealed("rescued work"), null, "notes.mdenc", snapshotView);
        crashed.Dispose();

        var heir = New("encheir");
        heir.Start();
        var (meta, container) = Assert.Single(heir.FindEncryptedOrphans());
        Assert.True(heir.AdoptEncrypted(meta, container, windowView));
        heir.Dispose();

        Assert.Equal(windowView, Meta("encheir").SourceView);
    }

    [Fact]
    public void WindowsThatCrashTogetherEachComeBackInTheirOwnView()
    {
        // Several windows die together: one holds the view in the launching window,
        // the rest go to their own windows by id (--recover). The view is per
        // snapshot, so every one of those lookups has to answer for its own session.
        foreach (var (id, view) in new[] { ("one", true), ("two", false) })
        {
            var s = New(id);
            s.Start();
            s.Save("work in " + id, null, id + ".md", view);
            s.Dispose();
        }
        var enc = New("three");
        enc.Start();
        enc.SaveEncrypted(Sealed("locked work"), null, "three.mdenc", sourceView: true);
        enc.Dispose();

        var next = New("next");
        next.Start();
        var scanned = next.FindOrphans().ToDictionary(o => o.Meta.SessionId, o => o.Meta.SourceView);
        Assert.True(scanned["one"]);
        Assert.False(scanned["two"]);
        Assert.True(next.FindOrphan("one")!.Value.Meta.SourceView);
        Assert.False(next.FindOrphan("two")!.Value.Meta.SourceView);
        Assert.True(next.FindEncryptedOrphan("three")!.Value.Meta.SourceView);
    }

    // ===== the blank document recovery opens on the way into the source view =====

    [Fact]
    public void TheBlankDocumentTakenOnTheWayIntoTheSourceViewCannotCostTheSnapshotAnything()
    {
        // A window showing the splash has no document to flip, so recovery gives it a
        // blank one before switching views. That load drops THIS window's session
        // (LoadDocumentAsync calls DiscardBackup) — and a session's own discard must
        // not reach across to the snapshot it is about to recover, whatever happens to
        // the load afterwards.
        var crashed = New("waiting");
        crashed.Start();
        crashed.Save("unsaved work", @"C:\docs\notes.md", null, sourceView: true);
        crashed.Dispose();

        var window = New("window");
        window.Start();
        window.Save("something this window had", null, "its own.md", sourceView: false);
        window.Discard();

        Assert.False(File.Exists(Path.Combine(_dir, "window.json")));
        Assert.False(File.Exists(Path.Combine(_dir, "window.md")));
        var still = Meta("waiting");
        Assert.True(still.SourceView);
        Assert.Equal(0, still.RecoveryAttempts);
        Assert.Equal("unsaved work", File.ReadAllText(Path.Combine(_dir, "waiting.md")));
    }

    [Fact]
    public void AnAdoptionThatCannotWriteLeavesTheOrphanWhereItIs()
    {
        // The other half of the same promise, and the reason copy-before-delete is the
        // rule: if the recovering window cannot write its own copy, the orphan is
        // still on disk for the next launch rather than purged on the strength of a
        // save that did not happen.
        var crashed = New("stranded");
        crashed.Start();
        crashed.Save("unsaved work", null, "notes.md", sourceView: true);
        crashed.Dispose();

        var scanner = New("scanner");
        scanner.Start();
        var (meta, markdown) = Assert.Single(scanner.FindOrphans());

        var heir = New("heir");   // no Start(): the lock was never taken, so nothing may be written
        Assert.False(heir.Adopt(meta, markdown, sourceView: true));

        Assert.True(File.Exists(Path.Combine(_dir, "stranded.md")));
        Assert.True(Meta("stranded").SourceView);
        Assert.Equal("unsaved work", File.ReadAllText(Path.Combine(_dir, "stranded.md")));
    }
}

/// <summary>Which view recovery opens a snapshot in.</summary>
public class RecoveryViewDecisionTests
{
    private static BackupSnapshot Snap(bool sourceView) =>
        new() { SessionId = "s", DisplayName = "notes.md", SourceView = sourceView };

    [Theory]
    [InlineData(true, false, true)]    // written in the source view, window is elsewhere: go there
    [InlineData(true, true, false)]    // already there — nothing to do, and nothing to re-enter
    [InlineData(false, false, false)]  // written in the formatted view: as it has always been
    [InlineData(false, true, false)]   // a --source window keeps the view its user asked for
    public void RecoveryEntersTheSourceViewOnlyForWorkThatWasBeingDoneThere(
        bool snapshotView, bool windowInSourceView, bool enters)
        => Assert.Equal(enters, RecoveryPlan.EntersSourceView(Snap(snapshotView), windowInSourceView));
}

/// <summary>
/// The window's half, read from its source: which view each tick files its snapshot
/// under, and that recovery takes the snapshot's view before it loads the work. None
/// of it can be run — it needs a real window, a WebView2 and a crash — so these pin
/// the path, and the tests above pin what the path leads to.
/// </summary>
public class RecoveredViewWiringTests
{
    private static string Body(string signature) =>
        RepoSources.MethodBody(RepoSources.Read("src", "MarkdownMidget", "MainWindow.xaml.cs"), signature);

    private static int Occurrences(string text, string what)
    {
        var count = 0;
        for (var at = text.IndexOf(what, StringComparison.Ordinal); at >= 0;
             at = text.IndexOf(what, at + 1, StringComparison.Ordinal)) count++;
        return count;
    }

    private static void Precedes(string code, string first, string then, string because)
    {
        var a = code.IndexOf(first, StringComparison.Ordinal);
        var b = code.IndexOf(then, StringComparison.Ordinal);
        Assert.True(a >= 0, $"`{first}` is not in the method");
        Assert.True(b >= 0, $"`{then}` is not in the method");
        Assert.True(a < b, because);
    }

    /// <summary>A method's statements, comments out and whitespace collapsed onto one
    /// line — so a pin can say "these, in this order, and nothing else".</summary>
    private static string Statements(string methodBody)
    {
        var code = RepoSources.WithoutComments(methodBody);
        var open = code.IndexOf('{', StringComparison.Ordinal);
        return string.Join(' ', code[(open + 1)..]
            .Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
    }

    [Fact]
    public void EveryBackupTickRecordsTheViewItReadTheDocumentFrom()
    {
        var body = Body("private async Task WriteBackupAsync(");
        // Read before the awaits, because _sourceMode is what decides WHICH surface the
        // document comes from (TryGetDocumentMarkdownAsync hands back the source box in
        // source view, the editor otherwise). A Ctrl+E during the editor call or the key
        // derivation below would otherwise file one view's text under the other's name.
        RepoSources.AssertRunsUnconditionally(body, "var sourceView = _sourceMode;",
                                              after: "_backupDirty = false;");
        var code = RepoSources.WithoutComments(body);
        Precedes(code, "var sourceView = _sourceMode;", "await TryGetDocumentMarkdownAsync();",
            "the view has to be read before the document, or it describes a different surface");
        Assert.Contains("_backup.Save(markdown, _currentPath, _displayName, sourceView);",
                        code, StringComparison.Ordinal);
        Assert.Contains("_backup.SaveEncrypted(container, _currentPath, _displayName, sourceView);",
                        code, StringComparison.Ordinal);
        // Once, so nothing further down re-reads the field across an await.
        Assert.Equal(1, Occurrences(code, "_sourceMode"));
    }

    [Fact]
    public void APlaintextRecoveryTakesItsViewBeforeTheWorkIsLoaded()
    {
        var body = Body("private async Task LoadRecoveredAsync(");
        RepoSources.AssertRunsUnconditionally(body, "await EnterRecoveredViewAsync(mine);");
        Precedes(RepoSources.WithoutComments(body), "await EnterRecoveredViewAsync(mine);",
            "await LoadDocumentAsync(disk with { Text = markdown }, mine.Path);",
            "the view must be taken BEFORE the load: SourceText.AfterLoad, inside LoadDocumentAsync, is "
            + "what puts the recovered work in the box, and a switch made after the load shows the "
            + "editor's re-serialisation of that work instead — the bug this fixes");
    }

    [Fact]
    public void AnEncryptedRecoveryTakesItsViewBeforeTheWorkIsLoaded()
    {
        // Everything in the password prompt is inside `while (true)`, where "runs
        // unconditionally" has no meaning, so the part that runs once a password has
        // opened the copy is its own method and that is what is pinned. The loop's
        // Cancel and Discard exits therefore cannot reach the view switch at all: it
        // is not in the loop.
        var body = Body("private async Task LoadRecoveredEncryptedAsync(");
        RepoSources.AssertRunsUnconditionally(body, "await EnterRecoveredViewAsync(meta);");
        Precedes(RepoSources.WithoutComments(body), "await EnterRecoveredViewAsync(meta);",
            "await LoadDocumentAsync(disk with { Text = text }, meta.Path, password);",
            "the view must be taken BEFORE the load, as it is for a plaintext recovery");

        var loop = RepoSources.WithoutComments(Body("private async Task RecoverEncryptedAsync("));
        Assert.DoesNotContain("EnterRecoveredViewAsync", loop, StringComparison.Ordinal);
        Precedes(loop, "text = await Task.Run(", "await LoadRecoveredEncryptedAsync(meta, container, pw, text);",
            "nothing may touch the window until the password has actually opened the copy");
    }

    [Fact]
    public void TakingTheRecoveredViewIsTheDecisionThenTheLandingAStartupSourceLaunchUses()
    {
        var body = Body("private async Task EnterRecoveredViewAsync(");
        RepoSources.AssertRunsUnconditionally(body, "await SetSourceModeAsync(true);",
            after: "if (!Backup.RecoveryPlan.EntersSourceView(snapshot, _sourceMode)) return;");
        // The whole of it, so the blank document a splash window needs cannot quietly
        // go away: SetSourceModeAsync refuses to move a window with no document open,
        // and without this line such a window recovers in the formatted view.
        Assert.Equal(
            "if (!Backup.RecoveryPlan.EntersSourceView(snapshot, _sourceMode)) return; "
            + "if (_closed) await StartBlankDocumentAsync(); "
            + "await SetSourceModeAsync(true);",
            Statements(body));
    }

    [Fact]
    public void AdoptionIsToldTheViewTheWindowEndedUpIn()
    {
        var plain = Body("private async Task LoadRecoveredAsync(");
        RepoSources.AssertRunsUnconditionally(plain,
            "if (_backup?.Adopt(mine, markdown, _sourceMode) == false) _backupDirty = true;");
        Precedes(RepoSources.WithoutComments(plain), "await EnterRecoveredViewAsync(mine);",
            "if (_backup?.Adopt(mine, markdown, _sourceMode) == false) _backupDirty = true;",
            "the snapshot this window takes over records the view it ended up in, so the switch "
            + "has to have happened (or failed) first");

        var encrypted = Body("private async Task LoadRecoveredEncryptedAsync(");
        RepoSources.AssertRunsUnconditionally(encrypted,
            "if (_backup?.AdoptEncrypted(meta, container, _sourceMode) == false) _backupDirty = true;");
        Precedes(RepoSources.WithoutComments(encrypted), "await EnterRecoveredViewAsync(meta);",
            "if (_backup?.AdoptEncrypted(meta, container, _sourceMode) == false) _backupDirty = true;",
            "as for a plaintext recovery");
    }

    [Fact]
    public void SwitchingViewsOnAModifiedDocumentArmsTheNextSnapshot()
    {
        // "The latest snapshot's view wins" only means anything if a switch actually
        // produces a later snapshot. Ctrl+E raises no change message of its own; what
        // arms the tick is the dirty check the switch ends with.
        RepoSources.AssertRunsUnconditionally(Body("private async Task SetSourceModeAsync("),
            "_ = UpdateDirtyAsync();", after: "_sourceMode = on;");
        RepoSources.AssertRunsUnconditionally(Body("private async Task UpdateDirtyAsync("),
            "if (dirty) _backupDirty = true;", after: "var dirty = !IsUnmodifiedText(current);");
    }

    [Fact]
    public void TheBlankDocumentTakenOnTheWayIntoTheSourceViewLeavesNothingBehind()
    {
        // Both of these sit inside LoadDocumentAsync's try block, so neither can be
        // pinned as unconditional — that reading only works at a method's own level,
        // and it says so rather than guessing. What the text can show is that a load
        // drops this window's snapshot and disarms the tick; that such a drop cannot
        // reach the snapshot being recovered is the store's promise, pinned in
        // RecoveredViewTests.
        var load = RepoSources.WithoutComments(Body("private async Task LoadDocumentAsync("));
        Assert.Contains("DiscardBackup();", load, StringComparison.Ordinal);
        Assert.Contains("_backupDirty = false;", load, StringComparison.Ordinal);

        var tick = RepoSources.WithoutComments(Body("private async Task WriteBackupAsync("));
        Assert.Contains("if (IsUnmodifiedText(markdown)) { DiscardBackup(); return; }",
                        tick, StringComparison.Ordinal);
    }
}
