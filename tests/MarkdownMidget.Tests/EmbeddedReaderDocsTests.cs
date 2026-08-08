using System.Linq;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using MarkdownMidget.Updates;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>
/// The two embedded docs Help ▸ View Help and Help ▸ What's New extract and open
/// read-only. Nothing here can exercise the click path itself — that needs a real
/// window and a mouse — but the two things that matter and CAN be checked without
/// one are: does the resource actually ship under the name the code asks for, and
/// is the changelog's "newest first" claim, which the What's New feature exists to
/// make good on, actually true rather than assumed.
/// </summary>
public class EmbeddedReaderDocsTests
{
    private static readonly Assembly App = typeof(WhatsNewState).Assembly;

    [Theory]
    [InlineData("HELP.md")]
    [InlineData("CHANGELOG.md")]
    public void TheResourceIsEmbeddedUnderExactlyThisName(string name)
    {
        // The LogicalName in the csproj has to match this string character for
        // character. A rename on one side and not the other fails silently at
        // runtime — GetManifestResourceStream returns null, and
        // OpenEmbeddedReaderDoc's `stream!.CopyTo` turns that into a
        // NullReferenceException the user sees as "Couldn't open CHANGELOG.md:
        // Object reference not set...", which names no missing resource at all.
        using var stream = App.GetManifestResourceStream(name);
        Assert.NotNull(stream);
        Assert.True(stream!.Length > 0);
    }

    [Fact]
    public void TheChangelogsNewestEntryComesFirst()
    {
        // Keep a Changelog format puts the newest entry first by convention, but a
        // convention is not a guarantee, and getting this backwards is invisible in
        // a diff review and obvious the moment someone actually opens the file.
        var versions = Regex.Matches(Read("CHANGELOG.md"), @"^## \[(.+?)\]", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)
            .ToList();
        Assert.True(versions.Count >= 2, "need at least two version headings to prove an order");

        // "Unreleased" always leads when present; every dated release after it must
        // be strictly descending by UpdateVersion's own comparison rules.
        var dated = versions.Where(v => v != "Unreleased").ToList();
        for (var i = 1; i < dated.Count; i++)
        {
            var prev = UpdateVersion.Parse(dated[i - 1]);
            var next = UpdateVersion.Parse(dated[i]);
            Assert.NotNull(prev);
            Assert.NotNull(next);
            Assert.True(prev!.CompareTo(next) > 0,
                $"[{dated[i - 1]}] does not sort after [{dated[i]}] — newest-first is broken here");
        }
    }

    [Fact]
    public void TheChangelogHasAnEntryForThisBuild()
    {
        // The one gap that would make the badge lie: the running exe's own version
        // has no heading, so the reader opens straight past what changed in THIS
        // build and lands on the previous release's notes instead.
        var version = App.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        Assert.NotNull(version);

        var text = Read("CHANGELOG.md");
        // An explicit heading for this exact version, or an "Unreleased" section —
        // the normal state of a dev build before the release step renames it.
        var hasOwnHeading = Regex.IsMatch(text, $@"^## \[{Regex.Escape(version!)}\]", RegexOptions.Multiline);
        var hasUnreleased = Regex.IsMatch(text, @"^## \[Unreleased\]", RegexOptions.Multiline);
        Assert.True(hasOwnHeading || hasUnreleased,
            $"CHANGELOG.md has no [{version}] heading and no [Unreleased] section");
    }

    private static string Read(string resourceName)
    {
        using var stream = App.GetManifestResourceStream(resourceName);
        using var reader = new StreamReader(stream!);
        return reader.ReadToEnd();
    }
}
