using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MarkdownMidget.Updates;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>
/// The portable half of the multi-window update problem. Another window updates
/// first, so the versioned exe is already sitting next to ours — and is very likely
/// the running image of the instance that window started. Copying onto it throws
/// "The process cannot access the file … because it is being used by another
/// process", which is a confusing thing to say about a file that is already
/// byte-for-byte what we were about to write.
/// </summary>
public class SameFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "mm-samefile-" + Guid.NewGuid().ToString("N"));

    public SameFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string Write(string name, string content)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllText(p, content);
        return p;
    }

    [Fact]
    public void IdenticalContent_IsRecognised_SoTheCopyIsSkipped()
    {
        var a = Write("a.exe", "the same bytes");
        var b = Write("b.exe", "the same bytes");
        Assert.True(UpdateService.SameFile(a, b));
    }

    [Fact]
    public void DifferentContent_IsNotSkipped()
        => Assert.False(UpdateService.SameFile(Write("a.exe", "v1"), Write("b.exe", "v2")));

    [Fact]
    public void SameLengthDifferentBytes_IsNotSkipped()
    {
        // Length alone is the cheap pre-check; it must not be the whole answer, or
        // two builds of the same size would be mistaken for each other.
        var a = Write("a.exe", "AAAA");
        var b = Write("b.exe", "BBBB");
        Assert.Equal(new FileInfo(a).Length, new FileInfo(b).Length);
        Assert.False(UpdateService.SameFile(a, b));
    }

    [Fact]
    public void MissingDestination_IsNotSkipped()
        => Assert.False(UpdateService.SameFile(Write("a.exe", "x"), Path.Combine(_dir, "nope.exe")));

    [Fact]
    public void MissingSource_IsNotSkipped_AndDoesNotThrow()
        => Assert.False(UpdateService.SameFile(Path.Combine(_dir, "nope.exe"), Write("b.exe", "x")));

    [Fact]
    public void DestinationHeldOpenTheWayAWindowsExecutableIs_StillCompares()
    {
        // The case that matters: Windows keeps a running image open with read
        // sharing, so we can still hash it and discover there is nothing to do.
        var a = Write("a.exe", "identical payload");
        var b = Write("b.exe", "identical payload");
        using var running = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.Read);
        Assert.True(UpdateService.SameFile(a, b));
    }

    [Fact]
    public void DestinationLockedOutright_ReportsNotSame_RatherThanThrowing()
    {
        // Nothing can be read and nothing can be written, so the honest answer is
        // "not the same" — the copy is attempted and its real error is reported.
        var a = Write("a.exe", "payload");
        var b = Write("b.exe", "payload");
        using var exclusive = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.False(UpdateService.SameFile(a, b));
    }

    [Fact]
    public void EmptyFiles_AreEqual()
        => Assert.True(UpdateService.SameFile(Write("a.exe", ""), Write("b.exe", "")));

    // ---- the in-place swap ----
    //
    // Two renames with a rollback. It is the only code on the update path that can
    // leave a directory with no usable exe, so every branch is pinned here rather
    // than reasoned about.

    [Fact]
    public void Swap_ReplacesTheTargetAndParksTheOldOne()
    {
        var target = Write("app.exe", "old version");
        var staged = Write("staged.exe", "new version");
        var old = target + ".old";

        UpdateService.SwapInPlace(target, staged, old);

        Assert.Equal("new version", File.ReadAllText(target));
        Assert.Equal("old version", File.ReadAllText(old));
        Assert.False(File.Exists(staged));           // moved, and cleaned up
    }

    [Fact]
    public void Swap_OverwritesAnOldFileLeftByAPreviousUpdate()
    {
        var target = Write("app.exe", "v2");
        var staged = Write("staged.exe", "v3");
        var old = Write("app.exe.old", "v1 from last time");

        UpdateService.SwapInPlace(target, staged, old);

        Assert.Equal("v3", File.ReadAllText(target));
        Assert.Equal("v2", File.ReadAllText(old));
    }

    [Fact]
    public void Swap_RollsBackWhenTheSecondMoveFails_LeavingTheInstallIntact()
    {
        var target = Write("app.exe", "the working version");
        var staged = Write("staged.exe", "the new version");
        var old = target + ".old";

        // Hold the staged file exclusively so its move into place fails — the window
        // where `target` doesn't exist yet. This is the CHANGELOG's "usual reason": a
        // scanner with the freshly written 6.5 MB copy open.
        InvalidOperationException ex;
        using (var _ = new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.None))
            ex = Assert.Throws<InvalidOperationException>(
                () => UpdateService.SwapInPlace(target, staged, old));

        // The install must be exactly as it was, and the failure must be visible.
        Assert.True(File.Exists(target));
        Assert.Equal("the working version", File.ReadAllText(target));
        // And reported in words. The raw text here is "The process cannot access the
        // file because it is being used by another process" — which names no file at
        // all, so it can't be acted on. The portable flow already replaces it.
        Assert.DoesNotContain("used by another process", ex.Message);
        Assert.Contains("Nothing has changed", ex.Message);
        Assert.IsAssignableFrom<IOException>(ex.InnerException);   // still diagnosable
    }

    [Fact]
    public void Swap_KeepsTheStagedCopyWhenThereIsNoTargetToFallBackOn()
    {
        // Pins the `&& File.Exists(target)` guard on the cleanup: whenever nothing
        // sits at `target` — which is what a failed rollback would leave — the
        // verified staged copy is the last recoverable binary and must survive.
        //
        // It reaches that state via the FIRST move failing on a missing source, not
        // via a failed rollback, which can't be forced from outside. The guard is
        // what's under test, not the route to it.
        var dir = Path.Combine(_dir, "gone");
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, "app.exe");        // deliberately absent
        var staged = Path.Combine(dir, "staged.exe");
        File.WriteAllText(staged, "the verified new version");
        var old = target + ".old";

        var clock = Stopwatch.StartNew();
        var ex = Assert.Throws<InvalidOperationException>(
            () => UpdateService.SwapInPlace(target, staged, old));
        clock.Stop();

        // The property behind the park's larger budget: a sibling that reaches its own
        // recovery holds the gap for about 600ms, so giving up sooner than that would
        // announce a missing program while the window next door is putting one back.
        Assert.True(clock.ElapsedMilliseconds > 700,
                    $"gave up after only {clock.ElapsedMilliseconds}ms");
        Assert.True(File.Exists(staged), "the staged copy is the only binary left — keep it");
        Assert.Equal("the verified new version", File.ReadAllText(staged));
        // And it must not blame a race for a missing program: closing windows can't
        // produce an exe that isn't there.
        Assert.DoesNotContain("Close the other windows", ex.Message);
        Assert.Contains("no Markdown Midget program file", ex.Message);
        // A sibling's own recovery can hold the gap for ~600ms, so this must not give
        // up first and announce a missing program while the window next door is putting
        // one back — and where it does give up, it says that is a possibility.
        Assert.Contains("wait a moment and try again", ex.Message);
    }

    // ---- the instant when nothing sits at `target` ----
    //
    // Between the two renames the install directory has no exe at all. If the second
    // one fails there, the recovery is the only thing standing between the user and an
    // app that cannot be launched — so it is exercised directly rather than through the
    // timing-dependent interleavings that reach it.

    [Fact]
    public void Recovery_PutsTheOldVersionBack()
    {
        var target = Path.Combine(_dir, "app.exe");            // the empty instant
        var parked = Write("app.exe.old", "the working version");
        var staged = Write("staged.exe", "the new version");

        Assert.Equal(UpdateService.Recovery.OldVersionRestored,
                     UpdateService.PutSomethingBack(target, parked, staged));
        Assert.Equal("the working version", File.ReadAllText(target));
    }

    [Fact]
    public void Recovery_UsesTheStagedCopyWhenTheOldOneWontGoBack()
    {
        // Both moves have now failed. Which binary ends up there matters far less than
        // there being one: the staged copy is verified and newer, so it is a perfectly
        // good resident — and the update has in fact landed, so this reports success.
        var target = Path.Combine(_dir, "app.exe");
        var parked = Write("app.exe.old", "the working version");
        var staged = Write("staged.exe", "the new version");

        using var stuck = new FileStream(parked, FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.Equal(UpdateService.Recovery.StagedInstalled,
                     UpdateService.PutSomethingBack(target, parked, staged));
        Assert.Equal("the new version", File.ReadAllText(target));
    }

    [Fact]
    public void Recovery_ThatCannotPlaceAnythingSaysWhichFileToRename()
    {
        // The worst state the app can reach: no exe where one is expected. What must
        // NOT be reported is a sentence about file handles — the user needs to know
        // the install is unlaunchable and exactly how to fix it in Explorer.
        var target = Path.Combine(_dir, "app.exe");
        var parked = Write("app.exe.old", "the working version");
        var staged = Write("staged.exe", "the new version");

        using var stuckOld = new FileStream(parked, FileMode.Open, FileAccess.Read, FileShare.None);
        using var stuckNew = new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.None);

        var ex = Assert.Throws<InvalidOperationException>(
            () => UpdateService.PutSomethingBack(target, parked, staged));

        Assert.Contains("app.exe.old", ex.Message);
        Assert.Contains("staged.exe", ex.Message);
        Assert.Contains("rename", ex.Message);
        // Nothing was lost, which is what makes the instructions followable.
        Assert.True(File.Exists(parked) && File.Exists(staged));
    }

    [Fact]
    public void Recovery_AcceptsACopyThatIsExactlyWhatWasVerified()
    {
        // The second move can fail *because* something refilled the target in the same
        // instant. When what's there is byte-for-byte the file whose signature we
        // checked, the update is done however it arrived, and reporting a failure for
        // it would be wrong.
        var target = Write("app.exe", "the new version");
        var parked = Write("app.exe.old", "the working version");
        var staged = Write("staged.exe", "the new version");

        var clock = Stopwatch.StartNew();
        Assert.Equal(UpdateService.Recovery.VerifiedCopyAlreadyThere,
                     UpdateService.PutSomethingBack(target, parked, staged));
        clock.Stop();

        Assert.Equal("the new version", File.ReadAllText(target));
        Assert.Equal("the working version", File.ReadAllText(parked));
        // Recognised up front rather than after both retry loops have spent 600ms
        // trying renames that cannot succeed against a sibling's fresh install. The
        // final look-again would return the same answer, so this is the one thing that
        // distinguishes the two — hence a clock, with a wide margin.
        Assert.True(clock.ElapsedMilliseconds < 400, $"took {clock.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void Recovery_RefusesToCallAStrangerTheUpdate()
    {
        // The interleaving the "another window beat us to it" reading gets wrong. A
        // sibling *update* cannot land here at all — the park is a mutual exclusion,
        // and for the whole of this gap there is no target to park. What can appear is
        // an unrelated write to the same path (RegistrationService copies a portable
        // exe there without taking the park). Calling that the update would restart the
        // user into an unverified, quite possibly OLDER binary, repoint every shortcut
        // at it, and let the next sweep delete the copy they were actually running.
        var target = Write("app.exe", "some other build entirely");
        var parked = Write("app.exe.old", "the version they were running");
        var staged = Write("staged.exe", "the verified new version");

        var ex = Assert.Throws<InvalidOperationException>(
            () => UpdateService.PutSomethingBack(target, parked, staged));

        Assert.Contains("not the version that was just downloaded and verified", ex.Message);
        // Nothing touched: no unverified binary promoted, no working one destroyed.
        Assert.Equal("some other build entirely", File.ReadAllText(target));
        Assert.Equal("the version they were running", File.ReadAllText(parked));
        Assert.Equal("the verified new version", File.ReadAllText(staged));
    }

    [Fact]
    public void Recovery_DoesNotClaimAMismatchItCouldNotCheck()
    {
        // The comparison has two sides, and the lock that stopped the rename is an
        // excellent reason our OWN copy is the unreadable one. "That is not the version
        // you downloaded" would then be an assertion this window has no way to make —
        // about a file that may well be byte-for-byte correct.
        var target = Write("app.exe", "quite possibly exactly what we verified");
        var parked = Write("app.exe.old", "the version they were running");
        var staged = Write("staged.exe", "quite possibly exactly what we verified");
        using var scanner = new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.None);

        var ex = Assert.Throws<InvalidOperationException>(
            () => UpdateService.PutSomethingBack(target, parked, staged));

        Assert.Contains("couldn't read the two files to compare them", ex.Message);
        Assert.DoesNotContain("not the version that was just downloaded", ex.Message);
    }

    [Fact]
    public void Recovery_WontCallAHalfWrittenCopyAMismatchEither()
    {
        // The same path mid-File.Copy: unreadable, on the OTHER side of the comparison.
        // "Couldn't read" has to reach the same verdict whichever file it was, and the
        // bytes here are deliberately identical — so an implementation that only checks
        // our own copy would announce a mismatch between two files that match.
        var target = Write("app.exe", "the verified new version");
        var parked = Write("app.exe.old", "the version they were running");
        var staged = Write("staged.exe", "the verified new version");
        using var beingWritten = new FileStream(target, FileMode.Open, FileAccess.Write, FileShare.None);

        var ex = Assert.Throws<InvalidOperationException>(
            () => UpdateService.PutSomethingBack(target, parked, staged));

        Assert.Contains("couldn't read the two files", ex.Message);
        Assert.DoesNotContain("not the version that was just downloaded", ex.Message);
        Assert.Equal("the verified new version", File.ReadAllText(staged));
    }

    [Fact]
    public void Recovery_DoesNotRaceItselfIntoAFalseAccusation()
    {
        // Asking the same question twice is not the same as asking it once. With two
        // comparisons, a holder releasing its handle in between turns "couldn't read"
        // into "same", the second answer no longer matches the branch that let it
        // through, and control falls to the mismatch throw — about two files that are
        // byte-for-byte identical.
        //
        // The verdict is taken once now, so with identical bytes the only outcomes are
        // "same" and "couldn't read". This can only fail if that stops being true: the
        // flicker makes it likely to catch a regression, and nothing makes it likely to
        // fail spuriously.
        var target = Write("app.exe", "the verified new version");
        var parked = Write("app.exe.old", "the version they were running");
        var staged = Write("staged.exe", "the verified new version");

        using var stop = new CancellationTokenSource();
        var flicker = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    using var _ = new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.None);
                }
                catch { /* the comparison has it — try again */ }
            }
        });

        try
        {
            for (var i = 0; i < 300; i++)
            {
                try
                {
                    UpdateService.PutSomethingBack(target, parked, staged);
                }
                catch (InvalidOperationException ex)
                {
                    Assert.DoesNotContain("not the version that was just downloaded", ex.Message);
                }
            }
        }
        finally { stop.Cancel(); flicker.Wait(); }
    }

    [Theory]
    [InlineData("same", "same", "Same")]
    [InlineData("one", "another", "Different")]
    [InlineData("same", null, "CouldNotRead")]      // null = present but locked
    public void ComparingFilesDistinguishesDifferentFromUnreadable(string a, string? b, string expected)
    {
        var pa = Write("a.exe", a);
        var pb = Write("b.exe", b ?? "same");
        using var lockIt = b is null
            ? new FileStream(pb, FileMode.Open, FileAccess.Read, FileShare.None)
            : null;

        Assert.Equal(Enum.Parse<UpdateService.FileMatch>(expected), UpdateService.Compare(pa, pb));
    }

    [Fact]
    public void Recovery_LooksAgainBeforeCallingItACatastrophe()
    {
        // Both loops together take about 600ms, and a sibling landing its install
        // inside that turns the worst outcome into the best one. Announcing "there is
        // no program — rename one of these" then would talk someone into overwriting a
        // working new version with the old one.
        var target = Path.Combine(_dir, "app.exe");
        var parked = Write("app.exe.old", "the working version");
        var staged = Write("staged.exe", "the new version");

        // Only the old copy is held, so the rollback can't work. The staged move then
        // fails the way it does in production — the name is taken — rather than by
        // being locked, which would also stop us reading the bytes to compare.
        using var stuckOld = new FileStream(parked, FileMode.Open, FileAccess.Read, FileShare.None);
        using var sibling = new Timer(_ => File.WriteAllText(target, "the new version"),
                                      null, TimeSpan.FromMilliseconds(100), Timeout.InfiniteTimeSpan);

        Assert.Equal(UpdateService.Recovery.VerifiedCopyAlreadyThere,
                     UpdateService.PutSomethingBack(target, parked, staged));
    }

    [Fact]
    public void MoveCanFailWithSomethingThatIsNotAnIOException()
    {
        // Load-bearing, and the reason the recovery's catches aren't `catch (IOException)`:
        // File.Move surfaces a denied rename as UnauthorizedAccessException, which is
        // not an IOException at all. Demonstrated here with a read-only destination,
        // standing in for the security software that causes it in the field — the point
        // is only that File.Move really does throw outside the IOException hierarchy.
        var src = Write("a.exe", "x");
        var dest = Write("b.exe", "y");
        File.SetAttributes(dest, FileAttributes.ReadOnly);
        try
        {
            var ex = Assert.ThrowsAny<Exception>(() => File.Move(src, dest, overwrite: true));
            Assert.IsNotAssignableFrom<IOException>(ex);
            Assert.True(UpdateService.IsRecoverableMoveFailure(ex),
                        "the recovery must catch it, or it skips the retries, the fallback " +
                        "and the instructions, and leaves the folder with no exe");
        }
        finally { File.SetAttributes(dest, FileAttributes.Normal); }
    }

    [Theory]
    [InlineData("io", true)]
    [InlineData("denied", true)]
    [InlineData("other", false)]
    public void OnlyMoveFailuresAreRecovered(string kind, bool recovered)
        => Assert.Equal(recovered, UpdateService.IsRecoverableMoveFailure(kind switch
        {
            "io" => new IOException("x"),
            "denied" => new UnauthorizedAccessException("x"),
            _ => new InvalidOperationException("a bug in our own code — let it out"),
        }));

    // ---- and the decision SwapInPlace makes about those three outcomes, which is
    // what determines whether the user is told the update failed ----

    [Theory]
    [InlineData(nameof(UpdateService.Recovery.OldVersionRestored), true)]
    [InlineData(nameof(UpdateService.Recovery.VerifiedCopyAlreadyThere), false)]
    [InlineData(nameof(UpdateService.Recovery.StagedInstalled), false)]
    public void OnlyARolledBackSwapIsReportedAsAFailure(string outcomeName, bool reports)
    {
        var outcome = Enum.Parse<UpdateService.Recovery>(outcomeName);
        // Two of the three leave a good binary at the canonical path, which is exactly
        // what a successful swap produces — so the caller must go on to refresh the
        // shortcuts and start it, not abort with an error that changed nothing.
        var target = Write("app.exe", "the working version");
        var staged = Write("staged.exe", "the new version");
        var parked = target + ".old";

        // Force the second move to fail, then answer with the outcome under test.
        using var blocked = new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.None);
        void Swap() => UpdateService.SwapInPlace(target, staged, () => parked, (t, p, s) =>
        {
            File.Move(p, t);        // whatever the outcome, put a binary back
            return outcome;
        });

        if (reports) Assert.Throws<InvalidOperationException>(Swap);
        else Swap();

        Assert.Equal("the working version", File.ReadAllText(target));
    }

    [Fact]
    public void Swap_KeepsTheFilesItsOwnFailureMessageTellsYouToRename()
    {
        // The invariant the direct PutSomethingBack tests assert, checked where it
        // actually has to hold. `SwapInPlace`'s cleanup runs on the way out of a throw
        // too, and it fires precisely when a file is at the target — which is the state
        // the mismatch message is reporting. Deleting the verified download there makes
        // the message a lie and destroys the only copy of it, and the next launch's
        // sweep then takes the parked one as well.
        var target = Write("app.exe", "the working version");
        var staged = Write("staged.exe", "the verified new version");
        var parked = target + ".old";

        // Share Read, not None: the move needs delete access and so fails, but the file
        // stays readable — which is what a scanner holding it actually looks like, and
        // what lets the byte comparison run at all.
        var scanner = new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.Read);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            UpdateService.SwapInPlace(target, staged, () => parked, (t, p, s) =>
            {
                // A stranger lands at the canonical path during the gap — the un-parked
                // File.Copy in RegistrationService, the one writer the park can't exclude.
                File.WriteAllText(t, "some other build entirely");
                // And the hold clears, so nothing but the guard under test stands
                // between the cleanup and this file.
                scanner.Dispose();
                return UpdateService.PutSomethingBack(t, p, s);   // the real judgement
            }));

        Assert.Contains(Path.GetFileName(staged), ex.Message);
        Assert.True(File.Exists(staged), "the message names this file — it must be there");
        Assert.Equal("the verified new version", File.ReadAllText(staged));
        Assert.True(File.Exists(parked), "and so is this one");
    }

    [Fact]
    public void Swap_StillTidiesTheStagedCopyAfterAnOrdinaryRollback()
    {
        // The other side of that guard: a plain rolled-back update leaves a ~6.5 MB
        // copy nothing else would ever remove, and its message doesn't send anyone
        // looking for it. That one still goes.
        var target = Write("app.exe", "the working version");
        var staged = Write("staged.exe", "the new version");
        var parked = target + ".old";
        var scanner = new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.Read);

        Assert.Throws<InvalidOperationException>(() =>
            UpdateService.SwapInPlace(target, staged, () => parked, (t, p, s) =>
            {
                scanner.Dispose();                 // the transient hold clears
                File.Move(p, t);                   // and the old version goes back
                return UpdateService.Recovery.OldVersionRestored;
            }));

        Assert.Equal("the working version", File.ReadAllText(target));
        Assert.False(File.Exists(staged), "nothing sends the user looking for this one");
    }

    // ---- the startup sweep ----

    [Fact]
    public void Sweep_ClearsWhatEarlierUpdatesLeftBehind()
    {
        var exe = Write("app.exe", "the installed version");
        Write("app.exe.old", "last version");
        Write($"app.exe.old-{Environment.ProcessId}-3", "the version before that");
        Write(".mdm-update-staged.exe", "abandoned by a pre-0.6.4 failure");
        Write(".mdm-update-staged-999999.exe", "abandoned by a dead process");

        UpdateService.CleanupOldBinaries(exe);

        Assert.Equal(new[] { exe }, Directory.GetFiles(_dir));
    }

    [Fact]
    public void Sweep_LeavesALiveWindowsStagingCopyAlone()
    {
        var exe = Write("app.exe", "the installed version");
        var mine = Write($".mdm-update-staged-{Environment.ProcessId}.exe", "mid-update right now");

        UpdateService.CleanupOldBinaries(exe);

        Assert.True(File.Exists(mine), "deleting it would break that window's update");
    }

    [Fact]
    public void Sweep_DoesNothingAtAllWhenTheInstallHasNoExe()
    {
        // A failed swap AND a failed rollback leaves the `.old` as the only working
        // binary in the folder. Sweeping it is the one unrecoverable mistake available
        // here, so the sweep declines to run rather than tidying an install to death.
        var missing = Path.Combine(_dir, "app.exe");
        var lastHope = Write("app.exe.old", "the only binary left");
        var staged = Write(".mdm-update-staged.exe", "the verified new one");

        UpdateService.CleanupOldBinaries(missing);

        Assert.True(File.Exists(lastHope));
        Assert.True(File.Exists(staged));
    }

    [Fact]
    public void ParkingName_TreatsADirectoryAsTaken()
    {
        // File.Exists is false for a directory, so on its own it would call the name
        // free, hand it over three times, and then blame a race that isn't happening.
        var target = Write("app.exe", "current");
        Directory.CreateDirectory(target + ".old");

        Assert.NotEqual(target + ".old", UpdateService.ChooseParkingName(target));
    }

    [Theory]
    // Written before 0.6.4, so no process owns it — anyone who hit the bug this
    // release fixes has one of these, and nothing else will ever reclaim it.
    [InlineData(".mdm-update-staged.exe", true)]
    // A dead process's leftover.
    [InlineData(".mdm-update-staged-999999.exe", true)]
    // Not a staging file at all.
    [InlineData("something-else.exe", false)]
    [InlineData("MarkdownMidget.exe", false)]
    // Ours by prefix but with a suffix we didn't write — leave it alone rather than
    // guess.
    [InlineData(".mdm-update-staged-notapid.exe", false)]
    public void StagedLeftovers_AreReclaimedOnlyWhenNobodyOwnsThem(string name, bool reclaim)
        => Assert.Equal(reclaim, UpdateService.IsReclaimableStagedFile(Path.Combine(_dir, name)));

    [Fact]
    public void AStagedFileBelongingToALiveProcess_IsLeftAlone()
    {
        // The reason the name carries a process id at all: two windows updating at
        // once must not delete each other's copy mid-rename. During the swap the
        // owner has it closed, not locked, so nothing else protects it.
        var mine = Path.Combine(_dir, $".mdm-update-staged-{Environment.ProcessId}.exe");
        Assert.False(UpdateService.IsReclaimableStagedFile(mine));
    }

    [Fact]
    public void ParkingName_IsThePlainOneWhenNothingIsInTheWay()
        => Assert.Equal(Path.Combine(_dir, "app.exe") + ".old",
                        UpdateService.ChooseParkingName(Path.Combine(_dir, "app.exe")));

    [Fact]
    public void ParkingName_ReusesThePlainOneWhenTheOldFileIsJustStale()
    {
        var target = Write("app.exe", "current");
        Write("app.exe.old", "left by a finished update");
        Assert.Equal(target + ".old", UpdateService.ChooseParkingName(target));
        Assert.False(File.Exists(target + ".old"), "the stale one is cleared out of the way");
    }

    [Fact]
    public void ParkingName_StepsAsideWhenTheOldFileIsSomeonesRunningImage()
    {
        // The last way to hit "Cannot create a file when that file already exists":
        // a window that was open during an earlier update is still executing
        // `…exe.old`, and this instance genuinely does need the update it's asking
        // for — so no version check can save it. Don't collide; park elsewhere.
        var target = Write("app.exe", "current");
        var stuck = Write("app.exe.old", "another window is running this");
        using var held = new FileStream(stuck, FileMode.Open, FileAccess.Read, FileShare.Read);

        var chosen = UpdateService.ChooseParkingName(target);

        Assert.NotEqual(stuck, chosen);
        Assert.StartsWith(stuck + "-", chosen);
        Assert.True(File.Exists(stuck), "the window still running it keeps its image");
        // And the swap it enables actually works.
        var staged = Write("staged.exe", "the new version");
        UpdateService.SwapInPlace(target, staged, chosen);
        Assert.Equal("the new version", File.ReadAllText(target));
        Assert.Equal("current", File.ReadAllText(chosen));
    }

    [Fact]
    public void ParkingName_KeepsLookingWhenThePidVariantIsTakenToo()
    {
        // Process ids get reused. `…old-<pid>` can already belong to a window still
        // running an image an earlier holder of this pid parked there — so assuming
        // the pid variant is free brings the original collision back by another
        // route. Both taken means take a third name, not fail.
        var target = Write("app.exe", "current");
        var first = Write("app.exe.old", "window A is running this");
        var second = Write($"app.exe.old-{Environment.ProcessId}", "window B is running this");
        using var heldA = new FileStream(first, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var heldB = new FileStream(second, FileMode.Open, FileAccess.Read, FileShare.Read);

        var chosen = UpdateService.ChooseParkingName(target);

        Assert.False(File.Exists(chosen), "the chosen name must actually be free");
        Assert.NotEqual(first, chosen);
        Assert.NotEqual(second, chosen);

        // And the swap onto it works, which is the whole point.
        var staged = Write("staged.exe", "the new version");
        UpdateService.SwapInPlace(target, staged, chosen);
        Assert.Equal("the new version", File.ReadAllText(target));
        Assert.Equal("current", File.ReadAllText(chosen));
    }

    [Fact]
    public void ParkingName_StaysWithinTheSweepsReach()
    {
        // Whatever name it picks has to be one CleanupOldBinaries will reclaim later,
        // or stepping aside just trades one leak for another. Asserted by running the
        // sweep's actual glob rather than by eyeballing the prefix — a string check
        // passes for names the enumerator would never return.
        var target = Write("app.exe", "current");
        Write("app.exe.old", "taken");
        using var held = new FileStream(target + ".old", FileMode.Open, FileAccess.Read, FileShare.Read);

        var chosen = UpdateService.ChooseParkingName(target);
        File.WriteAllText(chosen, "parked here");

        Assert.Contains(chosen, Directory.EnumerateFiles(_dir, "app.exe.old*"));
    }

    [Fact]
    public void ParkingName_FallsBackToAFreeNameWhenEveryCandidateIsTaken()
    {
        // Past the end of the probe list. Handing back the plain name here — a name
        // just proven undeletable — would walk straight into the rename error this
        // release exists to remove, which is not "failing honestly", it IS the bug.
        var target = Write("app.exe", "current");
        var pid = Environment.ProcessId;
        var names = new List<string> { target + ".old", $"{target}.old-{pid}" };
        for (var n = 1; n <= 20; n++) names.Add($"{target}.old-{pid}-{n}");

        var held = new List<FileStream>();
        try
        {
            foreach (var n in names)
            {
                File.WriteAllText(n, "someone is running this");
                held.Add(new FileStream(n, FileMode.Open, FileAccess.Read, FileShare.Read));
            }

            var chosen = UpdateService.ChooseParkingName(target);

            Assert.False(File.Exists(chosen), "the fallback must not be a name already taken");
            Assert.StartsWith(Path.Combine(_dir, "app.exe.old"), chosen);   // still swept
            // And it works, which is the only thing that matters at this point.
            var staged = Write("staged.exe", "the new version");
            UpdateService.SwapInPlace(target, staged, chosen);
            Assert.Equal("the new version", File.ReadAllText(target));
        }
        finally { foreach (var h in held) h.Dispose(); }
    }

    // ---- losing the race between choosing a name and taking it ----

    [Fact]
    public void Swap_TakesAnotherNameWhenASiblingClaimsTheOneItChose()
    {
        // ChooseParkingName reports what was free when it looked; nothing reserves it.
        // The plain `…exe.old` is the one candidate two processes can both pick (every
        // other shape carries a process id), so two windows updating at once can each
        // be told it is free — and the second one's rename lands on the first one's
        // image. Re-deciding is the only thing that closes it.
        var target = Write("app.exe", "current");
        var staged = Write("staged.exe", "the new version");
        var stolen = target + ".old";
        var free = target + ".old-later";
        FileStream? sibling = null;

        var calls = 0;
        string Chooser()
        {
            if (++calls > 1) return free;
            // First answer is honestly free at this instant; a sibling takes it in the
            // gap before the rename.
            File.WriteAllText(stolen, "the sibling's running image");
            sibling = new FileStream(stolen, FileMode.Open, FileAccess.Read, FileShare.Read);
            return stolen;
        }

        try
        {
            UpdateService.SwapInPlace(target, staged, Chooser);

            Assert.Equal(2, calls);
            Assert.Equal("the new version", File.ReadAllText(target));
            Assert.Equal("current", File.ReadAllText(free));
            Assert.Equal("the sibling's running image", File.ReadAllText(stolen));
        }
        finally { sibling?.Dispose(); }
    }

    [Fact]
    public void Swap_ExplainsItselfWhenItKeepsLosingTheRace()
    {
        // Losing the race every time over the full park budget means something another
        // spin won't fix. What must NOT happen is the bare "Cannot create a file when
        // that file already exists" reaching the user, since that is the report this
        // whole release started from.
        var target = Write("app.exe", "current");
        var staged = Write("staged.exe", "the new version");
        var stuck = Write("app.exe.old", "always taken");
        using var held = new FileStream(stuck, FileMode.Open, FileAccess.Read, FileShare.Read);

        var ex = Assert.Throws<InvalidOperationException>(
            () => UpdateService.SwapInPlace(target, staged, () => stuck));

        Assert.DoesNotContain("Cannot create a file", ex.Message);
        Assert.Contains("Close the other windows", ex.Message);
        // And the install is exactly as it was.
        Assert.Equal("current", File.ReadAllText(target));
        Assert.False(File.Exists(staged), "nothing was swapped, so the staged copy is litter");
    }

    [Fact]
    public void Swap_RollsBackToWhereverItActuallyParked_NotWhereItFirstMeantTo()
    {
        // After a retry the parked file is under the SECOND name. A rollback that moved
        // back the first name would restore nothing and lose the working exe outright.
        var target = Write("app.exe", "the working version");
        var staged = Write("staged.exe", "the new version");
        var stolen = target + ".old";
        var free = target + ".old-later";
        FileStream? sibling = null;

        var calls = 0;
        string Chooser()
        {
            if (++calls > 1) return free;
            File.WriteAllText(stolen, "the sibling's running image");
            sibling = new FileStream(stolen, FileMode.Open, FileAccess.Read, FileShare.Read);
            return stolen;
        }

        try
        {
            // Hold the staged file so its move into place fails, forcing the rollback.
            using (var _ = new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.None))
                Assert.ThrowsAny<Exception>(() => UpdateService.SwapInPlace(target, staged, Chooser));

            Assert.True(File.Exists(target), "the rollback must restore the running exe");
            Assert.Equal("the working version", File.ReadAllText(target));
        }
        finally { sibling?.Dispose(); }
    }

    [Fact]
    public void ADeniedRenameIsExplainedRatherThanRetried()
    {
        // The two decisions a denial has to get right, and the reason they are named
        // predicates: the state that produces one can't be staged inside a test — it
        // needs a real ACL denial or a security product — so what is pinned is what the
        // code does GIVEN that failure, not the route to it.
        var denied = new UnauthorizedAccessException("Access to the path is denied.");
        var raced = new IOException("x", unchecked((int)0x800700B7));

        // Explained by us: its own message doesn't even name a path.
        Assert.True(UpdateService.IsParkFailureWeExplain(denied));
        Assert.True(UpdateService.IsParkFailureWeExplain(raced));
        // A sharing violation is in for the same reason. Its text — "the process cannot
        // access the file because it is being used by another process" — names no file
        // either: File.Move's two-argument overload hands no path to the translator, so
        // NOTHING it raises here carries one.
        Assert.True(UpdateService.IsParkFailureWeExplain(
            new IOException("in use", unchecked((int)0x80070020))));
        // Left alone: a full disk does say something specific, and our own bugs should
        // never be dressed up as an expected condition.
        Assert.False(UpdateService.IsParkFailureWeExplain(
            new IOException("full", unchecked((int)0x80070070))));     // ERROR_DISK_FULL
        Assert.False(UpdateService.IsParkFailureWeExplain(new InvalidOperationException("our bug")));

        // And a denial is not retried: the same call will be denied again, so spending
        // the park budget on it only adds a second of frozen UI before the same report.
        // A sharing violation is the opposite — whoever has the file open is finishing
        // something, and waiting is exactly what helps.
        var inUse = new IOException("in use", unchecked((int)0x80070020));
        Assert.False(UpdateService.ShouldRetryPark(denied, 1));
        Assert.True(UpdateService.ShouldRetryPark(raced, 1));
        Assert.True(UpdateService.ShouldRetryPark(inUse, 1));
    }

    [Fact]
    public void SomethingHoldingTheFileIsNotSomethingRacingForItsName()
    {
        // Both reach the park's exhaustion, and the remedies differ: one is "close your
        // other windows", the other is "wait for whatever has it open". Getting this
        // arm wrong sends people hunting for a window that isn't there.
        var target = Write("app.exe", "held by something");
        var msg = UpdateService.ParkFailureMessage(
            target, new IOException("in use", unchecked((int)0x80070020)));

        Assert.Contains("has " + target + " open", msg);
        Assert.DoesNotContain("Close the other windows", msg);
    }

    [Fact]
    public void ADeniedRenameIsNotBlamedOnOtherWindows()
    {
        // File.Move raises UnauthorizedAccessException for a denied rename — a security
        // product guarding the folder, typically — and its own text is "Access to the
        // path is denied" with no path in it. Telling someone to close their other
        // windows would send them after a race that isn't happening.
        //
        // The trigger needs a real ACL denial and can't be staged from inside a test,
        // so what's pinned here is the routing: given that failure, this is what gets
        // said. `ParkTarget` passes the exception straight to it.
        var target = Write("app.exe", "present, and irrelevant to this arm");
        var msg = UpdateService.ParkFailureMessage(target, new UnauthorizedAccessException("x"));

        Assert.Contains("security product", msg);
        Assert.Contains(target, msg);                       // the raw one has no path at all
        Assert.DoesNotContain("Close the other windows", msg);
    }

    [Fact]
    public void ARaceIsBlamedOnOtherWindowsOnlyWhenAProgramIsActuallyThere()
    {
        var there = Write("app.exe", "still where it should be");
        var gone = Path.Combine(_dir, "vanished.exe");
        var raced = new IOException("x", unchecked((int)0x800700B7));

        Assert.Contains("Close the other windows", UpdateService.ParkFailureMessage(there, raced));
        // Nothing at the path: closing windows cannot conjure a program file.
        var missing = UpdateService.ParkFailureMessage(gone, raced);
        Assert.DoesNotContain("Close the other windows", missing);
        Assert.Contains("wait a moment and try again", missing);
    }

    [Theory]
    // The two shapes a sibling's in-progress swap presents, and the only two where
    // going round again can change the answer.
    [InlineData(183, true)]   // ERROR_ALREADY_EXISTS — it took the name we chose
    [InlineData(2, true)]     // ERROR_FILE_NOT_FOUND — it has parked, not yet landed
    [InlineData(3, true)]     // ERROR_PATH_NOT_FOUND
    [InlineData(80, false)]   // ERROR_FILE_EXISTS — a different call's code; not ours
    [InlineData(32, false)]   // sharing violation: a different name won't help
    [InlineData(112, false)]  // disk full: waiting certainly won't
    public void OnlyASiblingMidSwapIsWorthAnotherAttempt(int win32, bool retry)
        => Assert.Equal(retry,
            UpdateService.IsSiblingMidSwap(new IOException("x", unchecked((int)(0x80070000 | win32)))));

    [Theory]
    [InlineData(32, true)]    // ERROR_SHARING_VIOLATION — something else has it open
    [InlineData(33, true)]    // ERROR_LOCK_VIOLATION
    [InlineData(112, false)]  // ERROR_DISK_FULL — not a "another window has it" case
    [InlineData(5, false)]    // ERROR_ACCESS_DENIED
    public void OnlySharingViolationsMeanAnotherWindowHasTheFile(int win32, bool sharing)
        => Assert.Equal(sharing,
            UpdateService.IsSharingViolation(new IOException("x", unchecked((int)(0x80070000 | win32)))));
}
