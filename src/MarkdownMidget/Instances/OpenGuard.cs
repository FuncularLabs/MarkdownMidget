using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace MarkdownMidget.Instances;

/// <summary>
/// Answers "is this file already open in another Markdown Midget window?" and, if
/// not, claims it for this one. Every window is its own process (File ▸ New spawns
/// one, an Explorer double-click launches one), so the claim has to be something
/// the kernel keeps for us: one lock file per document under
/// %LocalAppData%\MarkdownMidget\open\, named by a hash of the normalised path and
/// held open for as long as the window shows that document.
///
/// The backup store's lock pattern, with one deliberate difference. Its locks are
/// FileShare.None because nobody needs to read them; these are FileShare.Read
/// because the whole point is that a SECOND process can read the holder's process
/// id and window handle out of a lock it failed to take, and go and focus that
/// window. The holder writes "pid\nhwnd\nfullpath\n" as soon as it has the handle.
///
/// A dead holder's lock is released by the kernel however the process died, so the
/// exclusive open simply succeeds and the stale file is overwritten. DeleteOnClose
/// removes the file on a clean exit; a power cut leaves it behind, harmlessly,
/// until the next open of that document takes it over. A sharing violation only
/// proves that SOME process has the file open, though: a backup or AV tool can sit
/// on a stale one, and the pid and handle inside then belong to a dead session (the
/// handle may since name an unrelated window). So before a holder is honoured,
/// OpenGuardDecision asks HolderIsLive - the recorded pid must be running and the
/// recorded window must belong to it - and treats anything else as stale.
///
/// This is the minimal form of the cross-instance registry the roadmap wants: the
/// same files, read by a Window menu later, list every open document.
/// </summary>
internal sealed class OpenGuard : IDisposable
{
    internal enum ProbeState
    {
        /// <summary>From Acquire: taken, and now held by this guard. From Peek:
        /// nobody holds it.</summary>
        Free,
        /// <summary>Somebody holds it; HolderPid/HolderHwnd say who, or are 0
        /// when the lock's content couldn't be read or parsed.</summary>
        Held,
        /// <summary>The guard itself couldn't run (its directory can't be
        /// created, for instance). Treated as Free by the decision: the guard is
        /// a convenience, and refusing to open documents because a folder under
        /// AppData is unwritable would be a worse failure than the one it prevents.</summary>
        Unavailable,
    }

    internal readonly record struct Probe(ProbeState State, int HolderPid, long HolderHwnd);

    private readonly string _dir;
    private FileStream? _lock;
    private string? _heldPath;
    // An open in progress (Begin): the new document's claim, held on a second handle
    // beside the current one until Commit or Abandon says which of the two survives.
    private FileStream? _pending;
    private string? _pendingPath;
    // The pause before a 0/0 read's second look (see ReadHolder). Injected so a test
    // can put the holder's write exactly there; the window uses the real one.
    private readonly Action<int> _wait;

    public OpenGuard(string directory, Action<int>? wait = null)
    {
        _dir = directory;
        _wait = wait ?? DefaultWait;
    }

    /// <summary>
    /// How long a reader that found a held lock empty gives its holder to finish
    /// writing before the second look. Take opens the lock and then writes into it,
    /// so a reader can land in between and see 0/0 for a window that is a few
    /// microseconds from saying who it is; ~20 ms covers that write and flush
    /// many times over, and is paid only by such reads and by reads of a lock a
    /// backup tool sits on.
    /// </summary>
    internal const int HolderWriteGraceMs = 20;

    private static readonly Action<int> DefaultWait = Thread.Sleep;

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MarkdownMidget", "open");

    /// <summary>The document this guard currently holds a claim on, as given to
    /// Acquire/Rekey; null when it holds nothing.</summary>
    public string? HeldPath => _heldPath;

    public string LockPathFor(string path) => Path.Combine(_dir, KeyFor(path) + ".lock");

    /// <summary>
    /// Try to claim <paramref name="path"/> for this window, at once. On success any
    /// claim this guard already held is released, because the window now shows a
    /// different document; on failure the existing claim is kept. For the switches
    /// that are already a fact when they are made: Rekey after a write, and the
    /// read-only lift, whose target is the document already on screen. An open that
    /// has a read and a password prompt still ahead of it uses Begin/Commit instead.
    /// </summary>
    public Probe Acquire(string path, int pid, long hwnd)
    {
        var probe = Take(LockPathFor(path), pid, hwnd, path, out var taken);
        if (probe.State == ProbeState.Free)
        {
            Release();
            _lock = taken;
            _heldPath = path;
        }
        return probe;
    }

