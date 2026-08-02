using MarkdownMidget.Updates;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>
/// What the About box is allowed to offer. The rule that matters: a prerelease is
/// only interesting while it still leads, because the releases feed goes on
/// reporting the newest one forever.
/// </summary>
public class UpdateOfferTests
{
    private static ReleaseInfo Rel(string tag, bool prerelease) =>
        new(tag, UpdateVersion.Parse(tag)!, prerelease, "", "asset.exe", "https://x/asset.exe", 1);

    private static UpdateVersion Cur(string v) => UpdateVersion.Parse(v)!;

    [Fact]
    public void SupersededPrerelease_IsNotOffered()
    {
        // The case this rule was written for: 0.6.0-beta2 is still the newest
        // prerelease on GitHub long after 0.6.2 shipped. Offering it would invite a
        // downgrade dressed up as an upgrade.
        Assert.False(UpdateOffer.ShowPrerelease(
            Rel("v0.6.0-beta2", true), Rel("v0.6.2", false), Cur("0.6.2")));
    }

    [Fact]
    public void PrereleaseAheadOfBoth_IsOffered()
    {
        Assert.True(UpdateOffer.ShowPrerelease(
            Rel("v0.7.0-beta1", true), Rel("v0.6.2", false), Cur("0.6.2")));
    }

    [Fact]
    public void PrereleaseNewerThanStableButOlderThanRunning_IsNotOffered()
    {
        // Running a later prerelease than the one on offer.
        Assert.False(UpdateOffer.ShowPrerelease(
            Rel("v0.7.0-beta1", true), Rel("v0.6.2", false), Cur("0.7.0-beta2")));
    }

    [Fact]
    public void NextPrerelease_IsOfferedToSomeoneOnTheEarlierOne()
    {
        Assert.True(UpdateOffer.ShowPrerelease(
            Rel("v0.7.0-beta2", true), Rel("v0.6.2", false), Cur("0.7.0-beta1")));
    }

    [Fact]
    public void PrereleaseOfAVersionAlreadyStable_IsNotOffered()
    {
        // 0.7.0-beta1 vs a shipped 0.7.0: same numeric, and stable outranks it.
        Assert.False(UpdateOffer.ShowPrerelease(
            Rel("v0.7.0-beta1", true), Rel("v0.7.0", false), Cur("0.7.0")));
    }

    [Fact]
    public void NoPrerelease_IsNotOffered()
        => Assert.False(UpdateOffer.ShowPrerelease(null, Rel("v0.6.2", false), Cur("0.6.2")));

    [Fact]
    public void PrereleaseWithNoStablePublishedYet_IsJudgedAgainstTheRunningVersion()
    {
        Assert.True(UpdateOffer.ShowPrerelease(Rel("v0.7.0-beta1", true), null, Cur("0.6.2")));
        Assert.False(UpdateOffer.ShowPrerelease(Rel("v0.6.0-beta1", true), null, Cur("0.6.2")));
    }

    [Fact]
    public void UnknownRunningVersion_StillRespectsSupersession()
    {
        // If we can't tell what's running, the stable comparison is all we have -
        // and it alone is enough to reject a superseded prerelease.
        Assert.False(UpdateOffer.ShowPrerelease(Rel("v0.6.0-beta2", true), Rel("v0.6.2", false), null));
        Assert.True(UpdateOffer.ShowPrerelease(Rel("v0.7.0-beta1", true), Rel("v0.6.2", false), null));
    }

    [Fact]
    public void NothingToCompareAgainst_OffersNoPrerelease()
    {
        // No running version AND no stable: there is no floor, so an ancient alpha
        // would otherwise be presented as an upgrade.
        Assert.False(UpdateOffer.ShowPrerelease(Rel("v0.1.0-alpha1", true), null, null));
    }

