using System.Collections.Generic;
using System.Windows;
using MarkdownMidget;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>
/// Restoring a remembered window position is how an app disappears: the monitor it
/// was on gets unplugged, or the laptop leaves its dock, and the window reopens
/// where no mouse can reach it. Every case here is one of those.
/// </summary>
public class WindowPlacementTests
{
    private static readonly Size Min = new(520, 360);
    private static readonly List<Rect> OneScreen = new() { new Rect(0, 0, 1920, 1080) };
    private static readonly List<Rect> TwoScreens = new()
    {
        new Rect(0, 0, 1920, 1080),          // primary
        new Rect(-1920, 0, 1920, 1080),      // a second monitor to the left
    };

    [Fact]
    public void NothingSaved_UsesTheDefault()
        => Assert.Null(WindowPlacement.Sanitize(null, OneScreen, Min));

    [Fact]
    public void FullyOnScreen_IsKeptExactly()
    {
        var saved = new Rect(100, 80, 1120, 720);
        Assert.Equal(saved, WindowPlacement.Sanitize(saved, OneScreen, Min));
    }

    [Fact]
    public void OnASecondMonitor_IsKept()
    {
        var saved = new Rect(-1500, 100, 1120, 720);   // sits on the left-hand screen
        Assert.Equal(saved, WindowPlacement.Sanitize(saved, TwoScreens, Min));
    }

    [Fact]
    public void SecondMonitorUnplugged_ComesBackToThePrimary()
    {
        // Saved on the left-hand monitor, which no longer exists.
        var saved = new Rect(-1500, 100, 1120, 720);
        var result = WindowPlacement.Sanitize(saved, OneScreen, Min);
        Assert.NotNull(result);
        Assert.Equal(1120, result!.Value.Width);       // size is preserved…
        Assert.Equal(720, result.Value.Height);
        Assert.True(result.Value.X >= 0 && result.Value.Y >= 0);   // …but it's reachable
        Assert.True(result.Value.Right <= 1920 && result.Value.Bottom <= 1080);
    }

    [Fact]
    public void MostlyOffTheBottom_IsRescued()
    {
        // Only a sliver of title bar left on screen — below the visibility floor.
        var saved = new Rect(200, 1060, 1120, 720);
        var result = WindowPlacement.Sanitize(saved, OneScreen, Min);
        Assert.NotNull(result);
        Assert.True(result!.Value.Y + 80 <= 1080, "the title bar must be grabbable");
    }

    [Fact]
    public void ResolutionDropped_SizeIsClampedToFit()
    {
        // Undocking from a 4K panel onto the laptop screen: the window still overlaps,
        // so it isn't recentred, but at 3000px wide its close button would sit past
        // the right edge. It has to be shrunk to the monitor it landed on.
        var saved = new Rect(0, 0, 3000, 2000);
        var small = new List<Rect> { new(0, 0, 1280, 720) };
        var result = WindowPlacement.Sanitize(saved, small, Min);
        Assert.NotNull(result);
        Assert.Equal(1280, result!.Value.Width);
        Assert.Equal(720, result.Value.Height);
        Assert.True(result.Value.Right <= 1280 && result.Value.Bottom <= 720);
    }

    [Fact]
    public void OversizeOnASecondMonitor_IsClampedToThatMonitor()
    {
        // The window it lands on is the one it must fit — not the primary, and not
        // the bounding box of both.
        var screens = new List<Rect>
        {
            new(0, 0, 1920, 1080),
            new(-1280, 0, 1280, 720),        // smaller monitor to the left
        };
        var result = WindowPlacement.Sanitize(new Rect(-1280, 0, 1800, 1000), screens, Min);
        Assert.NotNull(result);
        Assert.Equal(1280, result!.Value.Width);
        Assert.Equal(720, result.Value.Height);
        Assert.Equal(-1280, result.Value.X);
    }

    [Fact]
    public void InAGapBetweenMonitors_ComesBackToThePrimary()
    {
        // An L-shaped arrangement has holes the virtual-screen bounding box calls
        // "on screen". Per-monitor checking is what catches this.
        var screens = new List<Rect>
        {
            new(0, 0, 1920, 1080),           // primary
            new(1920, -1080, 1920, 1080),    // stacked up and to the right
        };
        var saved = new Rect(2400, 200, 800, 600);   // inside the bounding box, on no monitor
        var result = WindowPlacement.Sanitize(saved, screens, Min);
        Assert.NotNull(result);
        Assert.NotEqual(saved, result!.Value);
        Assert.True(result.Value.X >= 0 && result.Value.Right <= 1920, "recentred on the primary");
        Assert.True(result.Value.Y >= 0 && result.Value.Bottom <= 1080);
    }

