using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Highlighting;
using MarkdownMidget.Source;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>
/// The promise that the source view is never less legible than the theme's own prose:
/// after the theme is applied, every colour the highlighting paints either clears the
/// WCAG AA ratio for normal text (4.5:1) on the page OR is exactly the body text
/// colour. That is the legibility floor — a theme's accent that is a low-contrast bar
/// or border colour falls back to body text rather than being painted as text.
///
/// Note the floor is "no worse than body text", not "always 4.5": Solarized Light
/// keeps upstream's deliberate 4.13:1 body contrast (the reason Midget Solarized
/// exists as a higher-contrast alternative), so its source text matches its formatted
/// text exactly rather than being lifted to a contrast the rest of the theme lacks.
/// The check runs end to end through <see cref="SourceHighlighting.SetPalette"/>, so a
/// role the mapping forgot to floor would fail here.
/// </summary>
public class SourcePaletteContrastTests
{
    public static IEnumerable<object[]> BuiltInThemes()
    {
        // Red Sparks is the interesting case for this sweep and the reason to keep the list
        // current: its body text is 3.96:1, below the 4.5 the floor compares against, so
        // every accent it maps has to come back as body text or clear 4.5 on its own.
        foreach (var name in new[] { "Amber-Phosphor", "Dracula", "GitHub-Dark-Dimmed", "GitHub-Light",
                                     "Midget-Solarized", "Obsidiminutive", "One-Light",
                                     "Red-Sparks", "Red-Sparks-2X", "Solarized-Light" })
            yield return new object[] { name };
    }

    private static string ReadThemeCss(string name)
    {
        var asm = typeof(SourcePalette).Assembly;
        using var s = asm.GetManifestResourceStream($"themes/{name}.css")
            ?? throw new InvalidOperationException($"theme resource themes/{name}.css missing");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    private static Color Var(string css, string name)
    {
        var m = Regex.Match(css, $"--mdm-{Regex.Escape(name)}:\\s*(#[0-9a-fA-F]{{6}})");
        Assert.True(m.Success, $"theme is missing a hex --mdm-{name}");
        var hex = m.Groups[1].Value;
        return Color.FromRgb(
            Convert.ToByte(hex.Substring(1, 2), 16),
            Convert.ToByte(hex.Substring(3, 2), 16),
            Convert.ToByte(hex.Substring(5, 2), 16));
    }

    private static SourcePalette PaletteFor(string themeName)
    {
        var css = ReadThemeCss(themeName);
        // The page resolves an unset --mdm-strong to the body text colour (Default's
        // currentColor, read on a probe whose colour is --mdm-text), so that is what
        // the read-back carries for a theme that doesn't set one.
        var text = Var(css, "text");
        return new SourcePalette(
            Background: Var(css, "page-bg"),
            Text: text,
            Heading: Var(css, "heading"),
            Link: Var(css, "link"),
            Accent: Var(css, "quote-bar"),
            Quote: Var(css, "quote-text"),
            Strong: Regex.IsMatch(css, @"--mdm-strong:\s*#") ? Var(css, "strong") : text);
    }

    [Fact]
    public void TheSourceViewPaintsBoldInTheThemesBoldColour()
    {
        // Obsidiminutive sets --mdm-strong; the Strong span follows the read-back.
        var p = PaletteFor("Obsidiminutive");
        Assert.NotEqual(p.Text, p.Strong);
        Assert.Contains(("Strong", p.Strong!.Value), ApplyAndReadBack(p));
    }

    [Theory]
    [InlineData("Dracula", true)]
    [InlineData("Solarized-Light", true)]   // body text under 4.5:1: the floor must not move bold off it
    [InlineData("GitHub-Light", false)]     // a bundle that sends no bold colour at all
    public void WithNoBoldColourTheSourceViewKeepsBoldInBodyText(string themeName, bool readBackSendsText)
    {
        var p = PaletteFor(themeName);
        if (!readBackSendsText) p = p with { Strong = null };
        Assert.Contains(("Strong", p.Text), ApplyAndReadBack(p));
    }

    [Fact]
    public void AnIllegibleBoldColourFloorsToBodyText()
    {
        // Same floor as every other role: a bold colour under 4.5:1 is not painted.
        var p = PaletteFor("Obsidiminutive") with { Strong = Color.FromRgb(0x40, 0x4e, 0x51) };
        Assert.True(SourcePalette.ContrastRatio(p.Strong!.Value, p.Background) < 4.5);
        Assert.Contains(("Strong", p.Text), ApplyAndReadBack(p));
    }

    [Theory]
    [MemberData(nameof(BuiltInThemes))]
    public void MappedColoursAreNeverLessLegibleThanBodyText(string themeName)
    {
        var p = PaletteFor(themeName);
        foreach (var (role, applied) in new[]
                 {
                     ("heading", p.Legible(p.Heading)), ("link", p.Legible(p.Link)),
                     ("accent", p.Legible(p.Accent)), ("quote", p.Legible(p.Quote)),
                 })
            Assert.True(applied == p.Text || SourcePalette.ContrastRatio(applied, p.Background) >= 4.5,
                $"{themeName}: the {role} colour is neither ≥4.5:1 nor floored to body text");
    }

    [Fact]
    public void ALowContrastAccentActuallyFloorsToText()
    {
        // GitHub Light's quote-bar (#d0d7de, a faint rule) is well under 4.5:1 on
        // white, so it must fall back to body text — proving the floor bites, not just
        // that legible colours pass through.
        var p = PaletteFor("GitHub-Light");
        Assert.True(SourcePalette.ContrastRatio(p.Accent, p.Background) < 4.5);
        Assert.Equal(p.Text, p.Legible(p.Accent));
    }

    [Theory]
    [MemberData(nameof(BuiltInThemes))]
    public void EveryNamedColourAppliedToTheDefinitionIsLegible(string themeName)
    {
        var p = PaletteFor(themeName);
        // End to end: run the real SetPalette and read back every named colour the
        // highlighter will paint. Catches a role the mapping applied without the floor.
        var colours = ApplyAndReadBack(p);
        Assert.NotEmpty(colours);
        foreach (var (name, colour) in colours)
            Assert.True(colour == p.Text || SourcePalette.ContrastRatio(colour, p.Background) >= 4.5,
                $"{themeName}: named colour '{name}' is neither ≥4.5:1 nor floored to body text");
    }

    private static List<(string Name, Color Colour)> ApplyAndReadBack(SourcePalette palette)
    {
        List<(string, Color)> result = null!;
        Exception? error = null;
        var done = new ManualResetEventSlim();
        var t = new Thread(() =>
        {
            try
            {
                var sh = new SourceHighlighting();
                var editor = new SourceEditor();
                sh.Attach(editor);
                sh.SetPalette(editor, palette);
                var list = new List<(string, Color)>();
                foreach (var name in new[] { "Heading", "ListMarker", "Rule", "Link",
                                             "BlockQuote", "InlineCode", "Strong", "Emphasis" })
                {
                    var c = sh.Definition.GetNamedColor(name);
                    if (c?.Foreground is { } fg)
                    {
                        var brush = fg.GetBrush(null);
                        if (brush is SolidColorBrush scb) list.Add((name, scb.Color));
                    }
                }
                result = list;
            }
            catch (Exception ex) { error = ex; }
            finally { done.Set(); }
        }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        Assert.True(done.Wait(TimeSpan.FromSeconds(30)), "palette-apply harness timed out");
        if (error is not null) throw error;
        return result;
    }
}
