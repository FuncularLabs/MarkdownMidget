using System.Windows;

namespace MarkdownMidget;

public partial class RegisterDialog : Window
{
    public bool MoveInsteadOfCopy => MoveCheck.IsChecked == true && MoveCheck.IsEnabled;
    public bool AddStartMenu => StartMenuCheck.IsChecked == true;
    public bool AddDesktop => DesktopCheck.IsChecked == true;
    public bool SetAsDefault => SetDefaultCheck.IsChecked == true;

    public RegisterDialog()
    {
        InitializeComponent();
        ExePathText.Text = "This build: " + RegistrationService.CurrentExePath;

        // If we're already running the installed AppData copy, there's nothing to
        // move — hide that option (the file lives in the app folder already).
        if (RegistrationService.IsRunningFromAppDataInstall())
            MovePanel.Visibility = Visibility.Collapsed;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
