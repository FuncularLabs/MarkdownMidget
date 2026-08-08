namespace MarkdownMidget.Updates;

/// <summary>
/// Whether the mascot's "unseen changelog" badge should be showing.
///
/// Pulled out of MainWindow because it is the one piece of the What's New feature
/// that is actual logic rather than wiring — everything else is "read a file, open
/// a window" copied from the Help viewer. Comparison, not equality: a settings file
/// that still names an old version after an update is the normal case, not a
/// mismatch to special-case.
/// </summary>
internal static class WhatsNewState
{
    /// <summary>
    /// True when <paramref name="currentVersion"/> is newer than
    /// <paramref name="lastSeenVersion"/> — including when nothing has been seen yet.
    /// </summary>
    public static bool HasUnseenChangelog(string currentVersion, string? lastSeenVersion)
    {
        var current = UpdateVersion.Parse(currentVersion);
        // A version string the app can't parse is a build-config problem, not
        // something to nag about — fail toward no badge rather than a permanently
        // stuck one.
        if (current is null) return false;

        var seen = UpdateVersion.Parse(lastSeenVersion);
        return seen is null || current.CompareTo(seen) > 0;
    }
}
