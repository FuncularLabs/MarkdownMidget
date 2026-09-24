using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using MarkdownMidget.Source;
using MarkdownMidget.Themes;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>
/// A theme per Windows mode. What regresses here is a user's choice landing in the
/// wrong mode, a migration that throws a saved theme away, or a Windows mode switch
/// that quietly rewrites settings.json.
/// </summary>
public class ThemeModesTests
{
    private sealed class Saved : IThemeSettings
    {
        public string Theme { get; set; } = "";
        public string? SourceTheme { get; set; }
        public bool LinkThemes { get; set; } = true;
        public string? ThemeLight { get; set; }
        public string? ThemeDark { get; set; }
        public string? SourceThemeLight { get; set; }
        public string? SourceThemeDark { get; set; }

        public Saved Copy() => (Saved)MemberwiseClone();
    }

    /// <summary>What the theme files say about themselves; null is a file this launch can't read.</summary>
    private static bool? Kind(string key) => key switch
    {
        "Dracula.css" or "mine-dark.css" => true,
        "gone.css" => null,
        _ => false,
    };

    /// <summary>A window's ThemeModes over <see cref="Disk"/>, which stands in for
    /// settings.json: every write goes through <see cref="Writes"/>.</summary>
    private sealed class Window
    {
        public readonly Saved Disk;
        public bool Dark;
        public int Writes;
        public readonly ThemeModes Modes;

        public Window(Saved disk, bool dark = false)
        {
            Disk = disk;
            Dark = dark;
            Modes = new ThemeModes(disk.Copy(), Kind, () => Dark, apply => { Writes++; apply(Disk); });
        }
    }

    private static Saved Materialized(bool linked = true) => new()
    {
        Theme = "a.css", SourceTheme = "c.css", LinkThemes = linked,
        ThemeLight = "a.css", ThemeDark = "b.css",
        SourceThemeLight = "c.css", SourceThemeDark = "d.css",
    };

    // ===== which slot is applied, and which is ticked =====

    [Theory]
    // linked: the document's slot for the mode, in either view
    [InlineData(false, true, false, "a.css", null, "a.css")]
    [InlineData(false, true, true, "a.css", null, "a.css")]
    [InlineData(true, true, false, "b.css", null, "b.css")]
    [InlineData(true, true, true, "b.css", null, "b.css")]
    // unlinked: each view its own slot for the mode; the menu ticks the view you're in
    [InlineData(false, false, false, "a.css", "c.css", "a.css")]
    [InlineData(false, false, true, "a.css", "c.css", "c.css")]
    [InlineData(true, false, false, "b.css", "d.css", "b.css")]
    [InlineData(true, false, true, "b.css", "d.css", "d.css")]
    public void TheSlotForTheModeWindowsIsInIsAppliedAndTicked(
        bool dark, bool linked, bool sourceMode, string document, string? source, string ticked)
    {
        var window = new Window(Materialized(linked), dark);
        var remembered = window.Modes.Remembered();

        Assert.Equal(document, remembered.Document);
        Assert.Equal(document, window.Modes.DocumentKey);
        Assert.Equal(source, remembered.Source);
        // Linked, the source view shows what the document shows.
        Assert.Equal(ticked, ThemeLinking.TickedKey(linked, sourceMode, remembered.Document, remembered.Source ?? remembered.Document));
        Assert.Equal(0, window.Writes);
    }

    // ===== picks =====

    [Theory]
    [InlineData(true, true, false, nameof(Saved.ThemeDark))]
    [InlineData(false, true, false, nameof(Saved.ThemeLight))]
    [InlineData(true, true, true, nameof(Saved.ThemeDark))]          // linked: the source view's pick is the document's
    [InlineData(false, true, true, nameof(Saved.ThemeLight))]
    [InlineData(true, false, false, nameof(Saved.ThemeDark))]
    [InlineData(false, false, false, nameof(Saved.ThemeLight))]
    [InlineData(true, false, true, nameof(Saved.SourceThemeDark))]
    [InlineData(false, false, true, nameof(Saved.SourceThemeLight))]
    public void APickWritesOnlyTheSlotForTheModeItWasMadeIn(bool dark, bool linked, bool sourceMode, string slot)
    {
        var window = new Window(Materialized(linked), dark);
        var before = window.Disk.Copy();

        window.Modes.RememberPick(dark, sourceMode, "new.css");

        Assert.Equal(1, window.Writes);
        var isSource = slot.StartsWith("Source", StringComparison.Ordinal);
        foreach (var name in new[] { nameof(Saved.ThemeLight), nameof(Saved.ThemeDark), nameof(Saved.SourceThemeLight), nameof(Saved.SourceThemeDark) })
            Assert.Equal(name == slot ? "new.css" : Field(before, name), Field(window.Disk, name));
        // The pre-rc3 field follows the last pick, for an older build on the same settings.
        Assert.Equal(isSource ? before.Theme : "new.css", window.Disk.Theme);
        Assert.Equal(isSource ? "new.css" : before.SourceTheme, window.Disk.SourceTheme);
        // And the window shows it without relaunching.
        Assert.Equal("new.css", isSource ? window.Modes.Source.For(dark) : window.Modes.Document.For(dark));
    }

