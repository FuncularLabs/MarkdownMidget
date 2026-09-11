using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
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
    // Any pid that isn't ours. A held lock always belongs to a live process (the
    // kernel releases a dead one's), so the tests write the holder's pid themselves.
    private static readonly int Other = Environment.ProcessId + 1;

    private OpenGuard New()
    {
        var g = new OpenGuard(_dir);
        _cleanup.Add(g);
        return g;
    }

    private string Doc(string name) => Path.Combine(_dir, name);

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

        var decision = OpenGuardDecision.Decide(probe, Me);
        Assert.Equal(OpenVerdict.FocusOther, decision.Verdict);
        Assert.Equal(Other, decision.HolderPid);
        Assert.Equal(0x1234, decision.HolderHwnd);
    }

    // ---- W2 (decision level; the SetForegroundWindow hand-off itself is GUI) ----

    [Fact]
    public void FocusFailureOpensReadOnly()
    {
        var held = new OpenGuard.Probe(OpenGuard.ProbeState.Held, Other, 0);
        var decision = OpenGuardDecision.Decide(held, Me);
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
        Assert.Equal(OpenVerdict.Proceed, OpenGuardDecision.Decide(probe, Me).Verdict);
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
        Assert.Equal(OpenVerdict.Own, OpenGuardDecision.Decide(probe, Me).Verdict);
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

        // Held garbage is "someone has it, no idea who": held by pid 0, which the
        // decision turns into a FocusOther nobody can focus, so the caller falls
        // back to read-only rather than opening a second editable copy.
        using (var holder = new FileStream(lockPath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read))
        {
            holder.Write("garbage"u8);
            holder.Flush();
            var probe = New().Acquire(path, Me, hwnd: 1);
            Assert.Equal(OpenGuard.ProbeState.Held, probe.State);
            Assert.Equal(0, probe.HolderPid);
            Assert.Equal(0, probe.HolderHwnd);
            var decision = OpenGuardDecision.Decide(probe, Me);
            Assert.Equal(OpenVerdict.FocusOther, decision.Verdict);
            Assert.False(OpenGuard.TryFocusWindow(decision.HolderHwnd));
        }
    }

    [Fact]
    public void AcquireOfAnotherPathSwapsTheLock()
    {
        // OpenPathAsync's shape: the window opens a different file, so the one it
        // was showing is released as part of taking the new one.
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
        Assert.Equal(OpenVerdict.FocusOther, OpenGuardDecision.Decide(probe, Me).Verdict);
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
        Assert.Equal(OpenVerdict.Proceed, OpenGuardDecision.Decide(probe, Me).Verdict);
        Assert.Null(guard.HeldPath);
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
