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
        var registered = RegistrationService.IsRegistered();   // Settings lists only a registered app
        (MakeDefaultBtn.IsEnabled, MakeDefaultBtn.ToolTip) = (registered, DefaultApp.ButtonTip(registered));
    }

    private void MakeDefault_Click(object sender, RoutedEventArgs e) => DefaultApp.OpenSettings(this);

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
