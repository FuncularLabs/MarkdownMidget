using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MarkdownMidget.Source;

namespace MarkdownMidget.Themes;

/// <summary>The settings.json fields a theme choice lives in. MainWindow's AppSettings
/// is the real one; the interface lets <see cref="ThemeModes"/> be tested on a plain object.</summary>
internal interface IThemeSettings
{
    /// <summary>The one document theme before 1.0.0-rc3. Still written, as the last theme
    /// picked in either mode, so an older build run on these settings shows a theme the
    /// user chose. Read only to migrate settings that have no per-mode slots yet.</summary>
    string Theme { get; set; }
    /// <summary>The same, for the source view's own theme.</summary>
    string? SourceTheme { get; set; }
    bool LinkThemes { get; set; }
    // Null means never written by a build that has slots. "" is Default, chosen.
    string? ThemeLight { get; set; }
    string? ThemeDark { get; set; }
    string? SourceThemeLight { get; set; }
    string? SourceThemeDark { get; set; }
}

/// <summary>One theme key for Windows light mode and one for dark mode.</summary>
internal readonly record struct ThemePair(string Light, string Dark)
{
    public string For(bool dark) => dark ? Dark : Light;
    public ThemePair With(bool dark, string key) => dark ? this with { Dark = key } : this with { Light = key };
}

/// <summary>
/// A theme per Windows mode. The document remembers one theme for light mode and one
/// for dark, and so does the source view's own theme while "Same Theme for Both Views"
/// is off. View ▸ Theme sets the slot for the mode Windows was in when it was picked; a
/// switch of Windows mode shows the other slot and writes nothing, because it is not a
/// choice.
///
/// Only <see cref="RememberPick"/> and <see cref="RememberLink"/> change them, both
/// through the window's merge-on-write persister; a save of other settings restates them
/// through <see cref="CarryFromDisk"/> or <see cref="CopyTo"/>. Settings with no slots yet (saved by
/// an earlier build) are migrated in memory on every launch, and the first pick writes
/// both slots, so the migrated slot for the other mode is not lost.
/// </summary>
internal sealed class ThemeModes
{
    public const string LightDefault = "Midget-Solarized.css";

    /// <summary>Obsidiminutive ships as Themes\builtin\Obsidiminutive.css, the dark
    /// built-in added for 1.0.0-rc3. A themes folder without the file shows Default
    /// in dark mode and keeps this key, like any remembered theme that isn't there.</summary>
    public const string DarkDefault = "Obsidiminutive.css";

    private readonly Func<string, bool?> _isDarkTheme;
    private readonly Func<bool> _windowsIsDark;
    private readonly Action<Action<IThemeSettings>> _persist;

    /// <param name="saved">Settings as loaded.</param>
    /// <param name="isDarkTheme">Whether a theme file declares itself dark; null when it
    /// can't be read this launch. Used only to migrate a saved single theme.</param>
    /// <param name="windowsIsDark">Windows' effective app mode, now.</param>
    /// <param name="persist">Applies a change to the settings on disk (read, change, write).</param>
    public ThemeModes(IThemeSettings saved, Func<string, bool?> isDarkTheme, Func<bool> windowsIsDark,
                      Action<Action<IThemeSettings>> persist)
    {
        _isDarkTheme = isDarkTheme;
        _windowsIsDark = windowsIsDark;
        _persist = persist;
        Linked = saved.LinkThemes;
        Reload(saved);
    }

    // What settings.json holds besides the pairs: the pre-rc3 fields, and whether each
    // pair has been written — as loaded, or since written by this window.
    private string? _theme, _sourceTheme;
    private bool _documentWritten, _sourceWritten;

    public ThemePair Document { get; private set; }
    public ThemePair Source { get; private set; }
    public bool Linked { get; private set; }
    public bool IsDark => _windowsIsDark();
    public string DocumentKey => Document.For(IsDark);

