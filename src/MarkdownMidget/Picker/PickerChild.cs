using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MarkdownMidget.Picker;

/// <summary>
/// The other side of the out-of-process file dialog: this exe, launched with
/// --pick-open / --pick-save, shows the NATIVE Windows dialog, prints the chosen
/// path to stdout and exits. Nothing else is loaded — no editor, no WebView2,
/// no document — so when a faulty shell extension faults inside the dialog, the
/// only casualty is this throwaway process.
///
/// Contract with <see cref="FilePickerService"/>:
///   exit 0 + a path on stdout   the user chose it
///   exit 2, no output           the user cancelled
///   anything else               treated as a crash → built-in picker
/// </summary>
internal static class PickerChild
{
    public const string OpenSwitch = "--pick-open";
    public const string SaveSwitch = "--pick-save";

    /// <summary>True when these args mean "be a file picker, not an editor".</summary>
    public static bool IsPickerInvocation(IReadOnlyList<string> args)
    {
        foreach (var a in args)
            if (a is OpenSwitch or SaveSwitch) return true;
        return false;
    }

    /// <summary>
    /// Rebuild the request from the command line. Unknown switches are ignored
    /// rather than fatal: the parent and child are always the same build, so a
    /// mismatch can only come from someone running this by hand.
    /// </summary>
    public static FilePickerRequest Parse(IReadOnlyList<string> args)
    {
        var save = false;
        string? title = null, filter = null, dir = null, name = null, ext = null;
        var filterIndex = 1;
        var checkExists = false;
        long owner = 0;

        for (var i = 0; i < args.Count; i++)
        {
            string? Next() => i + 1 < args.Count ? args[++i] : null;
            switch (args[i])
            {
                case SaveSwitch: save = true; break;
                case OpenSwitch: save = false; break;
                case "--title": title = Next(); break;
                case "--filter": filter = Next(); break;
                case "--dir": dir = Next(); break;
                case "--name": name = Next(); break;
                case "--default-ext": ext = Next(); break;
                case "--check-exists": checkExists = true; break;
                case "--filter-index":
                    if (int.TryParse(Next(), out var parsed) && parsed > 0) filterIndex = parsed;
                    break;
                case "--owner":
                    if (long.TryParse(Next(), out var handle)) owner = handle;
                    break;
            }
        }

        return new FilePickerRequest
        {
            Save = save,
            Title = title,
            Filter = filter ?? "",
            FilterIndex = filterIndex,
            InitialDirectory = dir,
            FileName = name,
            DefaultExt = ext,
            CheckFileExists = checkExists,
            OwnerHandle = owner,
        };
    }

    /// <summary>Show the native dialog and return the process exit code, writing
    /// any chosen path to stdout.</summary>
    public static int Run(IReadOnlyList<string> args)
    {
        var request = Parse(args);
        // An invisible anchor window (fully transparent, never off-screen) owned
        // by the PARENT's HWND. The dialog is then shown owned by the anchor,
        // which gives the cross-process owner chain a real modal dialog has:
        // correct z-order over the editor, proper activation, and focus handed
        // back on close. Without it the dialog is a stray top-level that Windows
        // may leave behind the parent - which, with the parent disabled and
        // waiting, looks exactly like a hang.
        Window? anchor = null;
        if (request.OwnerHandle != 0)
        {
            try
            {
                anchor = new Window
                {
                    WindowStyle = WindowStyle.None,
                    ShowInTaskbar = false, ShowActivated = false,
                    AllowsTransparency = true, Background = System.Windows.Media.Brushes.Transparent,
                    Opacity = 0,
                    // The fallback shape, used when the parent's rectangle is
                    // unusable (minimised): the whole work area. NOT a 1x1 window -
                    // the dialog puts its top-left at its owner's top-left, so a
                    // tiny anchor anywhere but the top-left corner clips the
                    // dialog's buttons off the bottom-right.
                    Left = SystemParameters.WorkArea.Left,
                    Top = SystemParameters.WorkArea.Top,
                    Width = SystemParameters.WorkArea.Width,
                    Height = SystemParameters.WorkArea.Height,
                };
                var parent = new IntPtr(request.OwnerHandle);
                new WindowInteropHelper(anchor).Owner = parent;
                anchor.Show();
                // MEASURED (not assumed): the dialog places its top-left at its
                // OWNER's top-left, so the anchor is given the parent's whole
                // rectangle and the dialog opens over the editor. A 1x1 anchor at
                // the parent's centre - the obvious guess - put the dialog's
                // CORNER there and pushed it down-right off the window. Physical
                // pixels via SetWindowPos, which sidesteps WPF's DIP conversion on
                // scaled displays.
                var anchorHandle = new WindowInteropHelper(anchor).Handle;
                // A minimised parent reports a rectangle off in the -32000s, which
                // is exactly the off-screen placement this exists to avoid, so
                // leave the anchor where it was created: over the work area.
                if (anchorHandle != IntPtr.Zero && !IsIconic(parent) && GetWindowRect(parent, out var r)
                    && r.Left > -30000 && r.Top > -30000)
                    SetWindowPos(anchorHandle, IntPtr.Zero,
                        r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top,
                        SwpNoZOrder | SwpNoActivate);
            }
            catch
            {
                // A parent that died between spawn and here, or any interop
                // refusal: fall back to an unowned dialog rather than no dialog.
                try { anchor?.Close(); } catch { }
                anchor = null;
            }
        }
        try
        {
            string? path;
            if (request.Save)
            {
                var dlg = new Microsoft.Win32.SaveFileDialog();
                FilePickerService.Apply(dlg, request);
                path = Show(dlg, anchor) ? dlg.FileName : null;
            }
            else
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { CheckFileExists = request.CheckFileExists };
                FilePickerService.Apply(dlg, request);
                path = Show(dlg, anchor) ? dlg.FileName : null;
            }

            if (path is null) return FilePickerService.CancelledExitCode;
            Console.Out.Write(path);
            Console.Out.Flush();
            return 0;
        }
        finally { try { anchor?.Close(); } catch { } }
    }

    private static bool Show(Microsoft.Win32.CommonDialog dlg, Window? anchor) =>
        (anchor is null ? dlg.ShowDialog() : dlg.ShowDialog(anchor)) == true;

    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter,
        int x, int y, int cx, int cy, uint flags);
}
