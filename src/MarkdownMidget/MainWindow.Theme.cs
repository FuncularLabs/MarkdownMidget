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

    /// <summary>Windows' light or dark app mode, and when it changes. Created with the
    /// window and disposed when it closes. Only the theme code uses it so far, through a
    /// local; the field is here for the window chrome to follow the same mode.</summary>
    private WindowsAppearance? _appearance;

    /// <summary>The remembered themes, one per Windows mode for the document and for
    /// the source view's own, and "Same Theme for Both Views". Only a pick or a link
    /// toggle changes those settings (<see cref="ThemeModes"/>).</summary>
    private ThemeModes? _themes;

    /// <summary>Settings as LoadSettings read them, held until InitializeThemes can read
    /// the theme files a migration needs.</summary>
    private IThemeSettings? _loadedThemeSettings;

    /// <summary>
    /// The theme actually on screen, which is what the menu ticks.
    ///
    /// Separate from the remembered theme because the two genuinely differ when a
    /// chosen theme isn't there this launch, and collapsing them costs the user their
    /// preference — see <see cref="ApplyDocumentThemeAsync"/>.
    /// </summary>
    private string _appliedKey = ThemeStore.DefaultKey;

    /// <summary>What the source view was before a theme touched it, so Default puts
    /// it back exactly rather than to something that looks about right.</summary>
    private (Brush Background, Brush Foreground, Brush? Caret)? _sourceOriginal;

    /// <summary>What the source view is actually showing — what the menu ticks when
    /// the source view is active and unlinked. See <see cref="_appliedKey"/> for why
    /// "showing" and "remembered" are kept apart.</summary>
    private string _sourceAppliedKey = ThemeStore.DefaultKey;

    private void InitializeThemes()
    {
        var (root, fellBack) = ThemeStore.ResolveRoot();
        _themeStore = new ThemeStore(root);
        // The same string the About box shows, leading "v" and all — UpdateVersion
        // strips it, and a second way of asking the assembly its version is a second
        // thing to keep in step with CI's tag-derived InformationalVersion.
        _themeStore.Refresh(AppVersion);

        var appearance = _appearance = WindowsAppearance.Start(Dispatcher);
        _themes = new ThemeModes(_loadedThemeSettings ?? new AppSettings(), IsDarkTheme,
            () => appearance.IsDark, change => SavePersistentField(s => change(s)));
        _loadedThemeSettings = null;
        appearance.Changed += Appearance_Changed;
        Closed += (_, _) => appearance.Dispose();

        if (fellBack)
            FlashStatus("Themes are being kept in your profile — this folder isn't writable.");
    }

    /// <summary>Whether a theme declares itself dark, to migrate a theme saved before
    /// there was one per mode; null when its file can't be read this launch.</summary>
    private bool? IsDarkTheme(string key) =>
        _themeStore is { } store && store.Find(key) is { } theme && store.Read(theme, out _) is { } css
            ? ThemeModes.DeclaresDark(css)
            : null;

    /// <summary>
    /// Windows switched between light and dark mode: show that mode's remembered themes.
    /// A switch is not a choice, so no setting is written — only a pick from View ▸ Theme
    /// writes one. Settings are read again first, so a theme another window picked for
    /// this mode since this one launched is the one shown; that read is TryReadSettings,
    /// which moves a corrupt settings.json aside, as every other read does.
    /// </summary>
    private async void Appearance_Changed(object? sender, EventArgs e)
    {
        if (_themes is null) return;
        if (!_settingsUnknown && TryReadSettings(out var saved) && saved is not null) _themes.Reload(saved);
        await ApplyRememberedThemesAsync();
    }

    // ===== the menu =====

    // Rebuilt each time View opens rather than once at startup, so a file dropped into
    // custom\ shows up without restarting the app — the same reason Open Recent rebuilds.
    // On View's own opening, not Theme's: see MenuAccessKeys.IsOwnSubmenuOpening.
    private void ViewMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (MenuAccessKeys.IsOwnSubmenuOpening(sender, e.OriginalSource)) BuildThemeMenu();
    }

    private void BuildThemeMenu()
    {
        ThemeMenu.Items.Clear();
        if (_themeStore is null || _themes is null) return;

        // Which mode's theme a pick sets. High contrast counts as light.
        ThemeMenu.Items.Add(new MenuItem
        {
            Header = _themes.IsDark ? "For Windows dark mode" : "For Windows light mode",
            IsEnabled = false,
        });
        ThemeMenu.Items.Add(new Separator());

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
                IsChecked = string.Equals(theme.Key,
                    Source.ThemeLinking.TickedKey(_themes.Linked, _sourceMode, _appliedKey, _sourceAppliedKey),
                    StringComparison.OrdinalIgnoreCase),
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
        var link = new MenuItem
        {
            Header = "_Same Theme for Both Views",
            IsCheckable = true,
            IsChecked = _themes.Linked,
            ToolTip = "On: one theme for the formatted and source views. Off: View ▸ Theme " +
                      "changes only the view you're in, so the markdown source view can " +
                      "have its own theme — a dark one under a light document, say.",
        };
        link.Click += LinkThemes_Click;
        ThemeMenu.Items.Add(link);

        ThemeMenu.Items.Add(new Separator());
        var open = new MenuItem
        {
            Header = "_Open Themes Folder",
            ToolTip = "Put your own .css files in the custom folder — they appear here.",
        };
        open.Click += OpenThemesFolder_Click;
        ThemeMenu.Items.Add(open);
    }

    /// <summary>
    /// View ▸ Theme: apply <paramref name="sender"/>'s theme to the view(s) the pick is
    /// for — linked, both; unlinked, only the view the user is in
    /// (<see cref="Source.ThemeLinking"/>) — and remember it for the mode Windows is in
    /// at the click, if it applied. The mode is taken before the await: Windows can
    /// switch while the theme goes on, and the pick still belongs to the mode the menu
    /// named.
    /// </summary>
    private async void ThemeItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string key } || _themes is null) return;
        var (dark, sourceMode) = (_themes.IsDark, _sourceMode);
        var (toDocument, toSource) = Source.ThemeLinking.TargetsFor(_themes.Linked, sourceMode);
        var applied = toDocument
            ? await ApplyDocumentThemeAsync(key, alsoSource: toSource)
            : await ApplySourceThemeAsync(key);
        if (applied is not null) _themes.RememberPick(dark, sourceMode, applied);
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
    /// Install a theme on the document by name. Returns the key that applied, for the
    /// caller that is a pick to remember; null when it didn't. With
    /// <paramref name="alsoSource"/> the source view follows the same read-back (the
    /// linked case); without it the source view is left exactly as it is.
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
    private async Task<string?> ApplyDocumentThemeAsync(string? key, bool alsoSource)
    {
        if (_themeStore is null) return null;

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
            await InstallThemeAsync(string.Empty, alsoSource);
            _appliedKey = ThemeStore.DefaultKey;
            if (alsoSource) _sourceAppliedKey = ThemeStore.DefaultKey;
            BuildThemeMenu();
            return null;
        }

        var css = _themeStore.Read(theme, out var failure);
        if (css is null)
        {
            // The current theme stays applied. A file that has become unreadable
            // since the menu was built is not a reason to also take away what is on
            // screen — the update and backup paths refuse the same way.
            FlashStatus($"Can't use {theme.Name}: {failure}");
            BuildThemeMenu();
            return null;
        }

        await InstallThemeAsync(css, alsoSource);
        _appliedKey = theme.Key;
        if (alsoSource) _sourceAppliedKey = theme.Key;
        BuildThemeMenu();
        return theme.Key;
    }

    /// <summary>
    /// Hand the CSS to the page and take back what it resolved to.
    ///
    /// ExecuteScriptAsync directly rather than through RunEditorAsync, which
    /// deserializes its result as a string — this one returns an object, and reading
    /// it as the raw JSON that fell out of a failed string cast would work by
    /// accident rather than on purpose.
    /// </summary>
    private async Task InstallThemeAsync(string css, bool alsoSource)
    {
        // Nothing to install into yet, and nothing to report either: the menu is live
        // from the moment the window opens but the editor takes a moment, so an early
        // click lands here. Remembering the choice anyway is correct rather than
        // sloppy — the 'ready' handler applies the remembered theme the instant there is a page,
        // so the theme is simply already on when the editor appears. Do not "fix"
        // this into an error message without moving that apply.
        if (!_editorReady || Web.CoreWebView2 is null) return;
        try
        {
            var raw = await Web.CoreWebView2.ExecuteScriptAsync($"window.MDM.setTheme({JsLiteral(css)})");
            // Unlinked and applying to the document only: the source view keeps its own
            // theme, so the read-back must not be pushed onto it.
            if (alsoSource) ApplySourceColors(raw);
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
            SourceBox.LineNumbersForeground = Brushes.Gray;   // AvalonEdit's own, readable on the original pane
            SourceBox.MarksBrush = Source.FormattingMarks.BrushFor(null);   // the ¶ and → marks: the default theme's mark grey
            FlashStatus("The theme was applied, but the markdown source view couldn't follow it.");
            return;
        }

        SourceBox.Background = new SolidColorBrush(read.Background);
        SourceBox.Foreground = new SolidColorBrush(read.Foreground);
        // Without this the caret keeps WPF's default black and disappears entirely on
        // a dark theme — the pane looks right and typing looks broken.
        SourceBox.CaretBrush = new SolidColorBrush(read.Foreground);
        SourceBox.LineNumbersForeground = new SolidColorBrush(read.Foreground) { Opacity = 0.5 };   // the gutter: the text, dimmed
        SourceBox.MarksBrush = Source.FormattingMarks.BrushFor(read);   // the ¶ and → marks: the text, fainter still

        ApplySourceSyntax(json);
    }

    /// <summary>
    /// Recolour the markdown syntax highlighting from the same read-back.
    ///
    /// Separate from the background/foreground above and deliberately quiet when it
    /// can't proceed: the page-level palette (heading/link/quote) is an ADDITION to
    /// the read-back, so an older bundle that doesn't send it, or a theme that resolves
    /// the base colours but not these, leaves the highlighting on its previous palette
    /// rather than flashing a warning. The base pane is already themed by the time we
    /// get here, so there is no half-themed state to announce.
    /// </summary>
    private void ApplySourceSyntax(string? json)
    {
        if (_sourceHighlighting is null) return;
        if (Source.SourcePalette.Parse(json) is not { } palette) return;
        _sourceHighlighting.SetPalette(SourceBox, palette);
    }

    // ===== the source view's own theme (unlinked) =====

    /// <summary>
    /// Give the source view a theme of its own, resolved WITHOUT applying it to the
    /// document: <c>MDM.resolveTheme</c> installs it in a hidden frame that carries
    /// the bundle's layers but not the page's theme, and probes it with the same code
    /// the document read-back uses. Same fallbacks as the document: a missing file
    /// falls back to Default and keeps the preference; an unreadable one changes
    /// nothing. Returns the key that applied, or null.
    /// </summary>
    private async Task<string?> ApplySourceThemeAsync(string? key)
    {
        if (_themeStore is null) return null;

        var theme = _themeStore.Find(key);
        if (theme is null)
        {
            if (!string.IsNullOrEmpty(key))
                FlashStatus($"Theme \"{key}\" isn't in the themes folder — the source view is using Default for now.");
            await ResolveSourceThemeAsync(string.Empty);
            _sourceAppliedKey = ThemeStore.DefaultKey;
            BuildThemeMenu();
            return null;
        }

        var css = _themeStore.Read(theme, out var failure);
        if (css is null)
        {
            FlashStatus($"Can't use {theme.Name}: {failure}");
            BuildThemeMenu();
            return null;
        }

        await ResolveSourceThemeAsync(css);
        _sourceAppliedKey = theme.Key;
        BuildThemeMenu();
        return theme.Key;
    }

    private async Task ResolveSourceThemeAsync(string css)
    {
        // Same early-out as InstallThemeAsync: no page yet means the 'ready' handler
        // will apply the remembered choice the moment there is one.
        if (!_editorReady || Web.CoreWebView2 is null) return;
        try
        {
            var raw = await Web.CoreWebView2.ExecuteScriptAsync($"window.MDM.resolveTheme({JsLiteral(css)})");
            ApplySourceColors(raw);
        }
        catch (Exception ex)
        {
            FlashStatus("The source view's theme couldn't be applied: " + ex.Message);
        }
    }

    private async void LinkThemes_Click(object sender, RoutedEventArgs e)
    {
        if (_themes is null) return;
        // Persists the setting, and starts the source view's own themes, in both modes,
        // from the document's (ThemeModes.RememberLink).
        _themes.RememberLink(!_themes.Linked);
        if (_themes.Linked)
        {
            // Relinking: the source view snaps to the document theme. Re-installing the
            // document's REMEMBERED theme (not what happens to be on screen) is what
            // re-derives the source colours, and it keeps the preference intact when
            // the remembered file is missing this launch.
            await ApplyDocumentThemeAsync(_themes.DocumentKey, alsoSource: true);
        }
        // Unlinking changes nothing on screen. From here View ▸ Theme is per-view, and
        // the source view starts from what the document is showing.
        _sourceAppliedKey = _appliedKey;
        BuildThemeMenu();
        RefocusEditor();
    }

    /// <summary>At editor-ready, and when Windows switches mode: the document's theme
    /// for the mode, then the source view's own when the two are unlinked. Linked, the
    /// document read-back already dressed the source view. Applies only; writes no
    /// setting. (The mode-switch caller reads settings first: see Appearance_Changed.)</summary>
    private async Task ApplyRememberedThemesAsync()
    {
        if (_themes is null) return;
        var (document, source) = _themes.Remembered();
        await ApplyDocumentThemeAsync(document, alsoSource: source is null);
        if (source is not null) await ApplySourceThemeAsync(source);
    }
}