    /// <summary>
    /// The document moved (Save As, Encrypt, Convert to Unencrypted): let go of the
    /// old path and claim the new one. Released FIRST, unconditionally, because the
    /// write to the new path has already happened by the time this is called; if
    /// another window holds the new path, this window simply ends up unguarded there,
    /// which is honest - the old path is certainly no longer this window's.
    /// </summary>
    public Probe Rekey(string path, int pid, long hwnd)
    {
        Release();
        return Acquire(path, pid, hwnd);
    }

    /// <summary>
    /// The first half of an open. Claims <paramref name="path"/> on a second handle
    /// and leaves the current claim exactly as it is: the window goes on showing -
    /// and guarding - the old document through the read and the password prompt,
    /// until Commit says the new one is on screen. Abandon (a cancelled prompt, a
    /// failed read) lets the new handle go, and the old claim was never released.
    /// On a Held or Unavailable answer nothing is pending; Commit then decides what
    /// becomes of the old claim. Only one open can be pending: a second Begin
    /// while the first is still in flight (Ctrl+O and the Alt menu are not gated
    /// by the busy overlay) displaces the first's pending claim - or its handle
    /// would leak, and its path read as held until the collector closed it - and
    /// the first open then commits its document unguarded, or abandons only what
    /// is still its own (the path forms of Commit and Abandon).
    /// </summary>
    public Probe Begin(string path, int pid, long hwnd)
    {
        Abandon();
        var probe = Take(LockPathFor(path), pid, hwnd, path, out var taken);
        if (probe.State == ProbeState.Free)
        {
            _pending = taken;
            _pendingPath = path;
        }
        return probe;
    }

    /// <summary>
    /// The document at <paramref name="path"/> is on screen now. A pending claim on
    /// that path replaces the current one. Without one, either Begin found the path
    /// held - by this guard (the same document opened again) the claim simply
    /// stays; by anyone else, or with the guard unable to answer, the old document
    /// is no longer what this window shows, so its claim is released and the new
    /// one is shown unguarded - or a later open displaced this one's pending claim
    /// (see Begin). That open's claim is not this document's and is left pending
    /// for it; this document is shown unguarded, as after a Begin that found it
    /// held, and the old claim goes the same way.
    /// </summary>
    public void Commit(string path)
    {
        if (_pendingPath is not null && SamePath(_pendingPath, path))
        {
            Release();
            _lock = _pending;
            _heldPath = _pendingPath;
            _pending = null;
            _pendingPath = null;
        }
        else if (_heldPath is null || !SamePath(_heldPath, path))
        {
            Release();
        }
    }

    /// <summary>The open didn't happen: let the pending claim go, whatever it is on.
    /// The current claim was never touched. Safe to call when nothing is pending.
    /// For Begin (the previous pending claim is displaced) and Dispose; an open that
    /// failed uses the path form, so that it lets go of its own claim only.</summary>
    public void Abandon()
    {
        try { _pending?.Dispose(); } catch { }
        _pending = null;
        _pendingPath = null;
    }

    /// <summary>The open of <paramref name="path"/> didn't happen: let its pending
    /// claim go. A pending claim on another path is a later open still in flight
    /// (see Begin), and is left for that open to commit or abandon. Safe to call
    /// when nothing is pending.</summary>
    public void Abandon(string path)
    {
        if (_pendingPath is not null && SamePath(_pendingPath, path)) Abandon();
    }

    /// <summary>
    /// Who holds <paramref name="path"/>, without claiming it. For App.OnStartup,
    /// which asks before any window exists so that a double-click on an already-open
    /// file never flashes a second window. <paramref name="wait"/> is the pause
    /// before a 0/0 read's second look (see ReadHolder); tests inject theirs.
    /// </summary>
    public static Probe Peek(string directory, string path, Action<int>? wait = null)
    {
        var lockPath = Path.Combine(directory, KeyFor(path) + ".lock");
        if (!File.Exists(lockPath)) return new Probe(ProbeState.Free, 0, 0);
        try
        {
            // Same access and share as a real claim, minus create and DeleteOnClose:
            // it conflicts with a holder exactly the way Acquire would, and opens a
            // stale file harmlessly (nothing is written, the file is left for the
            // next Acquire to take over).
            using var _ = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            return new Probe(ProbeState.Free, 0, 0);
        }
        catch (FileNotFoundException) { return new Probe(ProbeState.Free, 0, 0); }
        catch (IOException ex) when (IsSharingViolation(ex)) { return ReadHolder(lockPath, wait ?? DefaultWait); }
        catch (Exception) { return new Probe(ProbeState.Unavailable, 0, 0); }
    }

