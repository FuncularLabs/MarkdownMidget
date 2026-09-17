using System;
using System.Linq;
using System.Text.RegularExpressions;
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
/// The only writers are <see cref="RememberPick"/> and <see cref="RememberLink"/>, both
/// through the window's merge-on-write persister. Settings with no slots yet (saved by
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
            _persist(s =>
            {
                var pair = DocumentPair(s).With(dark, key);
                (s.ThemeLight, s.ThemeDark, s.Theme) = (pair.Light, pair.Dark, key);
            });
        }
        else
        {
            Source = Source.With(dark, key);
            _persist(s =>
            {
                var pair = SourcePair(s).With(dark, key);
                (s.SourceThemeLight, s.SourceThemeDark, s.SourceTheme) = (pair.Light, pair.Dark, key);
            });
        }
    }

    /// <summary>"Same Theme for Both Views" turned on or off. Either way the source view's
    /// own themes start from the document's, in both modes: relinked, it shows the
    /// document's; unlinked, nothing changes on screen.</summary>
    public void RememberLink(bool linked)
    {
        Linked = linked;
        Source = Document;
        var (pair, current) = (Document, Document.For(IsDark));
        _persist(s =>
        {
            s.LinkThemes = linked;
            (s.SourceThemeLight, s.SourceThemeDark, s.SourceTheme) = (pair.Light, pair.Dark, current);
        });
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

    private static readonly Regex Comment = new(@"/\*.*?\*/", RegexOptions.Singleline | RegexOptions.CultureInvariant);

    // Custom property names are case-sensitive; the keyword is not.
    private static readonly Regex Scheme = new(@"--mdm-color-scheme\s*:\s*([^;}]*)", RegexOptions.CultureInvariant);

    /// <summary>
    /// Whether a theme's CSS declares <c>--mdm-color-scheme: dark</c> — the value the page
    /// hands to <c>color-scheme</c>, so the one the browser itself goes by. The last
    /// declaration wins; none means light, as base.css falls back. "light dark" is not
    /// a dark theme.
    /// </summary>
    public static bool DeclaresDark(string? css)
    {
        if (string.IsNullOrEmpty(css)) return false;
        if (Scheme.Matches(Comment.Replace(css, " ")).LastOrDefault() is not { } declared) return false;
        var words = declared.Groups[1].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return words.Contains("dark", StringComparer.OrdinalIgnoreCase)
            && !words.Contains("light", StringComparer.OrdinalIgnoreCase);
    }
}