    [Fact]
    public void APickBelongsToTheModeItWasMadeInEvenIfWindowsSwitchedSince()
    {
        // The click captures the mode; Windows can switch while the theme is applied.
        var window = new Window(Materialized(), dark: true);
        window.Dark = false;
        window.Modes.RememberPick(dark: true, sourceMode: false, "new.css");
        Assert.Equal("new.css", window.Disk.ThemeDark);
        Assert.Equal("a.css", window.Disk.ThemeLight);
    }

    [Fact]
    public void AFirstPickKeepsTheOtherModesMigratedTheme()
    {
        // The replay case. Settings from rc2 have one theme and no slots. Writing only
        // the picked slot would leave the other slot empty, and the next launch would
        // put a default where the user's own light theme was — while Theme, now the
        // pick, could no longer say what it had been.
        var window = new Window(new Saved { Theme = "mine-light.css" }, dark: true);

        window.Modes.RememberPick(dark: true, sourceMode: false, "Dracula.css");

        Assert.Equal("mine-light.css", window.Disk.ThemeLight);
        Assert.Equal("Dracula.css", window.Disk.ThemeDark);
        Assert.Equal("Dracula.css", window.Disk.Theme);
        var relaunched = new Window(window.Disk);
        Assert.Equal(new ThemePair("mine-light.css", "Dracula.css"), relaunched.Modes.Document);
    }

    [Theory]
    [InlineData(false)]   // the document's themes
    [InlineData(true)]    // unlinked, in the source view: its own
    public void APickMergesOntoWhatIsOnDiskNotOntoThisWindowsCopy(bool sourceView)
    {
        // Another window picked a light theme after this one launched. This window's
        // dark pick must not put its stale light theme back.
        var window = new Window(Materialized(linked: !sourceView), dark: true);
        if (sourceView) window.Disk.SourceThemeLight = "other-window.css";
        else window.Disk.ThemeLight = "other-window.css";

        window.Modes.RememberPick(dark: true, sourceMode: sourceView, "new.css");

        Assert.Equal(("other-window.css", "new.css"), sourceView
            ? (window.Disk.SourceThemeLight, window.Disk.SourceThemeDark)
            : (window.Disk.ThemeLight, window.Disk.ThemeDark));
    }

    [Fact]
    public void AFirstSourceViewPickKeepsItsOtherModesMigratedTheme()
    {
        var window = new Window(new Saved { Theme = "One-Light.css", SourceTheme = "mine-light.css", LinkThemes = false }, dark: true);

        window.Modes.RememberPick(dark: true, sourceMode: true, "Dracula.css");

        Assert.Equal(("mine-light.css", "Dracula.css", "Dracula.css"),
            (window.Disk.SourceThemeLight, window.Disk.SourceThemeDark, window.Disk.SourceTheme));
        Assert.Equal(((string?)null, (string?)null, "One-Light.css"), (window.Disk.ThemeLight, window.Disk.ThemeDark, window.Disk.Theme));
        Assert.Equal(new ThemePair("mine-light.css", "Dracula.css"), new Window(window.Disk).Modes.Source);
    }

    // ===== saves of other settings =====

