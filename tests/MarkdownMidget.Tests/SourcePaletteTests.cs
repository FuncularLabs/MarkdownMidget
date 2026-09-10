using System.Windows.Media;
using MarkdownMidget.Source;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>
/// The colours the source-view highlighting is drawn from, and the legibility floor
/// that keeps it readable. Parsing mirrors <see cref="MarkdownMidget.Themes.ThemeReadBack"/>'s
/// fail-closed posture: a shape that is not exactly right yields nothing rather than a
/// half-palette. The floor is what lets the mapping reach for a theme's accent colour
/// without ever landing under 4.5:1 on that theme's own page.
/// </summary>
public class SourcePaletteTests
{
    private const string Good =
        """
        {"background":{"r":255,"g":255,"b":255},"foreground":{"r":20,"g":20,"b":20},
         "source":{"heading":{"r":10,"g":80,"b":160},"link":{"r":0,"g":100,"b":200},
                   "accent":{"r":30,"g":120,"b":110},"quote":{"r":80,"g":90,"b":100}}}
        """;

    [Fact]
    public void TheOrdinaryAnswerParses()
    {
        var p = SourcePalette.Parse(Good);
        Assert.NotNull(p);
        Assert.Equal(Color.FromRgb(255, 255, 255), p!.Background);
        Assert.Equal(Color.FromRgb(20, 20, 20), p.Text);
        Assert.Equal(Color.FromRgb(10, 80, 160), p.Heading);
        Assert.Equal(Color.FromRgb(0, 100, 200), p.Link);
        Assert.Equal(Color.FromRgb(30, 120, 110), p.Accent);
        Assert.Equal(Color.FromRgb(80, 90, 100), p.Quote);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{oh dear")]
    // No source block at all.
    [InlineData("""{"background":{"r":1,"g":1,"b":1},"foreground":{"r":2,"g":2,"b":2}}""")]
    // source present but missing a role.
    [InlineData("""{"background":{"r":1,"g":1,"b":1},"foreground":{"r":2,"g":2,"b":2},"source":{"heading":{"r":1,"g":1,"b":1},"link":{"r":1,"g":1,"b":1},"accent":{"r":1,"g":1,"b":1}}}""")]
    // background missing.
    [InlineData("""{"foreground":{"r":2,"g":2,"b":2},"source":{"heading":{"r":1,"g":1,"b":1},"link":{"r":1,"g":1,"b":1},"accent":{"r":1,"g":1,"b":1},"quote":{"r":1,"g":1,"b":1}}}""")]
    // a channel out of range.
    [InlineData("""{"background":{"r":1,"g":1,"b":1},"foreground":{"r":2,"g":2,"b":2},"source":{"heading":{"r":300,"g":1,"b":1},"link":{"r":1,"g":1,"b":1},"accent":{"r":1,"g":1,"b":1},"quote":{"r":1,"g":1,"b":1}}}""")]
    public void AnythingNotAWholePaletteIsRefused(string? json)
        => Assert.Null(SourcePalette.Parse(json));

    [Fact]
    public void ALegibleRoleIsKept()
    {
        // Near-black on white is ~19:1 — kept as-is.
        var p = new SourcePalette(Colors.White, Color.FromRgb(20, 20, 20),
            Heading: Color.FromRgb(10, 60, 120), Link: Colors.Blue, Accent: Colors.Teal, Quote: Colors.DimGray);
        Assert.Equal(p.Heading, p.Legible(p.Heading));
    }

    [Fact]
    public void AnIllegibleRoleFallsBackToBodyText()
    {
        // A pale accent on white (well under 4.5:1) must not be used; the body text
        // colour, which the theme is built on, takes its place.
        var pale = Color.FromRgb(210, 215, 222);   // ~1.3:1 on white
        var text = Color.FromRgb(20, 20, 20);
        var p = new SourcePalette(Colors.White, text,
            Heading: text, Link: Colors.Blue, Accent: pale, Quote: Colors.DimGray);
        Assert.True(SourcePalette.ContrastRatio(pale, Colors.White) < 4.5);
        Assert.Equal(text, p.Legible(pale));
    }

    [Theory]
    [InlineData(0, 0, 0, 255, 255, 255, 21.0)]     // black on white
    [InlineData(255, 255, 255, 255, 255, 255, 1.0)] // white on white
    public void ContrastRatioMatchesWcag(byte r1, byte g1, byte b1, byte r2, byte g2, byte b2, double expected)
    {
        var ratio = SourcePalette.ContrastRatio(Color.FromRgb(r1, g1, b1), Color.FromRgb(r2, g2, b2));
        Assert.Equal(expected, ratio, precision: 1);
    }
}
