using System.Collections.Generic;
using System.Linq;
using MarkdownMidget.Spelling;
using Xunit;

namespace MarkdownMidget.Tests;

public class MarkdownCodeRangesTests
{
    private static string Cut(string md, (int Start, int End) r) => md[r.Start..r.End];

    [Fact]
    public void Find_ClosedFence_IsOneRange()
    {
        const string md = "prose before\n```csharp\nvar x = 1;\n```\nprose after";
        var ranges = MarkdownCodeRanges.Find(md);
        var hit = Assert.Single(ranges);
        Assert.StartsWith("```csharp", Cut(md, hit));
        Assert.EndsWith("```", Cut(md, hit));
    }

    [Fact]
    public void Find_TildeFence_Recognized()
    {
        const string md = "before\n~~~\ncode here\n~~~\nafter";
        var hit = Assert.Single(MarkdownCodeRanges.Find(md));
        Assert.Contains("code here", Cut(md, hit));
    }

    [Fact]
    public void Find_UnclosedFence_ExemptsRestOfDocument()
    {
        // Mid-typing a code block: everything after the opener is code, matching
        // how the WYSIWYG editor treats it.
        const string md = "prose\n```js\nstill typing code";
        var hit = Assert.Single(MarkdownCodeRanges.Find(md));
        Assert.Equal(md.Length, hit.End);
        Assert.Contains("still typing", Cut(md, hit));
    }

    [Fact]
    public void Find_InlineCode_AndNotInsideFence()
    {
        const string md = "use `httpClient` here\n```\ninner `notSeparate` stays fence\n```\n";
        var ranges = MarkdownCodeRanges.Find(md);
        Assert.Equal(2, ranges.Count); // the inline span + the fence; not three
        Assert.Equal("`httpClient`", Cut(md, ranges[0]));
    }

    [Fact]
    public void ProseSpans_AreTheComplement()
    {
        const string md = "alpha `x` beta\n```\ncode\n```\ngamma";
        var code = MarkdownCodeRanges.Find(md);
        var prose = MarkdownCodeRanges.ProseSpans(md, code)
            .Select(s => md[s.Start..s.End]).ToList();
        Assert.Contains("alpha ", prose[0]);
        Assert.DoesNotContain(prose, p => p.Contains("code"));
        Assert.Contains(prose, p => p.Contains("gamma"));
    }

    [Fact]
    public void Find_EmptyAndNoCode()
    {
        Assert.Empty(MarkdownCodeRanges.Find(""));
        Assert.Empty(MarkdownCodeRanges.Find("just prose, no code at all"));
    }
}

public class SpellTextMapTests
{
    // Two runs: plain "Hello world" at PM 1, then (after a gap for a block
    // boundary) "second block" at PM 20.
    private static readonly List<SpellSegment> Segs = new()
    {
        new SpellSegment(0, 1, 11),    // "Hello world"
        new SpellSegment(12, 20, 12),  // "second block" (plain 12..24 after the \n gap)
    };

    [Theory]
    [InlineData(0, 1)]     // 'H' -> PM 1
    [InlineData(10, 11)]   // 'd' of world
    [InlineData(12, 20)]   // 's' of second
    [InlineData(23, 31)]   // 'k' of block
    public void ToPm_MapsInsideSegments(int plain, int pm)
        => Assert.Equal(pm, SpellTextMap.ToPm(Segs, plain));

    [Theory]
    [InlineData(11)]   // the \n gap between blocks
    [InlineData(24)]   // past the end
    [InlineData(-1)]
    public void ToPm_GapAndOutOfRange_ReturnMinusOne(int plain)
        => Assert.Equal(-1, SpellTextMap.ToPm(Segs, plain));

    [Fact]
    public void MapRanges_MapsWordAndDropsGapCrossers()
    {
        var mapped = SpellTextMap.MapRanges(Segs, new[]
        {
            (6, 5),    // "world" -> PM [7, 12)
            (10, 3),   // "d\ns" crosses the gap -> dropped
            (12, 6),   // "second" -> PM [20, 26)
        });
        Assert.Equal(2, mapped.Count);
        Assert.Equal((7, 12), mapped[0]);
        Assert.Equal((20, 26), mapped[1]);
    }

    [Fact]
    public void MapRanges_EmptySegments_MapsNothing()
        => Assert.Empty(SpellTextMap.MapRanges(new List<SpellSegment>(), new[] { (0, 4) }));
}

public class SpellChunkTests
{
    private static List<string> Tiled(string text, int size)
    {
        var chunks = SpellService.Chunks(text, size).ToList();
        for (int i = 0, at = 0; i < chunks.Count; at += chunks[i++].Length)
            Assert.True(chunks[i].Start == at && chunks[i].Length > 0 && (i == chunks.Count - 1 || text[at + chunks[i].Length - 1] == '\n')
                && (chunks[i].Length <= size || !text.AsSpan(at, chunks[i].Length - 1).Contains('\n')));
        Assert.Equal(text.Length, chunks.Sum(c => c.Length));
        return chunks.Select(c => text.Substring(c.Start, c.Length)).ToList();
    }

    [Theory, InlineData("\n"), InlineData("\r\n")]
    public void Chunks_BreakAtALineBreak_PreferringABlankLine(string nl)
    {
        var line = "a line of words" + nl;   // four lines, a blank line, thirty lines
        var chunks = Tiled(string.Concat(Enumerable.Repeat(line, 4)) + nl + string.Concat(Enumerable.Repeat(line, 30)), 100);
        Assert.Equal(4 * line.Length, chunks[0].Length);   // at the blank line, not the last break before 100
        Assert.True(chunks.Count > 5);
    }

    [Fact]
    public void Chunks_KeepALongLineWhole_UseAbout16KB_AndLeaveNoEmptyChunk()
    {
        var longLine = new string('x', 250) + "\n";
        Assert.Equal(new[] { "short\n", longLine, "end" }, Tiled("short\n" + longLine + "end", 100));
        Assert.Equal(new[] { 16_000, 16_000, 8_000 }, SpellService.Chunks(string.Concat(Enumerable.Repeat(new string('w', 99) + "\n", 400))).Select(c => c.Length));
        Assert.Empty(Tiled("", 100));
    }

    [Fact]
    public void CheckInChunks_GivesTheRangesOfOneWholeTextCall()
    {
        static List<(int, int)> Flag(string s) => System.Text.RegularExpressions.Regex.Matches(s, @"\bteh\b").Select(m => (m.Index, m.Length)).ToList();
        var text = string.Concat(Enumerable.Range(0, 200).Select(i => i % 11 == 0 ? "\n" : "teh starts and ends teh\n"));
        Assert.Contains(SpellService.Chunks(text, 100), c => c.Start > 0 && text.AsSpan(c.Start).StartsWith("teh") && text.AsSpan(0, c.Start).EndsWith("teh\n"));
        Assert.Equal(Flag(text), SpellService.CheckInChunks(text, Flag, 100));
    }
}
