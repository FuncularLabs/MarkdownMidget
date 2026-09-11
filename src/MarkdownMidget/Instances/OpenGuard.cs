using System;
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
/// exclusive open simply succeeds and the stale file is overwritten: liveness is
/// never inferred from a pid (ids get reused). DeleteOnClose removes the file on a
/// clean exit; a power cut leaves it behind, harmlessly, until the next open of
/// that document takes it over.
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

    public OpenGuard(string directory) => _dir = directory;

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MarkdownMidget", "open");

    /// <summary>The document this guard currently holds a claim on, as given to
    /// Acquire/Rekey; null when it holds nothing.</summary>
    public string? HeldPath => _heldPath;

    public string LockPathFor(string path) => Path.Combine(_dir, KeyFor(path) + ".lock");

    /// <summary>
    /// Try to claim <paramref name="path"/> for this window. On success any claim this
    /// guard already held is released first, because the window is about to show a
    /// different document. On failure the existing claim is kept: the open is not
    /// going ahead, so the window keeps showing (and guarding) what it has.
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
    /// Who holds <paramref name="path"/>, without claiming it. For App.OnStartup,
    /// which asks before any window exists so that a double-click on an already-open
    /// file never flashes a second window.
    /// </summary>
    public static Probe Peek(string directory, string path)
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
        catch (IOException ex) when (IsSharingViolation(ex)) { return ReadHolder(lockPath); }
        catch (Exception) { return new Probe(ProbeState.Unavailable, 0, 0); }
    }

    private Probe Take(string lockPath, int pid, long hwnd, string path, out FileStream? taken)
    {
        taken = null;
        try
        {
            Directory.CreateDirectory(_dir);
            // OpenOrCreate, not Create: a stale file from a power cut is reused, and
            // Create would fail on a read-only attribute that some backup or AV tools
            // set on files they've seen. FileShare.Read is the contract (see the
            // class comment) - a second window reads the holder out of this.
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
        catch (IOException ex) when (IsSharingViolation(ex)) { return ReadHolder(lockPath); }
        catch (Exception) { return new Probe(ProbeState.Unavailable, 0, 0); }
    }

    /// <summary>
    /// Read pid and hwnd out of a lock somebody else holds. Delete must be in the
    /// share mode: the holder opened the file DeleteOnClose, and Windows refuses any
    /// later open of such a file that doesn't share delete. Unreadable or
    /// unparseable content is reported as held by nobody in particular (0/0): the
    /// lock IS held, so the caller must not open a second editable copy, but there
    /// is no window to send them to.
    /// </summary>
    private static Probe ReadHolder(string lockPath)
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

    // ---- lifetime ----

    /// <summary>Let go of the claim. DeleteOnClose removes the file as the handle
    /// closes. Safe to call when nothing is held.</summary>
    public void Release()
    {
        try { _lock?.Dispose(); } catch { }
        _lock = null;
        _heldPath = null;
    }

    public void Dispose() => Release();
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
    public static OpenGuardDecision Decide(OpenGuard.Probe probe, int currentPid)
    {
        if (probe.State != OpenGuard.ProbeState.Held) return new(OpenVerdict.Proceed, 0, 0);
        if (probe.HolderPid == currentPid) return new(OpenVerdict.Own, probe.HolderPid, probe.HolderHwnd);
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
