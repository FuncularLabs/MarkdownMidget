using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MarkdownMidget.Source;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>
/// Which spaces the source view dots while the ¶ toggle is on (<see cref="SpaceMarks"/>).
/// Each case is a document, one line per "\n"; the expected string lists each line's
/// marked offsets, lines separated by "|".
/// </summary>
public class SpaceMarksTests
{
    private static string Marks(string document)
    {
        var state = SpaceMarks.State.Start;
        var lines = new List<string>();
        foreach (var line in document.Split('\n'))
        {
            var (marks, next) = SpaceMarks.Step(line, state);
            lines.Add(string.Join(",", marks));
            state = next;
        }
        return string.Join("|", lines);
    }

    [Theory]
    [InlineData("hard break  ", "10,11")]
    [InlineData("one space ", "9")]
    [InlineData("a  b", "1,2")]
    [InlineData("a b c", "")]
    [InlineData("end.  Next   x", "4,5,10,11,12")]
    [InlineData("   ", "0,1,2")]                  // a whitespace-only line: every space
    [InlineData(" \t ", "0,2")]
    [InlineData("a \t ", "1,3")]                  // trailing spaces around a tab
    [InlineData("  indented  text", "10,11")]     // leading indentation is not marked
    [InlineData("-   item  two", "8,9")]          // nor the spaces after a list marker
    [InlineData("12.  item", "")]
    [InlineData("| a    | b |  ", "12,13")]      // a table's padding is not a run; its trailing spaces are marked
    public void TrailingSpacesRunsOfTwoAndWhitespaceOnlyLinesAreMarked(string document, string expected) =>
        Assert.Equal(expected, Marks(document));

    [Theory]
    [InlineData("```\nx  =  1  \n```\na  b", "|7,8||1,2")]
    [InlineData("~~~\n```\na  b\n~~~\nc  d", "||||1,2")]            // only its own kind of fence closes it…
    [InlineData("````\n```\na  b\n````\nc  d", "||||1,2")]          // …at least as long
    [InlineData("text\n\n    a  =  1 \nafter  x", "||11|5,6")]      // indented code after a blank line
    [InlineData("para\n    not  code", "|7,8")]                     // a paragraph's continuation is not code…
    [InlineData("para\n    ```\na  b", "||1,2")]                    // …nor a fence, four columns in
    [InlineData("# H\n    a  b", "|")]                              // code may follow a heading directly
    [InlineData("- item\n\n      a  b\n  text  x", "|||6,7")]       // code inside a list item is 4 past its text
    [InlineData("- a\n\n    - b  c", "||7,8")]                      // a four-space nested list is not code
    public void InCodeBlocksOnlyTrailingSpacesAreMarked(string document, string expected) =>
        Assert.Equal(expected, Marks(document));

    [Theory]
    [InlineData("para\n \tx", "|0")]              // tabs and spaces mixed (at the top of a document, 4 columns in, it would be code)
    [InlineData("para\n\t  x", "|1,2")]
    [InlineData("1. one\n  - two", "|0,1")]       // short of "1. ": a new list, not a nested one
    [InlineData("- a\n - b", "|0")]
    [InlineData("- a\n b", "|0")]
    [InlineData("10. ten\n   x", "|0,1,2")]
    [InlineData("```\n \tx\n```", "||")]          // not in code
    public void SuspectIndentationMarksItsLeadingSpaces(string document, string expected) =>
        Assert.Equal(expected, Marks(document));

    [Theory]
    [InlineData("- a\n  - b\n    - c\n- d")]
    [InlineData("1. a\n   - b\n      text\n2. c")]
    [InlineData("- a\n    - four-space nested\n        text")]
    [InlineData("* a\n\n  para in item\n\n  ```\n  code\n  ```")]
    [InlineData("- a\nlazy continuation")]
    [InlineData("* * *\n x")]                     // a thematic break opens no list
    [InlineData("---\ntitle: x\ntags:\n  - a\n  - b\n---")]
    public void WellFormedIndentationIsNotMarked(string document) =>
        Assert.Equal(string.Join("|", document.Split('\n').Select(_ => "")), Marks(document));

    [Theory]   // review finding 1: the blockquote prefix is neither indentation nor a run
    [InlineData("> - a\n>   - nested", "|")]
    [InlineData("> 1. x\n>    continuation", "|")]
    [InlineData("> > - a\n> >   - b  c", "|9,10")]
    [InlineData("> a  b\n>\n> \n>   ", "3,4|||2,3")]
    [InlineData("> - a\nlazy  x\n>   - b", "|4,5|")]
    [InlineData("1. one\n>  x", "|")]                      // a quote starts afresh: the list outside it is not its container…
    [InlineData("> 1. one\n  lazy", "|")]                  // …and the list inside it is not the lazy line's
    [InlineData("> 1. one\n>   - two", "|2,3")]           // suspect inside the quote, counted from the quote's text
    [InlineData("> ```\n> a  b\nc  d", "||1,2")]            // a fence ends with its quote
    public void BlockquoteMarkersAreNeitherIndentationNorRuns(string document, string expected) =>
        Assert.Equal(expected, Marks(document));

    [Theory]   // review findings 5 and 7: fences in list items, HTML comments, front matter
    [InlineData("- item\n  ```\n  a  b\nafter  x", "|||5,6")]           // (a) the fence ends with its list item
    [InlineData("- item\n\n  ```\n  a  b\n      ```\n  c  d", "|||||")]  // (b) four columns past the item is code, not a closing fence
    [InlineData("<!--\n```\n-->\na  b", "|||1,2")]                      // (c) a fence in a comment opens nothing
    [InlineData("<!-- note  \nx  y\n-->\na  b", "9,10|||1,2")]  
    [InlineData("- ```\n  a  b\n  ```\nc  d", "|||1,2")]                // a fence straight after a list marker
    [InlineData("---\ntitle:  x\n---\na  b", "|||1,2")]                 // front matter
    [InlineData("---\nk:  v\n...\na  b", "|||1,2")]
    [InlineData("\n---\nk:  v", "||2,3")]                                // only on the first line
    [InlineData("Title\n=====\n    a  b", "||")]                         // code may follow a setext heading
    public void FencesCommentsAndFrontMatterMarkOnlyTrailingSpaces(string document, string expected) =>
        Assert.Equal(expected, Marks(document));

    public static TheoryData<string> Corpus() =>
        [.. Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures", "space-marks"), "*.md").Select(Path.GetFileName)!];

    /// <summary>Review finding 2: realistic documents, each written with · on exactly the
    /// spaces that should be marked; the input is the same file with every · a space.</summary>
    [Theory]
    [MemberData(nameof(Corpus))]
    public void EachCorpusDocumentIsDottedExactlyWhereItsFileShows(string file)
    {
        var expected = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "space-marks", file)).Replace("\r\n", "\n").Split('\n');
        var state = SpaceMarks.State.Start;
        var wrong = new List<string>();
        for (var n = 0; n < expected.Length; n++)
        {
            var line = expected[n].Replace('·', ' ').ToCharArray();
            var (marks, next) = SpaceMarks.Step(new string(line), state);
            foreach (var i in marks) line[i] = '·';
            if (new string(line) != expected[n]) wrong.Add($"{file}:{n + 1}: [{new string(line)}]");
            state = next;
        }
        Assert.Empty(wrong);
    }
}