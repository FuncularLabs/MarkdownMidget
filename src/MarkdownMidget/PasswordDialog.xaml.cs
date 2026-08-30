using System.Windows;

namespace MarkdownMidget;

/// <summary>
/// The two faces of the Secure Markdown password prompt. Set mode carries the
/// non-negotiable no-recovery warning (design §8) and enforces enter-twice with
/// a strength readout; Enter mode is a single box with room for a wrong-password
/// message on retry. One dialog rather than two so the framing, icon and tab
/// order can never drift apart.
/// </summary>
public partial class PasswordDialog : Window
{
    private readonly bool _setMode;

    private PasswordDialog(bool setMode)
    {
        InitializeComponent();
        _setMode = setMode;
        if (!setMode)
        {
            WarningBlock.Visibility = Visibility.Collapsed;
            StrengthText.Visibility = Visibility.Collapsed;
            Label2.Visibility = Visibility.Collapsed;
            Box2.Visibility = Visibility.Collapsed;
            MatchText.Visibility = Visibility.Collapsed;
        }
        Loaded += (_, _) => Box1.Focus();
    }

    public string Password => Box1.Password;

    /// <summary>Set/confirm a password. Returns null on cancel. <paramref name="extraWarning"/>
    /// appears under the standard no-recovery text (e.g. "the unencrypted copy will be
    /// removed from disk").</summary>
    public static string? Set(Window owner, string title, string intro, string? extraWarning = null)
    {
        var dlg = new PasswordDialog(setMode: true) { Owner = owner, Title = title };
        dlg.Intro.Text = intro;
        if (extraWarning is not null)
            dlg.WarningBlock.Text += "\n\n" + extraWarning;
        return dlg.ShowDialog() == true ? dlg.Password : null;
    }

    /// <summary>Ask for an existing password. Returns null on cancel. Pass
    /// <paramref name="error"/> on a retry so the user knows why they're back.</summary>
    public static string? Enter(Window owner, string title, string intro, string? error = null)
    {
        var dlg = new PasswordDialog(setMode: false) { Owner = owner, Title = title };
        dlg.Intro.Text = intro;
        if (error is not null)
        {
            dlg.ErrorBlock.Text = error;
            dlg.ErrorBlock.Visibility = Visibility.Visible;
        }
        return dlg.ShowDialog() == true ? dlg.Password : null;
    }

    /// <summary>True when the user chose the recovery prompt's Discard option.
    /// The caller owns the are-you-sure confirmation and the actual deletion.</summary>
    public bool DiscardChosen { get; private set; }

    /// <summary>The recovery flavour of Enter: adds a Discard button, because a
    /// snapshot whose password is genuinely lost needs a way OUT that isn't
    /// hand-deleting files under %LocalAppData% - or being prompted on every
    /// launch forever.</summary>
    public static (string? Password, bool Discard) EnterForRecovery(
        Window owner, string title, string intro, string? error = null)
    {
        var dlg = new PasswordDialog(setMode: false) { Owner = owner, Title = title };
        dlg.Intro.Text = intro;
        dlg.DiscardButton.Visibility = Visibility.Visible;
        if (error is not null)
        {
            dlg.ErrorBlock.Text = error;
            dlg.ErrorBlock.Visibility = Visibility.Visible;
        }
        var ok = dlg.ShowDialog() == true;
        if (dlg.DiscardChosen) return (null, true);
        return (ok ? dlg.Password : null, false);
    }

    private void Discard_Click(object sender, RoutedEventArgs e)
    {
        DiscardChosen = true;
        DialogResult = false;
        Close();
    }

    private void Box_Changed(object sender, RoutedEventArgs e)
    {
        if (_setMode)
        {
            StrengthText.Text = Secure.SecureUi.DescribeStrength(Box1.Password);
            var match = Box1.Password == Box2.Password;
            MatchText.Text = Box2.Password.Length == 0 ? "" : match ? "Passwords match" : "Passwords don't match yet";
            OkButton.IsEnabled = Box1.Password.Length > 0 && match;
        }
        else
        {
            OkButton.IsEnabled = Box1.Password.Length > 0;
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
