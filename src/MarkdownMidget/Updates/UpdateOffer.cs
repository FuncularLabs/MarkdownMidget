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

    /// <summary>
    /// Does this instance need *restarting* rather than updating?
    ///
    /// Two ways to be sure, and the second is the one that's easy to miss:
    ///
    /// - the exe on disk already satisfies what's being offered, so there is nothing
    ///   to download or install; or
    /// - the exe on disk is ahead of the version we are *running*, whatever is being
    ///   offered. That is the real precondition for the failure this exists to
    ///   prevent: it means another window renamed our image out from under us, so
    ///   the swap's `File.Move(target, target + ".old")` would land on a name that
    ///   already exists and is locked — by us.
    ///
    /// Checking only the first would miss a window left open across two releases:
    /// disk at 0.6.4, this process still on 0.6.3, and 0.6.5 on offer. Nothing is
    /// "already updated" from the offer's point of view, yet the swap still cannot
    /// work.
    ///
    /// Unknown answers false — "let it try and report honestly" beats refusing an
    /// update on a guess.
    /// </summary>
    public static bool NeedsRestartNotUpdate(
        UpdateVersion? onDisk, UpdateVersion? wanted, UpdateVersion? running)
    {
        if (onDisk is null) return false;
        if (wanted is not null && onDisk.CompareTo(wanted) >= 0) return true;
        return running is not null && onDisk.CompareTo(running) > 0;
    }
}
