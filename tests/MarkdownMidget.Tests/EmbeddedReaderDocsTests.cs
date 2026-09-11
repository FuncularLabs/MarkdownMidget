using System;
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
/// window and a mouse — but three things that matter CAN be checked without one:
/// does the resource actually ship under the name the code asks for; is the
/// changelog's "newest first" claim, which the What's New feature exists to make
/// good on, actually true rather than assumed; and do the numbers HELP states about
/// the running program still match the constants the program uses.
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

    [Theory]
    [InlineData(1, "0.10.0")]
    [InlineData(2, "0.10.0")]
    [InlineData(3, "0.10.0")]
    [InlineData(4, "0.10.0")]
    [InlineData(5, "0.10.0")]
    [InlineData(6, "0.10.0")]
    [InlineData(7, "0.10.0")]
    [InlineData(9, "0.10.0")]
    public void AnIssueIsCreditedToOneSectionNewerThanTheReleaseThatLackedIt(int issue, string lastReleaseWithout)
    {
        // A conflict-free merge once filed a branch's [Unreleased] ### Fixed block
        // under [0.10.0]: git anchored the block to the [0.10.0-beta1] heading,
        // which the release merge had just pushed below a new [0.10.0] section, and
        // two fixes were credited to a binary that does not contain them - in
        // What's New, which shows this file as embedded, and in the release notes,
        // which release.yml cuts by section. So every issue this line ships is
        // pinned to exactly one section, and that section must be newer than the
        // last release that went out without it. Add a row when an issue merges;
        // the row stays true through the rename of [Unreleased] at the cut.
        var sections = Regex.Split(Read("CHANGELOG.md"), @"(?m)^(?=## \[)")
            .Where(s => s.StartsWith("## [", StringComparison.Ordinal))
            .ToList();
        var owners = sections
            .Where(s => s.Contains($"(#{issue})"))
            .Select(s => Regex.Match(s, @"^## \[(.+?)\]").Groups[1].Value)
            .ToList();
        var owner = Assert.Single(owners);
        if (owner == "Unreleased") return;

        var floor = UpdateVersion.Parse(lastReleaseWithout);
        var got = UpdateVersion.Parse(owner);
        Assert.NotNull(floor);
        Assert.NotNull(got);
        Assert.True(got!.CompareTo(floor!) > 0,
            $"(#{issue}) is credited to [{owner}], which is not newer than {lastReleaseWithout} - the entry is filed under the wrong release");
    }

    [Fact]
    public void TheHelpStatesThePictureCeilingTheDropActuallyApplies()
    {
        // HELP said "64 MB" as a literal, with nothing tying it to MaxPictureBytes.
        // Change the constant and HELP goes on telling the user the old number —
        // invisible in a diff review, and the first thing anyone reads when a drop
        // refuses their picture. Pinned the way DocumentExtensions is pinned to
        // OpenFilter: the doc has to state the number the code uses.
        var megabytes = DropRouting.MaxPictureBytes / (1024 * 1024);
        Assert.Contains($"picture **larger than {megabytes} MB**", Read("HELP.md"), StringComparison.Ordinal);
    }

    [Fact]
    public void TheHelpStatesTheWaitTheDropActuallyAppliesAndWhichMessageEachFailureGives()
    {
        // AR-4. HELP said a drop "gets no answer within a few seconds (a page that
        // has stopped responding, or a picture too large for one message)" and gave
        // the timeout message for both. Neither half was true: the wait is 10 s at
        // its floor and 18 s for one picture at the ceiling, and a picture too large
        // to cross the bridge is the case the editor DOES answer — postAnswer follows
        // the refused payload with a small post-failed message, which Decide turns
        // into Refuse and the window reports by name. Two causes, two messages, and
        // the numbers are pinned to the function that produces them the way the
        // ceiling is pinned to MaxPictureBytes.
        var help = Read("HELP.md");
        var floor = (int)DropHandshake.ReadTimeout(0).TotalSeconds;
        var onePicture = (int)DropHandshake.ReadTimeout(DropRouting.MaxPictureBytes).TotalSeconds;
        var tenPictures = (int)DropHandshake.ReadTimeout(10 * DropRouting.MaxPictureBytes).TotalSeconds;

        Assert.Contains($"**{floor} seconds plus a second for every 8 MB**", help, StringComparison.Ordinal);
        Assert.Contains($"{onePicture} seconds for a single picture", help, StringComparison.Ordinal);
        Assert.Contains($"{tenPictures} seconds for ten of them", help, StringComparison.Ordinal);

        // Each cause quoted with the message it actually produces.
        Assert.Contains(DropHandshake.TimedOutNotice, help, StringComparison.Ordinal);
        Assert.Contains(DropHandshake.UnreadableNotice(["a.png", "b.png"]), help, StringComparison.Ordinal);
    }

    [Fact]
    public void TheChangelogStatesTheSameWaitTheHelpDoes()
    {
        // NF-6. AR-4 struck "gets no answer within a few seconds" from HELP and
        // pinned the real numbers there — and left the same sentence standing in the
        // CHANGELOG entry, which is the copy most people read (Help ▸ What's New
        // opens it, and it is what a release note is built from). Both documents are
        // now pinned to the one function that produces the numbers, so neither can
        // drift from the code or from the other.
        var changelog = Read("CHANGELOG.md");
        var floor = (int)DropHandshake.ReadTimeout(0).TotalSeconds;
        var onePicture = (int)DropHandshake.ReadTimeout(DropRouting.MaxPictureBytes).TotalSeconds;

        Assert.Contains($"{floor} seconds plus a second for every 8 MB", changelog, StringComparison.Ordinal);
        Assert.Contains($"{onePicture} seconds for one picture", changelog, StringComparison.Ordinal);
        Assert.DoesNotContain("within a few seconds", changelog, StringComparison.Ordinal);

        // And the refusal AR-2 added, which the entry never mentioned at all: a drop
        // can also end because the document moved under it, which is a different
        // outcome with a different message and is the one a user is most likely to
        // hit by accident.
        Assert.Contains("while a drop is being read", changelog, StringComparison.Ordinal);
    }

    [Fact]
    public void TheHelpNamesTheMenuTheReadOnlyItemIsActuallyUnder()
    {
        // NF-1 (HOUSE-RULE). The item is MenuReadOnly, and it sits under
        // Header="_Edit" in MainWindow.xaml — there is no View menu with a Read Only
        // item on it, so naming the View menu sent the user hunting through one that
        // does not have it. Pinned on the drop bullet's OWN wording ("turn on …"), not
        // on the bare menu path: the read-only section higher up already says
        // "Edit ▸ Read Only", so a pin on that alone would go green with the drop
        // bullet still wrong.
        Assert.Contains("turn on **Edit ▸ Read Only**", Read("HELP.md"), StringComparison.Ordinal);
    }

    private static string Read(string resourceName)
    {
        using var stream = App.GetManifestResourceStream(resourceName);
        using var reader = new StreamReader(stream!);
        return reader.ReadToEnd();
    }
}
