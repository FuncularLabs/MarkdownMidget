using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MarkdownMidget;

/// <summary>
/// View ▸ Mode: Light, Dark or System. The effective mode lives in <see cref="_appearance"/>
/// (<see cref="WindowsAppearance"/>); the window chrome and the document theme slots both
/// follow it through its Changed event, wired in InitializeThemes.
/// </summary>
public partial class MainWindow
{
    /// <summary>What a View ▸ Mode pick writes: that one field, merged by
    /// <see cref="SavePersistentField"/> onto the settings on disk.</summary>
    internal static Action<AppSettings> RememberMode(AppearanceMode mode) => s => s.AppearanceMode = mode.ToString();

    private void Mode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag } || _appearance is null) return;
        var mode = AppearanceModes.Parse(tag);
        // Saved first, then applied here. Every other window's settings watcher reads the
        // pick back and follows it, and writes nothing; so does this window's, finding it
        // already applied. A save that fails leaves the pick on this window only, and says so.
        var saved = SavePersistentField(RememberMode(mode));
        _appearance.SetMode(mode, saved);
        if (!saved) FlashStatus("Couldn't save the mode — this window uses it for now.");
        SyncModeMenu();
        RefocusEditor();
    }

    // ===== a dark start's document area, before the theme applies =====

    /// <summary>The dark chrome's window background when <paramref name="dark"/>, for the
    /// document area until its theme is on: the WebView paints white, and the page the
    /// default theme's light grey, until 'ready'. Null when light.</summary>
    private Color? DarkStartColor(bool dark) =>
        dark && TryFindResource("Chrome.Window.Background") is SolidColorBrush { Color: var c } ? c : null;

    /// <summary>The WebView's own background, which shows before the page paints; it follows
    /// every mode change (from InitializeThemes, after the chrome).</summary>
    private void ApplyWebBackground(bool dark) =>
        Web.DefaultBackgroundColor = DarkStartColor(dark) is { } c
            ? System.Drawing.Color.FromArgb(c.R, c.G, c.B)
            : System.Drawing.Color.White;

    /// <summary>Registered before the editor page loads when the mode is dark: its top frame
    /// only (the hidden frame that resolves a source view theme must read the real theme)
    /// draws in <paramref name="colour"/> until <see cref="DropDarkStartScript"/> runs, which
    /// InstallThemeAsync does in the same script as setTheme, before it reads the theme back.</summary>
    private static string DarkStartScript(string colour) =>
        "if (window === window.top) { const s = new CSSStyleSheet(); s.replaceSync(':root{--mdm-app-bg:" + colour +
        ";--mdm-page-bg:" + colour + ";--mdm-color-scheme:dark}'); " +
        "document.adoptedStyleSheets = [...document.adoptedStyleSheets, s]; window.__mdmDarkStart = s; }";

    private const string DropDarkStartScript =
        "if (window.__mdmDarkStart) { document.adoptedStyleSheets = document.adoptedStyleSheets.filter(s => s !== window.__mdmDarkStart); window.__mdmDarkStart = null; }\n";

    /// <summary>Ticks the mode in force: on View's opening, since another window's pick can
    /// change it without changing the effective mode, and so without an event here.</summary>
    private void SyncModeMenu()
    {
        var mode = _appearance?.Mode ?? AppearanceMode.System;
        ModeLight.IsChecked = mode == AppearanceMode.Light;
        ModeDark.IsChecked = mode == AppearanceMode.Dark;
        ModeSystem.IsChecked = mode == AppearanceMode.System;
    }
}
