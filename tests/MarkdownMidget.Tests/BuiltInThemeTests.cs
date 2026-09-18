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
            "themes/Midget-Solarized.css",
            "themes/Obsidiminutive.css",
            "themes/One-Light.css",
            "themes/Solarized-Light.css",
        }, shipped);
    }

    [Fact]
    public void HelpsCountOfBuiltInThemeFilesIsTheNumberThatShip()
    {
        // Help says the number twice — once for the themes folder, once for the
        // Known limits bullet about edits to it being overwritten — and the number
        // is easy to get wrong, because the MENU offers one more theme than the folder
        // holds FILES: Default has no file, it is the palette every other theme
        // replaces. Both sentences are read here so neither can drift alone, and the
        // truth comes from the embedded resources rather than a constant, so adding
        // or removing a palette fails this until Help is told.
        var shipped = App.GetManifestResourceNames()
            .Count(n => n.StartsWith("themes/", StringComparison.Ordinal)
                     && n.EndsWith(".css", StringComparison.OrdinalIgnoreCase));
        var word = new[] { "zero", "one", "two", "three", "four", "five", "six",
                           "seven", "eight", "nine", "ten", "eleven", "twelve" };
        Assert.InRange(shipped, 1, word.Length - 1);

        var claims = Regex.Matches(Help(), @"\b([A-Za-z]+|\d+) built-in theme files\b")
            .Select(m => m.Groups[1].Value)
            .ToArray();
        Assert.True(claims.Length >= 2,
            $"HELP.md states the built-in theme FILE count {claims.Length} time(s); both the " +
            "themes-folder line and the Known limits bullet are supposed to say it");
        foreach (var claim in claims)
            Assert.True(claim.Equals(word[shipped], StringComparison.OrdinalIgnoreCase)
                        || claim == shipped.ToString(CultureInfo.InvariantCulture),
                $"HELP.md says \"{claim} built-in theme files\"; {shipped} .css files ship " +
                "under themes/ (Default is one more THEME and has no file)");
    }

    [Fact]
    public void HelpsCountOfThemeVariablesIsTheNumberThePaletteDefines()
    {
        // Help tells a theme author how many variables there are to set, and the number
        // was "about forty" while the palette grew past that - which is how a count in
        // prose fails: not wrongly, just quietly. The truth is the linked palette, and
        // the two Help names before the count are excluded because it says "and N more".
        var defined = DefaultVars.Count;
        var claim = Regex.Match(Help(), @"`--mdm-page-bg`, `--mdm-text` and (\d+) more");
        Assert.True(claim.Success,
            "HELP.md's Writing your own section no longer says \"`--mdm-page-bg`, " +
            "`--mdm-text` and N more\"; the variable count is what this pins");
        Assert.Equal(defined - 2, int.Parse(claim.Groups[1].Value, CultureInfo.InvariantCulture));
    }

    private static string Help()
    {
        using var stream = App.GetManifestResourceStream("HELP.md");
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        return reader.ReadToEnd();
    }

    [Fact]
    public void TheirNamesSurviveTheTripThroughTheMenu()
        // The filenames carry their own capitalisation because the display name is
        // derived from them and nothing else — `github-light.css` would appear as
        // "Github Light", which is not what the theme is called.
        => Assert.Equal(
            new[] { "Dracula", "GitHub Dark Dimmed", "GitHub Light", "Midget Solarized", "Obsidiminutive", "One Light", "Solarized Light" },
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
        var missing = DefaultVars.Keys
            .Where(k => !mine.ContainsKey(k) && !OptionalVariables.Contains(k))
            .OrderBy(k => k).ToArray();
        Assert.Equal(Array.Empty<string>(), missing);
    }

    /// <summary>
    /// Variables a theme may leave unset because Default's value is what the editor
    /// ALREADY drew, so unset is a choice rather than a light value leaking into a dark
    /// page — and, for each, the value that makes that true. Every one of them arrived
    /// after the palettes below it, which is why none of them sets one.
    /// </summary>
    private static readonly Dictionary<string, string> InertDefaults = new(StringComparer.Ordinal)
    {
        // Bold with no colour of its own: it stays the colour of the text around it,
        // which is how every theme looked before the variable existed.
        ["--mdm-strong"] = "currentcolor",
        // Inline code and list markers were painted by Milkdown's Nord palette, never by
        // us, so Default reproduces nord10 exactly. The value is held to the VENDOR's own
        // token in editor-src/test/theme-parity.test.mjs, which reads it out of the built
        // bundle - so a Nord bump fails there rather than silently recolouring both here.
        ["--mdm-code-fg"] = "#5e81ac",
        ["--mdm-list-marker"] = "#5e81ac",
        // The size .mdm-prosemirror has always been. Not a colour, and the one entry here
        // that is a metric: structure.css derives the document's text from it.
        ["--mdm-font-size"] = "16px",
    };

    private static HashSet<string> OptionalVariables => new(InertDefaults.Keys, StringComparer.Ordinal);

    [Fact]
    public void OptionalVariablesReallyAreInertInDefault()
    {
        // The exemption above is only safe while Default's value renders what the editor
        // rendered before the variable existed. Give --mdm-strong a real colour, or
        // --mdm-code-fg anything but Nord's, and every theme that leaves it unset changes
        // appearance - the failure the exemption skips. A metric is the same bargain:
        // 16px is what the editor was, so unset is invisible.
        foreach (var (name, inert) in InertDefaults)
            Assert.Equal(inert, DefaultVars[name].ToLowerInvariant());

        // And the list is only honest while every name on it is a real variable.
        Assert.Equal(Array.Empty<string>(),
            InertDefaults.Keys.Where(k => !DefaultVars.ContainsKey(k)).OrderBy(k => k).ToArray());
    }

    [Theory]
    [MemberData(nameof(BuiltIns))]
    public void TheShippedPalettesLeaveTheNewestVariablesUnset(string resource)
    {
        // The three that arrived with the size and marker work: no built-in sets one, so
        // every shipped palette renders exactly as it did before they existed, and a NEW
        // palette has to decide rather than slip past. (--mdm-strong is the older case and
        // has its own pair of tests above and below: Obsidiminutive does set that one.)
        var mine = Variables(Read(resource));
        var set = new[] { "--mdm-code-fg", "--mdm-list-marker", "--mdm-font-size" }
            .Where(mine.ContainsKey).ToArray();
        Assert.Equal(Array.Empty<string>(), set);
    }

    // ===== Obsidiminutive, and bold with a colour of its own =====

    private const string Obsidiminutive = "themes/Obsidiminutive.css";

    /// <summary>The six palettes that shipped before --mdm-strong existed.</summary>
    public static TheoryData<string> PalettesThatPredateStrong => new()
    {
        "themes/Dracula.css", "themes/GitHub-Dark-Dimmed.css", "themes/GitHub-Light.css",
        "themes/Midget-Solarized.css", "themes/One-Light.css", "themes/Solarized-Light.css",
    };

    [Fact]
    public void ObsidiminutiveShipsAsADarkThemeWithNothingLeftToTheLightDefault()
    {
        // Listed, usable and built-in, through the same store the menu reads - in a
        // temp folder, never the real profile.
        var root = Path.Combine(Path.GetTempPath(), "mm-obsidiminutive-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ThemeStore(root);
            Assert.True(store.Refresh("1.0.0", App));
            var listed = store.List().SingleOrDefault(t => t.Key == "Obsidiminutive.css");
            Assert.NotNull(listed);
            Assert.Equal("Obsidiminutive", listed!.Name);
            Assert.True(listed.IsUsable, listed.Unusable);
            Assert.False(listed.IsCustom);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* temp */ }
        }

        var css = Read(Obsidiminutive);
        Assert.Null(CssValidator.Validate(css));

        var mine = Variables(css);
        Assert.Equal("dark", mine["--mdm-color-scheme"]);

        // Everything the other dark built-ins set, plus the bold colour this theme
        // exists to fix - so nothing here falls through to Default's light values.
        var expected = Variables(Read("themes/Dracula.css")).Keys
            .Concat(Variables(Read("themes/GitHub-Dark-Dimmed.css")).Keys)
            .Append("--mdm-strong")
            .Distinct().OrderBy(k => k, StringComparer.Ordinal);
        Assert.Equal(Array.Empty<string>(), expected.Where(k => !mine.ContainsKey(k)).ToArray());
    }

    [Theory]
    [MemberData(nameof(PalettesThatPredateStrong))]
    public void ThePalettesThatPredateStrongLeaveBoldAsItWas(string resource)
        // Unset means Default's currentColor, which is bold in the colour of the text
        // around it - exactly what these themes rendered before. Setting it in one of
        // them is a visual change for everyone using it, and belongs in its own commit.
        => Assert.False(Variables(Read(resource)).ContainsKey("--mdm-strong"),
            $"{resource} sets --mdm-strong; its users would see bold change colour");

    [Fact]
    public void ObsidiminutiveClearsAAOnEveryTextPairItDefines()
    {
        // WCAG AA for normal text, 4.5:1, on every background each text colour can land
        // on - not just body text on the page, which is all the older palettes are held
        // to. Built from the surfaces rather than listed pair by pair: the hand-picked
        // list this replaced missed a link in a table header and a heading in a quote.
        var vars = Variables(Read(Obsidiminutive));
        var failures = ReachableTextPairs(vars)
            .Select(p => (p.Fg, p.Bg, Ratio: Contrast(Rgb(vars[p.Fg]), Rgb(vars[p.Bg]))))
            .Where(p => p.Ratio < 4.5)
            .Select(p => $"{p.Fg} ({vars[p.Fg]}) on {p.Bg} ({vars[p.Bg]}) is {p.Ratio:0.00}:1")
            .ToArray();
        Assert.Equal(Array.Empty<string>(), failures);
    }

    [Fact]
    public void EveryObsidiminutiveColourIsTestedAsTextOrNamedAsNotText()
    {
        // The sweep above is only complete if a new variable can't slip past it: each
        // one is in a text pair, or named below with the reason it isn't text.
        var vars = Variables(Read(Obsidiminutive));
        var inPairs = ReachableTextPairs(vars).SelectMany(p => new[] { p.Fg, p.Bg }).ToHashSet();
        Assert.Equal(Array.Empty<string>(),
            vars.Keys.Where(k => !inPairs.Contains(k) && !NotText.ContainsKey(k)).OrderBy(k => k).ToArray());
        Assert.Equal(Array.Empty<string>(), NotText.Keys.Where(k => !vars.ContainsKey(k)).ToArray());
    }

    private static readonly Dictionary<string, string> NotText = new(StringComparer.Ordinal)
    {
        ["--mdm-color-scheme"] = "not a colour",
        ["--mdm-mermaid-theme"] = "not a colour",
        ["--mdm-app-bg"] = "the surround; nothing is written on it",
        ["--mdm-page-shadow"] = "a shadow",
        ["--mdm-quote-bar"] = "a rule", ["--mdm-hr"] = "a rule",
        ["--mdm-table-border"] = "a border", ["--mdm-cell-border"] = "a border",
        ["--mdm-mermaid-border"] = "a border", ["--mdm-mermaid-error-border"] = "a border",
        ["--mdm-squiggle"] = "non-text, 3:1 above", ["--mdm-resize-handle"] = "non-text, 3:1 above",
        ["--mdm-mark"] = "formatting marks, faint on purpose (1.5:1 above)",
        ["--mdm-cell-selected"] = "a translucent tint over a cell",
        ["--mdm-print-row-alt-bg"] = "paper stripe under print's #000 text, 12:1 above",
    };

    /// <summary>
    /// Every text colour on every background it can be drawn on, by surface. Inline
    /// text - a link, its hover, bold - goes wherever a paragraph can, so it is crossed
    /// with every such surface instead of listed where someone thought of it. Headings
    /// are blocks: a page or a quote holds one; a GFM table cell can't.
    /// </summary>
    private static IEnumerable<(string Fg, string Bg)> ReachableTextPairs(IReadOnlyDictionary<string, string> vars)
    {
        var headings = vars.Keys.Where(k => k == "--mdm-heading" || Regex.IsMatch(k, @"^--mdm-h\d$")).ToArray();
        var tokens = vars.Keys.Where(k => k.StartsWith("--mdm-token-", StringComparison.Ordinal));
        var inline = new[] { "--mdm-link", "--mdm-link-hover", "--mdm-strong" };
        var surfaces = new (string Bg, IEnumerable<string> Fg)[]
        {
            ("--mdm-page-bg", inline.Append("--mdm-text").Concat(headings)),
            ("--mdm-quote-bg", inline.Append("--mdm-quote-text").Concat(headings)),
            ("--mdm-td-bg", inline.Append("--mdm-text")),
            ("--mdm-row-alt-bg", inline.Append("--mdm-text")),
            ("--mdm-th-bg", inline.Append("--mdm-th-text")),
            // Paper's header row keeps its screen look. In a dark theme a link in it
            // prints in the header's text colour, and headings and links on white
            // paper print in print.css's own dark colours (theme-parity.test.mjs holds
            // those to 4.5:1). No hover on paper; bold prints as the text around it.
            ("--mdm-print-th-bg", new[] { "--mdm-print-th-text" }),
            // Inline code: this theme's own rule reads --mdm-token-number
            // (editor-src/test/theme-parity.test.mjs pins it).
            ("--mdm-code-bg", new[] { "--mdm-token-number" }),
            ("--mdm-pre-bg", tokens.Append("--mdm-pre-fg")),
            ("--mdm-mermaid-bg", new[] { "--mdm-mermaid-empty" }),
            ("--mdm-mermaid-error-bg", new[] { "--mdm-mermaid-error-text" }),
        };
        return surfaces.SelectMany(s => s.Fg.Select(fg => (fg, s.Bg)));
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
        //
        // Matched EXACTLY, not by substring. `Contains("Solarized")` also matches
        // `Midget-Solarized.css` — a theme whose entire purpose is MORE contrast —
        // so the substring form silently handed the lax floor to the one palette
        // that must never have it. An exemption that spreads by name-similarity is
        // not the "by name" exemption this comment claims to describe.
        var vars = Variables(Read(resource));
        var floor = resource == "themes/Solarized-Light.css" ? 4.0 : 4.5;
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

    [Theory]
    [MemberData(nameof(BuiltIns))]
    public void ThePrintHeaderPairIsLegibleOnPaper(string resource)
    {
        // The printed header follows the theme (print.css forces its background onto
        // paper with print-color-adjust: exact), so its fg/bg pair must carry its own
        // contrast - there is no white page behind it to save an illegible pair.
        var vars = Variables(Read(resource));
        var ratio = Contrast(Rgb(vars["--mdm-print-th-text"]), Rgb(vars["--mdm-print-th-bg"]));
        Assert.True(ratio >= 3.0,
            $"{resource}: print header {vars["--mdm-print-th-text"]} on {vars["--mdm-print-th-bg"]} " +
            $"is {ratio:0.00}:1, below the 3:1 floor");
    }

    [Theory]
    [MemberData(nameof(BuiltIns))]
    public void ThePrintRowStripeStaysLightBecausePrintedTextIsBlack(string resource)
    {
        // print.css pins body text to #000 on paper, so the alternating stripe must
        // stay LIGHT for every theme - including the dark ones, whose SCREEN stripe
        // is dark and must not be reused. This is the constraint that made the print
        // values separate variables in the first place; a mutation collapsing them
        // back to the screen values fails here on Dracula and GitHub Dark Dimmed.
        var vars = Variables(Read(resource));
        var stripe = Rgb(vars["--mdm-print-row-alt-bg"]);
        var vsBlackText = Contrast(stripe, (0, 0, 0));
        Assert.True(vsBlackText >= 12.0,
            $"{resource}: print stripe {vars["--mdm-print-row-alt-bg"]} gives only " +
            $"{vsBlackText:0.00}:1 against the #000 print.css pins for body text");
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
        // The print-table pair too — the theories above cover only the embedded
        // resources, and Default is a theme with no resource. Without this line the
        // "enforced for every theme" claim skipped one.
        Assert.True(Contrast(Rgb(vars["--mdm-print-th-text"]), Rgb(vars["--mdm-print-th-bg"])) >= 3.0);
        Assert.True(Contrast(Rgb(vars["--mdm-print-row-alt-bg"]), (0, 0, 0)) >= 12.0);
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
