namespace MarkdownMidget.Updates;

/// <summary>
/// Decides what the About box should actually offer.
///
/// The releases feed keeps reporting the newest prerelease forever, which is only
/// interesting while it leads. Once a stable release catches up — 0.6.0-beta2 next
/// to a shipped 0.6.2 — showing it is worse than showing nothing: it reads as a
/// newer, more adventurous build when it is in fact older code that has already
/// been superseded. So a prerelease is offered only while it leads BOTH the
/// running version and the newest stable.
/// </summary>
internal static class UpdateOffer
{
    /// <summary>Is this prerelease still ahead of everything else?</summary>
    public static bool ShowPrerelease(ReleaseInfo? prerelease, ReleaseInfo? stable, UpdateVersion? current)
    {
        if (prerelease is null) return false;
        // Nothing to measure against at all — don't push a prerelease on a guess.
        // (Unlike a stable release, where offering the newest is a safe default.)
        if (current is null && stable is null) return false;
        // Not newer than what's running — nothing to offer.
        if (current is not null && prerelease.Version.CompareTo(current) <= 0) return false;
        // Superseded by a stable release: taking it would be a downgrade in
        // everything but the version label.
        if (stable is not null && prerelease.Version.CompareTo(stable.Version) <= 0) return false;
        return true;
    }

    /// <summary>
    /// Is this stable release worth offering? Yes when it's newer than what's
    /// running — and also when we can't tell what's running, because the failure
    /// that must never happen is a real update going unoffered. Re-installing the
    /// version you already have costs a download; being silently stranded on an old
    /// build costs the fix you were waiting for.
    /// </summary>
    public static bool ShowStableUpdate(ReleaseInfo? stable, UpdateVersion? current) =>
        stable is not null && (current is null || stable.Version.CompareTo(current) > 0);
}
