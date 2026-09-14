using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>#10: the status bar's column rule and Go to Line's reading of what was typed.</summary>
public class LineColumnTests
{
    [Theory]
    [InlineData("", 1)]
    [InlineData("abc", 4)]
    [InlineData("\t\t", 3)]            // a tab is one character, not four columns
    [InlineData("\U0001F600", 2)]      // an emoji is one, though it is two UTF-16 units
    [InlineData("é", 2)]         // a letter and its combining accent are one
    [InlineData("中文", 3)]    // a wide character is one
    public void TheColumnCountsCharactersTheWayAReaderDoes(string beforeCaret, int column) =>
        Assert.Equal(column, LineColumn.Column(beforeCaret));

    [Fact]
    public void TheStatusTextNamesTheLineAndColumn() =>
        Assert.Equal("Ln 400, Col 50", LineColumn.StatusText(400, 50));

    [Theory]
    [InlineData("400", 2000, 400)]
    [InlineData(" 7 ", 10, 7)]
    [InlineData("5000", 2000, 2000)]           // past the end: the last line
    [InlineData("99999999999999", 10, 10)]
    [InlineData("0", 10, 1)]
    [InlineData("-3", 10, 1)]
    [InlineData("3", 0, 1)]                     // an empty document still has line 1
    [InlineData("abc", 10, null)]
    [InlineData("", 10, null)]
    [InlineData(null, 10, null)]
    public void GoToLineClampsANumberAndRefusesAnythingElse(string? typed, int lineCount, int? line) =>
        Assert.Equal(line, LineColumn.ParseLine(typed, lineCount));
}
