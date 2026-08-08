using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using MarkdownMidget.Themes;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>
/// The shipped palettes.
///
/// Screenshots are the wrong instrument here — the failures that matter are the
/// ones you don't notice: a variable left undeclared so a light value survives into
/// a dark page, or a squiggle that technically renders and cannot be seen. Both are
/// measurable, so they're measured.
/// </summary>
public class BuiltInThemeTests
{
    private static readonly Assembly App = typeof(ThemeStore).Assembly;

    /// <summary>The palette every theme is a replacement for. Linked into the test
    /// output rather than copied, so it can't drift from the shipped one.</summary>
    private static IReadOnlyDictionary<string, string> DefaultVars =>
        Variables(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "theme-default.css")));

    public static TheoryData<string> BuiltIns
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var name in App.GetManifestResourceNames()
                         .Where(n => n.StartsWith("themes/", StringComparison.Ordinal))
                         .OrderBy(n => n, StringComparer.Ordinal))
                data.Add(name);
            return data;
        }
    }

    [Fact]
    public void TheThemesTheMenuPromisesAreAllHere()
    {
        // Named individually, because a csproj glob that stops matching fails by
        // shipping nothing — and every other test in this file is a [Theory] over
        // whatever it found, which passes vacuously on an empty set.
        var shipped = App.GetManifestResourceNames()
            .Where(n => n.StartsWith("themes/", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[]
        {
            "themes/Dracula.css",
            "themes/GitHub-Dark-Dimmed.css",
            "themes/GitHub-Light.css",
            "themes/One-Light.css",
            "themes/Solarized-Light.css",
        }, shipped);
    }

    [Fact]
    public void TheirNamesSurviveTheTripThroughTheMenu()
        // The filenames carry their own capitalisation because the display name is
        // derived from them and nothing else — `github-light.css` would appear as
        // "Github Light", which is not what the theme is called.
        => Assert.Equal(
            new[] { "Dracula", "GitHub Dark Dimmed", "GitHub Light", "One Light", "Solarized Light" },
            App.GetManifestResourceNames()
                .Where(n => n.StartsWith("themes/", StringComparison.Ordinal))
                .OrderBy(n => n, StringComparer.Ordinal)
                .Select(n => ThemeStore.DisplayName(Path.GetFileName(n)))
                .ToArray());

    [Theory]
    [MemberData(nameof(BuiltIns))]
    public void EveryThemeIsOneTheAppWouldAccept(string resource)
        // A built-in refused by our own validator would arrive greyed out in the
        // menu, which is a bad first impression and a worse bug report.
        => Assert.Null(CssValidator.Validate(Read(resource)));

    [Theory]
    [MemberData(nameof(BuiltIns))]
    public void EveryThemeSetsEveryVariable(string resource)
    {
        // The one failure that hides. An unset variable falls through to Default's
        // value — which is correct behaviour, and is exactly why a dark theme that
        // forgets --mdm-td-bg gets white table cells and looks like a rendering bug
        // rather than a missing line.
        var mine = Variables(Read(resource));
        var missing = DefaultVars.Keys.Where(k => !mine.ContainsKey(k)).OrderBy(k => k).ToArray();
        Assert.Equal(Array.Empty<string>(), missing);
    }

    [Theory]
    [MemberData(nameof(BuiltIns))]
    public void NoThemeInventsAVariableNothingReads(string resource)
    {
        // The other direction: a name with a typo in it sets nothing and silently
        // leaves the real variable at Default's value.
        var extra = Variables(Read(resource)).Keys
            .Where(k => !DefaultVars.ContainsKey(k)).OrderBy(k => k).ToArray();
        Assert.Equal(Array.Empty<string>(), extra);
    }

    [Theory]
    [MemberData(nameof(BuiltIns))]
    public void EveryThemeNamesAMermaidThemeMermaidKnows(string resource)
    {
        // An unknown name makes mermaid throw, and every diagram in the document
        // becomes an error box. The JS falls back rather than propagating that, but
        // a built-in relying on the fallback is a built-in with a typo in it.
        var value = Variables(Read(resource))["--mdm-mermaid-theme"];
        Assert.Contains(value, new[] { "default", "dark", "neutral", "forest", "base" });
    }

    [Theory]
    [MemberData(nameof(BuiltIns))]
    public void ADarkThemeSaysSoToTheBrowser(string resource)
    {
        // color-scheme is not a colour and is the one every dark theme forgets:
        // leave it `light` and the scrollbar, form controls and default canvas stay
        // light against a dark page.
        var vars = Variables(Read(resource));
        var scheme = vars["--mdm-color-scheme"];
        Assert.Contains(scheme, new[] { "light", "dark" });

        // Derived from the page colour rather than trusted: a theme whose page is
        // darker than its text is a dark theme whatever it declares.
        var page = Rgb(vars["--mdm-page-bg"]);
        var expected = Luminance(page) < 0.5 ? "dark" : "light";
        Assert.Equal(expected, scheme);
    }

    // ===== the four things a good-looking palette breaks =====

    [Theory]
    [MemberData(nameof(BuiltIns))]
    public void BodyTextIsReadable(string resource)
    {
        // 4.5:1 is WCAG AA for body text, and every shipped theme clears it except
        // one — Solarized, at 4.12:1, which is not an oversight but the entire point
        // of Solarized: base00 on base3 is a deliberately reduced contrast chosen so
        // long reading sessions hurt less. Exempted by name, with the measurement
        // recorded, so that any OTHER theme dropping below AA still fails here.
        var vars = Variables(Read(resource));
        var floor = resource.Contains("Solarized", StringComparison.Ordinal) ? 4.0 : 4.5;
        AssertContrast(vars, "--mdm-text", floor, resource);
    }

    [Theory]
    [MemberData(nameof(BuiltIns))]
    public void TheSquiggleAndTheResizeHandleClearTheNonTextFloor(string resource)
    {
        // 3:1, which is WCAG 1.4.11 for a non-text indicator you are meant to see and
        // a control you are meant to grab. This is the check that catches the classic
        // dark-theme mistake of keeping the default #e51d1d squiggle, which measures
        // 1.5:1 on Dracula's page and is effectively invisible.
        var vars = Variables(Read(resource));
        AssertContrast(vars, "--mdm-squiggle", 3.0, resource);
        AssertContrast(vars, "--mdm-resize-handle", 3.0, resource);
    }

    [Theory]
    [MemberData(nameof(BuiltIns))]
    public void FormattingMarksAreFaintButNotAbsent(string resource)
        // Pilcrows and dots are meant to be unobtrusive — the default measures 1.69:1
        // and holding them to 3:1 would make every theme shout. The floor is only
        // there to catch a mark set to the page colour, which renders and shows
        // nothing.
        => AssertContrast(Variables(Read(resource)), "--mdm-mark", 1.5, resource);

    [Theory]
    [MemberData(nameof(BuiltIns))]
    public void TheSelectedCellTintStaysATint(string resource)
    {
        // It paints OVER the cell's text, so it has to be translucent: opaque hides
        // what you selected, and near-zero alpha means the selection is invisible.
        // A hex value here would be opaque, which is why the shape is asserted and
        // not just the alpha.
        var value = Variables(Read(resource))["--mdm-cell-selected"];
        var m = Regex.Match(value, @"^rgba\(\s*\d+\s*,\s*\d+\s*,\s*\d+\s*,\s*([\d.]+)\s*\)$");
        Assert.True(m.Success, $"{resource}: --mdm-cell-selected is {value}, which is not an rgba() tint");

        var alpha = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        Assert.InRange(alpha, 0.10, 0.50);
    }

    [Fact]
    public void TheDefaultPaletteMeetsItsOwnBar()
    {
        // The thresholds above are only honest if the palette that has shipped all
        // along clears them too — otherwise they are numbers picked to fit whatever
        // was written last.
        var vars = DefaultVars;
        AssertContrast(vars, "--mdm-text", 4.5, "theme-default.css");
        AssertContrast(vars, "--mdm-squiggle", 3.0, "theme-default.css");
        AssertContrast(vars, "--mdm-resize-handle", 3.0, "theme-default.css");
        AssertContrast(vars, "--mdm-mark", 1.5, "theme-default.css");
    }

    // ===== helpers =====

    private static string Read(string resource)
    {
        using var stream = App.GetManifestResourceStream(resource);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        return reader.ReadToEnd();
    }

    /// <summary>Every <c>--mdm-*</c> declaration, comments stripped first so a
    /// commented-out line doesn't count as setting anything.</summary>
    private static Dictionary<string, string> Variables(string css)
    {
        var bare = Regex.Replace(css, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        return Regex.Matches(bare, @"(--mdm-[\w-]+)\s*:\s*([^;}]+)")
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value.Trim());
    }

    private static void AssertContrast(
        IReadOnlyDictionary<string, string> vars, string name, double floor, string where)
    {
        var ratio = Contrast(Rgb(vars[name]), Rgb(vars["--mdm-page-bg"]));
        Assert.True(ratio >= floor,
            $"{where}: {name} ({vars[name]}) is {ratio:0.00}:1 against " +
            $"--mdm-page-bg ({vars["--mdm-page-bg"]}), below the {floor:0.0}:1 floor");
    }

    private static (int R, int G, int B) Rgb(string value)
    {
        var v = value.Trim();
        if (v.StartsWith('#'))
        {
            var hex = v[1..];
            if (hex.Length is 3 or 4)
                hex = string.Concat(hex[..3].Select(c => new string(c, 2)));
            return (Convert.ToInt32(hex[..2], 16),
                    Convert.ToInt32(hex.Substring(2, 2), 16),
                    Convert.ToInt32(hex.Substring(4, 2), 16));
        }
        var m = Regex.Match(v, @"rgba?\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)");
        Assert.True(m.Success, $"can't read {value} as a colour");
        return (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
    }

    /// <summary>WCAG relative luminance.</summary>
    private static double Luminance((int R, int G, int B) c)
    {
        static double Channel(int v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    private static double Contrast((int R, int G, int B) a, (int R, int G, int B) b)
    {
        var (hi, lo) = (Luminance(a), Luminance(b));
        if (hi < lo) (hi, lo) = (lo, hi);
        return (hi + 0.05) / (lo + 0.05);
    }
}
