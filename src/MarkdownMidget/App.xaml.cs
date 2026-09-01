using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace MarkdownMidget;

/// <summary>
/// Interaction logic for App.xaml — and the process's last line of defence.
/// </summary>
public partial class App : Application
{
    // Survive-and-log has to be bounded: an exception that recurs on every
    // dispatcher cycle would otherwise turn into an infinite loop of message
    // boxes. After this many handled exceptions in one run, the next one is
    // allowed to crash the process (still logged), which is the honest outcome
    // for a genuinely wedged app.
    private int _handledCount;
    private const int MaxHandled = 3;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // File-picker child process: show the NATIVE dialog, print the path,
        // exit. Nothing else starts - no editor, no WebView2, no document - so a
        // shell extension that faults inside the dialog takes only this
        // throwaway process with it.
        //
        // This is why App.xaml has no StartupUri and the editor window is created
        // by hand below: StartupUri would have WPF build a MainWindow the moment
        // this method returns, and clearing it here is not an option (the setter
        // rejects null - verified: it threw, killing the child before the dialog
        // ever appeared, which made every native pick look like a crash).
        var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
        if (Picker.PickerChild.IsPickerInvocation(args))
        {
            int code;
            try { code = Picker.PickerChild.Run(args); }
            catch (Exception ex)
            {
                // A managed failure here is still "no result" to the parent, which
                // will fall back to the built-in picker. Logged so it is not silent.
                CrashLog.Write("PickerChild", ex);
                code = 1;
            }
            Shutdown(code);
            return;
        }

        // UI-thread exceptions. The important, non-obvious case: a native file
        // dialog (Open, Save As, the dictionary import) runs a NESTED message
        // pump, so an exception sitting in any queued dispatcher operation — a
        // timer tick, an async-void continuation — detonates the moment the
        // dialog opens. Field report: "when the file explorer dialog comes up it
        // crashes." Before this handler that was a silent process death; now it
        // is a crash.log entry and, within bounds, a survived event — the
        // document is unaffected and the backup timer keeps protecting it.
        DispatcherUnhandledException += (_, args) =>
        {
            CrashLog.Write("DispatcherUnhandledException", args.Exception);
            if (_handledCount >= MaxHandled) return;   // let it crash; logged above
            _handledCount++;
            args.Handled = true;
            try
            {
                MessageBox.Show(
                    "Markdown Midget hit an unexpected error and kept running.\n\n" +
                    $"Details were saved to:\n{CrashLog.LogPath}\n\n" +
                    "If this keeps happening, that file is what to send with the report.",
                    "Markdown Midget", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch { /* a broken message box must not undo the recovery */ }
        };

        // Non-UI-thread exceptions: can't be marked handled, but must not die
        // unrecorded.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            CrashLog.Write("AppDomain.UnhandledException", args.ExceptionObject as Exception);

        // Faulted tasks nobody awaited. Observed so they don't escalate.
        TaskScheduler.UnobservedTaskException += (_, taskArgs) =>
        {
            CrashLog.Write("UnobservedTaskException", taskArgs.Exception);
            taskArgs.SetObserved();
        };

        // The editor window, created explicitly rather than by StartupUri (see
        // above). Everything it needs comes from the command line it reads itself.
        new MainWindow().Show();
    }
}
