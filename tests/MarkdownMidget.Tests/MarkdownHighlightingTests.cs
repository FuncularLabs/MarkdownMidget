using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Highlighting;
using MarkdownMidget.Source;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>
/// The markdown highlighting definition, checked by asking a real
/// <see cref="DocumentHighlighter"/> which named colour each construct receives. This
/// is where the two faults of AvalonEdit's built-in definition are held off: greedy
/// spans that merge two bolds into one, and constructs (lists, rules, fences) with no
/// rule at all. Colour VALUES are not asserted here — those come from the theme; the
/// names are the definition's contract.
/// </summary>
public class MarkdownHighlightingTests
{
    /// <summary>Highlight one line and return each coloured section as
    /// (colour-name, covered-text), in document order.</summary>
    private static List<(string Name, string Text)> Sections(string singleLine)
        => Sections(singleLine, 1);

    private static List<(string Name, string Text)> Sections(string document, int lineNumber)
    {
        List<(string, string)> result = null!;
        Exception? error = null;
        var done = new ManualResetEventSlim();
        var t = new Thread(() =>
        {
            try
            {
                var def = new SourceHighlighting().Definition;
                var doc = new TextDocument(document);
                var hl = new DocumentHighlighter(doc, def);
                var line = doc.GetLineByNumber(lineNumber);
                var hlLine = hl.HighlightLine(lineNumber);
                var baseOffset = line.Offset;
                result = hlLine.Sections
                    .Select(s => (s.Color.Name, doc.GetText(s.Offset, s.Length)))
                    .ToList();
            }
            catch (Exception ex) { error = ex; }
            finally { done.Set(); }
        }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        Assert.True(done.Wait(TimeSpan.FromSeconds(30)), "highlighter harness timed out");
        if (error is not null) throw error;
        return result;
    }

    private static string? ColourOf(string line, string fragment)
    {
        foreach (var (name, text) in Sections(line))
            if (text.Contains(fragment, StringComparison.Ordinal))
                return name;
        return null;
    }

    // ===== 2.1 the greedy-merge bug must not exist =====

    [Fact]
    public void TwoStrongSpansOnOneLineDoNotMerge()
    {
        var sections = Sections("**one** plain **two**").Where(s => s.Name == "Strong").ToList();
        Assert.Equal(2, sections.Count);
        Assert.Equal("**one**", sections[0].Text);
        Assert.Equal("**two**", sections[1].Text);
    }

    [Fact]
    public void TwoInlineCodeSpansDoNotMerge()
    {
        var sections = Sections("a `x` b `y` c").Where(s => s.Name == "InlineCode").ToList();
        Assert.Equal(2, sections.Count);
        Assert.Equal("`x`", sections[0].Text);
        Assert.Equal("`y`", sections[1].Text);
    }

    // ===== 2.2 each construct gets its colour =====

    [Theory]
    [InlineData("# Title", "# Title", "Heading")]
    [InlineData("### Deeper", "### Deeper", "Heading")]
    [InlineData("**bold**", "**bold**", "Strong")]
    [InlineData("__bold__", "__bold__", "Strong")]
    [InlineData("*em*", "*em*", "Emphasis")]
    [InlineData("text with `code` in it", "`code`", "InlineCode")]
    [InlineData("[label](https://x)", "[label](https://x)", "Link")]
    [InlineData("![alt](img.png)", "![alt](img.png)", "Link")]
    [InlineData("> quoted", ">", "BlockQuote")]
    public void EachConstructGetsItsColour(string line, string fragment, string expected)
        => Assert.Equal(expected, ColourOf(line, fragment));

    [Fact]
    public void ListMarkerIsColouredButNotTheWholeItem()
    {
        var sections = Sections("- an item");
        Assert.Contains(sections, s => s.Name == "ListMarker" && s.Text.Trim() == "-");
        // The item text is not swept into ListMarker.
        Assert.DoesNotContain(sections, s => s.Name == "ListMarker" && s.Text.Contains("item"));
    }

    [Fact]
    public void OrderedListMarkerIsColoured()
    {
        var sections = Sections("3. third");
        Assert.Contains(sections, s => s.Name == "ListMarker" && s.Text.Trim() == "3.");
    }

    [Theory]
    [InlineData("---")]
    [InlineData("***")]
    [InlineData("___")]
    public void ThematicBreakIsColouredRule(string line)
        => Assert.Equal("Rule", Sections(line).Single().Name);

    [Fact]
    public void SnakeCaseIsNotItalicised()
    {
        // A lone underscore pair inside a word must not become emphasis.
        Assert.Null(ColourOf("some_long_name here", "some_long_name"));
    }

    [Fact]
    public void EmphasisInsideAWordBoundaryStillWorks()
        => Assert.Equal("Emphasis", ColourOf("an *italic* word", "*italic*"));

    // ===== 2.2 fenced code across lines =====

    [Fact]
    public void FenceOpeningLineIsColouredAsCode()
    {
        // Line 1 of a fenced block: the ``` and the language word are code-coloured.
        var sections = Sections("```csharp\nvar x = 1;\n```", 1);
        Assert.Contains(sections, s => s.Name == "InlineCode" && s.Text.Contains("```"));
    }

    [Fact]
    public void FenceBodyIsColouredAsCode()
    {
        // The whole fenced block reads as code, body included (multiline Span).
        var sections = Sections("```\nplain body\n```", 2);
        Assert.Contains(sections, s => s.Name == "InlineCode");
    }

    [Fact]
    public void FenceClosingLineIsColouredAsCode()
    {
        var sections = Sections("```\nbody\n```", 3);
        Assert.Contains(sections, s => s.Name == "InlineCode" && s.Text.Contains("```"));
    }

    [Fact]
    public void StrongTakesPrecedenceOverEmphasisSoDoubleStarsAreBold()
    {
        // The line has only **bold**; it must be Strong, not two Emphasis runs.
        var names = Sections("**bold**").Select(s => s.Name).Distinct().ToList();
        Assert.Contains("Strong", names);
        Assert.DoesNotContain("Emphasis", names);
    }
}
