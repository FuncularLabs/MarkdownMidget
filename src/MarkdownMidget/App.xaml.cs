using System;
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
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            CrashLog.Write("UnobservedTaskException", args.Exception);
            args.SetObserved();
        };
    }
}
