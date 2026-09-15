using MarkdownMidget;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>The busy lightbox of a switch from the source view (<see cref="SwitchBusy"/>): whose it is, and when it shows.</summary>
public class SwitchBusyTests
{
    [Fact]
    public void OnlyTheSwitchUnderWayOwnsTheIndicator()
    {
        Assert.True(SwitchBusy.Owns(owner: 5, busy: 5));
        Assert.False(SwitchBusy.Owns(owner: 6, busy: 5));   // a later switch's: an earlier one's end or paint must not clear it
        Assert.False(SwitchBusy.Owns(owner: 0, busy: 5));   // already ended, by a switch back to source or a failed install
        Assert.False(SwitchBusy.Owns(owner: 0, busy: 0));   // none at all
    }

    [Fact]
    public void TheLightboxShowsOnlyOverAStillInstallingSourceViewNoOpenHasCovered()
    {
        Assert.True(SwitchBusy.ShowsNow(owner: 5, busy: 5, sourceView: true, overlayShowing: false));
        Assert.False(SwitchBusy.ShowsNow(owner: 5, busy: 5, sourceView: false, overlayShowing: false));   // already swapped: the WebView2 would cover it
        Assert.False(SwitchBusy.ShowsNow(owner: 5, busy: 5, sourceView: true, overlayShowing: true));     // an open's overlay is up
        Assert.False(SwitchBusy.ShowsNow(owner: 0, busy: 5, sourceView: true, overlayShowing: false));    // over before the delay: a small file never flashes it
    }
}