    private Probe Take(string lockPath, int pid, long hwnd, string path, out FileStream? taken)
    {
        taken = null;
        try
        {
            Directory.CreateDirectory(_dir);
            // OpenOrCreate: a stale file from a power cut is reused (and truncated
            // below) rather than being an obstacle. FileShare.Read is the contract
            // (see the class comment) - a second window reads the holder out of this.
            var fs = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                                    FileShare.Read, 1, FileOptions.DeleteOnClose);
            try
            {
                fs.SetLength(0);   // whatever the stale file said, this window is the holder now
                var bytes = Encoding.UTF8.GetBytes($"{pid}\n{hwnd}\n{Normalize(path)}\n");
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush();
            }
            catch
            {
                fs.Dispose();
                throw;
            }
            taken = fs;
            return new Probe(ProbeState.Free, 0, 0);
        }
        catch (IOException ex) when (IsSharingViolation(ex)) { return ReadHolder(lockPath, _wait); }
        catch (Exception) { return new Probe(ProbeState.Unavailable, 0, 0); }
    }

    /// <summary>
    /// Read pid and hwnd out of a lock somebody else holds. Unreadable or
    /// unparseable content is reported as held by nobody in particular (0/0), which
    /// HolderIsLive refuses outright: the lock IS held, but by nothing that can be
    /// shown to be a window of ours, and there is no window to send them to. But
    /// the holder writes AFTER it opens (Take), and a reader can land in that gap -
    /// or an AV scanner's exclusive open right after the create can make the
    /// holder's own write fail for the moment - so a first look that yields 0/0
    /// waits HolderWriteGraceMs and looks once more before the holder is refused. A
    /// second 0/0 stands: the guard does not sit polling a lock a backup tool holds.
    /// </summary>
    private static Probe ReadHolder(string lockPath, Action<int> wait)
    {
        var probe = ReadHolderOnce(lockPath);
        if (probe.HolderPid == 0 && probe.HolderHwnd == 0)
        {
            wait(HolderWriteGraceMs);
            probe = ReadHolderOnce(lockPath);
        }
        return probe;
    }

    /// <summary>One look. Delete must be in the share mode: the holder opened the
    /// file DeleteOnClose, and Windows refuses any later open of such a file that
    /// doesn't share delete.</summary>
    private static Probe ReadHolderOnce(string lockPath)
    {
        try
        {
            using var fs = new FileStream(lockPath, FileMode.Open, FileAccess.Read,
                                          FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            var text = reader.ReadToEnd();
            var lines = text.Split('\n');
            if (lines.Length >= 2
                && int.TryParse(lines[0].Trim(), out var pid)
                && long.TryParse(lines[1].Trim(), out var hwnd))
                return new Probe(ProbeState.Held, pid, hwnd);
        }
        catch { /* held, by someone we can't identify */ }
        return new Probe(ProbeState.Held, 0, 0);
    }

    private static bool IsSharingViolation(IOException ex) =>
        (ex.HResult & 0xFFFF) == 32;   // ERROR_SHARING_VIOLATION

    // ---- path identity ----

    /// <summary>
    /// The lock-file name for a path: SHA-256 hex of the normalised, case-folded
    /// path. Two spellings of one file must land on one lock, or the guard is a
    /// coin toss: Explorer hands us long names, the command line whatever the user
    /// typed, Open Recent whatever we saved.
    /// </summary>
    public static string KeyFor(string path) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(path).ToUpperInvariant())));

    /// <summary>The same document, by KeyFor's identity rather than by spelling.</summary>
    private static bool SamePath(string a, string b) =>
        string.Equals(KeyFor(a), KeyFor(b), StringComparison.Ordinal);

    /// <summary>
    /// Full path, relative segments resolved, trailing separators trimmed, and 8.3
    /// short names expanded when the file exists (GetLongPathNameW needs to walk the
    /// real directories, so a path to nothing stays as it is). Casing is left as
    /// found - KeyFor folds it; this form is what the lock file records.
    /// </summary>
    internal static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? string.Empty;
        // Only below the root: "C:\" trimmed to "C:" would mean the current directory
        // on C:, a different thing entirely. No document lives at a root anyway.
        if (full.Length > root.Length)
            full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return ExpandShortName(full) ?? full;
    }

    private static string? ExpandShortName(string full)
    {
        try
        {
            var buffer = new char[1024];
            var n = GetLongPathNameW(full, buffer, (uint)buffer.Length);
            if (n > buffer.Length)
            {
                buffer = new char[n];
                n = GetLongPathNameW(full, buffer, (uint)buffer.Length);
            }
            return n == 0 || n > buffer.Length ? null : new string(buffer, 0, (int)n);
        }
        catch { return null; }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathNameW(string lpszShortPath, [Out] char[] lpszLongPath, uint cchBuffer);

    // ---- focusing the holder ----

    /// <summary>
    /// Bring the holder's window to the front. False when there is no usable handle
    /// (0 is what a holder records if it acquired before its window was initialised),
    /// when the handle no longer names a window, or when Windows declines. A
    /// minimized holder is restored first: SetForegroundWindow alone would report
    /// success while the window stayed in the taskbar, and to the user that reads as
    /// "nothing happened".
    /// </summary>
    public static bool TryFocusWindow(long hwnd)
    {
        if (hwnd == 0) return false;
        var h = (IntPtr)hwnd;
        try
        {
            if (!IsWindow(h)) return false;
            if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
            return SetForegroundWindow(h);
        }
        catch { return false; }
    }

    private const int SW_RESTORE = 9;

    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);

    // ---- is the holder one of ours? ----

    /// <summary>
    /// Is the holder a lock file names a live window of ours? True only when the
    /// recorded pid is running AND the recorded window belongs to that pid. A
    /// sharing violation proves that some process has the lock open, not which: a
    /// backup or AV tool holding a stale file would otherwise send the user to a
    /// dead session's pid and a handle that may since have been recycled to an
    /// unrelated window. The decision treats a holder this refuses as stale.
    /// </summary>
    public static bool HolderIsLive(int pid, long hwnd) =>
        HolderIsLive(pid, hwnd, ProcessIsRunning, WindowOwnerPid);

    /// <summary>The pure form, with the two probes injected. 0/0 is refused before
    /// either is asked: it is what unreadable content reads as, and
    /// GetWindowThreadProcessId fails on handle 0 leaving the owner at 0, which
    /// would "match" pid 0.</summary>
    internal static bool HolderIsLive(int pid, long hwnd, Func<int, bool> processIsRunning, Func<long, int> windowOwnerPid)
    {
        if (pid <= 0 || hwnd == 0) return false;
        if (!processIsRunning(pid)) return false;
        return windowOwnerPid(hwnd) == pid;
    }

    /// <summary>GetProcessById throws for a pid that isn't running; HasExited can
    /// throw for one that can't be queried. Neither is a window we can send the
    /// user to, so both read as not live.</summary>
    private static bool ProcessIsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch { return false; }
    }

    private static int WindowOwnerPid(long hwnd)
    {
        try
        {
            GetWindowThreadProcessId((IntPtr)hwnd, out var owner);
            return (int)owner;
        }
        catch { return 0; }
    }

    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    // ---- lifetime ----

    /// <summary>Let go of the claim. DeleteOnClose removes the file as the handle
    /// closes. Safe to call when nothing is held. A pending Begin is not touched:
    /// Commit moves it into place, Abandon or Dispose lets it go.</summary>
    public void Release()
    {
        try { _lock?.Dispose(); } catch { }
        _lock = null;
        _heldPath = null;
    }

    public void Dispose()
    {
        Abandon();
        Release();
    }
}

