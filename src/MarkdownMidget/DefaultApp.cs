using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;

namespace MarkdownMidget;

/// <summary>Helps make Markdown Midget the .md default again: Windows hash-guards that choice, so this only reads it and opens
/// Settings, from Windows 11 22H2 (build 22621) on the page for the app Register lists, before that on Default apps.</summary>
internal static class DefaultApp
{
    internal const string LaunchFailed = "Couldn't open Windows Settings. Open Settings ▸ Apps ▸ Default apps and choose Markdown Midget for .md files there.";
    internal static string SettingsUri(int windowsBuild) => windowsBuild >= 22621
        ? "ms-settings:defaultapps?registeredAppUser=" + Uri.EscapeDataString(RegistrationService.DisplayName) : "ms-settings:defaultapps";
    internal static string ButtonTip(bool registered) => registered ? "Opens Windows Settings, where you choose Markdown Midget for .md files."
        : "Register Markdown Midget first: File ▸ Windows Integration ▸ Register as .md editor…";
    internal static void OpenSettings(Window owner) => OpenSettings(RegistrationService.IsRegistered(), uri => Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose(),
        text => MessageBox.Show(owner, text, "Markdown Midget", MessageBoxButton.OK, MessageBoxImage.Information));

    /// <summary>Every Make default entry point: launches <see cref="SettingsUri"/> if registered; if not, or if the launch fails, says why.</summary>
    internal static void OpenSettings(bool registered, Action<string> launch, Action<string> tell)
    {
        if (!registered) { tell(ButtonTip(false)); return; }
        try { launch(SettingsUri(Environment.OSVersion.Version.Build)); } catch { tell(LaunchFailed); }
    }

    /// <summary>After an update of the installed copy, unless suppressed: .md opened with us at the last run, or nothing was recorded (rc1 didn't), and doesn't now.</summary>
    internal static bool ShouldOfferDefault(bool? previousRecord, bool currentOpensWithUs, bool versionChanged, bool isInstalledCopy, bool suppressed)
        => isInstalledCopy && versionChanged && !suppressed && !currentOpensWithUs && previousRecord != false;
    internal static bool IsSameExe(string? resolved, string exePath) => !string.IsNullOrWhiteSpace(resolved)
        && string.Equals(Path.GetFullPath(resolved), Path.GetFullPath(exePath), StringComparison.OrdinalIgnoreCase);

    internal enum NoticeStep { Record, Show, Wait }
    /// <summary>With a notice to give (<paramref name="offer"/>), show it over a clean window, a startup file loaded unmodified included;
    /// wait over Help, a closing window, --recover or unsaved work, recording nothing, so the evidence survives. With none, record.</summary>
    internal static NoticeStep Step(bool offer, bool help, bool closing, bool recovering, bool unsaved)
        => !offer ? NoticeStep.Record : help || closing || recovering || unsaved ? NoticeStep.Wait : NoticeStep.Show;

    /// <summary>"No longer" only when the last run recorded that .md opened with us. With no record (rc1 kept none), a reset by
    /// Windows and a choice of another app look alike, so the notice claims neither.</summary>
    internal static string NoticeText(bool? previousRecord) => previousRecord == true
        ? "Windows no longer opens .md files with Markdown Midget." : "Windows doesn't open .md files with Markdown Midget.";

    /// <summary>The per-session lock a showing notice holds, so a second window shows none; null if another holds it. Release on this thread.</summary>
    internal static Mutex? TryHoldNotice(string name = @"Local\MarkdownMidget.DefaultNotice")
    {
        Mutex? mutex = null;
        try { mutex = new Mutex(false, name); if (mutex.WaitOne(0)) return mutex; }
        catch (AbandonedMutexException) { return mutex; }   // a window that died showing it: this one holds it now
        catch { /* can't tell: no notice this launch */ }
        mutex?.Dispose();
        return null;
    }

    /// <summary>Whether opening a .md file runs the installed exe now. Read-only: AssocQueryString(ASSOCF_NONE, ASSOCSTR_EXECUTABLE).</summary>
    internal static bool MdOpensWithUs()
    {
        var exe = new System.Text.StringBuilder(1024); var size = (uint)exe.Capacity;
        try { return AssocQueryString(0, 2, ".md", "open", exe, ref size) == 0 && IsSameExe(exe.ToString(), RegistrationService.AppDataInstallExe); }
        catch { return false; }
    }
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int AssocQueryString(int flags, int str, string assoc, string? extra, System.Text.StringBuilder? outBuffer, ref uint outSize);

    /// <summary>The notice after an update, saying <paramref name="text"/>: whether Make it the default… was clicked, and its checkbox.</summary>
    internal static (bool Make, bool DontShowAgain) AskAfterUpdate(Window owner, string text)
    {
        var never = new CheckBox { Content = "_Don't show this again", Margin = new Thickness(0, 14, 0, 14) };
        var make = new Button { Content = "_Make it the default…", IsDefault = true, Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 8, 0) };
        var dlg = new Window { Owner = owner, Title = "Markdown Midget", SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = new StackPanel { Margin = new Thickness(18), Children = {
                new TextBlock { Text = text }, never, new StackPanel { Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right, Children = { make, new Button { Content = "_Not now", IsCancel = true, MinWidth = 80 } } } } } };
        make.Click += (_, _) => dlg.DialogResult = true;
        return (dlg.ShowDialog() == true, never.IsChecked == true);   // left to right: the checkbox is read after the dialog closes
    }
}