    [Fact]
    public void AnotherSettingsSaveKeepsThemeFieldsAsTheyAreOnDisk()
    {
        // Window B holds every slot in memory. On disk, window A's pick left no light slot
        // and no source theme. B toggles word wrap: its save restates B's preferences, then
        // takes every theme field from disk — nulls too, which mean "never written".
        var window = new Window(Materialized(linked: false));
        var disk = new Saved { Theme = "GitHub-Dark-Dimmed.css", ThemeDark = "GitHub-Dark-Dimmed.css" };
        var save = new Saved();

        window.Modes.CopyTo(save);
        ThemeModes.CarryFromDisk(disk, save);

        Assert.Equivalent(disk, save);
    }

    [Fact]
    public void ASaveWithNoFileLeavesNeverChosenSlotsEmpty()
    {
        // First run: writing the resolved defaults would make today's defaults a choice.
        var save = new Saved { Theme = "junk.css", ThemeLight = "junk.css" };
        new Window(new Saved()).Modes.CopyTo(save);
        Assert.Equivalent(new Saved(), save);

        // An earlier build's single theme stays as it was, still to be migrated.
        var legacy = new Window(new Saved { Theme = "mine-light.css" });
        legacy.Modes.CopyTo(save);
        Assert.Equivalent(new Saved { Theme = "mine-light.css" }, save);

        // A pick is a choice: its pair is written; the source view's still isn't.
        legacy.Modes.RememberPick(dark: true, sourceMode: false, "Dracula.css");
        legacy.Modes.CopyTo(save);
        Assert.Equivalent(new Saved { Theme = "Dracula.css", ThemeLight = "mine-light.css", ThemeDark = "Dracula.css" }, save);

        // A link toggle is a choice too: the source view's pair is written from the document's.
        legacy.Modes.RememberLink(false);
        legacy.Modes.CopyTo(save);
        Assert.Equal((false, "mine-light.css", "Dracula.css", "mine-light.css"),
            (save.LinkThemes, save.SourceThemeLight, save.SourceThemeDark, save.SourceTheme));

        // Slots loaded from disk were written: they are restated, as the file is gone.
        new Window(Materialized(linked: false)).Modes.CopyTo(save);
        Assert.Equivalent(Materialized(linked: false), save);
    }

    // ===== migration =====

    [Theory]
    [InlineData("One-Light.css", "One-Light.css", "Obsidiminutive.css")]           // built-in light
    [InlineData("mine-light.css", "mine-light.css", "Obsidiminutive.css")]         // custom light
    [InlineData("Dracula.css", "Midget-Solarized.css", "Dracula.css")]             // built-in dark
    [InlineData("mine-dark.css", "Midget-Solarized.css", "mine-dark.css")]         // custom dark
    [InlineData("", "Midget-Solarized.css", "Obsidiminutive.css")]                 // Default: never chosen
    [InlineData(null, "Midget-Solarized.css", "Obsidiminutive.css")]
    [InlineData("gone.css", "gone.css", "Obsidiminutive.css")]                     // not readable this launch: the preference is kept
    public void ASavedThemeMigratesToTheModeItMatches(string? legacy, string light, string dark)
    {
        var window = new Window(new Saved { Theme = legacy! });
        Assert.Equal(new ThemePair(light, dark), window.Modes.Document);
        Assert.Equal(0, window.Writes);   // migrating is not a write; the first pick is
    }

    [Fact]
    public void MigratedSettingsAreLeftAloneLaunchAfterLaunch()
    {
        // Slots present: the old field is history, even when it names a theme of the other mode.
        var disk = new Saved { Theme = "Dracula.css", ThemeLight = "a.css", ThemeDark = "b.css" };
        var before = disk.Copy();
        for (var launch = 0; launch < 2; launch++)
        {
            var window = new Window(disk, dark: launch == 1);
            Assert.Equal(new ThemePair("a.css", "b.css"), window.Modes.Document);
            Assert.Equal(0, window.Writes);
        }
        Assert.Equivalent(before, disk);

        // A slot that holds Default was chosen; only a missing one takes the default.
        Assert.Equal(new ThemePair("", "Obsidiminutive.css"),
            new Window(new Saved { Theme = "Dracula.css", ThemeLight = "" }).Modes.Document);
    }

    [Fact]
    public void TheSourceViewsOwnThemeMigratesByTheSameRule()
    {
        var own = new Window(new Saved { Theme = "One-Light.css", SourceTheme = "Dracula.css", LinkThemes = false });
        Assert.Equal(new ThemePair("Midget-Solarized.css", "Dracula.css"), own.Modes.Source);

        // Never given one: it follows the document, as SourceTheme = null always meant.
        var none = new Window(new Saved { ThemeLight = "a.css", ThemeDark = "b.css", Theme = "b.css" });
        Assert.Equal(new ThemePair("a.css", "b.css"), none.Modes.Source);
    }

