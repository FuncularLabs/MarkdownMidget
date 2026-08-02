using MarkdownMidget;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>
/// The status-bar count is read as "how much have I written", so markdown syntax
/// the writer doesn't think of as words must not inflate it.
/// </summary>
public class TextStatsTests
{
    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("   \n\t ", 0)]
    [InlineData("hello", 1)]
    [InlineData("hello world", 2)]
    [InlineData("  hello   world  ", 2)]
    [InlineData("one\ntwo\nthree", 3)]
    public void CountsWords(string? text, int expected)
        => Assert.Equal(expected, TextStats.Measure(text).Words);

    [Theory]
    [InlineData("## Heading", 1)]              // '##' is syntax, not a word
    [InlineData("- item", 1)]
    [InlineData("> quoted text", 2)]
    [InlineData("---", 0)]                     // a horizontal rule is not a word
    [InlineData("| a | b |", 2)]               // pipes don't count
    [InlineData("**bold**", 1)]                // still one word
    [InlineData("[link](http://x)", 1)]        // no space in it, and it renders as one word
    [InlineData("see [the docs](http://x) now", 4)]  // link text counts, the URL rides along
    [InlineData("# ## ### ----", 0)]           // pure syntax
    public void MarkdownSyntaxDoesNotInflateTheCount(string text, int expected)
        => Assert.Equal(expected, TextStats.Measure(text).Words);

    [Fact]
    public void CountsCharactersVerbatim()
        => Assert.Equal(11, TextStats.Measure("hello world").Characters);

    [Fact]
    public void DigitsCountAsWords()
        => Assert.Equal(2, TextStats.Measure("chapter 12").Words);

    [Theory]
    [InlineData("word", "1 word   4 chars")]
    [InlineData("a b", "2 words   3 chars")]
    [InlineData("", "0 words   0 chars")]
    public void FormatsForTheStatusBar(string text, string expected)
        => Assert.Equal(expected, TextStats.Measure(text).ToStatusText());

    [Fact]
    public void ThousandsAreGrouped()
    {
        var many = string.Join(" ", System.Linq.Enumerable.Repeat("word", 1500));
        Assert.Contains("1,500 words", TextStats.Measure(many).ToStatusText());
    }
}
