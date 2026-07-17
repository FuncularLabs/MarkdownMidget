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
