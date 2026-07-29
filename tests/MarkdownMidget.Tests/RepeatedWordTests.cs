using MarkdownMidget;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>
/// The engine flags a repeated word ("the the") as an error, but the word itself is
/// spelled correctly — so it must never be offered the ordinary actions. Suggesting
/// "them/then/they" for it corrupts the sentence, and adding it to the dictionary
/// would permanently whitelist a stopword. This decides when the menu switches to
/// "Delete Repeated Word".
/// </summary>
public class RepeatedWordTests
{
    [Theory]
    [InlineData("This is the ", "the")]          // the ordinary case
    [InlineData("This is The ", "the")]          // case-insensitive
    [InlineData("word word\nword ", "word")]     // across a newline
    [InlineData("the ", "the")]                  // at the very start
    [InlineData("a  ", "a")]                     // multiple spaces
    public void RecognisesARepeat(string before, string word)
        => Assert.True(MainWindow.EndsWithRepeatOf(before, word));

    [Theory]
    [InlineData("This is a ", "the")]            // different word
    [InlineData("I will bathe ", "the")]         // tail of a longer word, not a repeat
    [InlineData("", "the")]                      // nothing before it
    [InlineData("the", "the")]                   // no separator: "thethe" is one token
    [InlineData("This is ", "")]                 // empty word
    public void RejectsNonRepeats(string before, string word)
        => Assert.False(MainWindow.EndsWithRepeatOf(before, word));

    [Fact]
    public void HyphenatedTailIsNotARepeat()
        => Assert.False(MainWindow.EndsWithRepeatOf("state-of-the", "the"));
}

/// <summary>
/// Deleting a repeated word must remove the whole whitespace run between the two
/// occurrences. Assuming a single literal space left stray whitespace behind, and
/// in the WYSIWYG view — where the replacement is guarded by an expected string —
/// made the menu item silently do nothing across a non-breaking space.
/// </summary>
public class SeparatorTests
{
    [Theory]
    [InlineData("This is the ", " ")]
    [InlineData("This is the   ", "   ")]        // several spaces
    [InlineData("This is the\u00A0", "\u00A0")]  // non-breaking space (the C2 case)
    [InlineData("word word\n", "\n")]
    [InlineData("no gap", "")]
    public void CapturesTheWholeSeparator(string before, string expected)
        => Assert.Equal(expected, MainWindow.TrailingWhitespace(before));

    [Fact]
    public void EmptyPrefixHasNoSeparator()
        => Assert.Equal("", MainWindow.TrailingWhitespace(""));

    // Invisible characters that arrive with text pasted from Word or the web.
    // char.IsWhiteSpace says no to all of them; the checker has been observed to
    // repeat-flag across the zero-width space and the soft hyphen, and treating the
    // rest the same way costs nothing. Missing them left the menu with nothing
    // actionable on a word it had squiggled.
    [Theory]
    [InlineData('\u200B')]   // zero-width space
    [InlineData('\u200C')]   // zero-width non-joiner
    [InlineData('\u200D')]   // zero-width joiner
    [InlineData('\uFEFF')]   // zero-width no-break space
    [InlineData('\u00AD')]   // soft hyphen
    public void ZeroWidthCharactersCountAsSeparators(char c)
    {
        Assert.True(MainWindow.IsSeparator(c));
        Assert.Equal(c.ToString(), MainWindow.TrailingWhitespace("the" + c));
        Assert.True(MainWindow.EndsWithRepeatOf("This is the" + c, "the"));
    }

    [Theory]
    [InlineData('a')]
    [InlineData('-')]
    [InlineData('\'')]
    public void OrdinaryCharactersAreNotSeparators(char c)
        => Assert.False(MainWindow.IsSeparator(c));
}
