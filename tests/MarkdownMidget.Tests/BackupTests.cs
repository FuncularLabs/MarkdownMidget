using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MarkdownMidget.Backup;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>
/// Crash recovery. Every case here is a way unsaved work gets lost: the snapshot
/// wasn't written, was written but not found, was found but discarded too eagerly,
/// or was handed back to a window that was already showing something.
/// </summary>
public class BackupStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "mm-backup-tests-" + Guid.NewGuid().ToString("N"));

    private BackupStore New(string id) => new(_dir, id);

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ---- encrypted snapshots (docs/plans/secure-markdown.md section 7a) ----

    private static byte[] Sealed(string markdown) =>
        MarkdownMidget.Secure.SecureMarkdownFormat.Encrypt(markdown, "pw",
            MarkdownMidget.Secure.SecureMarkdownFormat.KdfProfile.FastForTests);

    [Fact]
    public void SaveEncrypted_ReplacesThePlaintextSnapshotAndLeaksNothing()
    {
        // The crux of section 7a: the moment a document is encrypted, its next
        // snapshot is encrypted and the prior plaintext snapshot is gone - the
        // 5-second backup tick must not be a plaintext leak of exactly the
        // content the user chose to protect.
        const string sentinel = "SENSITIVE-ACCOUNT-4417";
        var store = New("enc");
        Assert.True(store.Start());
        store.Save($"before encryption {sentinel}", @"C:\docs.mdenc", null);
        Assert.True(store.SaveEncrypted(Sealed($"after encryption {sentinel}"), @"C:\docs.mdenc", null));

        Assert.False(File.Exists(Path.Combine(_dir, "enc.md")));
        Assert.True(File.Exists(Path.Combine(_dir, "enc.mdenc")));
        foreach (var f in Directory.GetFiles(_dir))
        {
            if (f.EndsWith(".lock")) continue;   // held exclusively and zero-byte by design
            Assert.DoesNotContain(sentinel,
                System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(f)));
        }
    }

    [Fact]
    public void APlaintextSaveAfterAnEncryptedOne_RemovesTheEncryptedSnapshot()
    {
        // Convert-to-unencrypted symmetry: whichever kind the document is now,
        // exactly one snapshot represents it.
        var store = New("conv");
        store.Start();
        store.SaveEncrypted(Sealed("secret era"), @"C:\docs.mdenc", null);
        store.Save("public era", @"C:\docs.md", null);

        Assert.False(File.Exists(Path.Combine(_dir, "conv.mdenc")));
        Assert.True(File.Exists(Path.Combine(_dir, "conv.md")));
    }

    [Fact]
    public void AnEncryptedOrphan_IsHeldBackFromRecovery_ButNeverPurged()
    {
        // The recovery UI stage (password prompt) doesn't exist yet, so
        // FindOrphans holds encrypted snapshots back - but purging one would
        // destroy a crashed encrypted document's only remaining copy. The
        // "metadata without content" purge must therefore look at the .mdenc,
        // not the .md it knows isn't there.
        var crashed = New("crashedenc");
        crashed.Start();
        crashed.SaveEncrypted(Sealed("locked-away work"), @"C:\docs.mdenc", null);
        crashed.Dispose();

        var next = New("next");
        next.Start();
        Assert.Empty(next.FindOrphans());   // held back...
        Assert.True(File.Exists(Path.Combine(_dir, "crashedenc.mdenc")));   // ...not destroyed
        Assert.True(File.Exists(Path.Combine(_dir, "crashedenc.json")));
        // And a second scan (every later launch) still doesn't eat it.
        Assert.Empty(next.FindOrphans());
        Assert.True(File.Exists(Path.Combine(_dir, "crashedenc.mdenc")));
    }

    [Fact]
    public void Discard_RemovesTheEncryptedSnapshotToo()
    {
        var store = New("disc");
        store.Start();
        store.SaveEncrypted(Sealed("about to be saved for real"), @"C:\docs.mdenc", null);
        store.Discard();
        Assert.False(File.Exists(Path.Combine(_dir, "disc.mdenc")));
        Assert.False(File.Exists(Path.Combine(_dir, "disc.json")));
    }

    [Fact]
    public void AnEncryptedSnapshotRoundTripsThroughItsPassword()
    {
        // What the future recovery stage will actually do: read the orphan's
        // .mdenc and open it with the password the user supplies.
        var store = New("rt");
        store.Start();
        store.SaveEncrypted(Sealed("recover me"), null, "untitled");
        var bytes = File.ReadAllBytes(Path.Combine(_dir, "rt.mdenc"));
        Assert.Equal("recover me", MarkdownMidget.Secure.SecureMarkdownFormat.Decrypt(bytes, "pw"));
    }

    [Fact]
    public void ASessionStillRunning_IsNotTreatedAsAbandoned()
    {
        // The whole design rests on this: a live window's snapshot must be invisible
        // to everyone else, or two instances fight over the same document.
        var live = New("live");
        Assert.True(live.Start());
        live.Save("work in progress", @"C:\docs\a.md", null);

        var other = New("other");
        other.Start();
        Assert.Empty(other.FindOrphans());

        live.Dispose();                     // simulate the process going away
        Assert.Single(other.FindOrphans());
    }

    [Fact]
    public void AfterAClosedSession_TheSnapshotIsRecoverable()
    {
        var crashed = New("crashed");
        crashed.Start();
        crashed.Save("# unsaved heading", @"C:\docs\notes.md", null);
        crashed.Dispose();                  // died without discarding

        var next = New("next");
        next.Start();
        var orphans = next.FindOrphans();
        var (meta, markdown) = Assert.Single(orphans);
        Assert.Equal("# unsaved heading", markdown);
        Assert.Equal(@"C:\docs\notes.md", meta.Path);
        Assert.Equal("notes.md", meta.Describe());
    }

    [Fact]
    public void AGracefulExit_LeavesNothingToRecover()
    {
        var clean = New("clean");
        clean.Start();
        clean.Save("typed something", null, null);
        clean.Discard();                    // saved or deliberately abandoned
        clean.Dispose();

        var next = New("next");
        next.Start();
        Assert.Empty(next.FindOrphans());
    }

    [Fact]
    public void AnUntitledDocument_KeepsItsName()
    {
        var s = New("dropped");
        s.Start();
        s.Save("pasted text", null, "clipboard.md");
        s.Dispose();

        var next = New("next");
        next.Start();
        var (meta, _) = Assert.Single(next.FindOrphans());
        Assert.Null(meta.Path);
        Assert.Equal("clipboard.md", meta.Describe());
    }

    [Fact]
    public void AdoptingAnOrphan_MovesItRatherThanCopyingIt()
    {
        var dead = New("dead");
        dead.Start();
        dead.Save("rescued", @"C:\docs\x.md", null);
        dead.Dispose();

        var live = New("live");
        live.Start();
        var (meta, markdown) = Assert.Single(live.FindOrphans());
        Assert.True(live.Adopt(meta, markdown));

        // The orphan is gone, so a second window won't restore it again...
        Assert.Empty(live.FindOrphans());
        // ...but the content is now ours, and survives if WE crash.
        live.Dispose();
        var third = New("third");
        third.Start();
        var (_, again) = Assert.Single(third.FindOrphans());
        Assert.Equal("rescued", again);
    }

    [Fact]
    public void MetadataWithoutContent_IsCleanedUpNotReturned()
    {
        var s = New("partial");
        s.Start();
        s.Save("something", null, null);
        s.Dispose();
        File.Delete(Path.Combine(_dir, "partial.md"));   // content lost, metadata left

        var next = New("next");
        next.Start();
        Assert.Empty(next.FindOrphans());
        Assert.False(File.Exists(Path.Combine(_dir, "partial.json")));
    }

    [Fact]
    public void AnUnreadableSnapshot_IsLeftAloneRatherThanDeleted()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "junk.json"), "{ not json");
        File.WriteAllText(Path.Combine(_dir, "junk.md"), "possibly precious");

        var next = New("next");
        next.Start();
        Assert.Empty(next.FindOrphans());
        // We couldn't understand it, so we don't get to throw it away.
        Assert.True(File.Exists(Path.Combine(_dir, "junk.md")));
    }

    [Fact]
    public void RecoveryIsSerialized_SoTwoWindowsCantRestoreTheSameWork()
    {
        var a = New("a"); a.Start();
        var b = New("b"); b.Start();
        using var claim = a.BeginRecovery();
        Assert.NotNull(claim);
        Assert.Null(b.BeginRecovery());     // a is already doing it
    }

    [Fact]
    public void RecoveryClaim_IsReleasedWhenDone()
    {
        var a = New("a"); a.Start();
        a.BeginRecovery()!.Dispose();
        var b = New("b"); b.Start();
        Assert.NotNull(b.BeginRecovery());
    }

    [Fact]
    public void AttemptsAreCounted_SoACrashingDocumentStopsBeingRetried()
    {
        var dead = New("dead");
        dead.Start();
        dead.Save("the document that kills us", null, null);
        dead.Dispose();

        var live = New("live");
        live.Start();
        for (var i = 0; i < 3; i++)
        {
            var (meta, _) = Assert.Single(live.FindOrphans());
            live.RecordAttempt(meta);
        }
        var (final, _) = Assert.Single(live.FindOrphans());
        Assert.Equal(3, final.RecoveryAttempts);
    }

    [Fact]
    public void AttemptsSurviveAdoption_SoACrashLoopActuallyTerminates()
    {
        // The counter only protects anyone if it survives the thing it's counting.
        // Adopting writes fresh metadata, so without care the count resets to zero on
        // every recovery — and the document that crashes the app gets restored, and
        // crashes it, forever.
        var id = "poison";
        var first = New(id);
        first.Start();
        first.Save("the document that kills us", null, "poison.md");
        first.Dispose();

        var attempts = 0;
        for (var launch = 1; launch <= 5; launch++)
        {
            var window = New("launch" + launch);
            window.Start();
            var orphans = window.FindOrphans();
            var (meta, markdown) = Assert.Single(orphans);
            if (meta.RecoveryAttempts >= RecoveryPlan.MaxAttempts) break;   // gave up, as intended

            window.RecordAttempt(meta);     // what the app does before handing it over
                                            // (it increments the snapshot in place)
            window.Discard();               // LoadDocumentAsync drops this window's own
                                            // copy first, which also resets its count...
            window.Adopt(meta, markdown);   // ...and adoption must put it back
            attempts++;
            window.Dispose();               // crashed again
        }

        Assert.Equal(RecoveryPlan.MaxAttempts, attempts);
    }

    [Fact]
    public void AnOrdinarySave_DoesNotResetTheAttemptCount()
    {
        // The 5-second timer calls Save constantly. If Save forgot the count, the
        // first tick after a recovery would undo the guard just as thoroughly.
        var dead = New("dead");
        dead.Start();
        dead.Save("content", null, "x.md");
        dead.Dispose();

        var live = New("live");
        live.Start();
        var (meta, markdown) = Assert.Single(live.FindOrphans());
        meta.RecoveryAttempts = 2;
        live.Adopt(meta, markdown);
        live.Save("content, edited some more", null, "x.md");   // a timer tick
        live.Dispose();

        var next = New("next");
        next.Start();
        var (after, _) = Assert.Single(next.FindOrphans());
        Assert.Equal(2, after.RecoveryAttempts);
    }

    [Fact]
    public void AfterTheSnapshotGoes_NewWorkStartsWithACleanRecord()
    {
        // The attempt count belongs to the snapshot, not the window. A window that
        // recovered a troublesome document must not stamp that history onto whatever
        // the user writes next -- new work would then be refused on its first crash.
        var dead = New("dead");
        dead.Start();
        dead.Save("troublesome", null, "bad.md");
        dead.Dispose();

        var live = New("live");
        live.Start();
        var (meta, markdown) = Assert.Single(live.FindOrphans());
        meta.RecoveryAttempts = RecoveryPlan.MaxAttempts - 1;
        live.Adopt(meta, markdown);

        live.Discard();                       // the user saves, so the snapshot goes
        live.Save("a completely new document", null, "new.md");   // then types afresh
        live.Dispose();

        var next = New("next");
        next.Start();
        var (fresh, _) = Assert.Single(next.FindOrphans());
        Assert.Equal(0, fresh.RecoveryAttempts);
        // ...and so it is actually offered back, rather than written off.
        var plan = RecoveryPlan.Decide([fresh], windowIsFree: true);
        Assert.NotNull(plan.Here);
        Assert.Empty(plan.GivenUp);
    }

    [Fact]
    public void AReadOnlyLockLeftByAPowerCut_DoesNotHideTheWork()
    {
        // A power cut leaves the lock file behind (DeleteOnClose never runs). If a
        // backup or AV tool then marks it read-only, probing it for WRITE access
        // fails and the session looks alive -- hiding the user's work permanently.
        var dead = New("dead");
        dead.Start();
        dead.Save("precious", null, "x.md");
        dead.Dispose();

        var lockFile = Path.Combine(_dir, "dead.lock");
        File.WriteAllText(lockFile, "");
        File.SetAttributes(lockFile, FileAttributes.ReadOnly);
        try
        {
            var next = New("next");
            next.Start();
            Assert.Single(next.FindOrphans());
        }
        finally { File.SetAttributes(lockFile, FileAttributes.Normal); }
    }

    [Fact]
    public void LockFilesWithNothingToProtect_AreSweptUp()
    {
        // A window that never went dirty has no snapshot, so a power cut leaves a
        // lock nothing would ever clean up.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "ghost.lock"), "");

        var next = New("next");
        next.Start();
        next.FindOrphans();
        Assert.False(File.Exists(Path.Combine(_dir, "ghost.lock")));
    }

    [Fact]
    public void TheSweep_LeavesLocksThatStillGuardASnapshot()
    {
        var dead = New("dead");
        dead.Start();
        dead.Save("still wanted", null, "x.md");
        dead.Dispose();
        File.WriteAllText(Path.Combine(_dir, "dead.lock"), "");   // as a power cut would leave it

        var next = New("next");
        next.Start();
        Assert.Single(next.FindOrphans());       // runs the sweep
        Assert.True(File.Exists(Path.Combine(_dir, "dead.md")));
    }

    [Fact]
    public void GiveUpReported_SurvivesToTheNextLaunch()
    {
        var dead = New("dead");
        dead.Start();
        dead.Save("stubborn", null, "x.md");
        dead.Dispose();

        var live = New("live");
        live.Start();
        var (meta, _) = Assert.Single(live.FindOrphans());
        live.MarkGiveUpReported(meta);
        live.Dispose();

        var next = New("next");
        next.Start();
        var (again, _) = Assert.Single(next.FindOrphans());
        Assert.True(again.GiveUpReported);
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef", true)]
    [InlineData("0123456789ABCDEF0123456789ABCDEF", true)]
    [InlineData("live", false)]                               // a name, not an id
    [InlineData("0123456789abcdef0123456789abcde", false)]    // one short
    [InlineData("x --readonly x --readonly xxxxxx", false)]   // would become argv
    [InlineData("", false)]
    public void OnlyOurOwnIdShape_IsAcceptedForHandingToAChildProcess(string id, bool ok)
        => Assert.Equal(ok, BackupStore.IsSessionId(id));

    [Fact]
    public void TheIdsWeActuallyMint_PassTheirOwnGate()
    {
        // The load-bearing half: if the shape the app writes ever stopped satisfying
        // the gate, recovery would silently stop opening windows for anything.
        for (var i = 0; i < 100; i++)
            Assert.True(BackupStore.IsSessionId(System.Guid.NewGuid().ToString("N")));
    }

    [Fact]
    public void TargetedLookup_FindsOnlyTheRequestedSession()
    {
        foreach (var id in new[] { "one", "two" })
        {
            var s = New(id);
            s.Start();
            s.Save("content of " + id, null, id + ".md");
            s.Dispose();
        }
        var live = New("live");
        live.Start();
        Assert.Equal("content of two", live.FindOrphan("two")!.Value.Markdown);
        Assert.Null(live.FindOrphan("nope"));
    }

    [Fact]
    public void SavingRepeatedly_LeavesNoTempFilesBehind()
    {
        var s = New("busy");
        s.Start();
        for (var i = 0; i < 20; i++) s.Save($"revision {i}", null, null);
        Assert.Empty(Directory.EnumerateFiles(_dir, "*.tmp"));

        s.Dispose();
        var next = New("next");
        next.Start();
        var (_, markdown) = Assert.Single(next.FindOrphans());
        Assert.Equal("revision 19", markdown);      // last write wins, intact
    }

    [Fact]
    public void WithoutStart_NothingIsWritten()
    {
        // Failing to take the lock must mean "don't back up", not "back up anyway"
        // — an unlocked snapshot would look abandoned to every other window.
        var s = New("nolock");
        Assert.False(s.Save("content", null, null));
        // Assert on the files unconditionally - guarding this with Directory.Exists
        // would short-circuit away the only assertion that matters.
        Directory.CreateDirectory(_dir);
        Assert.Empty(Directory.EnumerateFiles(_dir, "nolock.*"));
    }
}

