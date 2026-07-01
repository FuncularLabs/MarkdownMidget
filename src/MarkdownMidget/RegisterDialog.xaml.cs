using System.Windows;

namespace MarkdownMidget;

public partial class RegisterDialog : Window
{
    public bool InstallToAppData => InstallToAppDataCheck.IsChecked == true;
    public bool SetAsDefault => SetDefaultCheck.IsChecked == true;

    public RegisterDialog()
    {
        InitializeComponent();
        ExePathText.Text = "Current build: " + RegistrationService.CurrentExePath;

        // If we're already the AppData-installed copy, the checkbox is meaningless.
        if (RegistrationService.IsRunningFromAppDataInstall())
        {
            InstallToAppDataCheck.IsChecked = false;
            InstallToAppDataCheck.IsEnabled = false;
            InstallToAppDataCheck.ToolTip = "You're already running the AppData-installed copy.";
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
