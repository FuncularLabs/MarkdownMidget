using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MarkdownMidget.Themes;

namespace MarkdownMidget;

/// <summary>
/// View ▸ Theme: the menu, the persisted choice, and the two places outside the
/// WebView that have to follow it.
///
/// The theme itself is a few lines — read a file, hand it to
/// <c>MDM.setTheme</c>. What takes the room is everything that would otherwise be
/// half-themed: the WPF source-view TextBox, which is not a web page and cannot
/// read a stylesheet, and mermaid, which draws its own SVG from its own palette.
/// Both are answered the same way — the browser is asked what the CSS resolved to,
/// rather than the host parsing the CSS to work it out.
/// </summary>
public partial class MainWindow
{
    private ThemeStore? _themeStore;
    private string _themeKey = ThemeStore.DefaultKey;

    /// <summary>What the source view was before a theme touched it, so Default puts
    /// it back exactly rather than to something that looks about right.</summary>
    private (Brush Background, Brush Foreground, Brush? Caret)? _sourceOriginal;

    private void InitializeThemes()
    {
        var (root, fellBack) = ThemeStore.ResolveRoot();
        _themeStore = new ThemeStore(root);
        // The same string the About box shows, leading "v" and all — UpdateVersion
        // strips it, and a second way of asking the assembly its version is a second
        // thing to keep in step with CI's tag-derived InformationalVersion.
        _themeStore.Refresh(AppVersion);

        if (fellBack)
            FlashStatus("Themes are being kept in your profile — this folder isn't writable.");
    }

    // ===== the menu =====

    // Rebuilt on open rather than once at startup, so a file dropped into custom\
    // shows up without restarting the app — the same reason Open Recent rebuilds.
    private void ThemeMenu_Opened(object sender, RoutedEventArgs e) => BuildThemeMenu();

    private void BuildThemeMenu()
    {
        ThemeMenu.Items.Clear();
        if (_themeStore is null) return;

        var wasCustom = false;
        var first = true;
        foreach (var theme in _themeStore.List())
        {
            // One separator, where built-ins end and the user's begin.
            if (theme.IsCustom && !wasCustom && !first) ThemeMenu.Items.Add(new Separator());
            wasCustom = theme.IsCustom;
            first = false;

            var item = new MenuItem
            {
                // WPF eats a single underscore and makes the next character an access
                // key, so a theme called my_theme would show as "My Theme" with a
                // phantom shortcut on T.
                Header = theme.Name.Replace("_", "__"),
                Tag = theme.Key,
                IsCheckable = true,
                IsChecked = string.Equals(theme.Key, _themeKey, StringComparison.OrdinalIgnoreCase),
                IsEnabled = theme.IsUsable,
            };

            // A greyed entry with no explanation is a bug report. The validator says
            // which line and why, and that belongs where the user is already looking.
            item.ToolTip = theme.IsUsable
                ? theme.Path
                : "This theme can't be used — " + theme.Unusable;

            item.Click += ThemeItem_Click;
            ThemeMenu.Items.Add(item);
        }

        ThemeMenu.Items.Add(new Separator());
        var open = new MenuItem
        {
            Header = "_Open Themes Folder",
            ToolTip = "Put your own .css files in the custom folder — they appear here.",
        };
        open.Click += OpenThemesFolder_Click;
        ThemeMenu.Items.Add(open);
    }

    private async void ThemeItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string key }) return;
        await ApplyThemeAsync(key, announceFallback: true);
        RefocusEditor();
    }

    private void OpenThemesFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_themeStore is null) return;
        try
        {
            Directory.CreateDirectory(_themeStore.CustomDir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_themeStore.CustomDir)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex) { FlashStatus("Couldn't open the themes folder: " + ex.Message); }
    }

    // ===== applying =====

    /// <summary>
    /// Install a theme by its persisted name, and remember it if it worked.
    ///
    /// Every failure lands in the same place: say what happened and fall back to
    /// Default. Silence would leave the user looking at the palette they didn't
    /// choose with no idea why — which is the specific complaint the plan set out to
    /// avoid for a theme that has gone missing between launches.
    /// </summary>
    private async Task ApplyThemeAsync(string? key, bool announceFallback)
    {
        if (_themeStore is null) return;

        var theme = _themeStore.Find(key);
        if (theme is null)
        {
            if (announceFallback && !string.IsNullOrEmpty(key))
                FlashStatus($"Theme \"{key}\" is no longer there — using Default.");
            await InstallThemeAsync(string.Empty);
            SetThemeKey(ThemeStore.DefaultKey);
            return;
        }

        var css = _themeStore.Read(theme, out var failure);
        if (css is null)
        {
            // The current theme stays applied. A file that has become unreadable
            // since the menu was built is not a reason to also take away what is on
            // screen — the update and backup paths refuse the same way.
            FlashStatus($"Can't use {theme.Name}: {failure}");
            BuildThemeMenu();
            return;
        }

        await InstallThemeAsync(css);
        SetThemeKey(theme.Key);
    }

    private void SetThemeKey(string key)
    {
        if (string.Equals(_themeKey, key, StringComparison.OrdinalIgnoreCase)) { BuildThemeMenu(); return; }
        _themeKey = key;
        SaveSettings();
        BuildThemeMenu();
    }

    /// <summary>
    /// Hand the CSS to the page and take back what it resolved to.
    ///
    /// ExecuteScriptAsync directly rather than through RunEditorAsync, which
    /// deserializes its result as a string — this one returns an object, and reading
    /// it as the raw JSON that fell out of a failed string cast would work by
    /// accident rather than on purpose.
    /// </summary>
    private async Task InstallThemeAsync(string css)
    {
        if (!_editorReady || Web.CoreWebView2 is null) return;
        try
        {
            var raw = await Web.CoreWebView2.ExecuteScriptAsync($"window.MDM.setTheme({JsLiteral(css)})");
            ApplySourceColors(raw);
        }
        catch (Exception ex)
        {
            FlashStatus("The theme couldn't be applied: " + ex.Message);
        }
    }

    /// <summary>
    /// Repaint the markdown source view to match.
    ///
    /// Ctrl+E onto a blinding white pane is the obvious bug a dark theme creates, and
    /// the source view is the document rather than chrome, so it follows. The colours
    /// arrive already resolved and already flattened to opaque 8-bit sRGB by the
    /// page — see readThemeBack() — because every interesting colour syntax
    /// (<c>oklch()</c>, <c>color-mix()</c>, <c>rgb()</c> itself) is one that WPF's
    /// ColorConverter cannot read, and asking the engine is the only answer that
    /// doesn't need updating when CSS grows another one.
    /// </summary>
    private void ApplySourceColors(string? json)
    {
        _sourceOriginal ??= (SourceBox.Background, SourceBox.Foreground, SourceBox.CaretBrush);

        if (ThemeReadBack.Parse(json) is not { } read)
        {
            // Nothing usable came back. Put the original colours back rather than
            // leaving the previous theme's on a pane that no longer matches it.
            var (bg, fg, caret) = _sourceOriginal.Value;
            SourceBox.Background = bg;
            SourceBox.Foreground = fg;
            SourceBox.CaretBrush = caret;
            return;
        }

        SourceBox.Background = new SolidColorBrush(read.Background);
        SourceBox.Foreground = new SolidColorBrush(read.Foreground);
        // Without this the caret keeps WPF's default black and disappears entirely on
        // a dark theme — the pane looks right and typing looks broken.
        SourceBox.CaretBrush = new SolidColorBrush(read.Foreground);
    }
}
