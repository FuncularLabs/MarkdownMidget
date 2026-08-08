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

    /// <summary>The theme the user chose, which is what settings.json holds.</summary>
    private string _themeKey = ThemeStore.DefaultKey;

    /// <summary>
    /// The theme actually on screen, which is what the menu ticks.
    ///
    /// Separate from <see cref="_themeKey"/> because the two genuinely differ when a
    /// chosen theme isn't there this launch, and collapsing them costs the user their
    /// preference — see <see cref="ApplyThemeAsync"/>.
    /// </summary>
    private string _appliedKey = ThemeStore.DefaultKey;

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
                // Ticks what is ON SCREEN, not what is remembered. When a chosen theme
                // is missing this launch the two differ, and ticking the preference
                // would claim a palette the user is not looking at.
                IsChecked = string.Equals(theme.Key, _appliedKey, StringComparison.OrdinalIgnoreCase),
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
        await ApplyThemeAsync(key);
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
    /// Install a theme by name, and remember it if it worked.
    ///
    /// Two failures, and they are answered differently on purpose:
    ///
    /// - **Not in the folder.** Fall back to Default — there is nothing else to show —
    ///   but keep the preference, because one launch that couldn't find the file is
    ///   not evidence the user changed their mind.
    /// - **There but unreadable or refused.** Change nothing. Whatever is on screen is
    ///   still a theme the user chose; replacing it with Default would be a second,
    ///   unasked-for change on top of the failure.
    ///
    /// Both say so. Silence would leave someone looking at a palette they didn't pick
    /// with no idea why, which is the complaint this whole path exists to avoid.
    /// </summary>
    private async Task ApplyThemeAsync(string? key)
    {
        if (_themeStore is null) return;

        var theme = _themeStore.Find(key);
        if (theme is null)
        {
            // Fall back to Default, and KEEP the preference. Overwriting it here is
            // the tempting one line, and it silently throws the user's choice away on
            // the strength of one launch that couldn't see the file.
            //
            // "Couldn't see the file" is not the same as "the file is gone". A
            // portable copy whose exe directory is briefly unwritable resolves its
            // themes to the profile instead, where a custom theme was never written —
            // so run N falls back, run N erases the preference, and run N+1, with the
            // directory writable again and the theme sitting right there, opens on
            // Default with nothing left to say why.
            //
            // So the preference outlives a launch that couldn't honour it. It is only
            // ever replaced by a theme that actually applied.
            if (!string.IsNullOrEmpty(key))
                FlashStatus($"Theme \"{key}\" isn't in the themes folder — using Default for now.");
            await InstallThemeAsync(string.Empty);
            _appliedKey = ThemeStore.DefaultKey;
            BuildThemeMenu();
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

    /// <summary>Record a theme that actually applied. The only writer of the
    /// persisted preference.</summary>
    private void SetThemeKey(string key)
    {
        _appliedKey = key;
        if (!string.Equals(_themeKey, key, StringComparison.OrdinalIgnoreCase))
        {
            _themeKey = key;
            SaveSettings();
        }
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
        // Nothing to install into yet, and nothing to report either: the menu is live
        // from the moment the window opens but the editor takes a moment, so an early
        // click lands here. Persisting the choice anyway is correct rather than
        // sloppy — the 'ready' handler applies _themeKey the instant there is a page,
        // so the theme is simply already on when the editor appears. Do not "fix"
        // this into an error message without moving that apply.
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
            // The page took the theme and then failed to describe it — a script error,
            // or a canvas context the machine wouldn't hand out. So the formatted view
            // is themed and this pane cannot know to what.
            //
            // Restoring the original is the only defined answer available, and under a
            // dark theme it is a white pane, which is precisely the half-themed state
            // the read-back exists to prevent. So it SAYS so. A silent revert here
            // would be indistinguishable from the feature not working, and would send
            // someone looking in the theme file for a fault that isn't in it.
            var (bg, fg, caret) = _sourceOriginal.Value;
            SourceBox.Background = bg;
            SourceBox.Foreground = fg;
            SourceBox.CaretBrush = caret;
            FlashStatus("The theme was applied, but the markdown source view couldn't follow it.");
            return;
        }

        SourceBox.Background = new SolidColorBrush(read.Background);
        SourceBox.Foreground = new SolidColorBrush(read.Foreground);
        // Without this the caret keeps WPF's default black and disappears entirely on
        // a dark theme — the pane looks right and typing looks broken.
        SourceBox.CaretBrush = new SolidColorBrush(read.Foreground);
    }
}
