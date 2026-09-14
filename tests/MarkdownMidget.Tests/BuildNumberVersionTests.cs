using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using MarkdownMidget.Updates;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>
/// The local build number (build/LocalBuildNumber.targets): that this build has one,
/// that it came from the repository's own counter rather than this checkout's, and
/// that nothing in the update path reads <c>+build.N</c> as a different version.
///
/// Nothing here starts a process. The build is the thing under test, and the build
/// that produced the assembly these tests are running against has already happened -
/// so the evidence is in that assembly and in this repository, and it costs nothing
/// to read.
/// </summary>
public class BuildNumberVersionTests
{
    private static readonly Assembly App = typeof(AboutDialog).Assembly;

    [Fact]
    public void ThisVeryBuildCarriesItsNumberInBothVersions()
    {
        // The one test that proves the feature is wired in at all: the targets file
        // can be perfect and still be imported by nothing.
        var informational = InformationalVersion();
        var components = (FileVersionInfo.GetVersionInfo(App.Location).FileVersion ?? "").Split('.');

        if (IsContinuousIntegration())
        {
            // A release is a CI build: it carries no number, which is the other half
            // of the same claim.
            Assert.DoesNotContain("build.", informational, StringComparison.Ordinal);
            Assert.Equal("0", components.Last());
            return;
        }

        var numbered = Regex.Match(informational, @"build\.(\d+)$");
        Assert.True(numbered.Success,
            $"the assembly under test reports \"{informational}\": this build took no number. Either the " +
            "Import of build/LocalBuildNumber.targets is gone from MarkdownMidget.csproj, or it was built " +
            "with -p:UseLocalBuildNumber=false.");
        Assert.Equal(4, components.Length);
        Assert.Equal(numbered.Groups[1].Value, components[3]);
    }

    [Fact]
    public void TheNumberCameFromTheRepositorysCounterAndNotThisCheckouts()
    {
        // The resolution, against this repository as it actually is: .git is a
        // directory in a clone, and in a worktree it is a file naming that worktree's
        // gitdir, whose commondir names the directory every worktree shares. Followed
        // here independently of the build, so a build that counted somewhere else -
        // per checkout, or per worktree - fails this.
        if (IsContinuousIntegration()) return;   // no number, so no counter
        var number = int.Parse(Regex.Match(InformationalVersion(), @"build\.(\d+)$").Groups[1].Value);

        var checkout = RepoSources.Root();
        var dotGit = Path.Combine(checkout, ".git");
        var gitDir = Directory.Exists(dotGit)
            ? dotGit
            : Path.GetFullPath(Path.Combine(checkout, File.ReadAllText(dotGit).Trim()["gitdir: ".Length..]));
        var commonFile = Path.Combine(gitDir, "commondir");
        if (File.Exists(commonFile))
            gitDir = Path.GetFullPath(Path.Combine(gitDir, File.ReadAllText(commonFile).Trim()));

        var counter = Path.Combine(gitDir, "mm-local-build-number");
        Assert.True(File.Exists(counter), $"no build counter at {counter}");
        Assert.True(int.Parse(File.ReadAllText(counter).Trim()) >= number,
            $"{counter} is behind build {number}, so that number came from somewhere else");
        // The per-checkout fallback is for a source copy with no .git at all. Its
        // presence here would mean two checkouts of this repository counting apart.
        Assert.False(File.Exists(Path.Combine(checkout, ".local-build-number")),
            "a per-checkout counter was used in a checkout that has a repository");
    }

    [Fact]
    public void TheAppProjectImportsTheBuildNumberTargets()
    {
        // The element, not a mention of it: the comment above the Import names the
        // file too, so a substring search stayed green with the Import deleted.
        Assert.Matches(@"<Import\s+Project=""[^""]*LocalBuildNumber\.targets""",
            RepoSources.Read("src", "MarkdownMidget", "MarkdownMidget.csproj"));
    }

    [Theory]
    [InlineData("0.11.0-dev+build.57", "0.11.0-dev", true)]
    // UpdateService.VersionOnDisk falls back to the FILE version when a file has no
    // product version, and that one carries the number as a fourth component: it has
    // to read as 0.11.0, not as something 57 revisions ahead of it.
    [InlineData("0.11.0.57", "0.11.0", false)]
    public void ANumberedVersionParsesToTheVersionWithoutIt(string text, string expected, bool prerelease)
    {
        var version = UpdateVersion.Parse(text);

        Assert.NotNull(version);
        Assert.Equal(expected, version!.ToString());
        Assert.Equal(prerelease, version.IsPrerelease);
    }

    [Theory]
    [InlineData("0.11.0-dev+build.57", "0.11.0-dev+build.58")]
    [InlineData("0.11.0-dev+build.57", "0.11.0-dev")]
    public void ABuildNumberDoesNotMakeAVersionNewerOrOlder(string a, string b)
    {
        // SemVer: build metadata takes no part in precedence. So the update check
        // never offers a numbered build an "update" to the version it is already
        // running, and the What's New badge does not come back on every local build.
        var first = UpdateVersion.Parse(a)!;
        var second = UpdateVersion.Parse(b)!;

        Assert.Equal(0, first.CompareTo(second));
        Assert.Equal(0, second.CompareTo(first));
    }

    private static string InformationalVersion() =>
        App.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

    /// <summary>The signals build/LocalBuildNumber.targets uses to decide a build is
    /// not a local one. Only the environment ones are visible from in here.</summary>
    private static bool IsContinuousIntegration()
    {
        var ci = Environment.GetEnvironmentVariable("CI");
        return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"))
            || (!string.IsNullOrEmpty(ci) && ci != "false")
            || Environment.GetEnvironmentVariable("TF_BUILD") == "true"
            || Environment.GetEnvironmentVariable("ContinuousIntegrationBuild") == "true";
    }
}
