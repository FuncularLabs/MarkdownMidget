using System;
using System.Security;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>
/// Where a toolbar tooltip opens (<see cref="ToolbarToolTip"/>), from the pointer size Windows keeps in CursorBaseSize.
/// Every read here is a fake: the tests use ToolbarToolTip.Mapping, and never ToolbarToolTip's own fields, whose static
/// constructor reads the registry. That the toolbar's tooltips use these values is checked by hand: it needs the window.
/// </summary>
public class ToolbarToolTipTests
{
    private static readonly (PlacementMode, double) Unchanged = (PlacementMode.Mouse, 0d);

    [Fact]
    public void AStandardPointerKeepsWpfsPlacementUnderThePointer() => Assert.Equal(Unchanged, ToolbarToolTip.Mapping.For(() => 32));

    // The Size slider at 2, 5 and 15 (its largest): each step adds 16 pixels to 32. Under the button, down by the whole
    // size: the pointer's tip can rest at the button's bottom edge, and its image reaches that far below the tip.
    [Theory]
    [InlineData(48)]
    [InlineData(96)]
    [InlineData(256)]
    public void AnEnlargedPointerMovesTheTooltipUnderTheButtonByItsWholeSize(int size)
        => Assert.Equal((PlacementMode.Bottom, (double)size), ToolbarToolTip.Mapping.For(() => size));

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
    public void AMissingInvalidOrSmallSizeKeepsWpfsPlacement(object? value)
        => Assert.Equal(Unchanged, ToolbarToolTip.Mapping.For(() => value));

    [Fact]
    public void AReadThatThrowsKeepsWpfsPlacement()
        => Assert.Equal(Unchanged, ToolbarToolTip.Mapping.For(() => throw new SecurityException("Requested registry access is not allowed.")));

    /// <summary>
    /// The styles set the standard values explicitly, and a tooltip takes its owner's value once it is set at all. So
    /// "unchanged" holds only if the values are what toolbar controls and a tooltip have when nothing sets them: no theme
    /// style of their own (no window).
    /// </summary>
    [Fact]
    public void TheStandardValuesAreWhatToolbarControlsAndTheirTooltipsHaveUnset()
    {
        Exception? error = null;
        var t = new Thread(() => { try {
            var grouped = new ToolBarToggle();
            var controls = new Control[] { new Button(), new ToggleButton(), new ComboBox(), grouped };
            var bar = new ToolBar { Items = { controls[0], controls[1], controls[2], new Border { Child = grouped } } };
            var tip = new ToolTip { Content = "Bold (Ctrl+B)" };
            foreach (var e in new FrameworkElement[] { bar, tip })
            {
                e.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity)); e.Arrange(new Rect(e.DesiredSize));
            }
            Assert.NotNull(tip.Template); // its theme style applied
            foreach (var c in controls)
            {
                Assert.NotNull(c.Template);
                Assert.Equal(Unchanged, (ToolTipService.GetPlacement(c), ToolTipService.GetVerticalOffset(c)));
                Assert.Equal(BaseValueSource.Default, DependencyPropertyHelper.GetValueSource(c, ToolTipService.PlacementProperty).BaseValueSource);
                Assert.Equal(BaseValueSource.Default, DependencyPropertyHelper.GetValueSource(c, ToolTipService.VerticalOffsetProperty).BaseValueSource);
            }
            Assert.Equal(Unchanged, (tip.Placement, tip.VerticalOffset));
            Assert.Equal(BaseValueSource.Default, DependencyPropertyHelper.GetValueSource(tip, ToolTip.PlacementProperty).BaseValueSource);
            Assert.Equal(BaseValueSource.Default, DependencyPropertyHelper.GetValueSource(tip, ToolTip.VerticalOffsetProperty).BaseValueSource);
        } catch (Exception ex) { error = ex; } });
        t.SetApartmentState(ApartmentState.STA); t.Start();
        Assert.True(t.Join(TimeSpan.FromSeconds(30)), "timed out"); if (error is not null) throw error;
    }
}
