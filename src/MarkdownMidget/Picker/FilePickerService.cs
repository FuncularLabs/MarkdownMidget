using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace MarkdownMidget.Picker;

/// <summary>
/// The one place the app asks for a file path, and the strategy behind it
/// (docs/plans/secure-markdown.md, Part B).
///
/// Windows' file dialog loads third-party SHELL EXTENSIONS — thumbnail,
/// preview and context-menu handlers — into whatever process shows it. A
/// faulty one raises an access violation, which .NET cannot catch: the process
/// dies outright, no handler runs, nothing is logged. Markdown Monster had the
/// same symptom for the same reason. So:
///
///   1. The native dialog runs in a SHORT-LIVED CHILD of this same exe. A
///      shell extension that faults kills only the child; the editor and the
///      open document survive.
///   2. If the child dies without a result, the built-in managed picker takes
///      over immediately — isolation alone would leave the user unable to open
///      or save anything, which is why the two halves ship together.
///   3. That crash also turns the "always use the built-in picker" setting ON,
///      so a machine with a bad shell extension pays the cost once rather than
///      once per dialog.
///
/// The setting is also directly switchable in Settings, which is how the
/// built-in picker gets exercised without a broken shell.
/// </summary>
internal static class FilePickerService
{
    /// <summary>Use the built-in picker outright, skipping the native attempt.</summary>
    public static bool UseBuiltIn { get; set; }

    /// <summary>
    /// Raised when a native-dialog crash flips <see cref="UseBuiltIn"/> on. The
    /// host persists the setting and tells the user; this class deliberately
    /// knows nothing about settings files or message boxes.
    /// </summary>
    public static Action? AutoSwitchedToBuiltIn { get; set; }

    /// <summary>Child-process exit code meaning "the user cancelled" — distinct
    /// from every failure code so a cancel is never mistaken for a crash.</summary>
    public const int CancelledExitCode = 2;

    /// <summary>
    /// Show a picker and return the chosen path, or null if the user cancelled.
    /// Synchronous, like the dialog it replaces.
    /// </summary>
    public static string? Show(Window owner, FilePickerRequest request)
    {
        if (UseBuiltIn) return ShowBuiltIn(owner, request);

        switch (TryNativeOutOfProcess(owner, request))
        {
            case { Outcome: NativeOutcome.Chose } r:
                return r.Path;
            case { Outcome: NativeOutcome.Cancelled }:
                return null;
            case { Outcome: NativeOutcome.Crashed }:
                // The isolation worked: only the child died. Switch permanently,
                // say so once, and finish the job the user actually asked for.
                UseBuiltIn = true;
                try { AutoSwitchedToBuiltIn?.Invoke(); } catch { /* never let the notice break the pick */ }
                MessageBox.Show(owner,
                    "Windows' file picker closed unexpectedly. That is almost always a faulty " +
                    "Explorer add-on (a preview or thumbnail handler), not Markdown Midget — and " +
                    "it can't crash the app from here.\n\n" +
                    "Markdown Midget has switched to its own built-in file picker, which doesn't " +
                    "load those add-ons. You can switch back in Edit ▸ Settings.",
                    "Markdown Midget", MessageBoxButton.OK, MessageBoxImage.Information);
                return ShowBuiltIn(owner, request);
            default:
                // The child could not be started at all (no process path, policy,
                // AV). Fall back to the in-process native dialog: that is exactly
                // what every release before this one did, so it can't be a
                // regression, and it keeps a working picker on machines where
                // spawning is blocked.
                return ShowNativeInProcess(owner, request);
        }
    }

    private static string? ShowBuiltIn(Window owner, FilePickerRequest request)
    {
        var dlg = new MidgetFilePicker(request) { Owner = owner };
        return dlg.ShowDialog() == true ? dlg.SelectedPath : null;
    }

    private enum NativeOutcome { Chose, Cancelled, Crashed, CouldNotStart }

    private readonly record struct NativeResult(NativeOutcome Outcome, string? Path);

