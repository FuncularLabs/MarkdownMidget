using MarkdownMidget;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>
/// Covers the Regex builder for each of the four Find modes. The regex object
/// itself is a runtime-composable value, so we just build it and probe with
/// <c>IsMatch</c> on representative samples.
/// </summary>
public class FindEngineTests
{
    // ===== Empty / invalid input =====

    [Fact]
    public void Empty_query_returns_null()
        => Assert.Null(FindEngine.Build("", FindEngine.Mode.Normal, false, false));

    [Fact]
    public void Invalid_regex_returns_null()
        => Assert.Null(FindEngine.Build("(unclosed", FindEngine.Mode.Regex, false, false));

    // ===== Normal =====

    [Theory]
    [InlineData("hello", "say hello world", true)]
    [InlineData("Hello", "say hello world", true)]    // case-insensitive by default
    [InlineData("Hello", "say HELLO world", true)]
    [InlineData("xyz", "nothing here", false)]
    public void Normal_case_insensitive_by_default(string query, string sample, bool expected)
    {
        var re = FindEngine.Build(query, FindEngine.Mode.Normal, false, false);
        Assert.NotNull(re);
        if (expected) Assert.Matches(re!, sample); else Assert.DoesNotMatch(re!, sample);
    }

    [Theory]
    [InlineData("Hello", "Hello world", true)]
    [InlineData("Hello", "hello world", false)]
    public void Normal_match_case_requires_exact(string query, string sample, bool expected)
    {
        var re = FindEngine.Build(query, FindEngine.Mode.Normal, matchCase: true, wholeWord: false);
        Assert.NotNull(re);
        if (expected) Assert.Matches(re!, sample); else Assert.DoesNotMatch(re!, sample);
    }

    [Theory]
    [InlineData("foo", "foobar", false)]      // partial word — reject
    [InlineData("foo", "foo bar", true)]      // whole word — accept
    [InlineData("foo", "(foo)", true)]        // word boundary at parens
    public void WholeWord_wraps_boundary(string query, string sample, bool expected)
    {
        var re = FindEngine.Build(query, FindEngine.Mode.Normal, false, wholeWord: true);
        Assert.NotNull(re);
        if (expected) Assert.Matches(re!, sample); else Assert.DoesNotMatch(re!, sample);
    }

    [Fact]
    public void Normal_escapes_regex_specials()
    {
        // The literal string "a.b" should NOT match "aXb" (the '.' is escaped).
        var re = FindEngine.Build("a.b", FindEngine.Mode.Normal, false, false);
        Assert.NotNull(re);
        Assert.Matches(re!, "a.b");
        Assert.DoesNotMatch(re, "aXb");
    }

    // ===== Extended =====

    [Theory]
    [InlineData(@"line\nbreak", "line\nbreak")]
    [InlineData(@"tab\there", "tab\there")]
    [InlineData(@"cr\rreturn", "cr\rreturn")]
    public void Extended_recognizes_common_escapes(string query, string sample)
    {
        var re = FindEngine.Build(query, FindEngine.Mode.Extended, false, false);
        Assert.NotNull(re);
        Assert.Matches(re!, sample);
    }

    [Fact]
    public void Extended_hex_byte_escape()
    {
        // \x20 = space
        var re = FindEngine.Build(@"has\x20a\x20space", FindEngine.Mode.Extended, false, false);
        Assert.NotNull(re);
        Assert.Matches(re!, "has a space here");
    }

    [Fact]
    public void Extended_unicode_escape()
    {
        // — = em dash (—)
        var re = FindEngine.Build(@"em—dash", FindEngine.Mode.Extended, false, false);
        Assert.NotNull(re);
        Assert.Matches(re!, "an em—dash");
    }

    [Fact]
    public void Extended_literal_backslash()
    {
        var re = FindEngine.Build(@"a\\b", FindEngine.Mode.Extended, false, false);
        Assert.NotNull(re);
        Assert.Matches(re!, @"a\b");
        Assert.DoesNotMatch(re, "aXb");
    }

    [Fact]
    public void Extended_unknown_escape_treats_char_literally()
    {
        // \. in Extended mode → literal '.', not "any char"
        var re = FindEngine.Build(@"a\.b", FindEngine.Mode.Extended, false, false);
        Assert.NotNull(re);
        Assert.Matches(re!, "a.b");
        Assert.DoesNotMatch(re, "aXb");
    }

    // ===== Wildcards =====

    [Theory]
    [InlineData("h*o", "hello", true)]                   // '*' = any chars
    [InlineData("h*o", "ho", true)]                       // '*' = also zero chars
    [InlineData("h?llo", "hello", true)]                  // '?' = exactly one
    [InlineData("h?llo", "heello", false)]                // '?' does NOT match two
    [InlineData("*star*", "big star!", true)]
    public void Wildcards_expand_star_and_question(string query, string sample, bool expected)
    {
        var re = FindEngine.Build(query, FindEngine.Mode.Wildcards, false, false);
        Assert.NotNull(re);
        if (expected) Assert.Matches(re!, sample); else Assert.DoesNotMatch(re!, sample);
    }

    [Fact]
    public void Wildcards_escape_star()
    {
        // \* → literal '*', not "any chars"
        var re = FindEngine.Build(@"say\*star", FindEngine.Mode.Wildcards, false, false);
        Assert.NotNull(re);
        Assert.Matches(re!, "say*starred");
        Assert.DoesNotMatch(re, "say Xstarred");
    }

    [Fact]
    public void Wildcards_escape_question()
    {
        var re = FindEngine.Build(@"say\?done", FindEngine.Mode.Wildcards, false, false);
        Assert.NotNull(re);
        Assert.Matches(re!, "say?done");
        Assert.DoesNotMatch(re, "sayXdone");
    }

    [Fact]
    public void Wildcards_escape_regex_specials()
    {
        // Non-wildcard regex metachars should still be treated literally.
        var re = FindEngine.Build("(hello)", FindEngine.Mode.Wildcards, false, false);
        Assert.NotNull(re);
        Assert.Matches(re!, "say (hello) yo");
    }

    // ===== Regex =====

    [Theory]
    [InlineData(@"\d{4}", "year 2026", true)]
    [InlineData(@"^Heading", "Heading text", true)]
    [InlineData(@"^Heading", "  Heading indented", false)]
    [InlineData(@"foo|bar", "just bar here", true)]
    public void Regex_passes_through(string query, string sample, bool expected)
    {
        var re = FindEngine.Build(query, FindEngine.Mode.Regex, false, false);
        Assert.NotNull(re);
        if (expected) Assert.Matches(re!, sample); else Assert.DoesNotMatch(re!, sample);
    }

    [Fact]
    public void Regex_multiline_anchors_work_line_by_line()
    {
        var re = FindEngine.Build(@"^bar", FindEngine.Mode.Regex, false, false);
        Assert.NotNull(re);
        // Multiline mode is on so ^ matches the start of each line, not just the doc.
        Assert.Matches(re!, "foo\nbar\nbaz");
    }
}