/// <summary>What OpenPathAsync should do with a probe.</summary>
internal enum OpenVerdict
{
    /// <summary>The claim is ours (or the guard couldn't run): open the file.</summary>
    Proceed,
    /// <summary>Held by THIS process - the window re-opening its own document.
    /// Never a block.</summary>
    Own,
    /// <summary>Held by another window: bring it forward instead of opening.</summary>
    FocusOther,
}

/// <summary>What the window does once the focus attempt has an answer.</summary>
internal enum OpenFallback
{
    /// <summary>The other window is in front; this one keeps whatever it was showing.</summary>
    Yield,
    /// <summary>The other window is in front and this one was only launched to
    /// open that file: go away without a trace.</summary>
    Exit,
    /// <summary>Nothing could be focused: open here, read-only, and say why.</summary>
    OpenReadOnly,
}

/// <summary>
/// The pure half of the guard, kept apart from the file handling so it can be
/// tested without a lock directory: a probe and the current process id in, a
/// verdict out.
/// </summary>
internal readonly record struct OpenGuardDecision(OpenVerdict Verdict, int HolderPid, long HolderHwnd)
{
    /// <summary>
    /// <paramref name="holderIsLive"/> is asked about a holder that isn't this
    /// process before it is honoured; a holder it refuses is treated as a stale lock
    /// and the open proceeds - unguarded, since the claim itself failed, which is
    /// the guard-as-convenience rule again. OpenGuard.HolderIsLive is the real
    /// check; tests inject their own.
    /// </summary>
    public static OpenGuardDecision Decide(OpenGuard.Probe probe, int currentPid, Func<int, long, bool> holderIsLive)
    {
        if (probe.State != OpenGuard.ProbeState.Held) return new(OpenVerdict.Proceed, 0, 0);
        if (probe.HolderPid == currentPid) return new(OpenVerdict.Own, probe.HolderPid, probe.HolderHwnd);
        if (!holderIsLive(probe.HolderPid, probe.HolderHwnd)) return new(OpenVerdict.Proceed, 0, 0);
        return new(OpenVerdict.FocusOther, probe.HolderPid, probe.HolderHwnd);
    }

    /// <summary>After a FocusOther verdict. <paramref name="startup"/> is true when
    /// this process was launched to open the file and has nothing else to show.</summary>
    public static OpenFallback AfterFocus(bool focused, bool startup)
    {
        if (!focused) return OpenFallback.OpenReadOnly;
        return startup ? OpenFallback.Exit : OpenFallback.Yield;
    }
}