/// <summary>Which recovered documents go where.</summary>
public class RecoveryPlanTests
{
    private static BackupSnapshot Snap(string id, int attempts = 0) =>
        new() { SessionId = id, DisplayName = id + ".md", RecoveryAttempts = attempts };

    [Fact]
    public void AFreeWindow_TakesTheFirstAndSpawnsTheRest()
    {
        var plan = RecoveryPlan.Decide([Snap("a"), Snap("b"), Snap("c")], windowIsFree: true);
        Assert.Equal("a", plan.Here!.SessionId);
        Assert.Equal(["b", "c"], plan.Elsewhere.Select(s => s.SessionId));
        Assert.Equal(3, plan.RestoreCount);
    }

    [Fact]
    public void AnOccupiedWindow_KeepsWhatItIsShowing()
    {
        // The user opened a file; recovery must not replace it.
        var plan = RecoveryPlan.Decide([Snap("a")], windowIsFree: false);
        Assert.Null(plan.Here);
        Assert.Equal(["a"], plan.Elsewhere.Select(s => s.SessionId));
    }

    [Fact]
    public void RepeatedlyFailedSnapshots_AreSetAsideNotRetriedForever()
    {
        var plan = RecoveryPlan.Decide(
            [Snap("poison", RecoveryPlan.MaxAttempts), Snap("fine")], windowIsFree: true);
        Assert.Equal("fine", plan.Here!.SessionId);
        Assert.Equal(["poison"], plan.GivenUp.Select(s => s.SessionId));
        Assert.Equal(1, plan.RestoreCount);
    }

    [Fact]
    public void NothingToRecover_IsNotAnEvent()
    {
        var plan = RecoveryPlan.Decide([], windowIsFree: true);
        Assert.Null(plan.Here);
        Assert.Empty(plan.Elsewhere);
        Assert.Equal(0, plan.RestoreCount);
    }

    [Fact]
    public void AllGivenUp_RestoresNothingButStillReportsThem()
    {
        var plan = RecoveryPlan.Decide([Snap("x", 5)], windowIsFree: true);
        Assert.Equal(0, plan.RestoreCount);
        Assert.Single(plan.GivenUp);
    }

    [Fact]
    public void OnceReported_AGivenUpSnapshotIsNotAnnouncedAgain()
    {
        // The file stays on disk - it's the user's work - but a modal warning on
        // every launch for the rest of the install's life is nagging, not helping.
        var reported = Snap("x", 5);
        reported.GiveUpReported = true;
        var plan = RecoveryPlan.Decide([reported], windowIsFree: true);
        Assert.Empty(plan.GivenUp);
        Assert.Equal(0, plan.RestoreCount);   // still not restored, just not re-announced
    }
}
