using MarkdownMidget.Updates;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>
/// The mascot's unseen-changelog badge. UpdateVersion's own comparison rules
/// (numeric first, prerelease below its own stable) are exercised in
/// UpdateVersionTests; what belongs here is the badge-specific decision layered on
/// top of that comparison — never-seen, downgrade, and unreadable input.
/// </summary>
public class WhatsNewStateTests
{
    [Fact]
    public void NothingSeenYetShowsTheBadge()
        // The out-of-the-box case: settings.json has no LastSeenChangelogVersion at
        // all, which is every install's very first launch.
        => Assert.True(WhatsNewState.HasUnseenChangelog("v0.7.0", null));

    [Fact]
    public void TheCurrentVersionsChangelogAlreadySeenHidesTheBadge()
        => Assert.False(WhatsNewState.HasUnseenChangelog("v0.7.0", "v0.7.0"));

    [Fact]
    public void AnOlderSeenVersionShowsTheBadge()
        // The ordinary case after an update: last seen was the version before this one.
        => Assert.True(WhatsNewState.HasUnseenChangelog("v0.7.0", "v0.6.4"));

    [Fact]
    public void ANewerSeenVersionDoesNotShowTheBadge()
    {
        // Not "seen == current", "seen >= current". Running an OLDER build after
        // having already read a NEWER version's notes — a rollback, or two builds
        // installed side by side — must not nag about something already read.
        Assert.False(WhatsNewState.HasUnseenChangelog("v0.7.0", "v0.7.1"));
        Assert.False(WhatsNewState.HasUnseenChangelog("v0.7.0", "v0.8.0-beta1"));
    }

    [Fact]
    public void APrereleaseChangelogCountsAsUnseenAgainstItsOwnStable()
        // UpdateVersion ranks a prerelease below its own stable, so 0.7.0-beta1 IS
        // newer than a last-seen 0.6.4 — reading the beta's notes doesn't retroactively
        // cover the stable release that follows it.
        => Assert.True(WhatsNewState.HasUnseenChangelog("v0.7.0", "v0.7.0-beta1"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a version")]
    public void AnUnparseableCurrentVersionNeverShowsTheBadge(string junk)
        // A version string the running build can't parse is a build-config problem,
        // not something to nag a user about — and one that would otherwise nag on
        // EVERY launch, forever, since there is no valid value that could ever compare
        // as "seen" against it.
        => Assert.False(WhatsNewState.HasUnseenChangelog(junk, "v1.0.0"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a version")]
    public void AnUnparseableLastSeenValueCountsAsNeverSeen(string junk)
        // A corrupted or hand-edited settings.json shouldn't hide the badge forever;
        // it should behave exactly like the never-seen case.
        => Assert.True(WhatsNewState.HasUnseenChangelog("v0.7.0", junk));
}
