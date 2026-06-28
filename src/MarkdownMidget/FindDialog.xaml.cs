using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MarkdownMidget;

/// <summary>
/// Modeless Find dialog. Raises <see cref="FindRequested"/> when the user
/// triggers a search (Find Next / Find Previous / Enter / typing). The host
/// runs the search and reports back via <see cref="SetStatus"/>.
/// </summary>
public partial class FindDialog : Window
{
    public event EventHandler<FindRequest>? FindRequested;
    public event EventHandler? Closed2;

    public FindDialog()
    {
        InitializeComponent();
        ModeExtended.ToolTip = FindEngine.ExtendedTooltip;
        ModeWildcards.ToolTip = FindEngine.WildcardsTooltip;
        ModeRegex.ToolTip = FindEngine.RegexTooltip;
        Loaded += (_, _) => { QueryBox.Focus(); QueryBox.SelectAll(); };
        Closed += (_, _) => Closed2?.Invoke(this, EventArgs.Empty);
    }

    public string Query => QueryBox.Text;
    public FindEngine.Mode CurrentMode =>
        ModeRegex.IsChecked == true ? FindEngine.Mode.Regex
        : ModeWildcards.IsChecked == true ? FindEngine.Mode.Wildcards
        : ModeExtended.IsChecked == true ? FindEngine.Mode.Extended
        : FindEngine.Mode.Normal;
    public bool MatchCaseOn => MatchCase.IsChecked == true;
    public bool WholeWordOn => WholeWord.IsChecked == true;
    public bool WrapOn => WrapAround.IsChecked == true;

    public void FocusQuery()
    {
        Activate();
        QueryBox.Focus();
        QueryBox.SelectAll();
    }

    /// <summary>Updates the status line with "Match m of n" or an error.</summary>
    public void SetStatus(string text) => StatusText.Text = text;

    private FindRequest CurrentRequest(bool forward) => new(
        Query, CurrentMode, MatchCaseOn, WholeWordOn, WrapOn, forward);

    private void Query_Changed(object sender, TextChangedEventArgs e)
        => FindRequested?.Invoke(this, CurrentRequest(true) with { LiveTyping = true });

    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        FindRequested?.Invoke(this, CurrentRequest(true) with { LiveTyping = true });
    }

    private void Option_Changed(object sender, RoutedEventArgs e)
        => FindRequested?.Invoke(this, CurrentRequest(true) with { LiveTyping = true });

    private void FindNext_Click(object sender, RoutedEventArgs e)
        => FindRequested?.Invoke(this, CurrentRequest(true));

    private void FindPrev_Click(object sender, RoutedEventArgs e)
        => FindRequested?.Invoke(this, CurrentRequest(false));

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.F3)
        {
            FindRequested?.Invoke(this,
                CurrentRequest(forward: (Keyboard.Modifiers & ModifierKeys.Shift) == 0));
            e.Handled = true;
            return;
        }
        base.OnPreviewKeyDown(e);
    }
}

/// <summary>
/// A single Find request from the dialog. LiveTyping=true means the trigger was
/// passive (typing / toggle change) — the host should rebuild the match set and
/// jump to the first/nearest match without warning when nothing is found.
/// </summary>
public record FindRequest(
    string Query,
    FindEngine.Mode Mode,
    bool MatchCase,
    bool WholeWord,
    bool Wrap,
    bool Forward,
    bool LiveTyping = false);
