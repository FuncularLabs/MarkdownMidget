using System.IO;
using System.Windows;

namespace MarkdownMidget;

public partial class UnregisterDialog : Window
{
    private readonly RegistrationService.InstallInfo? _info;
    private readonly bool _runningFromInstall;

    public bool RemoveRegistration => RemoveRegCheck.IsChecked == true;
    public bool RestoreToOriginal => RestorePanel.Visibility == Visibility.Visible && RestoreCheck.IsChecked == true;
    public bool RemoveStartMenu => StartMenuPanel.Visibility == Visibility.Visible && RemoveStartMenuCheck.IsChecked == true;
    public bool RemoveDesktop => DesktopPanel.Visibility == Visibility.Visible && RemoveDesktopCheck.IsChecked == true;
    public bool RemoveAppDataCopy => RemoveCopyPanel.Visibility == Visibility.Visible
                                     && RemoveCopyCheck.IsChecked == true && RemoveCopyCheck.IsEnabled;

    public string? OriginalPath => _info?.OriginalPath;

    public UnregisterDialog()
    {
        InitializeComponent();

        _info = RegistrationService.ReadInstallInfo();
        _runningFromInstall = RegistrationService.IsRunningFromAppDataInstall();

        // Restore-to-original: only if we know where it came from.
        if (!string.IsNullOrEmpty(_info?.OriginalPath))
        {
            RestoreCheck.IsChecked = _info!.Moved;   // default on only if we moved (deleted) the original
            var exists = File.Exists(_info.OriginalPath);
            RestoreCaption.Text = _info.Moved && !exists
                ? $"Puts a copy back at {_info.OriginalPath} (the download was moved away)."
                : $"Copies the exe to {_info.OriginalPath}" + (exists ? " (a file is already there — it would be overwritten)." : ".");
        }
        else
        {
            RestorePanel.Visibility = Visibility.Collapsed;
        }

        if (!RegistrationService.HasStartMenuShortcut()) StartMenuPanel.Visibility = Visibility.Collapsed;
        if (!RegistrationService.HasDesktopShortcut()) DesktopPanel.Visibility = Visibility.Collapsed;

        if (!RegistrationService.IsInstalledToAppData())
        {
            RemoveCopyPanel.Visibility = Visibility.Collapsed;
        }
        else if (_runningFromInstall)
        {
            // Can't delete the folder we're running from — offer it, but disabled.
            RemoveCopyCheck.IsChecked = false;
            RemoveCopyCheck.IsEnabled = false;
            RemoveCopyCaption.Text = "Close this window's app first (it's running from the app folder), then unregister again to remove it.";
        }
        else
        {
            RemoveCopyCaption.Text = "Deletes the stable copy in %LocalAppData%\\Programs\\MarkdownMidget.";
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