    [Fact]
    public void ScreenSmallerThanTheMinimum_ClampsToTheScreen()
    {
        // The clamp must not overshoot into a negative or degenerate size when the
        // monitor is smaller than the app's own minimum.
        var tiny = new List<Rect> { new(0, 0, 400, 300) };
        var result = WindowPlacement.Sanitize(new Rect(0, 0, 1120, 720), tiny, Min);
        Assert.NotNull(result);
        Assert.Equal(400, result!.Value.Width);
        Assert.Equal(300, result.Value.Height);
    }

    [Fact]
    public void StraddlingTwoMonitors_IsLeftAlone()
    {
        // Spanning the seam is a deliberate choice on a multi-monitor desk. Snapping
        // it onto whichever screen holds the larger share, every single launch, would
        // be the app fighting the user.
        var screens = new List<Rect>
        {
            new(0, 0, 1920, 1080),
            new(1920, 0, 1920, 1080),
        };
        var saved = new Rect(1500, 100, 1000, 700);   // 420px on the left, 580 on the right
        Assert.Equal(saved, WindowPlacement.Sanitize(saved, screens, Min));
    }

    [Fact]
    public void TitleBarAboveTheTopEdge_IsRescued()
    {
        // The body is amply visible, but the caption bar - the only draggable part -
        // is off the top. Judging by the whole window would strand it here, and it
        // would re-save itself on close and come back stranded every launch.
        var saved = new Rect(200, -200, 1120, 720);
        var result = WindowPlacement.Sanitize(saved, OneScreen, Min);
        Assert.NotNull(result);
        Assert.True(result!.Value.Y >= 0, "the title bar must be on screen");
    }

    [Fact]
    public void StraddlingATopEdgeSeam_IsKeptWhileTheUpperMonitorExists()
    {
        // Same rect as above, but now there IS a monitor above: it's a legitimate
        // straddle and must survive untouched.
        var screens = new List<Rect>
        {
            new(0, 0, 1920, 1080),
            new(0, -1080, 1920, 1080),
        };
        var saved = new Rect(200, -200, 1120, 720);
        Assert.Equal(saved, WindowPlacement.Sanitize(saved, screens, Min));
    }

    [Fact]
    public void HangingOffAnEdgeButGrabbable_IsLeftAlone()
    {
        var saved = new Rect(1400, 900, 1000, 700);   // well past the right and bottom
        Assert.Equal(saved, WindowPlacement.Sanitize(saved, OneScreen, Min));
    }

    [Theory]
    [InlineData(300, 720)]    // too narrow
    [InlineData(1120, 100)]   // too short
    [InlineData(0, 0)]
    public void UnusableSize_FallsBackToTheDefault(double w, double h)
        => Assert.Null(WindowPlacement.Sanitize(new Rect(10, 10, w, h), OneScreen, Min));

    /// <summary>The floor the app actually passes is 240x160 physical, not the WPF
    /// minimum - see MainWindow.MinSaved. Exercise the real one.</summary>
    private static readonly Size ProductionMin = new(240, 160);

    [Theory]
    [InlineData(300, 720)]     // above the real floor: accepted, unlike at Min
    [InlineData(240, 160)]     // exactly the floor
    public void AtTheProductionFloor_ModestRectsAreKept(double w, double h)
    {
        var saved = new Rect(100, 100, w, h);
        Assert.Equal(saved, WindowPlacement.Sanitize(saved, OneScreen, ProductionMin));
    }

    [Theory]
    [InlineData(239, 400)]
    [InlineData(400, 159)]
    public void BelowTheProductionFloor_IsRejectedAsGarbage(double w, double h)
        => Assert.Null(WindowPlacement.Sanitize(new Rect(100, 100, w, h), OneScreen, ProductionMin));

    [Fact]
    public void NoScreensReported_FallsBackToTheDefault()
        => Assert.Null(WindowPlacement.Sanitize(new Rect(0, 0, 1120, 720), new List<Rect>(), Min));

    [Fact]
    public void NaNBounds_FallBackToTheDefault()
        => Assert.Null(WindowPlacement.Sanitize(
            new Rect(double.NaN, 0, 1120, 720), OneScreen, Min));
}
