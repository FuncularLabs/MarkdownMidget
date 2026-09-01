using System;
using System.Collections.Generic;

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
        };
    }

    /// <summary>Show the native dialog and return the process exit code, writing
    /// any chosen path to stdout.</summary>
    public static int Run(IReadOnlyList<string> args)
    {
        var request = Parse(args);
        string? path;
        if (request.Save)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog();
            FilePickerService.Apply(dlg, request);
            path = dlg.ShowDialog() == true ? dlg.FileName : null;
        }
        else
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { CheckFileExists = request.CheckFileExists };
            FilePickerService.Apply(dlg, request);
            path = dlg.ShowDialog() == true ? dlg.FileName : null;
        }

        if (path is null) return FilePickerService.CancelledExitCode;
        Console.Out.Write(path);
        Console.Out.Flush();
        return 0;
    }
}