    private static NativeResult TryNativeOutOfProcess(Window owner, FilePickerRequest request)
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return new(NativeOutcome.CouldNotStart, null);

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(request.Save ? "--pick-save" : "--pick-open");
        void Arg(string name, string? value)
        {
            if (string.IsNullOrEmpty(value)) return;
            psi.ArgumentList.Add(name);
            psi.ArgumentList.Add(value);
        }
        Arg("--filter", request.Filter);
        Arg("--filter-index", request.FilterIndex.ToString());
        Arg("--dir", request.InitialDirectory);
        Arg("--name", request.FileName);
        Arg("--default-ext", request.DefaultExt);
        Arg("--title", request.Title);
        if (request.CheckFileExists) psi.ArgumentList.Add("--check-exists");

        Process? child;
        try { child = Process.Start(psi); }
        catch { return new(NativeOutcome.CouldNotStart, null); }
        if (child is null) return new(NativeOutcome.CouldNotStart, null);

        string output;
        using (child)
        {
            // Read stdout on a background thread: a child that outgrows the pipe
            // buffer while we wait for exit would deadlock, and the classic
            // ordering trap (WaitForExit then read) is exactly that bug.
            var reader = child.StandardOutput.ReadToEndAsync();
            WaitWhilePumping(owner, child);
            output = reader.GetAwaiter().GetResult();
            var code = child.ExitCode;

            if (code == CancelledExitCode) return new(NativeOutcome.Cancelled, null);
            var path = output.Trim();
            if (code == 0 && path.Length > 0) return new(NativeOutcome.Chose, path);
            // Exit 0 with nothing, or any other code: the dialog never gave us a
            // result. An access violation in a shell extension lands here.
            return new(NativeOutcome.Crashed, null);
        }
    }

    /// <summary>
    /// Wait for the child while keeping this window alive and repainting, and
    /// disabled so the wait is genuinely modal — the same contract ShowDialog
    /// has. A nested dispatcher frame is how WPF's own modal dialogs do it;
    /// blocking the UI thread on WaitForExit instead would freeze and smear the
    /// editor behind the picker.
    /// </summary>
    private static void WaitWhilePumping(Window owner, Process child)
    {
        var wasEnabled = owner.IsEnabled;
        owner.IsEnabled = false;
        try
        {
            var frame = new DispatcherFrame();
            child.EnableRaisingEvents = true;
            child.Exited += (_, _) => frame.Continue = false;
            if (child.HasExited) frame.Continue = false;
            Dispatcher.PushFrame(frame);
            // Exited fires on a pool thread as the process ends; WaitForExit with
            // no argument also flushes the async stdout readers to completion.
            child.WaitForExit();
        }
        finally { owner.IsEnabled = wasEnabled; }
    }

    private static string? ShowNativeInProcess(Window owner, FilePickerRequest request)
    {
        if (request.Save)
        {
            var save = new Microsoft.Win32.SaveFileDialog();
            Apply(save, request);
            return save.ShowDialog(owner) == true ? save.FileName : null;
        }
        var open = new Microsoft.Win32.OpenFileDialog { CheckFileExists = request.CheckFileExists };
        Apply(open, request);
        return open.ShowDialog(owner) == true ? open.FileName : null;
    }

    /// <summary>Shared configuration for the native dialog — used by the
    /// in-process fallback here AND by the child process, so the two can never
    /// drift into offering different filters.</summary>
    internal static void Apply(Microsoft.Win32.FileDialog dlg, FilePickerRequest request)
    {
        if (!string.IsNullOrEmpty(request.Title)) dlg.Title = request.Title;
        if (!string.IsNullOrEmpty(request.Filter)) dlg.Filter = request.Filter;
        if (request.FilterIndex > 0) dlg.FilterIndex = request.FilterIndex;
        if (!string.IsNullOrEmpty(request.DefaultExt)) dlg.DefaultExt = request.DefaultExt;
        if (!string.IsNullOrEmpty(request.FileName)) dlg.FileName = request.FileName;
        // Only a folder that exists: a dangling InitialDirectory makes the shell
        // fall back to somewhere unpredictable, and is one more reason for it to
        // instantiate handlers for files nobody asked about.
        if (!string.IsNullOrEmpty(request.InitialDirectory) && Directory.Exists(request.InitialDirectory))
            dlg.InitialDirectory = request.InitialDirectory;
    }
}
