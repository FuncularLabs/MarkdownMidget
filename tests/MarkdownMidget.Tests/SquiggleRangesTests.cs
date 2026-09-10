using MarkdownMidget.Source;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>
/// The offset arithmetic that keeps misspelling underlines glued to their words
/// through an edit. Pure, so it is tested without a control: a range before the edit
/// is untouched, one after slides by the net delta, and one the edit touched is
/// dropped rather than left pointing at text that may have changed.
/// </summary>
public class SquiggleRangesTests
{
    private static (int, int)[] R(params (int, int)[] r) => r;

    [Fact]
    public void ARangeWhollyBeforeTheEditIsUntouched()
    {
        // edit at 20 (+5): the word at [2,4) is before it.
        var shifted = SquiggleRanges.Shift(R((2, 4)), offset: 20, added: 5, removed: 0);
        Assert.Equal(new[] { (2, 4) }, shifted);
    }

    [Fact]
    public void ARangeWhollyAfterAnInsertSlidesByTheDelta()
    {
        var shifted = SquiggleRanges.Shift(R((30, 4)), offset: 10, added: 3, removed: 0);
        Assert.Equal(new[] { (33, 4) }, shifted);
    }

    [Fact]
    public void ARangeWhollyAfterADeletionSlidesBack()
    {
        var shifted = SquiggleRanges.Shift(R((30, 4)), offset: 10, added: 0, removed: 3);
        Assert.Equal(new[] { (27, 4) }, shifted);
    }

    [Fact]
    public void ARangeAfterAReplacementSlidesByTheNetDelta()
    {
        // replaced 6 chars with 2 at offset 5: net -4.
        var shifted = SquiggleRanges.Shift(R((30, 4)), offset: 5, added: 2, removed: 6);
        Assert.Equal(new[] { (26, 4) }, shifted);
    }

    [Fact]
    public void ARangeTheEditOverlapsIsDropped()
    {
        // edit at 3 lands inside the word [2,4) → [2,6): dropped, re-flagged next pass.
        var shifted = SquiggleRanges.Shift(R((2, 4)), offset: 3, added: 1, removed: 0);
        Assert.Empty(shifted);
    }

    [Fact]
    public void AnEditExactlyAtARangeStartCountsAsOverlapAndDrops()
    {
        // start >= offset+removed is "after"; at offset==start with removed 0 that
        // holds, so an insert exactly at the start slides rather than drops.
        var slid = SquiggleRanges.Shift(R((10, 4)), offset: 10, added: 2, removed: 0);
        Assert.Equal(new[] { (12, 4) }, slid);

        // but a deletion straddling the start overlaps and drops.
        var dropped = SquiggleRanges.Shift(R((10, 4)), offset: 9, added: 0, removed: 2);
        Assert.Empty(dropped);
    }

    [Fact]
    public void AnEditExactlyAtARangeEndLeavesItAlone()
    {
        // start+len <= offset is "before": an insert at the character just past the
        // word does not disturb it.
        var shifted = SquiggleRanges.Shift(R((2, 4)), offset: 6, added: 3, removed: 0);
        Assert.Equal(new[] { (2, 4) }, shifted);
    }

    [Fact]
    public void MixedRangesEachFollowTheirOwnRule()
    {
        // edit at 10 (+2, -0): [2,4) before untouched; [10,3) starts at the edit →
        // slides; [30,2) after → slides.
        var shifted = SquiggleRanges.Shift(R((2, 4), (10, 3), (30, 2)), offset: 10, added: 2, removed: 0);
        Assert.Equal(new[] { (2, 4), (12, 3), (32, 2) }, shifted);
    }

    [Fact]
    public void EmptyInputReturnsEmpty()
        => Assert.Empty(SquiggleRanges.Shift(R(), 5, 1, 1));
}