    /// <summary>What a launch or a Windows mode switch applies: the document's theme for
    /// the mode, and the source view's own when unlinked (null: it follows the document).</summary>
    public (string Document, string? Source) Remembered()
    {
        var dark = IsDark;
        return (Document.For(dark), Linked ? null : Source.For(dark));
    }

    /// <summary>Take the slots from settings read again, such as a pick another window
    /// made since this one launched. Reads only.</summary>
    public void Reload(IThemeSettings saved)
    {
        Document = DocumentPair(saved);
        Source = SourcePair(saved);
        (_theme, _sourceTheme) = (saved.Theme, saved.SourceTheme);
        _documentWritten = saved.ThemeLight is not null || saved.ThemeDark is not null;
        _sourceWritten = saved.SourceThemeLight is not null || saved.SourceThemeDark is not null;
    }

    /// <summary>
    /// A theme picked from View ▸ Theme, which applied, for the mode Windows was in when
    /// it was clicked. Linked, or in the formatted view, it is the document's; unlinked in
    /// the source view, the source view's own (<see cref="ThemeLinking"/>). Writes that
    /// view's slot for that mode, and both slots of the pair: the other one as it is on
    /// disk, migrated if it has never been written.
    /// </summary>
    public void RememberPick(bool dark, bool sourceMode, string key)
    {
        if (ThemeLinking.TargetsFor(Linked, sourceMode).Document)
        {
            Document = Document.With(dark, key);
            (_theme, _documentWritten) = (key, true);
            _persist(s =>
            {
                var pair = DocumentPair(s).With(dark, key);
                (s.ThemeLight, s.ThemeDark, s.Theme) = (pair.Light, pair.Dark, key);
            });
        }
        else
        {
            Source = Source.With(dark, key);
            (_sourceTheme, _sourceWritten) = (key, true);
            _persist(s =>
            {
                var pair = SourcePair(s).With(dark, key);
                (s.SourceThemeLight, s.SourceThemeDark, s.SourceTheme) = (pair.Light, pair.Dark, key);
            });
        }
    }

    /// <summary>"Same Theme for Both Views" turned on or off. Either way the source view's
    /// own themes start from the document's, in both modes: relinked, it shows the
    /// document's; unlinked, nothing changes on screen. Saved from the document's themes
    /// as they are on disk, which may hold another window's newer picks.</summary>
    public void RememberLink(bool linked)
    {
        var dark = IsDark;
        Linked = linked;
        Source = Document;
        (_sourceTheme, _sourceWritten) = (Document.For(dark), true);
        _persist(s =>
        {
            var pair = DocumentPair(s);
            s.LinkThemes = linked;
            (s.SourceThemeLight, s.SourceThemeDark, s.SourceTheme) = (pair.Light, pair.Dark, pair.For(dark));
        });
    }

    /// <summary>This window's theme settings as settings.json holds them, for a save that
    /// restates the window's preferences: a pair never written stays null, so a first run
    /// doesn't make today's defaults a choice.</summary>
    public void CopyTo(IThemeSettings target)
    {
        (target.Theme, target.SourceTheme, target.LinkThemes) = (_theme ?? ThemeStore.DefaultKey, _sourceTheme, Linked);
        target.ThemeLight = _documentWritten ? Document.Light : null;
        target.ThemeDark = _documentWritten ? Document.Dark : null;
        target.SourceThemeLight = _sourceWritten ? Source.Light : null;
        target.SourceThemeDark = _sourceWritten ? Source.Dark : null;
    }

    /// <summary>For a save of other settings over a file that exists: every theme field as
    /// it is on disk, nulls included. Only a pick or a link toggle writes them; a window
    /// that toggled word wrap holds older ones, or migrated ones nobody chose.</summary>
    public static void CarryFromDisk(IThemeSettings disk, IThemeSettings target)
    {
        (target.Theme, target.SourceTheme, target.LinkThemes) = (disk.Theme, disk.SourceTheme, disk.LinkThemes);
        (target.ThemeLight, target.ThemeDark) = (disk.ThemeLight, disk.ThemeDark);
        (target.SourceThemeLight, target.SourceThemeDark) = (disk.SourceThemeLight, disk.SourceThemeDark);
    }

