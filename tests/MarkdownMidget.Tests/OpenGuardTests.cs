using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using MarkdownMidget.Instances;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>
/// The already-open guard (issue #1, release-1.0 stage 0.11). Every window is its
/// own process, so "is this file open somewhere else" is a cross-process question
/// answered by a per-path lock file. Every case here is a way two windows end up
/// editing one file - or a way one window gets wrongly told it can't.
/// </summary>
public class OpenGuardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "mm-openguard-" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _cleanup = new();

    private static readonly int Me = Environment.ProcessId;
    // Any pid that isn't ours. One past a real pid is never a pid itself (Windows
    // hands them out in multiples of 4), so the real liveness check calls it dead;
    // tests that want the holder honoured vouch for it with Live instead.
    private static readonly int Other = Environment.ProcessId + 1;
    // A liveness check that vouches for any holder: the decision's shape, tested
    // apart from whether the recorded pid and window really exist.
    private static readonly Func<int, long, bool> Live = (_, _) => true;

    private OpenGuard New()
    {
        var g = new OpenGuard(_dir);
        _cleanup.Add(g);
        return g;
    }

    private string Doc(string name) => Path.Combine(_dir, name);

    // Held or free, without taking it: Peek conflicts with a holder in this process
    // exactly as it would with one in another (share modes are per handle, not per
    // process), and a probe that TOOK a free lock would hold it for the rest of the test.
    private OpenGuard.ProbeState StateOf(string path) => OpenGuard.Peek(_dir, path).State;

    public OpenGuardTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var d in _cleanup) { try { d.Dispose(); } catch { } }
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static string ReadLock(string lockPath)
    {
        // What a second process does to learn who holds a lock: Delete in the share
        // mode because the holder opened it DeleteOnClose, and Windows refuses any
        // later open of such a file that doesn't share delete.
        using var fs = new FileStream(lockPath, FileMode.Open, FileAccess.Read,
                                      FileShare.ReadWrite | FileShare.Delete);
        using var r = new StreamReader(fs);
        return r.ReadToEnd();
    }

    // ---- W1 ----

    [Fact]
    public void SecondAcquireSeesTheHolder()
    {
        var path = Doc("shared.md");
        var first = New();
        var mine = first.Acquire(path, Other, hwnd: 0x1234);
        Assert.Equal(OpenGuard.ProbeState.Free, mine.State);

        var second = New();
        var probe = second.Acquire(path, Me, hwnd: 1);
        Assert.Equal(OpenGuard.ProbeState.Held, probe.State);
        Assert.Equal(Other, probe.HolderPid);
        Assert.Equal(0x1234, probe.HolderHwnd);

        var decision = OpenGuardDecision.Decide(probe, Me, Live);
        Assert.Equal(OpenVerdict.FocusOther, decision.Verdict);
        Assert.Equal(Other, decision.HolderPid);
        Assert.Equal(0x1234, decision.HolderHwnd);
    }

    // ---- W2 (decision level; the SetForegroundWindow hand-off itself is GUI) ----

    [Fact]
    public void FocusFailureOpensReadOnly()
    {
        var held = new OpenGuard.Probe(OpenGuard.ProbeState.Held, Other, 0);
        var decision = OpenGuardDecision.Decide(held, Me, Live);
        Assert.Equal(OpenVerdict.FocusOther, decision.Verdict);

        // A recorded handle of 0 (window not initialised when the holder acquired)
        // can't be focused, and neither can a handle that isn't a window any more.
        Assert.False(OpenGuard.TryFocusWindow(0));
        Assert.False(OpenGuard.TryFocusWindow(0x7FFF_FFF0));

        // The window's fallback once the focus attempt has an answer: a failed focus
        // opens read-only whether or not this is the launching process; a successful
        // one yields, and the launching process (nothing open yet) exits.
        Assert.Equal(OpenFallback.OpenReadOnly, OpenGuardDecision.AfterFocus(focused: false, startup: false));
        Assert.Equal(OpenFallback.OpenReadOnly, OpenGuardDecision.AfterFocus(focused: false, startup: true));
        Assert.Equal(OpenFallback.Yield, OpenGuardDecision.AfterFocus(focused: true, startup: false));
        Assert.Equal(OpenFallback.Exit, OpenGuardDecision.AfterFocus(focused: true, startup: true));
    }

    // ---- W3 ----

    [Fact]
    public void StaleLockIsIgnored()
    {
        // A power cut denies DeleteOnClose its chance, so a well-formed lock file
        // can be left behind with nobody holding it. It must not count as "open".
        var path = Doc("crashed.md");
        var guard = New();
        File.WriteAllText(guard.LockPathFor(path), "12345\n678\n" + path + "\n");

        var probe = guard.Acquire(path, Me, hwnd: 9);
        Assert.Equal(OpenGuard.ProbeState.Free, probe.State);
        Assert.Equal(OpenVerdict.Proceed, OpenGuardDecision.Decide(probe, Me, Live).Verdict);
        // ...and the file now says who really holds it, not the dead session.
        var lines = ReadLock(guard.LockPathFor(path)).Split('\n');
        Assert.Equal(Me.ToString(), lines[0]);
        Assert.Equal("9", lines[1]);
    }

    // ---- W4 ----

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathNameW(string lpszLongPath, [Out] char[] lpszShortPath, uint cchBuffer);

    [Fact]
    public void PathsNormalise()
    {
        // A name long enough (and with a space) to have an 8.3 alias distinct from it.
        var exact = Doc("Already Open Document.md");
        File.WriteAllText(exact, "x");
        var key = OpenGuard.KeyFor(exact);

        Assert.Equal(key, OpenGuard.KeyFor(exact.ToUpperInvariant()));
        Assert.Equal(key, OpenGuard.KeyFor(Path.Combine(_dir, ".", "sub", "..", "Already Open Document.md")));
        Assert.Equal(key, OpenGuard.KeyFor(exact + Path.DirectorySeparatorChar));
        Assert.Equal(key, OpenGuard.KeyFor(exact.Replace('\\', '/')));

        var buffer = new char[1024];
        var n = GetShortPathNameW(exact, buffer, (uint)buffer.Length);
        Assert.True(n > 0 && n < buffer.Length, "GetShortPathNameW failed on the fixture file");
        var shortName = new string(buffer, 0, (int)n);
        // 8.3 aliases can be switched off per volume; when they are, the short name IS
        // the long name and this line proves nothing extra, so it is reported.
        if (string.Equals(shortName, exact, StringComparison.OrdinalIgnoreCase))
            Console.WriteLine("8.3 names are disabled on this volume; the short-name case did not run");
        else
            Assert.Equal(key, OpenGuard.KeyFor(shortName));

        // A file that doesn't exist can't be expanded, so casing has to be folded
        // outright - GetLongPathNameW hands back the on-disk casing for the file
        // above, which would mask a missing fold.
        Assert.Equal(OpenGuard.KeyFor(@"C:\nowhere\Draft.md"), OpenGuard.KeyFor(@"c:\NOWHERE\draft.MD"));
        Assert.NotEqual(OpenGuard.KeyFor(@"C:\nowhere\Draft.md"), OpenGuard.KeyFor(@"C:\nowhere\Draft2.md"));
        Assert.Equal(64, key.Length);   // SHA-256 hex; the lock file name is derived from it
    }

    // ---- W5 ----

    [Fact]
    public void OwnLockIsNotABlock()
    {
        // A window re-opening its own document (Open Recent on the current file)
        // trips over its own lock. That is never a reason to refuse.
        var path = Doc("mine.md");
        var first = New();
        Assert.Equal(OpenGuard.ProbeState.Free, first.Acquire(path, Me, hwnd: 5).State);

        var probe = New().Acquire(path, Me, hwnd: 5);
        Assert.Equal(OpenGuard.ProbeState.Held, probe.State);
        Assert.Equal(Me, probe.HolderPid);
        Assert.Equal(OpenVerdict.Own, OpenGuardDecision.Decide(probe, Me, Live).Verdict);
    }

    // ---- W6 ----

    [Fact]
    public void RekeysOnPathChange()
    {
        // Save As / Encrypt / Convert to Unencrypted move the document to another
        // path: the old one must become free and the new one held.
        var a = Doc("a.md");
        var b = Doc("b.md");
        var guard = New();
        Assert.Equal(OpenGuard.ProbeState.Free, guard.Acquire(a, Me, hwnd: 1).State);
        Assert.Equal(OpenGuard.ProbeState.Free, guard.Rekey(b, Me, hwnd: 1).State);
        Assert.Equal(b, guard.HeldPath);

        var other = New();
        Assert.Equal(OpenGuard.ProbeState.Free, other.Acquire(a, Other, hwnd: 2).State);   // old path released
        Assert.Equal(OpenGuard.ProbeState.Held, other.Acquire(b, Other, hwnd: 2).State);   // new path held

        // Re-keying onto a path someone else holds still lets go of the old one: the
        // document IS at the new path now (the write happened), whatever the other
        // window thinks.
        var c = Doc("c.md");
        var d = Doc("d.md");
        var mover = New();
        var squatter = New();
        Assert.Equal(OpenGuard.ProbeState.Free, mover.Acquire(c, Me, hwnd: 1).State);
        Assert.Equal(OpenGuard.ProbeState.Free, squatter.Acquire(d, Other, hwnd: 3).State);
        Assert.Equal(OpenGuard.ProbeState.Held, mover.Rekey(d, Me, hwnd: 1).State);
        Assert.Null(mover.HeldPath);
        Assert.Equal(OpenGuard.ProbeState.Free, New().Acquire(c, Other, hwnd: 4).State);
    }

    // ---- extras ----

    [Fact]
    public void DisposeReleases()
    {
        var path = Doc("closing.md");
        var guard = New();
        guard.Acquire(path, Me, hwnd: 1);
        var lockPath = guard.LockPathFor(path);
        Assert.True(File.Exists(lockPath));

        guard.Dispose();
        Assert.False(File.Exists(lockPath));   // DeleteOnClose
        Assert.Null(guard.HeldPath);
        Assert.Equal(OpenGuard.ProbeState.Free, New().Acquire(path, Other, hwnd: 2).State);
    }

    [Fact]
    public void GarbageLockContent()
    {
        var path = Doc("garbage.md");
        var guard = New();
        var lockPath = guard.LockPathFor(path);

        // Unheld garbage is just a stale file: taken over, content replaced.
        File.WriteAllText(lockPath, "not a pid at all");
        Assert.Equal(OpenGuard.ProbeState.Free, guard.Acquire(path, Me, hwnd: 1).State);
        Assert.StartsWith(Me + "\n", ReadLock(lockPath));
        guard.Dispose();

        // Held garbage is "someone has it, no idea who": held by pid 0, hwnd 0, which
        // no liveness check can vouch for. The decision treats that as stale and the
        // open proceeds - unguarded, since the claim itself failed - because a holder
        // that can't be identified is a backup tool sitting on a stale file far more
        // often than a window of ours, and there is no window to send them to anyway.
        using (var holder = new FileStream(lockPath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read))
        {
            holder.Write("garbage"u8);
            holder.Flush();
            var probe = New().Acquire(path, Me, hwnd: 1);
            Assert.Equal(OpenGuard.ProbeState.Held, probe.State);
            Assert.Equal(0, probe.HolderPid);
            Assert.Equal(0, probe.HolderHwnd);
            var decision = OpenGuardDecision.Decide(probe, Me, OpenGuard.HolderIsLive);
            Assert.Equal(OpenVerdict.Proceed, decision.Verdict);
            Assert.False(OpenGuard.TryFocusWindow(probe.HolderHwnd));   // nothing to focus either way
        }
    }

    // ---- the 0/0 window: a holder caught between opening its lock and writing to it ----

    [Fact]
    public void AHolderCaughtBeforeItsWriteIsReadAgain()
    {
        // Take opens the lock and then writes pid and handle into it. A reader that
        // lands in between (tens of microseconds) sees an empty file: held, by nobody
        // it can name, which the decision refuses - and the open would proceed
        // unguarded beside a window of ours that was about to say who it was. So a
        // read that yields 0/0 waits once and looks again. The wait is injected;
        // here it is the moment the holder's write lands.
        var path = Doc("racing.md");
        var lockPath = New().LockPathFor(path);
        using var holder = new FileStream(lockPath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
        var waits = new List<int>();
        void WriteDuringTheWait(int ms)
        {
            waits.Add(ms);
            holder.Write(Encoding.UTF8.GetBytes($"{Other}\n{0x1234}\n{path}\n"));
            holder.Flush();
        }

        var guard = new OpenGuard(_dir, WriteDuringTheWait);
        _cleanup.Add(guard);
        var probe = guard.Acquire(path, Me, hwnd: 1);
        Assert.Equal(OpenGuard.ProbeState.Held, probe.State);
        Assert.Equal(Other, probe.HolderPid);
        Assert.Equal(0x1234, probe.HolderHwnd);
        Assert.Equal(new[] { OpenGuard.HolderWriteGraceMs }, waits);
        Assert.Null(guard.HeldPath);

        // Peek (App.OnStartup's probe) lands in the same gap and takes the same second look.
        holder.SetLength(0);
        waits.Clear();
        var peeked = OpenGuard.Peek(_dir, path, WriteDuringTheWait);
        Assert.Equal(OpenGuard.ProbeState.Held, peeked.State);
        Assert.Equal(Other, peeked.HolderPid);
        Assert.Equal(0x1234, peeked.HolderHwnd);
        Assert.Equal(new[] { OpenGuard.HolderWriteGraceMs }, waits);

        // A holder that has written is read the first time: no wait at all.
        waits.Clear();
        Assert.Equal(Other, guard.Acquire(path, Me, hwnd: 1).HolderPid);
        Assert.Equal(Other, OpenGuard.Peek(_dir, path, WriteDuringTheWait).HolderPid);
        Assert.Empty(waits);

        // Still nothing after the wait: held by nobody nameable, as before - and the
        // second look is the last. The guard does not sit polling a lock that a
        // backup tool holds.
        holder.SetLength(0);
        var looks = 0;
        var patient = new OpenGuard(_dir, _ => looks++);
        _cleanup.Add(patient);
        var empty = patient.Acquire(path, Me, hwnd: 1);
        Assert.Equal(OpenGuard.ProbeState.Held, empty.State);
        Assert.Equal(0, empty.HolderPid);
        Assert.Equal(0, empty.HolderHwnd);
        Assert.Equal(1, looks);
        looks = 0;
        Assert.Equal(0, OpenGuard.Peek(_dir, path, _ => looks++).HolderPid);
        Assert.Equal(1, looks);
    }

    [Fact]
    public void TheHolderWriteGraceIsARealPause()
    {
        // Without an injected wait the second look comes after a real pause: short,
        // since every read of a lock a backup tool holds pays it, but long enough to
        // cover a holder's write and flush. Only the lower bound is asserted - an
        // upper bound is the scheduler's to break, not the guard's.
        Assert.InRange(OpenGuard.HolderWriteGraceMs, 10, 100);
        var path = Doc("slow.md");
        var lockPath = New().LockPathFor(path);
        using var holder = new FileStream(lockPath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
        var clock = Stopwatch.StartNew();
        var probe = New().Acquire(path, Me, hwnd: 1);
        clock.Stop();
        Assert.Equal(OpenGuard.ProbeState.Held, probe.State);
        Assert.Equal(0, probe.HolderPid);
        Assert.True(clock.ElapsedMilliseconds >= OpenGuard.HolderWriteGraceMs / 2,
            $"the second look came after {clock.ElapsedMilliseconds} ms; the grace is {OpenGuard.HolderWriteGraceMs} ms");
    }

    [Fact]
    public void AcquireOfAnotherPathSwapsTheLock()
    {
        // The immediate swap (Rekey's shape, and the read-only lift's): taking a
        // different path releases the one held. OpenPathAsync no longer swaps -
        // see AcquireSecondKeepsFirstUntilCommitted.
        var a = Doc("first.md");
        var b = Doc("second.md");
        var guard = New();
        guard.Acquire(a, Me, hwnd: 1);
        Assert.Equal(OpenGuard.ProbeState.Free, guard.Acquire(b, Me, hwnd: 1).State);
        Assert.Equal(b, guard.HeldPath);
        Assert.Equal(OpenGuard.ProbeState.Free, New().Acquire(a, Other, hwnd: 2).State);
    }

    [Fact]
    public void AFailedAcquireKeepsTheCurrentLock()
    {
        // The other way round: the new file is held elsewhere, the open doesn't
        // happen, and the document this window keeps showing stays guarded.
        var a = Doc("keep.md");
        var b = Doc("elsewhere.md");
        var guard = New();
        var elsewhere = New();
        guard.Acquire(a, Me, hwnd: 1);
        elsewhere.Acquire(b, Other, hwnd: 2);
        Assert.Equal(OpenGuard.ProbeState.Held, guard.Acquire(b, Me, hwnd: 1).State);
        Assert.Equal(a, guard.HeldPath);
        Assert.Equal(OpenGuard.ProbeState.Held, New().Acquire(a, Other, hwnd: 3).State);
    }

    [Fact]
    public void PeekReportsTheHolderWithoutTaking()
    {
        // App.OnStartup's question, asked before any window exists: who has it?
        var path = Doc("peeked.md");
        Assert.Equal(OpenGuard.ProbeState.Free, OpenGuard.Peek(_dir, path).State);   // no file at all

        var holder = New();
        holder.Acquire(path, Other, hwnd: 77);
        var probe = OpenGuard.Peek(_dir, path);
        Assert.Equal(OpenGuard.ProbeState.Held, probe.State);
        Assert.Equal(Other, probe.HolderPid);
        Assert.Equal(77, probe.HolderHwnd);
        Assert.Equal(OpenVerdict.FocusOther, OpenGuardDecision.Decide(probe, Me, Live).Verdict);
        // Peeking took nothing: the holder still holds it.
        Assert.Equal(OpenGuard.ProbeState.Held, New().Acquire(path, Me, hwnd: 1).State);

        holder.Dispose();
        File.WriteAllText(holder.LockPathFor(path), "12345\n1\n" + path + "\n");   // stale
        Assert.Equal(OpenGuard.ProbeState.Free, OpenGuard.Peek(_dir, path).State);
    }

    [Fact]
    public void LockDirectoryUnavailableIsNotABlock()
    {
        // The guard is a convenience, not a security boundary: when its directory
        // can't be created (here: the path is a file), the open goes ahead.
        var blocker = Path.Combine(_dir, "not-a-directory");
        File.WriteAllText(blocker, "");
        var guard = new OpenGuard(blocker);
        _cleanup.Add(guard);
        var probe = guard.Acquire(Doc("any.md"), Me, hwnd: 1);
        Assert.Equal(OpenGuard.ProbeState.Unavailable, probe.State);
        Assert.Equal(OpenVerdict.Proceed, OpenGuardDecision.Decide(probe, Me, Live).Verdict);
        Assert.Null(guard.HeldPath);
    }

    // ---- F4: the old claim is held until the new document is on screen ----

    [Fact]
    public void AcquireSecondKeepsFirstUntilCommitted()
    {
        // OpenPathAsync's shape. The window shows a and starts opening b: through the
        // read and the password prompt it is still showing - and must still be
        // guarding - a. Only once b is on screen does a's claim go.
        var a = Doc("showing.md");
        var b = Doc("opening.md");
        var guard = New();
        Assert.Equal(OpenGuard.ProbeState.Free, guard.Acquire(a, Me, hwnd: 1).State);

        Assert.Equal(OpenGuard.ProbeState.Free, guard.Begin(b, Me, hwnd: 1).State);
        Assert.Equal(a, guard.HeldPath);                          // still a
        Assert.Equal(OpenGuard.ProbeState.Held, StateOf(a));     // a is guarded
        Assert.Equal(OpenGuard.ProbeState.Held, StateOf(b));     // and so is b already

        // The prompt was cancelled: b goes, a was never let go of.
        guard.Abandon();
        Assert.Equal(a, guard.HeldPath);
        Assert.Equal(OpenGuard.ProbeState.Held, StateOf(a));
        Assert.Equal(OpenGuard.ProbeState.Free, StateOf(b));

        // This time it loads: the claim moves.
        Assert.Equal(OpenGuard.ProbeState.Free, guard.Begin(b, Me, hwnd: 1).State);
        guard.Commit(b);
        Assert.Equal(b, guard.HeldPath);
        Assert.Equal(OpenGuard.ProbeState.Free, StateOf(a));
        Assert.Equal(OpenGuard.ProbeState.Held, StateOf(b));

        // Opening the document already shown (Open Recent on the current file) trips
        // over its own lock: nothing pending, and the commit keeps what it has.
        var own = guard.Begin(b, Me, hwnd: 1);
        Assert.Equal(OpenGuard.ProbeState.Held, own.State);
        Assert.Equal(OpenVerdict.Own, OpenGuardDecision.Decide(own, Me, Live).Verdict);
        guard.Commit(b);
        Assert.Equal(b, guard.HeldPath);
        Assert.Equal(OpenGuard.ProbeState.Held, StateOf(b));

        // A path someone else holds, shown here anyway (the decision found the holder
        // stale, or nothing could be focused): b is no longer what this window shows,
        // so its claim goes, and c is shown unguarded.
        var c = Doc("theirs.md");
        var squatter = New();
        Assert.Equal(OpenGuard.ProbeState.Free, squatter.Acquire(c, Other, hwnd: 3).State);
        Assert.Equal(OpenGuard.ProbeState.Held, guard.Begin(c, Me, hwnd: 1).State);
        guard.Commit(c);
        Assert.Null(guard.HeldPath);
        Assert.Equal(OpenGuard.ProbeState.Free, StateOf(b));

        // Abandon with nothing pending is a no-op; Dispose lets a pending claim go too.
        guard.Abandon();
        var d = Doc("disposed.md");
        var e = Doc("pending.md");
        var g2 = New();
        Assert.Equal(OpenGuard.ProbeState.Free, g2.Acquire(d, Me, hwnd: 1).State);
        Assert.Equal(OpenGuard.ProbeState.Free, g2.Begin(e, Me, hwnd: 1).State);
        g2.Dispose();
        Assert.Equal(OpenGuard.ProbeState.Free, StateOf(d));
        Assert.Equal(OpenGuard.ProbeState.Free, StateOf(e));
    }

    // ---- re-entrant opens: Ctrl+O and the Alt menu are not gated while a load runs ----

    [Fact]
    public void ASecondBeginLetsTheFirstPendingClaimGo()
    {
        // Two opens in flight at once: the second Begin displaces the first's pending
        // claim. Without that the first pending handle would leak, and its path would
        // read as held - by a window nobody could be sent to, since it never showed
        // the file - until the garbage collector got round to the handle.
        var a = Doc("shown.md");
        var b = Doc("first-open.md");
        var c = Doc("second-open.md");
        var guard = New();
        Assert.Equal(OpenGuard.ProbeState.Free, guard.Acquire(a, Me, hwnd: 1).State);
        Assert.Equal(OpenGuard.ProbeState.Free, guard.Begin(b, Me, hwnd: 1).State);
        Assert.Equal(OpenGuard.ProbeState.Free, guard.Begin(c, Me, hwnd: 1).State);
        Assert.Equal(OpenGuard.ProbeState.Free, StateOf(b));   // b's pending claim went with the second Begin
        Assert.Equal(OpenGuard.ProbeState.Held, StateOf(c));
        Assert.Equal(OpenGuard.ProbeState.Held, StateOf(a));   // the document on screen is guarded throughout
        Assert.Equal(a, guard.HeldPath);
    }

    [Fact]
    public void AReentrantOpenCommitsOnlyItsOwnPath()
    {
        // Open1 begins b; Open2 begins c before Open1 has landed (displacing b's
        // pending claim, above); Open1 lands first and commits b. The pending claim
        // is c's, not b's, and must not be promoted: the window would show b while
        // holding c, and a cancelled Open2 would then leave it holding a document it
        // never showed. What is true instead: b is on screen with its claim
        // displaced, so - like a Begin that found its path held - b is shown
        // unguarded; a is no longer shown, so its claim goes; c stays pending for
        // Open2 to commit or abandon.
        var a = Doc("shown.md");
        var b = Doc("first-open.md");
        var c = Doc("second-open.md");
        var guard = New();
        Assert.Equal(OpenGuard.ProbeState.Free, guard.Acquire(a, Me, hwnd: 1).State);
        Assert.Equal(OpenGuard.ProbeState.Free, guard.Begin(b, Me, hwnd: 1).State);
        Assert.Equal(OpenGuard.ProbeState.Free, guard.Begin(c, Me, hwnd: 1).State);
        guard.Commit(b);
        Assert.Null(guard.HeldPath);
        Assert.Equal(OpenGuard.ProbeState.Free, StateOf(a));
        Assert.Equal(OpenGuard.ProbeState.Free, StateOf(b));
        Assert.Equal(OpenGuard.ProbeState.Held, StateOf(c));   // still pending

        // Open2 lands: its own path, so its claim is promoted as usual - and the path
        // is matched by identity, not spelling (KeyFor), like every other comparison.
        guard.Commit(c.ToUpperInvariant());
        Assert.Equal(c, guard.HeldPath);
        Assert.Equal(OpenGuard.ProbeState.Held, StateOf(c));

        // The other order: Open2 lands first, then Open1. c is promoted; b's commit
        // then finds nothing pending and c held, and c is not what the window shows
        // any more, so it goes. b is shown unguarded, as above.
        var d = Doc("shown-2.md");
        var e = Doc("first-open-2.md");
        var f = Doc("second-open-2.md");
        var g2 = New();
        Assert.Equal(OpenGuard.ProbeState.Free, g2.Acquire(d, Me, hwnd: 1).State);
        Assert.Equal(OpenGuard.ProbeState.Free, g2.Begin(e, Me, hwnd: 1).State);
        Assert.Equal(OpenGuard.ProbeState.Free, g2.Begin(f, Me, hwnd: 1).State);
        g2.Commit(f);
        Assert.Equal(f, g2.HeldPath);
        Assert.Equal(OpenGuard.ProbeState.Free, StateOf(d));
        g2.Commit(e);
        Assert.Null(g2.HeldPath);
        Assert.Equal(OpenGuard.ProbeState.Free, StateOf(f));
        Assert.Equal(OpenGuard.ProbeState.Free, StateOf(e));
    }

    [Fact]
    public void AReentrantOpenAbandonsOnlyItsOwnPath()
    {
        // Open1 begins b, Open2 begins c, and Open1 fails (an unreadable file, an
        // editor that threw) while Open2 is still in flight. Open1 lets go of ITS
        // pending claim - which the second Begin already displaced - and must leave
        // c's alone, or Open2 would commit with nothing pending and show c unguarded.
        var a = Doc("shown.md");
        var b = Doc("first-open.md");
        var c = Doc("second-open.md");
        var guard = New();
        Assert.Equal(OpenGuard.ProbeState.Free, guard.Acquire(a, Me, hwnd: 1).State);
        Assert.Equal(OpenGuard.ProbeState.Free, guard.Begin(b, Me, hwnd: 1).State);
        Assert.Equal(OpenGuard.ProbeState.Free, guard.Begin(c, Me, hwnd: 1).State);
        guard.Abandon(b);
        Assert.Equal(OpenGuard.ProbeState.Held, StateOf(c));   // Open2's claim is untouched
        Assert.Equal(a, guard.HeldPath);
        guard.Commit(c);
        Assert.Equal(c, guard.HeldPath);

        // Its own pending claim it does let go of, however the path is spelled; and
        // with nothing pending there is nothing to do.
        var d = Doc("cancelled.md");
        Assert.Equal(OpenGuard.ProbeState.Free, guard.Begin(d, Me, hwnd: 1).State);
        guard.Abandon(d.ToUpperInvariant());
        Assert.Equal(OpenGuard.ProbeState.Free, StateOf(d));
        Assert.Equal(c, guard.HeldPath);
        guard.Abandon(d);
        Assert.Equal(c, guard.HeldPath);
        Assert.Equal(OpenGuard.ProbeState.Held, StateOf(c));
    }

    // ---- F3: a held lock proves a process has the file open, not that it is a window of ours ----

    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [Fact]
    public void AForeignOrDeadHolderIsNotHonoured()
    {
        // The two probes, injected: which pids are running, and which pid owns a
        // window. Each case below fails exactly ONE probe, so that neither can stand
        // in for the other: 0x9ABC is a window the table still credits to a pid
        // that is not running, and 0x5678 is owned by a pid that is.
        var running = new HashSet<int> { Other };
        var owner = new Dictionary<long, int> { [0x1234] = Other, [0x5678] = Other + 4, [0x9ABC] = Other + 8 };
        Func<int, bool> processIsRunning = pid => running.Contains(pid);
        Func<long, int> windowOwnerPid = hwnd => owner.TryGetValue(hwnd, out var p) ? p : 0;
        Func<int, long, bool> live = (pid, hwnd) => OpenGuard.HolderIsLive(pid, hwnd, processIsRunning, windowOwnerPid);

        // Dead pid: a backup or AV tool holding a stale lock open. The open proceeds,
        // and there is no holder to focus - even though the window table would have
        // vouched for the handle.
        var dead = new OpenGuard.Probe(OpenGuard.ProbeState.Held, Other + 8, 0x9ABC);
        Assert.False(live(Other + 8, 0x9ABC));
        var stale = OpenGuardDecision.Decide(dead, Me, live);
        Assert.Equal(OpenVerdict.Proceed, stale.Verdict);
        Assert.Equal(0, stale.HolderHwnd);

        // Live pid, but the recorded handle now names another process's window
        // (handles are recycled): not a window of ours either.
        var recycled = new OpenGuard.Probe(OpenGuard.ProbeState.Held, Other, 0x5678);
        Assert.False(live(Other, 0x5678));
        Assert.Equal(OpenVerdict.Proceed, OpenGuardDecision.Decide(recycled, Me, live).Verdict);

        // Both match: that IS a window of ours, and it is honoured.
        var real = new OpenGuard.Probe(OpenGuard.ProbeState.Held, Other, 0x1234);
        Assert.True(live(Other, 0x1234));
        var decision = OpenGuardDecision.Decide(real, Me, live);
        Assert.Equal(OpenVerdict.FocusOther, decision.Verdict);
        Assert.Equal(Other, decision.HolderPid);
        Assert.Equal(0x1234, decision.HolderHwnd);

        // Unreadable content reads as 0/0. GetWindowThreadProcessId(0) fails and leaves
        // the owner at 0 - which would "match" pid 0 - so 0/0 is refused before the
        // probes are asked, even when something claims pid 0 is running.
        running.Add(0);
        Assert.False(live(0, 0));

        // The check is never consulted for our own pid (Own is never a block) or for
        // a lock nobody holds.
        Assert.Equal(OpenVerdict.Own, OpenGuardDecision.Decide(new(OpenGuard.ProbeState.Held, Me, 0), Me, live).Verdict);
        Assert.Equal(OpenVerdict.Proceed, OpenGuardDecision.Decide(new(OpenGuard.ProbeState.Free, 0, 0), Me, live).Verdict);

        // The real probes. Other is one past a real pid, never a pid itself (Windows
        // hands them out in multiples of 4), so it is dead. The shell window belongs
        // to Explorer: to this process it is somebody else's window, to Explorer's
        // pid it is a live holder.
        Assert.False(OpenGuard.HolderIsLive(Other, 0x1234));
        Assert.False(OpenGuard.HolderIsLive(0, 0));
        var shell = GetShellWindow();
        if (shell == IntPtr.Zero)
        {
            Console.WriteLine("no shell window in this session; the real-probe cases did not run");
            return;
        }
        GetWindowThreadProcessId(shell, out var shellPid);
        Assert.False(OpenGuard.HolderIsLive(Me, shell.ToInt64()));
        Assert.True(OpenGuard.HolderIsLive((int)shellPid, shell.ToInt64()));
    }

    // ---- F1: the fallback's read-only is lifted by a claim, not by the checkbox ----

    [Fact]
    public void LiftingReadOnlyStaysReadOnlyWhileHeld()
    {
        // W1 holds the document; this window opened it read-only, with no claim. The
        // user un-checks View ▸ Read Only while W1 still has it: the claim fails, so
        // the checkbox goes back and W1 is the window to send them to.
        var path = Doc("held.md");
        var w1 = New();
        Assert.Equal(OpenGuard.ProbeState.Free, w1.Acquire(path, Other, hwnd: 0x1234).State);
        var here = New();
        var imposed = new ImposedReadOnly();
        imposed.Impose(readOnlyAlready: false);
        Assert.True(imposed.Imposed);

        var claim = OpenGuardDecision.Decide(here.Acquire(path, Me, hwnd: 1), Me, Live);
        Assert.Equal(OpenVerdict.FocusOther, claim.Verdict);
        Assert.Equal(ReadOnlyLift.StayReadOnly, imposed.Lift(claim));
        Assert.True(imposed.Imposed);             // still imposed: the next un-check asks again
        Assert.Null(here.HeldPath);               // and nothing was taken
        Assert.Equal(0x1234, claim.HolderHwnd);   // the window to focus

        // Read-only the user chose is lifted by the checkbox alone: nothing was
        // imposed, so the claim is not consulted.
        Assert.Equal(ReadOnlyLift.Edit, new ImposedReadOnly().Lift(claim));
    }

    [Fact]
    public void LiftingReadOnlyReclaimsWhenFree()
    {
        // W1 has since closed the document. Un-checking Read Only claims it for this
        // window, so the edits that follow are guarded: a double-click on the file
        // now finds this window rather than opening a second editable copy.
        var path = Doc("released.md");
        var w1 = New();
        Assert.Equal(OpenGuard.ProbeState.Free, w1.Acquire(path, Other, hwnd: 0x1234).State);
        var here = New();
        var imposed = new ImposedReadOnly();
        imposed.Impose(readOnlyAlready: false);
        w1.Dispose();

        var claim = OpenGuardDecision.Decide(here.Acquire(path, Me, hwnd: 1), Me, Live);
        Assert.Equal(OpenVerdict.Proceed, claim.Verdict);
        Assert.Equal(ReadOnlyLift.Edit, imposed.Lift(claim));
        Assert.False(imposed.Imposed);
        Assert.Equal(path, here.HeldPath);
        Assert.Equal(OpenGuard.ProbeState.Held, StateOf(path));

        // Own is as good as free: after a Save As this window already holds the
        // document it is showing, and the claim attempt trips over its own lock.
        var moved = new ImposedReadOnly();
        moved.Impose(readOnlyAlready: false);
        var own = OpenGuardDecision.Decide(here.Acquire(path, Me, hwnd: 1), Me, Live);
        Assert.Equal(OpenVerdict.Own, own.Verdict);
        Assert.Equal(ReadOnlyLift.Edit, moved.Lift(own));
        Assert.False(moved.Imposed);
        Assert.Equal(path, here.HeldPath);
    }

    [Fact]
    public void ANormalOpenAfterTheFallbackClearsIt()
    {
        // Read-only is window state. After the fallback, the next document opened
        // normally in this window must not land read-only with Save disabled and no
        // message: the open clears what the fallback imposed...
        var imposed = new ImposedReadOnly();
        imposed.Impose(readOnlyAlready: false);
        Assert.True(imposed.Clear());     // ...and the window turns read-only off
        Assert.False(imposed.Imposed);
        Assert.False(imposed.Clear());    // nothing imposed, nothing to lift

        // ...unless read-only was the user's own before the fallback (View ▸ Read
        // Only, --readonly): the fallback imposed nothing on top of it, so a later
        // open leaves it on. The un-check still goes through a claim meanwhile.
        var theirs = new ImposedReadOnly();
        theirs.Impose(readOnlyAlready: true);
        Assert.True(theirs.Imposed);
        Assert.Equal(ReadOnlyLift.StayReadOnly, theirs.Lift(new OpenGuardDecision(OpenVerdict.FocusOther, Other, 0x1234)));
        Assert.False(theirs.Clear());
        Assert.False(theirs.Imposed);

        // A failed un-check leaves the fallback's read-only imposed, and the next
        // normal open still clears it.
        var again = new ImposedReadOnly();
        again.Impose(readOnlyAlready: false);
        Assert.Equal(ReadOnlyLift.StayReadOnly, again.Lift(new OpenGuardDecision(OpenVerdict.FocusOther, Other, 0x1234)));
        Assert.True(again.Imposed);
        Assert.True(again.Clear());

        // Two fallbacks in a row (a second held file opened into the same window):
        // the window IS read-only when the second lands, but that is the first's
        // doing, not the user's, and a normal open after both must still clear it.
        var twice = new ImposedReadOnly();
        twice.Impose(readOnlyAlready: false);
        twice.Impose(readOnlyAlready: true);
        Assert.True(twice.Clear());
    }

    [Fact]
    public void TheRelaunchCarriesOnlyTheUsersOwnReadOnly()
    {
        // Apply-update, and the About dialog's update, restart this window on the
        // new exe with the document and its view flags. --readonly there reads to
        // the new process as the user's own choice: the fallback that then lands on
        // top of it records read-only as "already the user's", and every later
        // normal open in that window stays read-only, Save disabled, no message. So
        // the flag rides only when read-only IS the user's own; the new process
        // claims the document afresh and imposes its own read-only if it must.
        var doc = Doc("relaunch.md");
        var none = new ImposedReadOnly();
        Assert.Equal(new[] { doc }, MainWindow.RelaunchArguments(doc, readOnly: false, none, sourceMode: false));
        Assert.Equal(new[] { doc, "--readonly", "--source" }, MainWindow.RelaunchArguments(doc, readOnly: true, none, sourceMode: true));
        Assert.Equal(new[] { "--source" }, MainWindow.RelaunchArguments(null, readOnly: false, none, sourceMode: true));
        Assert.True(none.IsUsersOwn(readOnly: true));
        Assert.False(none.IsUsersOwn(readOnly: false));

        // The fallback's read-only is not carried.
        var imposed = new ImposedReadOnly();
        imposed.Impose(readOnlyAlready: false);
        Assert.False(imposed.IsUsersOwn(readOnly: true));
        Assert.Equal(new[] { doc, "--source" }, MainWindow.RelaunchArguments(doc, readOnly: true, imposed, sourceMode: true));
        Assert.Equal(new[] { doc }, MainWindow.RelaunchArguments(doc, readOnly: true, imposed, sourceMode: false));

        // ...unless read-only was the user's own before the fallback: then it is
        // theirs still, underneath, and the restart keeps it.
        var underneath = new ImposedReadOnly();
        underneath.Impose(readOnlyAlready: true);
        Assert.True(underneath.IsUsersOwn(readOnly: true));
        Assert.Equal(new[] { doc, "--readonly" }, MainWindow.RelaunchArguments(doc, readOnly: true, underneath, sourceMode: false));

        // Once the fallback's read-only is lifted (a successful claim) or cleared (a
        // normal open), a read-only the user chooses after is their own again.
        Assert.Equal(ReadOnlyLift.Edit, imposed.Lift(new OpenGuardDecision(OpenVerdict.Proceed, 0, 0)));
        Assert.True(imposed.IsUsersOwn(readOnly: true));
        var cleared = new ImposedReadOnly();
        cleared.Impose(readOnlyAlready: false);
        cleared.Clear();
        Assert.True(cleared.IsUsersOwn(readOnly: true));
    }

    [Fact]
    public void DocumentArgumentSkipsFlagValues()
    {
        // The startup parser's rule, shared by App.OnStartup and the window: the
        // first existing file that isn't the VALUE of --recover or --finish-move.
        // The latter's value is the downloaded exe, which very much exists.
        var exe = Doc("MarkdownMidget.exe");
        var doc = Doc("notes.md");
        File.WriteAllText(exe, "");
        File.WriteAllText(doc, "");

        Assert.Equal(doc, MainWindow.DocumentArgument(new[] { "--finish-move", exe, doc }));
        Assert.Equal(doc, MainWindow.DocumentArgument(new[] { "--readonly", doc, "--source" }));
        Assert.Equal(doc, MainWindow.DocumentArgument(new[] { "--recover", "abc", doc }));
        Assert.Null(MainWindow.DocumentArgument(new[] { "--recover", doc }));   // consumed as the id
        Assert.Null(MainWindow.DocumentArgument(new[] { "--new" }));
        Assert.Null(MainWindow.DocumentArgument(new[] { Doc("missing.md") }));
        Assert.Null(MainWindow.DocumentArgument(Array.Empty<string>()));
    }

    [Fact]
    public void DefaultDirectoryIsUnderLocalAppData()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MarkdownMidget", "open");
        Assert.Equal(expected, OpenGuard.DefaultDirectory);
    }
}
