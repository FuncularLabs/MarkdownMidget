using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MarkdownMidget;

/// <summary>
/// The settings that don't belong on a menu — a number to type and a startup
/// choice. Toggles that a user flips while working (spell check, word wrap,
/// auto-reload, document width) stay on the View menu where they're one click away.
/// </summary>
public partial class SettingsDialog : Window
{
    public const int MinRecent = 1;
    public const int MaxRecentLimit = 50;

    /// <summary>Start with a blank document rather than the no-document placeholder.</summary>
    public bool StartWithBlankDocument { get; private set; }

    public int RecentLimit { get; private set; }

    /// <summary>Keep a crash copy of unsaved work.</summary>
    public bool KeepBackup { get; private set; }

    public SettingsDialog(bool startWithBlankDocument, int recentLimit, bool keepBackup)
    {
        InitializeComponent();
        StartWithBlankDocument = startWithBlankDocument;
        RecentLimit = recentLimit;
        KeepBackup = keepBackup;
        KeepBackupCheck.IsChecked = keepBackup;
        StartBlankRadio.IsChecked = startWithBlankDocument;
        StartSplashRadio.IsChecked = !startWithBlankDocument;
        RecentLimitBox.Text = recentLimit.ToString();
        Loaded += (_, _) => { RecentLimitBox.Focus(); RecentLimitBox.SelectAll(); };
    }

    private void DigitsOnly_PreviewTextInput(object sender, TextCompositionEventArgs e)
        => e.Handled = !e.Text.All(char.IsDigit);

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(RecentLimitBox.Text, out var limit) ||
            limit < MinRecent || limit > MaxRecentLimit)
        {
            // Say what's wrong in place rather than throwing up a second dialog.
            RecentHint.Text = $"Enter a number between {MinRecent} and {MaxRecentLimit}.";
            RecentHint.Foreground = System.Windows.Media.Brushes.Firebrick;
            RecentLimitBox.Focus();
            RecentLimitBox.SelectAll();
            return;
        }
        RecentLimit = limit;
        StartWithBlankDocument = StartBlankRadio.IsChecked == true;
        KeepBackup = KeepBackupCheck.IsChecked == true;
        DialogResult = true;
    }
}
