using System;
using System.Security;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>
/// How far below its button a toolbar tooltip opens (<see cref="ToolbarToolTip"/>), from the pointer size Windows keeps
/// in CursorBaseSize. Every read here is a fake; no test reads the registry. That the toolbar's tooltips open under their
/// button with this offset is checked by hand: proving it needs the window.
/// </summary>
public class ToolbarToolTipTests
{
    [Fact]
    public void AStandardPointerAddsNoOffset() => Assert.Equal(0d, ToolbarToolTip.OffsetFor(() => 32));

    // The Size slider at 2, 5 and 15 (its largest): each step adds 16 pixels to 32. The whole size, not what it adds
    // to 32: the pointer's tip can rest at the button's bottom edge, and its image reaches that far below the tip.
    [Theory]
    [InlineData(48, 48d)]
    [InlineData(96, 96d)]
    [InlineData(256, 256d)]
    public void AnEnlargedPointerMovesTheTooltipDownByItsWholeSize(int size, double offset)
        => Assert.Equal(offset, ToolbarToolTip.OffsetFor(() => size));

    public static TheoryData<object?> NoLargerThanStandard => new()
    {
        null,              // never set: Registry.GetValue returns the default
        "64",              // a string, not a DWORD
        new byte[] { 64 },
        64L,               // a QWORD
        0, -1, 16,         // zero, a negative DWORD, smaller than standard
        257, int.MaxValue, // past the largest the slider sets
    };

    [Theory, MemberData(nameof(NoLargerThanStandard))]
    public void AMissingInvalidOrSmallSizeCountsAsStandard(object? value)
        => Assert.Equal(0d, ToolbarToolTip.OffsetFor(() => value));

    [Fact]
    public void AReadThatThrowsCountsAsStandard()
        => Assert.Equal(0d, ToolbarToolTip.OffsetFor(() => throw new SecurityException("Requested registry access is not allowed.")));
}
