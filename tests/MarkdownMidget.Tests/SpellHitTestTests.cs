using System.Collections.Generic;
using MarkdownMidget.Spelling;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>
/// The source-view context menu decides whether to offer suggestions / Add to
/// Dictionary purely from these ranges. Getting this wrong is what hid the whole
/// spell block on words the engine flagged but couldn't correct.
/// </summary>
public class SpellHitTestTests
{
    // "This is the the end of it." — the engine flags the repeated word at 12..15.
    private static readonly List<(int Start, int Length)> Ranges = new() { (12, 3), (20, 4) };
    private const int TextLength = 40;

    [Theory]
    [InlineData(12)]   // first char
    [InlineData(13)]   // middle
    [InlineData(14)]   // last char
    [InlineData(15)]   // trailing edge counts as on-word
    public void ClickOnRange_ReturnsIt(int index)
        => Assert.Equal((12, 3), SpellHitTest.RangeAt(Ranges, index, TextLength));

    [Theory]
    [InlineData(0)]
    [InlineData(11)]   // just before
    [InlineData(16)]   // gap between ranges
    [InlineData(-1)]
    public void ClickOffRange_ReturnsNull(int index)
        => Assert.Null(SpellHitTest.RangeAt(Ranges, index, TextLength));

    [Fact]
    public void PicksTheRangeUnderTheClick_NotTheFirst()
        => Assert.Equal((20, 4), SpellHitTest.RangeAt(Ranges, 21, TextLength));

    [Fact]
    public void RangeOutrunningTheLiveText_IsRefused()
    {
        // Ranges are shifted through edits; a stale one must not be read past the end.
        var stale = new List<(int, int)> { (30, 10) };
        Assert.Null(SpellHitTest.RangeAt(stale, 32, textLength: 35));
    }

    [Fact]
    public void EmptyRanges_ReturnNull()
        => Assert.Null(SpellHitTest.RangeAt(new List<(int, int)>(), 5, TextLength));

    /// The regression this whole change exists for: the menu must key off the
    /// engine's range, so a context-only error (repeated word) still offers the
    /// spell block even though re-checking that word alone reports no error.
    [Fact]
    public void ContextOnlyError_StillYieldsAHit()
    {
        const string doc = "This is the the end of it.";
        var engineRanges = new List<(int Start, int Length)> { (12, 3) };   // "the"
        var hit = SpellHitTest.RangeAt(engineRanges, 13, doc.Length);
        Assert.NotNull(hit);
        Assert.Equal("the", doc.Substring(hit!.Value.Start, hit.Value.Length));
    }
}
