using System.Windows.Media;
using MarkdownMidget.Themes;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>
/// The seam between the page and the source-view TextBox. Everything crossing it
/// was produced by script running against a stylesheet the user wrote, so the only
/// safe posture is that a shape which isn't exactly right yields nothing — the
/// caller can put the original colours back, but it cannot notice that one of the
/// two it just applied was nonsense.
/// </summary>
public class ThemeReadBackTests
{
    private const string Good =
        """{"background":{"r":255,"g":254,"b":253},"foreground":{"r":1,"g":2,"b":3},"mermaid":"dark"}""";

    [Fact]
    public void TheOrdinaryAnswerParses()
    {
        var read = ThemeReadBack.Parse(Good);
        Assert.NotNull(read);
        Assert.Equal(Color.FromRgb(255, 254, 253), read!.Value.Background);
        Assert.Equal(Color.FromRgb(1, 2, 3), read.Value.Foreground);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    // What ExecuteScriptAsync hands back when the script threw, or returned nothing.
    [InlineData("null")]
    [InlineData("undefined")]
    // Valid JSON, wrong shape.
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("\"a string\"")]
    [InlineData("{}")]
    // Not JSON at all.
    [InlineData("{oh dear")]
    public void AnythingThatIsNotAnAnswerIsNoAnswer(string? json)
        => Assert.Null(ThemeReadBack.Parse(json));

    [Theory]
    // One colour present, one missing: still nothing, because half a palette applied
    // to a TextBox is a pane whose text and background were chosen by different
    // people.
    [InlineData("""{"background":{"r":0,"g":0,"b":0}}""")]
    [InlineData("""{"foreground":{"r":0,"g":0,"b":0}}""")]
    // A channel missing from one of them.
    [InlineData("""{"background":{"r":0,"g":0},"foreground":{"r":1,"g":2,"b":3}}""")]
    // Channels as strings — what a JS change from bytes to "rgb(0,0,0)" would look
    // like on this side, and it must not be coerced.
    [InlineData("""{"background":{"r":"0","g":"0","b":"0"},"foreground":{"r":1,"g":2,"b":3}}""")]
    // A colour that isn't an object.
    [InlineData("""{"background":"#000000","foreground":{"r":1,"g":2,"b":3}}""")]
    [InlineData("""{"background":null,"foreground":{"r":1,"g":2,"b":3}}""")]
    public void AHalfAnswerIsRefusedRatherThanCompleted(string json)
        => Assert.Null(ThemeReadBack.Parse(json));

    [Theory]
    [InlineData(-1)]
    [InlineData(256)]
    [InlineData(1000)]
    public void AChannelOutOfRangeIsRefusedRatherThanWrapped(int value)
    {
        // A cast would turn 256 into 0 and 1000 into 232 — a plausible-looking colour
        // nobody chose, from an answer that was already wrong.
        var json = """{"background":{"r":VALUE,"g":0,"b":0},"foreground":{"r":1,"g":2,"b":3}}"""
            .Replace("VALUE", value.ToString());
        Assert.Null(ThemeReadBack.Parse(json));
    }

    [Theory]
    [InlineData("0.5")]
    [InlineData("1e3")]
    public void ANonIntegerChannelIsRefused(string value)
    {
        // TryGetInt32 declines these, which is the intent: the page's contract is
        // bytes off a canvas, and a float means something upstream changed.
        var json = """{"background":{"r":VALUE,"g":0,"b":0},"foreground":{"r":1,"g":2,"b":3}}"""
            .Replace("VALUE", value.ToString());
        Assert.Null(ThemeReadBack.Parse(json));
    }

    [Fact]
    public void TheBoundsThemselvesAreInsideTheRange()
    {
        var read = ThemeReadBack.Parse(
            """{"background":{"r":0,"g":0,"b":0},"foreground":{"r":255,"g":255,"b":255}}""");
        Assert.NotNull(read);
        Assert.Equal(Colors.Black, read!.Value.Background);
        Assert.Equal(Colors.White, read.Value.Foreground);
    }

    [Fact]
    public void ExtraFieldsAreIgnoredRatherThanFatal()
        // The page is free to send more back — mermaid's theme name already comes
        // along this way, and a later addition must not break an older host.
        => Assert.NotNull(ThemeReadBack.Parse(
            """{"background":{"r":0,"g":0,"b":0,"a":128},"foreground":{"r":1,"g":2,"b":3},"future":[1,2]}"""));
}
