using System;
using System.IO;
using System.Reflection;
using System.Threading;
using MarkdownMidget.Themes;
using MarkdownMidget.Updates;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>
/// What the rest of the app makes of a version string that carries a local build
/// number — <c>0.11.0-dev+build.57</c>.
///
/// SemVer is explicit that build metadata takes no part in precedence, and every
/// comparison in this app has to agree: the update check must not read a numbered
/// dev build as newer or older than the same version without a number, the What's
/// New badge must not come back on every local build, and the built-in themes must
/// not be rewritten on every launch of one.
/// </summary>
public class BuildNumberVersionTests
{
    [Theory]
    [InlineData("0.11.0-dev+build.57", "0.11.0-dev", true)]
    [InlineData("v0.11.0-dev+build.57", "0.11.0-dev", true)]
    [InlineData("0.11.0+build.57", "0.11.0", false)]
    // The InformationalVersion with the SDK's commit sha still on it, numbered.
    [InlineData("0.11.0-dev+abc1234.build.57", "0.11.0-dev", true)]
    // UpdateService.VersionOnDisk falls back to the FILE version when a file has no
    // product version, and that one carries the number as a fourth component. It
    // must read as 0.11.0 — not as a version 57 revisions ahead of anything.
    [InlineData("0.11.0.57", "0.11.0", false)]
    public void AVersionCarryingABuildNumberParsesToTheVersionWithoutIt(string text, string expected, bool prerelease)
    {
        var version = UpdateVersion.Parse(text);

        Assert.NotNull(version);
        Assert.Equal(expected, version!.ToString());
        Assert.Equal(prerelease, version.IsPrerelease);
    }

    [Theory]
    [InlineData("0.11.0-dev+build.57", "0.11.0-dev+build.58")]
    [InlineData("0.11.0-dev+build.57", "0.11.0-dev")]
    [InlineData("0.11.0+build.9", "0.11.0+build.10")]
    public void ABuildNumberDoesNotMakeAVersionNewerOrOlder(string a, string b)
    {
        var first = UpdateVersion.Parse(a)!;
        var second = UpdateVersion.Parse(b)!;

        Assert.Equal(0, first.CompareTo(second));
        Assert.Equal(0, second.CompareTo(first));
    }

    [Fact]
    public void ANumberedDevBuildIsStillOfferedAGenuinelyNewerRelease()
    {
        // The failure that must never happen: a dev build stops being offered real
        // releases because its version string grew a suffix.
        var release = Rel("v0.12.0");

        Assert.True(UpdateOffer.ShowStableUpdate(release, Cur("0.11.0-dev+build.57")));
    }

    [Fact]
    public void ANumberedDevBuildIsNotOfferedAnOlderRelease()
    {
        Assert.False(UpdateOffer.ShowStableUpdate(Rel("v0.10.0"), Cur("0.11.0-dev+build.57")));
    }

    [Fact]
    public void AWindowIsNotToldToRestartBecauseTheExeOnDiskHasADifferentBuildNumber()
    {
        // Copying a freshly published exe over the installed one is exactly what the
        // owner does while testing. Build 58 on disk beside build 57 in this window
        // is the SAME version by SemVer, and claiming otherwise would put an "Apply
        // update" item in the Help menu that reinstalls what is already running.
        Assert.False(UpdateOffer.NeedsRestartNotUpdate(
            onDisk: Cur("0.11.0-dev+build.58"), wanted: null, running: Cur("0.11.0-dev+build.57")));
    }

    [Fact]
    public void AWindowIsStillToldToRestartWhenTheExeOnDiskIsGenuinelyNewer()
    {
        Assert.True(UpdateOffer.NeedsRestartNotUpdate(
            onDisk: Cur("0.12.0"), wanted: null, running: Cur("0.11.0-dev+build.57")));
    }

    [Fact]
    public void TheWhatsNewBadgeDoesNotComeBackForEveryLocalBuild()
    {
        // The badge is driven by the running version against the one in settings.
        // If a build number counted, every single local build would re-badge a
        // changelog the owner has already read.
        Assert.False(WhatsNewState.HasUnseenChangelog("v0.11.0-dev+build.58", "v0.11.0-dev+build.57"));
        Assert.True(WhatsNewState.HasUnseenChangelog("v0.12.0", "v0.11.0-dev+build.57"));
    }

    [Fact]
    public void TheBuiltInThemesAreNotRewrittenForEveryLocalBuild()
    {
        // ThemeStore rewrites the built-ins when the version moves. A build number
        // that counted would make every launch of a fresh local build overwrite the
        // theme folder — and re-stamp it — for a version that has not moved.
        var dir = Path.Combine(Path.GetTempPath(), "mm-buildnumber-themes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new ThemeStore(dir);
            var assembly = typeof(BuildNumberVersionTests).Assembly;

            Assert.True(store.Refresh("v0.11.0-dev+build.57", assembly));    // first run: extracted
            Assert.False(store.Refresh("v0.11.0-dev+build.58", assembly));   // same version, one build later
            Assert.True(store.Refresh("v0.12.0", assembly));                 // a real move still extracts
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* leave it to the temp folder */ }
        }
    }

    [Fact]
    public void TheAboutBoxShowsExactlyTheVersionThisBuildWasStampedWith()
    {
        // Help ▸ About is where the number is read after launching, and it needs no
        // code of its own to show it: the box prints the assembly's
        // AssemblyInformationalVersion, which is where the build number lands. This
        // pin plus LocalBuildNumberTests' "the informational version carries the
        // number" is the whole claim — neither half asserts what the other proves.
        //
        // Constructed, never shown: no window appears on the desktop, and the
        // dialog's update check only runs on Loaded.
        var expected = typeof(AboutDialog).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

        var shown = Sta(() =>
        {
            // Relative pack URIs (the mascot image in AboutDialog.xaml) resolve
            // against this assembly rather than the test host's.
            System.Windows.Application.ResourceAssembly ??= typeof(AboutDialog).Assembly;
            return new AboutDialog().CurrentVersionText.Text;
        });

        Assert.StartsWith("Version " + expected, shown, StringComparison.Ordinal);
    }

    private static ReleaseInfo Rel(string tag) =>
        new(tag, UpdateVersion.Parse(tag)!, false, "", "asset.exe", "https://x/asset.exe", 1);

    private static UpdateVersion Cur(string v) => UpdateVersion.Parse(v)!;

    /// <summary>Runs <paramref name="work"/> on an STA thread, the way the other WPF
    /// tests here do — a timeout fails the test rather than hanging the run.</summary>
    private static T Sta<T>(Func<T> work)
    {
        var result = default(T)!;
        Exception? error = null;
        var done = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try { result = work(); }
            catch (Exception ex) { error = ex; }
            finally { done.Set(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(done.Wait(TimeSpan.FromSeconds(30)), "the STA harness timed out");
        if (error is not null) throw error;
        return result;
    }
}