    private ThemePair DocumentPair(IThemeSettings s) => Migrate(s.ThemeLight, s.ThemeDark, s.Theme);

    // No source theme of its own ever saved: it follows the document, as a null
    // SourceTheme always meant.
    private ThemePair SourcePair(IThemeSettings s) =>
        s.SourceThemeLight is null && s.SourceThemeDark is null && s.SourceTheme is null
            ? DocumentPair(s)
            : Migrate(s.SourceThemeLight, s.SourceThemeDark, s.SourceTheme);

    /// <summary>
    /// The slots from settings. Either slot written means migrated: a missing one takes
    /// its default. Neither written: the single saved theme goes to the mode it declares
    /// and the other mode gets its default; Default ("") counts as never chosen. A theme
    /// that can't be read this launch keeps its place in the light slot — the only mode
    /// there was before — rather than being replaced by a default.
    /// </summary>
    private ThemePair Migrate(string? light, string? dark, string? legacy)
    {
        if (light is not null || dark is not null) return new(light ?? LightDefault, dark ?? DarkDefault);
        if (string.IsNullOrEmpty(legacy)) return new(LightDefault, DarkDefault);
        return _isDarkTheme(legacy) == true ? new(LightDefault, legacy) : new(legacy, DarkDefault);
    }

    /// <summary>
    /// Whether a theme's CSS declares <c>--mdm-color-scheme: dark</c> — the value the page
    /// hands to <c>color-scheme</c>, so the one the browser itself goes by. Only
    /// declarations outside at-rules count, so an <c>@media (prefers-color-scheme: dark)</c>
    /// override doesn't make a light theme dark; the last of them wins; none means light,
    /// as base.css falls back. "light dark" is not a dark theme.
    ///
    /// One pass that skips comments, quoted strings and parentheses the way CssValidator
    /// scans: a comment stripper that didn't know strings rescanned the rest of the file
    /// from every "/*" inside one.
    /// </summary>
    public static bool DeclaresDark(string? css)
    {
        const string name = "--mdm-color-scheme";   // custom property names are case-sensitive
        if (string.IsNullOrEmpty(css)) return false;
        string? declared = null;
        var blocks = new Stack<bool>();              // each open block: inside an at-rule?
        var statement = new StringBuilder();
        var paren = 0;
        for (var i = 0; i < css.Length; i++)
        {
            var c = css[i];
            if (c == '/' && i + 1 < css.Length && css[i + 1] == '*')
            {
                var end = css.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0) break;
                i = end + 1;
                continue;
            }
            if (c is '"' or '\'')
            {
                var j = i + 1;   // to the closing quote, over escapes; a newline ends a bad string
                while (j < css.Length && css[j] != c && css[j] is not ('\n' or '\r' or '\f'))
                    j += css[j] == '\\' ? 2 : 1;
                statement.Append(c);
                i = j;
                continue;
            }
            if (paren > 0 || c is not (';' or '{' or '}'))
            {
                if (c == '(') paren++;
                else if (c == ')' && paren > 0) paren--;
                statement.Append(c);
                continue;
            }

            var text = statement.ToString().Trim();
            statement.Clear();
            var inAtRule = blocks.Count > 0 && blocks.Peek();
            if (c == '{')
            {
                blocks.Push(inAtRule || text.StartsWith('@'));
                continue;
            }
            if (blocks.Count > 0 && !inAtRule && text.StartsWith(name, StringComparison.Ordinal)
                && text[name.Length..].TrimStart() is { Length: > 0 } rest && rest[0] == ':')
                declared = rest[1..];
            if (c == '}' && blocks.Count > 0) blocks.Pop();
        }

        var words = (declared ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return words.Contains("dark", StringComparer.OrdinalIgnoreCase)
            && !words.Contains("light", StringComparer.OrdinalIgnoreCase);
    }
}
