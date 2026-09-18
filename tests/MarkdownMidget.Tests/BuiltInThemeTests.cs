using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
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

    /// <summary>A theme's variables as the PAGE resolves them: Default's, with the theme's
    /// own laid over. What a theme leaves unset still renders, and renders Default's value,
    /// so a sweep that reads only the theme's own file measures a page nobody sees.</summary>
    private static Dictionary<string, string> Effective(string resource)
    {
        var vars = new Dictionary<string, string>(DefaultVars, StringComparer.Ordinal);
        foreach (var (name, value) in Variables(Read(resource))) vars[name] = value;
        return vars;
    }

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
            "themes/Red-Sparks-2X.css",
            "themes/Red-Sparks.css",
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
    public void TheSampleThemeNamesExactlyTheVariablesThePaletteDefines()
    {
        // Help's count and the sample are one promise - "all listed in the sample" - and the
        // count is measured against the PALETTE, so the two files have to agree or the
        // sentence is false in the only place a theme author can check it. Both directions:
        // a variable the palette gained and the sample never mentioned is a promise broken,
        // and a name only the sample has is a line that sets nothing.
        var sample = Variables(Read("themes-sample.css")).Keys.OrderBy(k => k, StringComparer.Ordinal);
        Assert.Equal(DefaultVars.Keys.OrderBy(k => k, StringComparer.Ordinal), sample);
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

    [Fact]
    public void HelpsThemeTableNamesEveryThemeThatShips()
    {
        // The COUNT above was pinned and the LIST was not, so a palette could ship with the
        // number corrected and the theme itself missing from the table a reader picks from
        // — the same quiet failure the count test exists to stop, one row further along.
        // Read as the display names the menu shows, which is what the table lists, so a
        // file renamed without Help being told fails here as well.
        var rows = Regex.Matches(Help(), @"^\| \*\*([^*]+)\*\* \|", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        var missing = App.GetManifestResourceNames()
            .Where(n => n.StartsWith("themes/", StringComparison.Ordinal))
            .Select(n => ThemeStore.DisplayName(Path.GetFileName(n)))
            .Append("Default")          // the theme with no file: the app's own palette
            .Where(name => !rows.Contains(name))
            .OrderBy(name => name, StringComparer.Ordinal).ToArray();
        Assert.Equal(Array.Empty<string>(), missing);
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
            new[] { "Dracula", "GitHub Dark Dimmed", "GitHub Light", "Midget Solarized",
                    "Obsidiminutive", "One Light", "Red Sparks 2X", "Red Sparks", "Solarized Light" },
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

    /// <summary>The three variables the size and marker work added to the contract.</summary>
    private static readonly string[] NewestVariables =
        { "--mdm-code-fg", "--mdm-list-marker", "--mdm-font-size" };

    /// <summary>
    /// Which of the three each palette sets, by EXACT resource name; absent means none.
    ///
    /// This began as a two-way branch - predates them, or sets all three - and that shape
    /// was wrong the moment a palette had reason to set some. Obsidiminutive sets the two
    /// COLOURS and not the size: its markers were the vendor's nord10 at 3.29:1 on its own
    /// page and its inline code was a rule, and both are now variables, while its text size
    /// is the app's and has no reason to change. A three-way table says that; "sets them
    /// all" could only have said it by being relaxed into "sets at least one", which is
    /// how a pin like this stops meaning anything.
    /// </summary>
    private static readonly Dictionary<string, string[]> NewestVariablesSet = new(StringComparer.Ordinal)
    {
        // The size is the whole point of the pair, so all three.
        [RedSparks] = new[] { "--mdm-code-fg", "--mdm-list-marker", "--mdm-font-size" },
        [RedSparks2X] = new[] { "--mdm-code-fg", "--mdm-list-marker", "--mdm-font-size" },
        // Colours only: 16px is the size this theme has always rendered at.
        [Obsidiminutive] = new[] { "--mdm-code-fg", "--mdm-list-marker" },
    };

    [Theory]
    [MemberData(nameof(BuiltIns))]
    public void EveryShippedPaletteSetsExactlyTheNewestVariablesItIsNamedFor(string resource)
    {
        // Exact equality in both directions, per palette, so nothing slips between the
        // cases: a palette not named here that sets one of the three fails until someone
        // decides it should, and a palette named here that drops one fails too - which is
        // how the ::marker rule would come back, or how Obsidiminutive's markers would
        // quietly return to the vendor's blue-grey.
        var mine = Variables(Read(resource));
        var expected = NewestVariablesSet.TryGetValue(resource, out var named)
            ? named : Array.Empty<string>();
        Assert.Equal(expected.OrderBy(v => v, StringComparer.Ordinal),
                     NewestVariables.Where(mine.ContainsKey).OrderBy(v => v, StringComparer.Ordinal));
    }

    [Fact]
    public void EveryPaletteNamedForANewestVariableIsOneThatShips()
        // A name here that no longer ships would be excusing nothing, and a variable name
        // with a typo in it would excuse a variable that does not exist.
        => Assert.Equal(Array.Empty<string>(),
            NewestVariablesSet.Keys.Where(k => !App.GetManifestResourceNames().Contains(k))
                .Concat(NewestVariablesSet.Values.SelectMany(v => v).Distinct()
                    .Where(v => !NewestVariables.Contains(v)))
                .OrderBy(k => k, StringComparer.Ordinal).ToArray());

    // ===== Red Sparks, and a palette written against the contract =====

    private const string RedSparks = "themes/Red-Sparks.css";
    private const string RedSparks2X = "themes/Red-Sparks-2X.css";

    [Fact]
    public void TheRedSparksPairShipsAsDarkBuiltInsThroughTheStoreTheMenuReads()
    {
        // Listed, usable and built-in, through the same store the menu reads - in a temp
        // folder, never the real profile. Two files, so the menu offers the palette and its
        // double-size sibling as separate entries, which is how a reader picks the size.
        var root = Path.Combine(Path.GetTempPath(), "mm-red-sparks-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ThemeStore(root);
            Assert.True(store.Refresh("1.0.0", App));
            foreach (var (key, name) in new[] { ("Red-Sparks.css", "Red Sparks"),
                                                ("Red-Sparks-2X.css", "Red Sparks 2X") })
            {
                var listed = store.List().SingleOrDefault(t => t.Key == key);
                Assert.NotNull(listed);
                Assert.Equal(name, listed!.Name);
                Assert.True(listed.IsUsable, listed.Unusable);
                Assert.False(listed.IsCustom);
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* temp */ }
        }

        foreach (var resource in new[] { RedSparks, RedSparks2X })
            Assert.Equal("dark", Variables(Read(resource))["--mdm-color-scheme"]);
    }

    /// <summary>Each block a theme opens: its prelude, and how deeply it is nested. A small
    /// scanner rather than a CSS parser — enough for files whose rules are this shape, and
    /// it strips comments first so a commented-out rule isn't counted as one.</summary>
    private static (string Prelude, int Depth)[] Blocks(string css)
    {
        var bare = Regex.Replace(css, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        var blocks = new List<(string, int)>();
        var run = new StringBuilder();
        var depth = 0;
        foreach (var c in bare)
        {
            if (c is '{')
            {
                blocks.Add((Regex.Replace(run.ToString(), @"\s+", " ").Trim(), depth));
                depth++;
                run.Clear();
            }
            else if (c is '}') { depth--; run.Clear(); }
            else if (c is ';') run.Clear();
            else run.Append(c);
        }
        Assert.Equal(0, depth);
        return blocks.ToArray();
    }

    [Theory]
    [InlineData(RedSparks)]
    [InlineData(RedSparks2X)]
    public void TheRedSparksPairIsVariablesAndOneScreenRuleTheContractCannotExpress(string resource)
    {
        // The pair arrived as custom themes carrying five hand-written rules - a ::marker
        // colour, an inline-code colour, the `pre code` handback that second rule needed, a
        // link underline and the mermaid overlay - and 2X carried four more for the size
        // alone. Four of the five are gone: --mdm-list-marker and --mdm-code-fg took the
        // first two, base.css's own `pre code` rule makes the third unnecessary, and
        // base.css already underlines links - the UNDERLINE, not the
        // `text-underline-offset: 2px` that rule also set, which was dropped because an
        // offset is a measurement of the app's type rather than a value a palette holds
        // (ROADMAP, Nits). --mdm-font-size took 2X's four. The fifth
        // cannot become a variable: none of ours reaches inside the SVG mermaid draws, and
        // the overlay works on its pixels from outside.
        //
        // Pinned as the whole list of blocks, with nesting, because that is what a
        // regression looks like here: a re-added `.mdm-prosemirror.mdm-prosemirror` rule
        // out-specifying the variable it was replaced by, in a file whose comments say the
        // variable is doing the work.
        Assert.Equal(
            new[] { (":root", 0), ("@media screen", 0), (".mdm-mermaid", 1), (".mdm-mermaid::after", 1) },
            Blocks(Read(resource)));
    }

    [Fact]
    public void TheOnlyDifferenceBetweenRedSparksAndItsTwoXIsTheSize()
    {
        // 2X is its sibling with one number doubled. As custom themes the difference was
        // four more rules - a root font-size, the vendor's two `rem` tokens, a table pin and
        // the gutter's `font` shorthand - and structure.css now derives all four SITES from
        // --mdm-font-size. It does not reproduce all four VALUES: the old theme pinned
        // `table` and `table p` together, so its cell text was 24px at 2X, where ours is the
        // body size (32px) in a cell box that is 0.75 of it. That difference is deliberate,
        // and editor-src/test/theme-parity.test.mjs pins it - "a table cell's text is the
        // body size". What is left here is worth holding on its own: the two palettes cannot
        // drift apart, and a correction to one is a correction to both.
        var (a, b) = (Variables(Read(RedSparks)), Variables(Read(RedSparks2X)));
        Assert.Equal(a.Keys.OrderBy(k => k, StringComparer.Ordinal),
                     b.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(new[] { "--mdm-font-size" },
            a.Where(kv => b[kv.Key] != kv.Value).Select(kv => kv.Key)
             .OrderBy(k => k, StringComparer.Ordinal).ToArray());
        Assert.Equal("16px", a["--mdm-font-size"]);
        Assert.Equal("32px", b["--mdm-font-size"]);

        // And byte for byte below the header comment, comments included: the palette's
        // reasoning is written down in both files, so a fix to one that isn't made in the
        // other leaves a file explaining a value it no longer holds.
        static string Body(string css) => css[css.IndexOf(":root", StringComparison.Ordinal)..]
            .Replace("--mdm-font-size: 32px", "--mdm-font-size: 16px", StringComparison.Ordinal);
        Assert.Equal(Body(Read(RedSparks)), Body(Read(RedSparks2X)));
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

    /// <summary>
    /// The palettes held to the FULL pair sweep below: every text colour on every
    /// background it can land on, not just body text on the page.
    ///
    /// Three of nine, and the shortfall is deliberate and measured rather than assumed.
    /// The sweep was a [Fact] over Obsidiminutive alone - the palette written to clear
    /// 4.5:1 everywhere - so a new theme was swept nowhere, which is how the Red Sparks
    /// pair shipped with eighteen sub-floor pairs while its own header claimed everything
    /// held to a floor cleared it. Widening it to those two closes that, and Obsidiminutive
    /// is down to one: the two marker pairs it briefly had here went when it set a marker
    /// colour of its own instead of borrowing the vendor's, and the one it keeps needs raw
    /// HTML to exist.
    ///
    /// Widening it to all nine is a bigger thing than it sounds, and the numbers are here
    /// so nobody has to re-derive them: the other six measure 86 sub-floor pairs between
    /// them under this model - Solarized Light 24, One Light 19, GitHub Dark Dimmed 14,
    /// Midget Solarized 13, Dracula 9, GitHub Light 7 - and 36 of those 86 are pairs none
    /// of the six chose at all, being the list marker and the inline code they leave unset
    /// and therefore draw in the vendor's nord10 (six each: the marker on the page, in a
    /// quote, in a body cell, on a striped row and in a header cell, and inline code on its
    /// panel). That last part
    /// is the same defect Obsidiminutive was just fixed for, six more times over, and it is
    /// the strongest argument for doing the audit. None of it is new behaviour: those
    /// palettes render exactly as they always have, and the numbers were never written
    /// down. But recording 80 exceptions here would be an audit of six palettes nobody
    /// asked for, and it would bury the eighteen that are actually new. So: measured,
    /// reported, and left as one decision per palette.
    /// </summary>
    public static TheoryData<string> FullySweptPalettes => new() { Obsidiminutive, RedSparks, RedSparks2X };

    /// <summary>Shared by the pair, which differ only in --mdm-font-size.</summary>
    private static readonly Dictionary<string, double> RedSparksDimPairs = new(StringComparer.Ordinal)
    {
        // 18 of 42, floor 3.9. Grouped as they read: the hover is the largest cluster,
        // because a hover that can only go dimmer than #FF0000 is dim on every surface.
        ["--mdm-mermaid-empty on --mdm-mermaid-bg"] = 2.44,
        ["--mdm-token-comment on --mdm-pre-bg"] = 2.55,
        ["--mdm-link-hover on --mdm-th-bg"] = 2.59,
        ["--mdm-link-hover on --mdm-row-alt-bg"] = 2.73,
        ["--mdm-link-hover on --mdm-page-bg"] = 2.78,
        ["--mdm-link-hover on --mdm-td-bg"] = 2.78,
        ["--mdm-link-hover on --mdm-quote-bg"] = 2.81,
        ["--mdm-token-punctuation on --mdm-pre-bg"] = 3.42,
        ["--mdm-token-operator on --mdm-pre-bg"] = 3.42,
        ["--mdm-quote-text on --mdm-quote-bg"] = 3.76,
        ["--mdm-code-fg on --mdm-code-bg"] = 3.85,
        ["--mdm-token-string on --mdm-pre-bg"] = 3.87,
        ["--mdm-token-number on --mdm-pre-bg"] = 3.87,
        ["--mdm-token-property on --mdm-pre-bg"] = 3.87,
        ["--mdm-token-regex on --mdm-pre-bg"] = 3.87,
        ["--mdm-text on --mdm-row-alt-bg"] = 3.89,
        // The marker is the body text's colour, so it is the body text's ratio on a striped
        // row. Found by crossing the marker with the cell surfaces, which a list reaches
        // through raw HTML; on the page and in a cell it is 3.96:1 and clears.
        ["--mdm-list-marker on --mdm-row-alt-bg"] = 3.89,
        // And in a header cell, the one cell ground this palette makes darker than the page.
        ["--mdm-list-marker on --mdm-th-bg"] = 3.68,
    };

    /// <summary>
    /// Text pairs a swept palette draws below its own floor, by EXACT resource name and
    /// exact pair, each with the ratio it measures today - floored to 2dp, so the number
    /// recorded is one the pair really clears. Two tests read it: the sweep, which lets a
    /// recorded pair sit below the floor but never below its recorded value, and the
    /// companion, which fails if an entry names a pair the theme doesn't draw or one that
    /// has since been brightened. Same exact-name discipline as BodyTextFloors: no
    /// substrings, no prefixes, one line per pair.
    ///
    /// Red Sparks: eighteen, and they are the honest cost of one hue. A single-hue night
    /// palette has one dim band to work in - that is what it is for - and the alternative
    /// to writing them down was a theme header claiming a floor nothing enforced.
    ///
    /// Obsidiminutive: two, and both arrived with the contract rather than with a palette.
    /// It predates --mdm-list-marker, so its bullets and numbers render in the vendor's
    /// nord10 (#5e81ac) rather than in anything it chose: 3.29:1 on its page and 2.94:1 in
    /// a quote. Every palette that predates the variable has the same markers; these two
    /// are simply the first to be measured. Worth a look by whoever owns that theme.
    /// </summary>
    private static readonly Dictionary<string, Dictionary<string, double>> DimTextPairs =
        new(StringComparer.Ordinal)
    {
        [Obsidiminutive] = new()   // 1 of 47, floor 4.5
        {
            // The only pair in this palette below AA, and the only one that needs raw HTML
            // to exist at all: a list inside a table HEADER cell. Its own lifted steel blue
            // clears 4.5:1 on every ground a markdown list can occupy - 5.80:1 on the page
            // and in a body cell, 5.40:1 on a striped row, 5.18:1 in a quote - and the
            // header row is the one ground lighter than those. Lifting the marker again to
            // clear it would move a colour chosen to match the blue-grey it replaced, for a
            // pair a document can only reach by hand-writing a table; recorded instead.
            // (It briefly had two entries, for markers at 3.29:1 on its page and 2.94:1 in
            // a quote, which is what the vendor's nord10 measured before it set its own.)
            ["--mdm-list-marker on --mdm-th-bg"] = 3.94,
        },
        [RedSparks] = RedSparksDimPairs,
        [RedSparks2X] = RedSparksDimPairs,     // the same palette, so the same numbers
    };

    [Theory]
    [MemberData(nameof(FullySweptPalettes))]
    public void EveryThemeClearsItsOwnFloorOnEveryTextPairItDefines(string resource)
    {
        // WCAG AA for normal text, 4.5:1, on every background each text colour can land
        // on - not just body text on the page, which is all the older palettes used to be
        // held to. Built from the surfaces rather than listed pair by pair: the hand-picked
        // list this replaced missed a link in a table header and a heading in a quote.
        //
        // This was a [Fact] over Obsidiminutive alone, which meant a palette could ship
        // with a link its users can't read as long as its body text cleared AA. The floor
        // is the theme's own (BodyTextFloors), because a palette allowed 3.9 for prose
        // cannot be held to 4.5 for a link inside it, and the pairs that sit below even
        // that are recorded one by one in DimTextPairs with what they measure.
        var vars = Effective(resource);
        var floor = BodyTextFloors.TryGetValue(resource, out var exempt) ? exempt : 4.5;
        var dim = DimTextPairs[resource];
        var failures = ReachableTextPairs(Variables(Read(resource)), resource)
            .Select(p => (Key: $"{p.Fg} on {p.Bg}", p.Fg, p.Bg,
                          Ratio: Contrast(Rgb(vars[p.Fg]), Rgb(vars[p.Bg]))))
            .Where(p => p.Ratio < (dim.TryGetValue(p.Key, out var recorded) ? recorded : floor))
            .Select(p => $"{p.Fg} ({vars[p.Fg]}) on {p.Bg} ({vars[p.Bg]}) is {p.Ratio:0.00}:1, under " +
                         (dim.ContainsKey(p.Key)
                             ? $"the {dim[p.Key]:0.00} recorded for it"
                             : $"this theme's {floor:0.0} floor"))
            .ToArray();
        Assert.Equal(Array.Empty<string>(), failures);
    }

    [Theory]
    [MemberData(nameof(FullySweptPalettes))]
    public void EveryRecordedDimPairIsOneThePaletteReallyDrawsAndStillNeeds(string resource)
    {
        // The other half, and the half an exception table rots without: an entry has to
        // name a pair the theme actually draws, and to still be below the floor. Brighten
        // a colour and its entry fails here until it is deleted, so the table is a record
        // of what a palette does today and never a list of floors nobody rechecks.
        var vars = Effective(resource);
        var floor = BodyTextFloors.TryGetValue(resource, out var exempt) ? exempt : 4.5;
        var reachable = ReachableTextPairs(Variables(Read(resource)), resource).ToDictionary(
            p => $"{p.Fg} on {p.Bg}", p => Contrast(Rgb(vars[p.Fg]), Rgb(vars[p.Bg])));
        foreach (var (key, recorded) in DimTextPairs[resource])
        {
            Assert.True(reachable.ContainsKey(key),
                $"{resource} records {key} as dim, but no such text pair is reachable in it");
            // The recorded number is this pair's own measurement, floored to 2dp - so it is
            // pinned to within that, not merely "somewhere below the floor". A number parked
            // well under what the pair measures would be a licence to dim it later, which is
            // the way a table like this goes soft.
            Assert.InRange(reachable[key], recorded, recorded + 0.01);
            Assert.True(recorded < floor,
                $"{resource} records {key} at {recorded:0.00}, which is not below its " +
                $"{floor:0.0} floor: the entry is not needed and is hiding nothing");
        }
    }

    [Theory]
    [MemberData(nameof(FullySweptPalettes))]
    public void EveryColourAThemeSetsIsTestedAsTextOrNamedAsNotText(string resource)
    {
        // The sweep above is only complete if a new variable can't slip past it: each
        // one is in a text pair, or named below with the reason it isn't text. Over every
        // built-in, not Obsidiminutive alone - the sweep widened, and a completeness check
        // narrower than the thing it certifies is how the marker colour went unswept.
        var vars = Variables(Read(resource));
        var inPairs = ReachableTextPairs(vars, resource).SelectMany(p => new[] { p.Fg, p.Bg }).ToHashSet();
        Assert.Equal(Array.Empty<string>(),
            vars.Keys.Where(k => !inPairs.Contains(k) && !NotText.ContainsKey(k)).OrderBy(k => k).ToArray());
    }

    [Fact]
    public void EveryNameCalledNotTextIsARealVariable()
        // Checked against the contract rather than one theme's file: a name here that no
        // palette happens to set would still be excusing a variable that doesn't exist.
        => Assert.Equal(Array.Empty<string>(),
            NotText.Keys.Where(k => !DefaultVars.ContainsKey(k)).OrderBy(k => k).ToArray());

    private static readonly Dictionary<string, string> NotText = new(StringComparer.Ordinal)
    {
        ["--mdm-color-scheme"] = "not a colour",
        ["--mdm-mermaid-theme"] = "not a colour",
        ["--mdm-font-size"] = "not a colour: a metric",
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
    /// with every such surface instead of listed where someone thought of it. Headings and
    /// list markers are blocks: a page or a quote holds one; a GFM table cell can't.
    ///
    /// A variable the theme leaves UNSET is left out, because unset means Default's value
    /// and Default is measured by its own test - and because reading one here threw before
    /// this sweep covered a palette that leaves --mdm-strong alone.
    /// </summary>
    private static IEnumerable<(string Fg, string Bg)> ReachableTextPairs(
        IReadOnlyDictionary<string, string> vars, string? resource = null)
    {
        var headings = vars.Keys.Where(k => k == "--mdm-heading" || Regex.IsMatch(k, @"^--mdm-h\d$")).ToArray();
        var tokens = vars.Keys.Where(k => k.StartsWith("--mdm-token-", StringComparison.Ordinal));
        var inline = new[] { "--mdm-link", "--mdm-link-hover", "--mdm-strong" }.Where(vars.ContainsKey).ToArray();
        // A bullet or a number is text the theme paints, and it is drawn whether the theme
        // sets a colour for it or not - unset, it is the vendor's nord10, which is what the
        // page really renders and so what the sweep really has to measure. That is not a
        // technicality: it is how Obsidiminutive's markers turned out to be 3.29:1 on its
        // own page, a pair no test had ever looked at.
        var blocks = headings.Append("--mdm-list-marker").ToArray();
        // A list can sit in any table cell, through raw HTML, so the marker is crossed with
        // every cell surface - the body cell, the striped row AND the header row. The header
        // row was excluded at first for being "a stretch", which was not a reason that
        // survived being asked about: editor-src/src/sanitize.js forbids a named list of
        // tags and allows the rest, so `<th><ul><li>` reaches the DOM intact, and
        // `.mdm-prosemirror ul > li::marker` matches inside it with `th`'s background
        // behind. Raw HTML reaches a header cell exactly as well as a body cell, and the
        // model cannot take the argument for one and refuse it for the other.
        //
        // The same argument does reach further than this model goes: a HEADING in a
        // raw-HTML cell is drawable too, and headings are crossed only with the page and a
        // quote (`a GFM table cell can't` hold one, which is true of markdown and not of
        // raw HTML). That asymmetry is left standing deliberately - closing it re-measures
        // eight palettes this branch does not own - but it is named here rather than
        // defended, because the reason above would have to be un-said to defend it.
        var cellBlocks = new[] { "--mdm-list-marker" };
        // Inline code is always --mdm-code-fg now: the theme's if it sets one, Default's
        // nord10 if it does not. Obsidiminutive used to colour it with a rule instead, which
        // this model had to special-case to measure the right colour at all; it sets the
        // variable since, and NoThemeColoursInlineCodeWithARuleAnyMore keeps it that way, so
        // the special case is gone rather than kept as dead weight.
        const string code = "--mdm-code-fg";
        var surfaces = new (string Bg, IEnumerable<string> Fg)[]
        {
            ("--mdm-page-bg", inline.Append("--mdm-text").Concat(blocks)),
            ("--mdm-quote-bg", inline.Append("--mdm-quote-text").Concat(blocks)),
            ("--mdm-td-bg", inline.Append("--mdm-text").Concat(cellBlocks)),
            ("--mdm-row-alt-bg", inline.Append("--mdm-text").Concat(cellBlocks)),
            ("--mdm-th-bg", inline.Append("--mdm-th-text").Concat(cellBlocks)),
            // Paper's header row keeps its screen look. In a dark theme a link in it
            // prints in the header's text colour, and headings and links on white
            // paper print in print.css's own dark colours (theme-parity.test.mjs holds
            // those to 4.5:1). No hover on paper; bold prints as the text around it.
            ("--mdm-print-th-bg", new[] { "--mdm-print-th-text" }),
            ("--mdm-code-bg", new[] { code }),
            ("--mdm-pre-bg", tokens.Append("--mdm-pre-fg")),
            ("--mdm-mermaid-bg", new[] { "--mdm-mermaid-empty" }),
            ("--mdm-mermaid-error-bg", new[] { "--mdm-mermaid-error-text" }),
        };
        return surfaces.SelectMany(s => s.Fg.Select(fg => (fg, s.Bg)));
    }

    [Theory]
    [MemberData(nameof(BuiltIns))]
    public void NoThemeColoursInlineCodeWithARuleAnyMore(string resource)
    {
        // What lets the sweep above treat --mdm-code-fg as the truth about inline code.
        // Obsidiminutive coloured it with `.mdm-prosemirror code { color: ... }` because
        // there was no variable; a theme doing that again would be measured at whatever the
        // variable says and drawn in whatever the rule says, which is the kind of
        // disagreement a sweep cannot see. The variable is the only route now.
        var bare = Regex.Replace(Read(resource), @"/\*.*?\*/", " ", RegexOptions.Singleline);
        Assert.DoesNotMatch(new Regex(@"(^|[\s,>+~])code\s*\{[^}]*\bcolor\s*:"), bare);
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

    /// <summary>
    /// The palettes whose body text is allowed below WCAG AA, by EXACT resource name, each
    /// with the floor its own measurement sits just above and the reason the design needs it.
    ///
    /// Matched EXACTLY, never by substring. `Contains("Solarized")` also matches
    /// `Midget-Solarized.css` — a theme whose entire purpose is MORE contrast — so the
    /// substring form silently handed the lax floor to the one palette that must never have
    /// it. An exemption that spreads by name-similarity is not an exemption "by name", and
    /// a dictionary keyed on the resource cannot spread that way. Two names rather than a
    /// prefix for the same reason: `Red-Sparks` as a prefix would cover a future
    /// `Red-Sparks-Light.css` that had no claim to it.
    /// </summary>
    private static readonly Dictionary<string, double> BodyTextFloors = new(StringComparer.Ordinal)
    {
        // 4.13:1. Not an oversight but the entire point of Solarized: base00 on base3 is a
        // deliberately reduced contrast chosen so long reading sessions hurt less.
        ["themes/Solarized-Light.css"] = 4.0,
        // 3.96:1, both files, which share the palette. A single-hue red page has nothing
        // brighter to offer short of pure #FF0000 (5.14:1), which the theme keeps for the
        // squiggle and the resize handle; dim red is the point of a palette for
        // dark-adapted eyes, and lifting the text is the one change that would undo it.
        [RedSparks] = 3.9,
        [RedSparks2X] = 3.9,
    };

    [Theory]
    [MemberData(nameof(BuiltIns))]
    public void BodyTextIsReadable(string resource)
    {
        // 4.5:1 is WCAG AA for body text, and every shipped theme clears it except the
        // three named above, each exempted by exact name with its measurement recorded, so
        // that any OTHER theme dropping below AA still fails here.
        var vars = Variables(Read(resource));
        var floor = BodyTextFloors.TryGetValue(resource, out var exempt) ? exempt : 4.5;
        AssertContrast(vars, "--mdm-text", floor, resource);
    }

    [Fact]
    public void EveryBodyTextExemptionIsForAThemeThatShipsAndStillNeedsIt()
    {
        // An exemption is a lax floor with a name on it, and both halves rot. A name that
        // no longer ships is a floor waiting for a rename to land on; a floor set well
        // below what the palette measures is a blanket pass wearing a measurement. So each
        // one has to name a theme that ships and sit within 0.25 of that theme's actual
        // ratio — tighten a palette to AA and its exemption fails here until it is deleted.
        var shipped = App.GetManifestResourceNames().ToHashSet(StringComparer.Ordinal);
        foreach (var (resource, floor) in BodyTextFloors)
        {
            Assert.True(shipped.Contains(resource),
                $"{resource} is exempted from the body-text floor but no longer ships");
            var vars = Variables(Read(resource));
            var ratio = Contrast(Rgb(vars["--mdm-text"]), Rgb(vars["--mdm-page-bg"]));
            Assert.InRange(ratio, floor, Math.Min(floor + 0.25, 4.5));
        }
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
