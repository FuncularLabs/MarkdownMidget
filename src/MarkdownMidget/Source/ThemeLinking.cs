namespace MarkdownMidget.Source;

/// <summary>
/// The two decisions behind View ▸ Theme when the source view may run its own theme:
/// which view(s) a selection applies to, and which theme the menu ticks. Pure, so
/// the rule is stated once and tested without a window.
///
/// The rule: with "Same theme for both views" ON, a selection applies to both and the
/// menu ticks the document theme. With it OFF, a selection applies only to the view
/// the user is in — the source view when it is showing, otherwise the document — and
/// the menu ticks that view's theme.
/// </summary>
internal static class ThemeLinking
{
    /// <summary>Which views a theme selection should be applied to.</summary>
    public static (bool Document, bool Source) TargetsFor(bool linked, bool sourceMode) =>
        linked ? (true, true)
        : sourceMode ? (false, true)
        : (true, false);

    /// <summary>The theme key the menu should tick: the active view's.</summary>
    public static string TickedKey(bool linked, bool sourceMode, string documentKey, string sourceKey) =>
        linked || !sourceMode ? documentKey : sourceKey;
}
