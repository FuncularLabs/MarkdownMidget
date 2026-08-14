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

    // The dictionary import, supplied by MainWindow (which owns the SpellService and
    // the file-picking). Returns a human-readable result, or null when cancelled.
    // Runs immediately rather than on OK: it is an action with its own result to
    // show, not a preference to stage — and cancelling the dialog afterwards should
    // not (and could not) un-import the words.
    private readonly System.Func<Window, string?>? _importDictionary;

    public SettingsDialog(bool startWithBlankDocument, int recentLimit, bool keepBackup,
                          System.Func<Window, string?>? importDictionary = null)
    {
        InitializeComponent();
        StartWithBlankDocument = startWithBlankDocument;
        RecentLimit = recentLimit;
        KeepBackup = keepBackup;
        KeepBackupCheck.IsChecked = keepBackup;
        StartBlankRadio.IsChecked = startWithBlankDocument;
        StartSplashRadio.IsChecked = !startWithBlankDocument;
        RecentLimitBox.Text = recentLimit.ToString();
        _importDictionary = importDictionary;
        ImportDicBtn.IsEnabled = importDictionary is not null;
        Loaded += (_, _) => { RecentLimitBox.Focus(); RecentLimitBox.SelectAll(); };
    }

    private void ImportDic_Click(object sender, RoutedEventArgs e)
    {
        if (_importDictionary is null) return;
        var result = _importDictionary(this);
        if (result is null) return;   // cancelled — say nothing rather than "0 imported"
        ImportResult.Text = result;
        ImportResult.Visibility = Visibility.Visible;
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
