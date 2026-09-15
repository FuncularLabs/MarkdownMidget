using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MarkdownMidget;

/// <summary>
/// Modeless Find and Replace dialog. Raises <see cref="FindRequested"/> when the
/// user triggers a search (Find Next / Find Previous / Enter / typing) and
/// <see cref="ReplaceRequested"/> for Replace / Replace All. The host runs the
/// search or replacement and reports back via <see cref="SetStatus"/>.
///
/// The window is created afresh each time Find is opened, so the last query and
/// replacement are kept here, per process, and put back into the boxes.
/// </summary>
public partial class FindDialog : Window
{
    public event EventHandler<FindRequest>? FindRequested;
    public event EventHandler<ReplaceRequest>? ReplaceRequested;
    public event EventHandler? Closed2;

    private static string s_lastQuery = "";
    private static string s_lastReplacement = "";

    private const string ReplaceTip = "The current match, then find the next";
    private const string ReplaceAllTip = "Every match — within the selection when there is one — as one undo step";
    private const string ReadOnlyTip = "The document is read-only.";

    public FindDialog()
    {
        InitializeComponent();
        ModeExtended.ToolTip = FindEngine.ExtendedTooltip;
        ModeWildcards.ToolTip = FindEngine.WildcardsTooltip;
        ModeRegex.ToolTip = FindEngine.RegexTooltip;
        // The Replace tooltips too: SetReadOnly owns both strings, so the markup
        // carries no copy of them to drift (#5 F-15). The host calls it again with
        // the document's real state as soon as the dialog is made.
        SetReadOnly(false);
        // Before the host subscribes, so putting the text back raises no search.
        QueryBox.Text = s_lastQuery;
        ReplaceBox.Text = s_lastReplacement;
        Loaded += (_, _) => FocusBox();
        Closed += (_, _) =>
        {
            s_lastQuery = QueryBox.Text;
            s_lastReplacement = ReplaceBox.Text;
            Closed2?.Invoke(this, EventArgs.Empty);
        };
    }

    public string Query => QueryBox.Text;
    public string Replacement => ReplaceBox.Text;
    public FindEngine.Mode CurrentMode =>
        ModeRegex.IsChecked == true ? FindEngine.Mode.Regex
        : ModeWildcards.IsChecked == true ? FindEngine.Mode.Wildcards
        : ModeExtended.IsChecked == true ? FindEngine.Mode.Extended
        : FindEngine.Mode.Normal;
    public bool MatchCaseOn => MatchCase.IsChecked == true;
    public bool WholeWordOn => WholeWord.IsChecked == true;
    public bool WrapOn => WrapAround.IsChecked == true;

    /// <summary>Ctrl+F / Edit ▸ Find…: the cursor in Find what.</summary>
    public void FocusQuery() { _replaceAsked = false; Activate(); FocusBox(); }

    /// <summary>Ctrl+H / Edit ▸ Replace…: the cursor in Replace with, unless the document is read-only.</summary>
    public void FocusReplace() { _replaceAsked = true; Activate(); FocusBox(); }

    /// <summary>Replace with takes the cursor when Replace was asked for and the document can change. Read-only,
    /// Replace is greyed and the host refuses one, so the cursor goes where Find puts it.</summary>
    public static bool FocusesReplaceBox(bool replaceAsked, bool readOnly) => replaceAsked && !readOnly;

    private bool _replaceAsked;   // the last Ctrl+F or Ctrl+H: Loaded can land after the host's call, and honours it
    private bool _readOnly;

    private void FocusBox()
    {
        var box = FocusesReplaceBox(_replaceAsked, _readOnly) ? ReplaceBox : QueryBox;
        box.Focus();
        box.SelectAll();
    }

    /// <summary>Updates the status line with "Match m of n" or an error.</summary>
    public void SetStatus(string text) => StatusText.Text = text;

    /// <summary>
    /// Replace and Replace All are greyed while the document is read-only, the way
    /// the Format menu is; the host also refuses the request itself, so this is the
    /// visible half of that gate, not the only one.
    /// </summary>
    public void SetReadOnly(bool readOnly)
    {
        _readOnly = readOnly;
        ReplaceButton.IsEnabled = ReplaceAllButton.IsEnabled = !readOnly;
        ReplaceButton.ToolTip = readOnly ? ReadOnlyTip : ReplaceTip;
        ReplaceAllButton.ToolTip = readOnly ? ReadOnlyTip : ReplaceAllTip;
    }

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

    private void Replace_Click(object sender, RoutedEventArgs e)
        => ReplaceRequested?.Invoke(this, new ReplaceRequest(CurrentRequest(true), Replacement, All: false));

    private void ReplaceAll_Click(object sender, RoutedEventArgs e)
        => ReplaceRequested?.Invoke(this, new ReplaceRequest(CurrentRequest(true), Replacement, All: true));

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

/// <summary>
/// A Replace (<paramref name="All"/> false: the current match, then find the next)
/// or Replace All request: the search as the dialog has it, and the replacement
/// text as typed — the host prepares it for the mode through FindEngine.
/// </summary>
public record ReplaceRequest(FindRequest Find, string Replacement, bool All);