    // ===== a Windows mode switch =====

    [Fact]
    public void SwitchingWindowsModeAppliesTheOtherSlotAndWritesNothing()
    {
        var window = new Window(Materialized(linked: false));
        var before = window.Disk.Copy();
        Assert.Equal(("a.css", (string?)"c.css"), window.Modes.Remembered());

        window.Dark = true;
        window.Modes.Reload(window.Disk.Copy());   // what the window does on the switch
        Assert.Equal(("b.css", (string?)"d.css"), window.Modes.Remembered());

        window.Dark = false;
        window.Modes.Reload(window.Disk.Copy());
        Assert.Equal(("a.css", (string?)"c.css"), window.Modes.Remembered());

        Assert.Equal(0, window.Writes);
        Assert.Equivalent(before, window.Disk);
    }

    [Fact]
    public void AModeSwitchShowsWhatAnotherWindowPickedForThatMode()
    {
        var window = new Window(Materialized());
        window.Disk.ThemeDark = "other-window.css";
        window.Dark = true;
        window.Modes.Reload(window.Disk.Copy());
        Assert.Equal("other-window.css", window.Modes.Remembered().Document);
        Assert.Equal(0, window.Writes);
    }

    // ===== View ▸ Mode =====

    [Fact]
    public void APickUnderAModeOverrideWritesThatModesSlot()
    {
        // Windows light, View ▸ Mode Dark: the pick is dark mode's theme.
        var disk = Materialized();
        using var appearance = new WindowsAppearance(() => 1, () => false, savedMode: () => AppearanceMode.Dark);
        var modes = new ThemeModes(disk.Copy(), Kind, () => appearance.IsDark, apply => apply(disk));

        modes.RememberPick(modes.IsDark, sourceMode: false, "Dracula.css");   // what ThemeItem_Click passes

        Assert.Equal(("a.css", "Dracula.css"), (disk.ThemeLight, disk.ThemeDark));
    }

    [Fact]
    public void SwitchingTheModeOverrideAppliesTheOtherSlotAndWritesNothing()
    {
        var disk = Materialized(linked: false);
        var before = disk.Copy();
        var writes = 0;
        using var appearance = new WindowsAppearance(() => 1, () => false);   // Windows light, System
        var modes = new ThemeModes(disk.Copy(), Kind, () => appearance.IsDark, apply => { writes++; apply(disk); });
        var shown = new List<(string, string?)>();
        appearance.Changed += (_, _) => { modes.Reload(disk.Copy()); shown.Add(modes.Remembered()); };   // Appearance_Changed

        appearance.SetMode(AppearanceMode.Dark);
        appearance.SetMode(AppearanceMode.System);  // Windows light: back to light
        appearance.SetMode(AppearanceMode.Light);   // light already: nothing to apply
        appearance.SetMode(AppearanceMode.Dark);
        appearance.SetMode(AppearanceMode.System);
        Assert.Equal(new (string, string?)[] { ("b.css", "d.css"), ("a.css", "c.css"), ("b.css", "d.css"), ("a.css", "c.css") }, shown);
        Assert.Equal(0, writes);
        Assert.Equivalent(before, disk);
    }

    // ===== Same Theme for Both Views =====

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TurningLinkingOnOrOffStartsTheSourceViewFromTheDocumentInBothModes(bool dark)
    {
        var window = new Window(Materialized(linked: true), dark);

        window.Modes.RememberLink(false);

        Assert.False(window.Modes.Linked);
        Assert.Equal(new ThemePair("a.css", "b.css"), window.Modes.Source);
        Assert.False(window.Disk.LinkThemes);
        Assert.Equal(("a.css", "b.css"), (window.Disk.SourceThemeLight, window.Disk.SourceThemeDark));
        Assert.Equal(dark ? "b.css" : "a.css", window.Disk.SourceTheme);
        Assert.Equal(("a.css", "b.css"), (window.Disk.ThemeLight, window.Disk.ThemeDark));   // the document's are untouched

        window.Modes.RememberLink(true);
        Assert.True(window.Disk.LinkThemes);
        Assert.Null(window.Modes.Remembered().Source);
    }