    [Theory]
    [InlineData("v0.6.2", "0.6.1", true)]           // newer stable available
    [InlineData("v0.6.2", "0.6.2", false)]          // already on it
    [InlineData("v0.6.1", "0.6.2", false)]          // running ahead of the feed
    [InlineData("v0.6.2", "0.6.2-beta1", true)]     // stable outranks its own prerelease
    public void StableUpdate_OfferedOnlyWhenNewer(string tag, string current, bool expected)
        => Assert.Equal(expected, UpdateOffer.ShowStableUpdate(Rel(tag, false), Cur(current)));

    [Fact]
    public void StableUpdate_NeedsAReleaseToOffer()
        => Assert.False(UpdateOffer.ShowStableUpdate(null, Cur("0.6.2")));

    [Fact]
    public void UnreadableRunningVersion_StillOffersTheStableRelease()
    {
        // The one failure that must never happen is a real update going unoffered.
        // If the running version can't be parsed we can't prove we're current, so
        // offer it: a redundant re-install is cheap, being stranded is not.
        Assert.True(UpdateOffer.ShowStableUpdate(Rel("v0.6.2", false), null));
    }

    // Shaped like the project's real releases list, including the two quirks in it:
    // v0.6.0-beta1/beta2 sit above every other prerelease, and v0.2.0-beta2 is
    // flagged prerelease=false on GitHub by mistake.
    private const string RealFeedShape = """
    [
      {"tag_name":"v0.6.2","prerelease":false,"draft":false,"html_url":"h",
       "assets":[{"name":"MarkdownMidget-v0.6.2-win-x64-net10.exe","browser_download_url":"u","size":1}]},
      {"tag_name":"v0.6.1","prerelease":false,"draft":false,"html_url":"h",
       "assets":[{"name":"MarkdownMidget-v0.6.1-win-x64-net10.exe","browser_download_url":"u","size":1}]},
      {"tag_name":"v0.6.0","prerelease":false,"draft":false,"html_url":"h",
       "assets":[{"name":"MarkdownMidget-v0.6.0-win-x64-net10.exe","browser_download_url":"u","size":1}]},
      {"tag_name":"v0.6.0-beta2","prerelease":true,"draft":false,"html_url":"h",
       "assets":[{"name":"MarkdownMidget-v0.6.0-beta2-win-x64-net10.exe","browser_download_url":"u","size":1}]},
      {"tag_name":"v0.6.0-beta1","prerelease":true,"draft":false,"html_url":"h",
       "assets":[{"name":"MarkdownMidget-v0.6.0-beta1-win-x64-net10.exe","browser_download_url":"u","size":1}]},
      {"tag_name":"v0.2.0-beta2","prerelease":false,"draft":false,"html_url":"h",
       "assets":[{"name":"MarkdownMidget-v0.2.0-beta2-win-x64-net10.exe","browser_download_url":"u","size":1}]}
    ]
    """;

    [Fact]
    public void AgainstTheRealFeed_TheSupersededBetaIsHidden()
    {
        var check = ReleaseFeed.Select(RealFeedShape);
        Assert.Equal("v0.6.2", check.Stable!.Tag);
        Assert.Equal("v0.6.0-beta2", check.PrereleaseRelease!.Tag);   // still newest by version

        // …but running 0.6.2, nothing is offered at all.
        Assert.False(UpdateOffer.ShowPrerelease(check.PrereleaseRelease, check.Stable, Cur("0.6.2")));
        Assert.False(UpdateOffer.ShowStableUpdate(check.Stable, Cur("0.6.2")));
    }

    [Fact]
    public void MislabelledPrerelease_IsNotTreatedAsStable()
    {
        // v0.2.0-beta2 carries prerelease=false on GitHub. The version tail is the
        // safety net: it must never be picked as the newest stable.
        var check = ReleaseFeed.Select("""
        [
          {"tag_name":"v0.2.0-beta2","prerelease":false,"draft":false,"html_url":"h",
           "assets":[{"name":"MarkdownMidget-v0.2.0-beta2-win-x64-net10.exe","browser_download_url":"u","size":1}]}
        ]
        """);
        Assert.Null(check.Stable);
        Assert.Equal("v0.2.0-beta2", check.PrereleaseRelease!.Tag);
    }
}