/// <summary>What View ▸ Read Only turning OFF does.</summary>
internal enum ReadOnlyLift
{
    /// <summary>Editable: the read-only state was the user's own, or the document is
    /// now this window's (claimed just now, or held already) or nobody's.</summary>
    Edit,
    /// <summary>Still open in another live window: the checkbox goes back and that
    /// window is the one to bring forward.</summary>
    StayReadOnly,
}

/// <summary>
/// The read-only state the already-open fallback imposes, kept apart from the
/// read-only the user chooses (View ▸ Read Only, --readonly). The fallback shows a
/// document another window holds, with no claim of its own, so turning read-only
/// off again is not a checkbox but a claim attempt: only a claim that succeeds (or
/// finds the document already this window's) may edit, or the un-check would hand
/// out an unguarded editor and a double-click on the file a second one. And
/// read-only is window state, so the next document opened here normally must not
/// inherit what was imposed for the last one. Pure: the window feeds it the claim
/// result and applies what it says.
/// </summary>
internal sealed class ImposedReadOnly
{
    private bool _imposed;
    private bool _wasReadOnly;

    /// <summary>True from the fallback until a lift or a normal open.</summary>
    public bool Imposed => _imposed;

    /// <summary>The fallback has landed. <paramref name="readOnlyAlready"/>: the window
    /// was read-only before it, which a later open must not undo on the fallback's
    /// behalf if that was the user's own choice. A second fallback on top of a first
    /// keeps the first's answer: read-only "already" then is the first's doing.</summary>
    public void Impose(bool readOnlyAlready)
    {
        if (!_imposed) _wasReadOnly = readOnlyAlready;
        _imposed = true;
    }

    /// <summary>A normal open has landed, or the claim was regained. Returns true when
    /// the window must turn read-only OFF: it was imposed, and it was not the user's
    /// own beforehand.</summary>
    public bool Clear()
    {
        var lift = _imposed && !_wasReadOnly;
        _imposed = false;
        _wasReadOnly = false;
        return lift;
    }

    /// <summary>The user turned Read Only off. <paramref name="claim"/> is a fresh claim
    /// on the document this window shows; it is only consulted while something is
    /// imposed. FocusOther keeps read-only and leaves it imposed, so the next un-check
    /// asks again; anything else lifts it.</summary>
    public ReadOnlyLift Lift(OpenGuardDecision claim)
    {
        if (!_imposed) return ReadOnlyLift.Edit;
        if (claim.Verdict == OpenVerdict.FocusOther) return ReadOnlyLift.StayReadOnly;
        Clear();
        return ReadOnlyLift.Edit;
    }
}