    [Fact]
    public void UnlinkingTakesTheDocumentsThemesFromDiskNotFromThisWindow()
    {
        // This window launched on a.css/b.css; another window has since picked both.
        var window = new Window(Materialized(linked: true));
        (window.Disk.ThemeLight, window.Disk.ThemeDark) = ("Dracula.css", "other.css");

        window.Modes.RememberLink(false);

        Assert.Equal(("Dracula.css", "other.css", "Dracula.css"),
            (window.Disk.SourceThemeLight, window.Disk.SourceThemeDark, window.Disk.SourceTheme));
    }

    // ===== reading a theme's mode =====

    [Theory]
    [InlineData(":root { --mdm-color-scheme: dark; }", true)]
    [InlineData(":root {\n  --mdm-color-scheme:dark\n}", true)]
    [InlineData(":root { --mdm-color-scheme: light; }", false)]
    [InlineData(":root { --mdm-text: #fff; }", false)]                                          // undeclared is light, as base.css falls back
    [InlineData(":root { --mdm-color-scheme: light; /* --mdm-color-scheme: dark; */ }", false)]  // a comment declares nothing
    [InlineData(":root { --mdm-color-scheme: light; } :root { --mdm-color-scheme: dark; }", true)] // the later declaration wins
    [InlineData(":root { --mdm-color-scheme: DARK !important; }", true)]
    [InlineData(":root { --mdm-color-scheme: light dark; }", false)]
    [InlineData(":root { --mdm-color-scheme-note: dark; }", false)]
    [InlineData("a::before{content:\"/*\"} :root{--mdm-color-scheme: dark;} b::after{content:\"*/\"}", true)]  // not a comment: strings
    [InlineData("a::before{content:\"}\"} :root{--mdm-color-scheme: dark}", true)]
    [InlineData("a::before{content:\"\\\"}\"} :root{--mdm-color-scheme: dark}", true)]  // an escaped quote doesn't end the string
    [InlineData(":root { background: url(data:x,}); --mdm-color-scheme: dark }", true)]     // not a block: parentheses
    [InlineData(":root { /* the scheme; */ --mdm-color-scheme: dark; }", true)]
    [InlineData(":root { --mdm-color-scheme: light; } @media (prefers-color-scheme: dark) { :root { --mdm-color-scheme: dark; } }", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void AThemesModeIsTheColorSchemeItDeclares(string? css, bool dark)
        => Assert.Equal(dark, ThemeModes.DeclaresDark(css));

    [Fact]
    public void ReadingAThemesModeIsOnePassEvenOverAFullSizeTheme()
    {
        // A theme the validator accepts, at the size cap, with "/*" in every string. A
        // comment stripper that doesn't know strings rescans the rest of the file from
        // each one: 15.9 s measured, on the UI thread, at launch and on a mode switch.
        const string rule = "a{content:\"/*\"}\n";
        var css = string.Concat(Enumerable.Repeat(rule, ThemeStore.MaxBytes / rule.Length));
        Assert.Null(CssValidator.Validate(css));

        var clock = Stopwatch.StartNew();
        Assert.False(ThemeModes.DeclaresDark(css));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"took {clock.Elapsed.TotalSeconds:0.0} s");
    }

    [Theory]
    [InlineData("themes/Amber-Phosphor.css", true)]
    [InlineData("themes/Dracula.css", true)]
    [InlineData("themes/GitHub-Dark-Dimmed.css", true)]
    [InlineData("themes/Midget-Solarized.css", false)]
    [InlineData("themes/One-Light.css", false)]
    public void TheShippedThemesAreClassifiedFromTheirOwnText(string resource, bool dark)
    {
        using var stream = typeof(ThemeStore).Assembly.GetManifestResourceStream(resource);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        Assert.Equal(dark, ThemeModes.DeclaresDark(reader.ReadToEnd()));
    }

    private static string? Field(Saved s, string name) => name switch
    {
        nameof(Saved.ThemeLight) => s.ThemeLight,
        nameof(Saved.ThemeDark) => s.ThemeDark,
        nameof(Saved.SourceThemeLight) => s.SourceThemeLight,
        nameof(Saved.SourceThemeDark) => s.SourceThemeDark,
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };
}
