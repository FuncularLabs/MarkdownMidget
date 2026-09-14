using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>
/// These tests spawn MSBuild — several processes at once, for seconds at a time.
/// Run in parallel with the rest of the suite they would saturate the machine while
/// <see cref="AltF4NoDocumentTests"/> and the other window tests wait on real Win32
/// focus timeouts, so this collection runs on its own.
/// </summary>
[CollectionDefinition("LocalBuildNumber", DisableParallelization = true)]
public sealed class LocalBuildNumberCollection;

/// <summary>
/// The local build counter — <c>build/LocalBuildNumber.targets</c> — which stamps a
/// number into the exe so a copy of it can be identified at a glance.
///
/// Everything here drives the REAL targets file with a real MSBuild, against
/// throwaway projects and counter files in the temp folder, so no test ever touches
/// the counter of the repository it is running in. Three shapes, in descending order
/// of cost and fidelity:
///
/// - a throwaway SDK project, built for real, whose BUILT DLL is then read with
///   <see cref="FileVersionInfo"/> — the same fields Explorer shows in
///   Properties ▸ Details, which is where the owner reads the number;
/// - the same project run to <c>GetAssemblyAttributes</c> only, where what is being
///   proved is which counter file the build picks and whether it takes a number; and
/// - a plain MSBuild project that calls the tasks directly, for the file-level
///   behaviour (missing, corrupt, locked, concurrent, which path is resolved) where
///   a compile would only add seconds.
///
/// The shared work — one restore, one allocation run, one decision run, one
/// resolution run — happens once in <see cref="BuildNumberProbes"/>.
/// </summary>
[Collection("LocalBuildNumber")]
public class LocalBuildNumberTests : IClassFixture<BuildNumberProbes>, IDisposable
{
    private readonly BuildNumberProbes _probes;
    private readonly List<string> _dirs = [];

    public LocalBuildNumberTests(BuildNumberProbes probes) => _probes = probes;

    public void Dispose()
    {
        foreach (var dir in _dirs) BuildNumberProbes.Delete(dir);
    }

    // ===== the counter reaches the binary =====

    [Fact]
    public void EveryBuildProducesANewlyNumberedBinary()
    {
        // Including the one where nothing changed: that is the rule this feature
        // picked, and the reason an exe published a moment ago can never carry the
        // number of the one it replaced. The number is read back from the BINARY,
        // so an incremental build that skipped the compile would leave it behind.
        var counter = _probes.NewCounter();

        var first = _probes.BuildProbe(counter);
        var second = _probes.BuildProbe(counter);                       // nothing changed
        File.AppendAllText(Path.Combine(_probes.ProbeDir, "Probe.cs"), "// edited\n");
        var third = _probes.BuildProbe(counter);

        Assert.Equal("9.9.9.1", first.FileVersion);
        Assert.Equal("9.9.9-probe+build.1", first.ProductVersion);
        Assert.Equal("9.9.9.2", second.FileVersion);
        Assert.Equal("9.9.9-probe+build.2", second.ProductVersion);
        Assert.Equal("9.9.9.3", third.FileVersion);
        Assert.Equal("9.9.9-probe+build.3", third.ProductVersion);
        Assert.Equal("3", File.ReadAllText(counter).Trim());
    }

    [Fact]
    public void APublishTakesItsOwnNumber()
    {
        // The exe the owner actually copies comes from `dotnet publish`, and publish
        // runs the build — so it must take a number of its own rather than shipping
        // whatever the last `dotnet build` stamped.
        var counter = _probes.NewCounter();
        _probes.BuildProbe(counter);

        var publishDir = Path.Combine(_probes.ProbeDir, "published-" + Guid.NewGuid().ToString("N")[..6]);
        _probes.RunProbe(counter, ["-t:Publish", "-p:PublishDir=" + publishDir + Path.DirectorySeparatorChar]);

        var published = FileVersionInfo.GetVersionInfo(Path.Combine(publishDir, "Probe.dll"));
        Assert.Equal("9.9.9.2", published.FileVersion);
        Assert.Equal("9.9.9-probe+build.2", published.ProductVersion);
        Assert.Equal("2", File.ReadAllText(counter).Trim());
    }

    [Fact]
    public void ABuildOnCiKeepsTheVersionStringsTheReleaseWorkflowPasses()
    {
        // The end-to-end CI proof, with the environment variable really exported to
        // the build process rather than passed as a property: .github/workflows/
        // release.yml publishes with -p:Version=<base> and -p:InformationalVersion=
        // <tag minus v>, and what the released exe reports must be byte-for-byte
        // what it reported before this feature existed.
        var counter = _probes.NewCounter();

        var built = _probes.BuildProbe(counter,
            extra: ["-p:Version=0.12.0", "-p:InformationalVersion=0.12.0-beta1"],
            env: new Dictionary<string, string> { ["GITHUB_ACTIONS"] = "true" });

        Assert.Equal("0.12.0.0", built.FileVersion);
        Assert.Equal("0.12.0-beta1", built.ProductVersion);
        Assert.False(File.Exists(counter), "a CI build wrote a counter file");
    }

    // ===== which counter file a build uses =====

    [Theory]
    [InlineData("repo")]              // a clone's root
    [InlineData("subdirectory")]      // where the project actually sits
    [InlineData("worktree")]          // .git is a FILE naming .git/worktrees/<name>
    [InlineData("nested-worktree")]   // a worktree added from inside a worktree
    [InlineData("submodule")]         // its own repository at .git/modules/<name>
    [InlineData("superproject")]      // and the repository that holds it
    [InlineData("bare")]              // no work tree at all: the directory IS the repository
    [InlineData("path-with-spaces")]
    public void TheCounterLandsWhereGitItselfSaysTheRepositoryIs(string shape)
    {
        // The resolver walks the filesystem instead of running git (review R2-F1),
        // so the thing worth proving is that the walk answers what git answers - for
        // every shape, measured against git itself rather than against what this
        // test's author believes git does.
        var resolved = _probes.Resolutions[shape];

        Assert.Equal(BuildNumberProbes.SharedCounterName, Path.GetFileName(resolved));
        Assert.Equal(_probes.GitCommonDirs[shape],
                     Path.GetFullPath(Path.GetDirectoryName(resolved)!),
                     ignoreCase: true);
    }

    [Fact]
    public void EveryWorktreeOfOneRepositorySharesOneCounter()
    {
        // The number's job is to say WHICH exe this is. Two checkouts of one
        // repository numbering independently would put 0.11.0.7 on two different
        // binaries.
        Assert.Equal(_probes.Resolutions["repo"], _probes.Resolutions["subdirectory"]);
        Assert.Equal(_probes.Resolutions["repo"], _probes.Resolutions["worktree"]);
        Assert.Equal(_probes.Resolutions["repo"], _probes.Resolutions["nested-worktree"]);
        Assert.StartsWith(Path.Combine(_probes.GitRepo, ".git") + Path.DirectorySeparatorChar,
            _probes.Resolutions["repo"], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ASubmoduleCountsInItsOwnRepositoryRatherThanItsSuperprojects()
    {
        // A submodule is a repository, and its binaries are its own: sharing the
        // superproject's sequence would be a different kind of wrong answer to
        // "which build is this?".
        Assert.NotEqual(_probes.Resolutions["superproject"], _probes.Resolutions["submodule"]);
        Assert.Contains(Path.Combine(".git", "modules"), _probes.Resolutions["submodule"],
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    // A .git file's gitdir, and a gitdir's commondir, are each relative to the file
    // that names them - or absolute. Both shapes are what git writes; neither can be
    // staged by running git on demand, so both are built by hand.
    [InlineData("relative-link", "relative-common")]
    [InlineData("absolute-link", "absolute-common")]
    public void ALinkedRepositoryDirectoryIsFollowedThroughToTheCommonOne(string shape, string expected)
    {
        Assert.Equal(Path.Combine(_probes.Root, expected, BuildNumberProbes.SharedCounterName),
                     _probes.Resolutions[shape], ignoreCase: true);
    }

    [Fact]
    public void FindingTheRepositoryNeedsNoGitOnThePath()
    {
        // Round 1 ran `git rev-parse` here, which could hang the build outright: it
        // drained one pipe then the other before waiting, so a git that filled stderr
        // blocked forever and the timeout never arrived (review R2-F1). There is no
        // child process any more, and the way to show that from outside is to take
        // git away - with only the dotnet directory on PATH, the repository is still
        // found, where anything that shelled out would fall back to the checkout.
        var dotnet = DotnetDirectory();
        Assert.False(File.Exists(Path.Combine(dotnet, "git.exe")), "this PATH still has a git on it");

        var dir = Path.Combine(_probes.Root, "no-git-path-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        var project = Path.Combine(dir, "resolve.proj");
        var result = Path.Combine(dir, "resolved.txt");
        File.WriteAllText(project, $"""
            <Project>
              <Import Project="{BuildNumberProbes.TargetsFile}" />
              <Target Name="Resolve">
                <ResolveLocalBuildNumberFile StartDirectory="$(ProbeStart)"
                                             FallbackFile="$(ProbeFallback)"
                                             FileName="$(LocalBuildNumberSharedName)">
                  <Output TaskParameter="CounterFile" PropertyName="Resolved" />
                </ResolveLocalBuildNumberFile>
                <WriteLinesToFile File="$(ProbeResult)" Lines="$(Resolved)" Overwrite="true" />
              </Target>
            </Project>
            """);

        var (exit, output) = _probes.Run(dir, [
            project, "-t:Resolve",
            "-p:ProbeStart=" + _probes.GitProbeDir,
            "-p:ProbeFallback=" + Path.Combine(dir, "fallback-counter"),
            "-p:ProbeResult=" + result,
        ], new Dictionary<string, string> { ["PATH"] = dotnet });
        Assert.True(exit == 0, output);

        Assert.Equal(_probes.Resolutions["subdirectory"], File.ReadAllText(result).Trim(), ignoreCase: true);
        Assert.DoesNotContain("MMBN0003", output, StringComparison.Ordinal);

        // And the structural half, which no PATH can hide: the task that does this
        // starts no process at all, so there is no pipe to fill and no wait to miss.
        var targets = File.ReadAllText(BuildNumberProbes.TargetsFile);
        var task = targets[targets.IndexOf("TaskName=\"ResolveLocalBuildNumberFile\"", StringComparison.Ordinal)..];
        task = task[..task.IndexOf("</UsingTask>", StringComparison.Ordinal)];
        Assert.DoesNotContain("Process", task, StringComparison.Ordinal);
    }

    /// <summary>The directory holding dotnet.exe — the only thing these no-git runs
    /// keep on PATH. Asserted rather than guessed: a wrong answer here would make the
    /// test above pass for the wrong reason.</summary>
    private static string DotnetDirectory()
    {
        var root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (string.IsNullOrEmpty(root) || !File.Exists(Path.Combine(root, "dotnet.exe")))
            // ...\dotnet\shared\Microsoft.NETCore.App\<version>\System.Private.CoreLib.dll
            root = Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(typeof(object).Assembly.Location)!, "..", "..", ".."));
        Assert.True(File.Exists(Path.Combine(root, "dotnet.exe")), "no dotnet.exe under " + root);
        return root;
    }

    [Fact]
    public void WithoutAGitRepositoryTheCounterFallsBackToTheCheckoutAndSaysSoOutLoud()
    {
        // A source copy with no .git: numbering still works, per checkout, which is
        // what a single checkout means anyway. But it must SAY so. A silent fallback
        // is how the review's F-2 comes back: one transient failure and a build is
        // numbered from a different sequence, printed exactly like a legitimate one.
        Assert.Equal(_probes.FallbackCounter, _probes.Resolutions["not-a-repository"]);
        Assert.Contains("warning MMBN0003", _probes.ResolutionOutput, StringComparison.Ordinal);
        Assert.Contains(_probes.FallbackCounter, _probes.ResolutionOutput, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheBuildItselfPutsTheCounterInTheRepositoryNotTheWorkingTree()
    {
        // The wiring, not the resolver: a build with nothing overridden must use the
        // resolved path. Two checkouts of one repository, one number each, both in
        // the repository's own directory and neither in a working tree.
        var main = _probes.GitProbeDir;
        var worktree = Path.Combine(_probes.Root, "probe-worktree");
        Git(_probes.GitRepo, "worktree add --detach \"" + worktree + "\"");
        _dirs.Add(worktree);
        // The same project, built from the second checkout. Its content need not be
        // committed: what is being proved is that a build inside a linked worktree
        // counts in the repository the worktree belongs to.
        var second = Path.Combine(worktree, "probe");
        Directory.CreateDirectory(second);
        foreach (var file in Directory.EnumerateFiles(main))
            File.Copy(file, Path.Combine(second, Path.GetFileName(file)), overwrite: true);

        var shared = Path.Combine(_probes.GitRepo, ".git", BuildNumberProbes.SharedCounterName);
        BuildNumberProbes.Delete(shared);

        RunOk(main, [Path.Combine(main, "Probe.csproj"), "-t:GetAssemblyAttributes"]);
        Assert.Equal("1", File.ReadAllText(shared).Trim());

        RunOk(second, [Path.Combine(second, "Probe.csproj"), "-restore", "-t:GetAssemblyAttributes"]);
        Assert.Equal("2", File.ReadAllText(shared).Trim());

        Assert.False(File.Exists(Path.Combine(main, ".local-build-number")), "a counter landed in the working tree");
        Assert.False(File.Exists(Path.Combine(second, ".local-build-number")), "a counter landed in the worktree");
    }

    // ===== who is excluded, and who is not =====

    [Theory]
    // The signals that turn numbering off.
    [InlineData("github-actions", false)]
    [InlineData("ci", false)]
    [InlineData("tf-build", false)]
    [InlineData("continuous-integration-build", false)]
    [InlineData("opted-out", false)]
    [InlineData("design-time", false)]
    [InlineData("not-building", false)]
    [InlineData("wpftmp", false)]
    // And the ones that must NOT: CI=false is a real value that some tools export
    // and means "not CI", and an empty one is no signal at all.
    [InlineData("plain", true)]
    [InlineData("ci-false", true)]
    [InlineData("ci-empty", true)]
    public void ABuildIsNumberedOnlyWhenItIsAPlainLocalBuild(string signal, bool numbered)
    {
        Assert.Equal(numbered ? "true" : "false", _probes.Decisions[signal]);
    }

    [Fact]
    public void TheDocumentedOptOutStopsAnActualBuildFromTakingANumber()
    {
        // README offers -p:UseLocalBuildNumber=false to anyone who wants a local
        // build with the release version strings. The decision case above proves the
        // property; this proves the target obeys it, which is the half a missing
        // Condition would break.
        var counter = _probes.NewCounter();

        _probes.RunProbe(counter, ["-t:GetAssemblyAttributes", "-p:UseLocalBuildNumber=false"]);
        Assert.False(File.Exists(counter), "an opted-out build took a number");

        _probes.RunProbe(counter, ["-t:GetAssemblyAttributes"]);
        Assert.Equal("1", File.ReadAllText(counter).Trim());
    }

    [Fact]
    public void ADesignTimeBuildTakesNoNumberFromAnActualBuild()
    {
        // The decision above is a property; this is the target honouring it. An IDE
        // runs design-time builds on every edit and compiles nothing in them, so a
        // number spent there is a number no binary ever carries.
        var counter = _probes.NewCounter();

        _probes.RunProbe(counter, ["-t:GetAssemblyAttributes", "-p:DesignTimeBuild=true"]);
        Assert.False(File.Exists(counter), "a design-time build took a number");

        // The control: the same invocation without the flag DOES take one, so the
        // assertion above is the guard working and not a broken command line.
        _probes.RunProbe(counter, ["-t:GetAssemblyAttributes"]);
        Assert.Equal("1", File.ReadAllText(counter).Trim());
    }

    // ===== what the number looks like =====

    [Fact]
    public void TheBuildNumberIsTheFourthComponentOfTheFileVersion()
    {
        // Properties ▸ Details ▸ File version is the pre-launch check, and a file
        // version has exactly four components — so the number goes in the one the
        // project does not use.
        var allocated = _probes.Allocations["plain"];

        Assert.Equal(1, allocated.Number);
        Assert.Equal("0.11.0.1", allocated.FileVersion);
    }

    [Fact]
    public void TheInformationalVersionCarriesTheNumberAsSemVerBuildMetadata()
    {
        Assert.Equal("0.11.0-dev+build.1", _probes.Allocations["plain"].InformationalVersion);
    }

    [Fact]
    public void AnInformationalVersionThatAlreadyHasMetadataGetsAFurtherIdentifier()
    {
        // The SDK appends "+<commit sha>" when IncludeSourceRevisionInInformationalVersion
        // is on. SemVer allows one '+' only, so the number joins the existing
        // metadata with a dot — exactly as the SDK itself does when it finds a '+'.
        Assert.Equal("0.11.0-dev+abc1234.build.1", _probes.Allocations["already-has-metadata"].InformationalVersion);
    }

    [Fact]
    public void TheNumberContinuesFromTheCounterRatherThanFromOne()
    {
        Assert.Equal(42, _probes.Allocations["continuing"].Number);
        Assert.Equal("0.11.0.42", _probes.Allocations["continuing"].FileVersion);
    }

    [Fact]
    public void AFileVersionThatIsNotAVersionIsLeftAloneAndTheBuildIsWarned()
    {
        // A file version has to parse as one before a component can be replaced.
        // Refusing to guess costs the Explorer half of the feature for that build;
        // guessing would write a version string nobody chose.
        var allocated = _probes.Allocations["not-a-version"];

        Assert.Equal("nightly", allocated.FileVersion);
        Assert.Equal("0.11.0-dev+build.1", allocated.InformationalVersion);   // the half that still works
        Assert.Contains("MMBN0002", _probes.AllocationOutput, StringComparison.Ordinal);
    }

    // ===== the counter file itself =====

    [Fact]
    public void AMissingCounterFileStartsAtOne()
    {
        // Deleting the file is how the owner resets numbering, so a missing file is
        // a normal state, not an error.
        Assert.Equal(1, _probes.Allocations["plain"].Number);
        Assert.Equal("1", File.ReadAllText(_probes.Allocations["plain"].CounterFile).Trim());
    }

    [Fact]
    public void ACorruptCounterFileLeavesTheBuildUnnumberedAndWarnsRatherThanFailing()
    {
        // A half-written file (a machine that lost power mid-build) must not stop
        // anyone from building. It must also not be silently reset to 1: that would
        // hand out numbers an older exe already carries.
        var allocated = _probes.Allocations["corrupt"];

        Assert.Equal(0, allocated.Number);
        Assert.Equal("0.11.0", allocated.FileVersion);               // unnumbered, as before this feature
        Assert.Equal("0.11.0-dev", allocated.InformationalVersion);
        Assert.Equal("banana", File.ReadAllText(allocated.CounterFile).Trim());
        Assert.Contains("MMBN0001", _probes.AllocationOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCeilingIsRefusedRatherThanOverflowingTheFileVersion()
    {
        // A file-version component cannot exceed 65534 — the compiler refuses one
        // that does. Wrapping round would re-issue numbers that are already out
        // there on real exes, so the counter stops and says how to restart it.
        var allocated = _probes.Allocations["at-the-ceiling"];

        Assert.Equal(0, allocated.Number);
        Assert.Equal("65534", File.ReadAllText(allocated.CounterFile).Trim());
        Assert.Contains("MMBN0001", _probes.AllocationOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryWarningCarriesACodeSoItCanBeDemotedUnderWarnAsError()
    {
        // As MSBuild's own "warning <code>" prefix rather than text inside a message:
        // that prefix is what MSBuildWarningsAsMessages matches on.
        Assert.Contains("warning MMBN0001", _probes.AllocationOutput, StringComparison.Ordinal);
        Assert.Contains("warning MMBN0002", _probes.AllocationOutput, StringComparison.Ordinal);
        Assert.Contains("warning MMBN0003", _probes.ResolutionOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRemedyTheReadmePrintsForWarnAsErrorIsACommandLineThatWorks()
    {
        // Round 1 shipped `-p:MSBuildWarningsAsMessages=MMBN0001;MMBN0002`, which
        // MSBuild splits on the semicolon and rejects with MSB1006 (review R2-F2).
        // Grepping the README for "MMBN0001" could never have caught that, so this
        // test takes the switch the README prints, verbatim, and runs it.
        var remedy = DocumentedDemotionSwitch();
        var harness = NewHarness();
        var counter = _probes.NewCounter();
        File.WriteAllText(counter, "banana");   // guarantees the MMBN0001 warning

        // Without the remedy, -warnaserror really does fail this build: otherwise
        // the remedy would be proving nothing.
        var (promoted, promotedOutput) = RunAllocate(harness, counter, ["-warnaserror"]);
        Assert.True(promoted != 0, "-warnaserror did not fail a build that warns:\n" + promotedOutput);

        var (demoted, demotedOutput) = RunAllocate(harness, counter, ["-warnaserror", remedy]);
        Assert.True(demoted == 0, "the README's remedy did not work:\n" + demotedOutput);
        Assert.Equal("banana", File.ReadAllText(counter).Trim());
    }

    [Fact]
    public void ACounterAnotherProcessIsHoldingLeavesTheBuildUnnumberedAndWarns()
    {
        // A backup tool with the lock file open, or a build that will not finish.
        // Waiting forever would hang the build; failing would stop it; so it gives
        // up after its timeout, says so, and builds. What it says is the error it
        // actually got, by exception type so the assertion survives a machine whose
        // Windows speaks another language.
        var counter = _probes.NewCounter();
        var harness = NewHarness();
        using (File.Open(counter + ".lock", FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            var allocated = Allocate(harness, counter, timeoutSeconds: 1);

            Assert.Equal(0, allocated.Number);
            Assert.Contains("MMBN0001", allocated.Output, StringComparison.Ordinal);
            Assert.Contains("IOException", allocated.Output, StringComparison.Ordinal);
        }
        Assert.False(File.Exists(counter), "a build that could not take the lock wrote the counter anyway");
    }

    [Fact]
    public void ALockPathThatCannotBeOpenedAtAllGivesUpAtOnceAndBlamesTheRightThing()
    {
        // Round 1 caught UnauthorizedAccessException in the retry loop - right for
        // the sub-millisecond window where Windows reports a file pending deletion
        // that way, wrong for a path that will never open. Every build then paid the
        // whole timeout and was told "another process held it" while nothing did
        // (review R2-F3). A directory where the lock file should be is that
        // permanent failure, with no ACL for a test to set up.
        var counter = _probes.NewCounter();
        Directory.CreateDirectory(counter + ".lock");
        var harness = NewHarness();

        var clock = Stopwatch.StartNew();
        // A 30-second budget it must NOT spend: the timeout path would take at least
        // that, and the fast path costs one MSBuild start.
        var allocated = Allocate(harness, counter, timeoutSeconds: 30);
        clock.Stop();

        Assert.Equal(0, allocated.Number);
        Assert.Contains("MMBN0001", allocated.Output, StringComparison.Ordinal);
        Assert.Contains("UnauthorizedAccessException", allocated.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("could not be taken within", allocated.Output);   // nothing held it
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15),
            $"an unopenable lock path cost {clock.Elapsed.TotalSeconds:N1}s of a 30s budget");
    }

    [Fact]
    public void TheLockDoesNotOutliveTheBuildThatTookIt()
    {
        // The lock is a holder's lifetime, not a file: it is opened DeleteOnClose so
        // that a build killed mid-allocation takes it with it. A lock left on disk
        // is not a broken mutual exclusion — the next build still waits properly for
        // it — but if nothing is holding it, that build waits the whole timeout and
        // then goes out unnumbered, for a holder that died yesterday.
        foreach (var allocated in _probes.Allocations.Values)
            Assert.False(File.Exists(allocated.CounterFile + ".lock"),
                "a lock file outlived the build that took it: " + allocated.CounterFile + ".lock");
    }

    [Fact]
    public void ConcurrentBuildsNeverTakeTheSameNumber()
    {
        // Parallel project builds, two terminals, an agent worktree and the owner's
        // clone at once — all now sharing one counter, which makes the mutual
        // exclusion load-bearing rather than theoretical. Read-then-write without it
        // hands the same number to two different binaries, the one thing this
        // feature must never do.
        //
        // Four processes, each allocating in a batched loop for about a second, so
        // the sub-millisecond critical sections really do overlap: five cold builds
        // that never overlap prove nothing at all.
        const int workers = 4;
        const int rounds = 120;
        var counter = _probes.NewCounter();
        var harness = NewHarness(rounds);

        var results = Enumerable.Range(0, workers)
            .AsParallel().WithDegreeOfParallelism(workers)
            .Select(worker =>
            {
                var result = Path.Combine(Path.GetDirectoryName(harness)!, "many" + worker + ".txt");
                var (exit, output) = _probes.Run(Path.GetDirectoryName(harness)!, [
                    harness, "-t:AllocateMany",
                    "-p:LocalBuildNumberFile=" + counter,
                    "-p:ProbeResult=" + result,
                ]);
                Assert.True(exit == 0, output);
                return File.ReadAllLines(result).Select(int.Parse).ToList();
            })
            .ToList();

        var numbers = results.SelectMany(n => n).ToList();
        Assert.Equal(workers * rounds, numbers.Count);
        Assert.DoesNotContain(0, numbers);
        Assert.Equal(numbers.Count, numbers.Distinct().Count());
        Assert.Equal(numbers.Count, numbers.Max());
        Assert.Equal(numbers.Count.ToString(), File.ReadAllText(counter).Trim());
    }

    // ===== the repository says where it lives =====

    [Fact]
    public void TheFallbackCounterFileIsGitIgnored()
    {
        // The shared counter lives inside .git and can never be committed. The
        // fallback one sits in the working tree, so the name in .gitignore has to be
        // the name the targets file actually uses for it.
        var name = FallbackCounterName();
        var ignores = RepoSources.Read(".gitignore")
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToList();

        Assert.Contains(ignores, line => line == name || line == name + "*" || line == "/" + name);
    }

    [Fact]
    public void TheReadmeSaysWhereTheCounterLivesAndHowToResetIt()
    {
        // The questions the number raises: where does it come from, is it mine
        // alone, and how do I start again? A number with no documented reset is a
        // number nobody trusts.
        var build = ReadmeBuildSection();

        Assert.Contains(FallbackCounterName(), build, StringComparison.Ordinal);
        Assert.Contains(BuildNumberProbes.SharedCounterName, build, StringComparison.Ordinal);
        Assert.Contains("worktree", build, StringComparison.OrdinalIgnoreCase);   // one counter per repository
        Assert.Contains("delete", build, StringComparison.OrdinalIgnoreCase);     // how to reset it
    }

    [Fact]
    public void TheReadmeShowsTheAboutBoxAndTheTitleBarSeparately()
    {
        // They are two different strings — "Version 0.11.0-dev+build.57" and
        // "... | Markdown Midget v0.11.0-dev+build.57" — and a reader checking the
        // title bar against the README's one row would find neither matches.
        var build = ReadmeBuildSection();

        Assert.Contains("Version 0.11.0-dev+build.57", build, StringComparison.Ordinal);
        Assert.Contains("Markdown Midget v0.11.0-dev+build.57", build, StringComparison.Ordinal);
        Assert.Contains("Properties", build, StringComparison.Ordinal);
        Assert.Contains("Help ▸ About", build, StringComparison.Ordinal);
    }

    [Fact]
    public void TheReadmeStatesWhatTheNumberingCostsTheSuite()
    {
        // These tests spawn MSBuild dozens of times; anyone who notices the suite
        // got slower should find the reason written down rather than go looking.
        var build = ReadmeBuildSection();

        Assert.Contains("LocalBuildNumberTests", build, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAppProjectImportsTheBuildNumberTargets()
    {
        // The import, not a mention of it: the csproj comment above it names the
        // file too, so Contains("LocalBuildNumber.targets") stayed green with the
        // Import deleted and the shipped exe unnumbered (review F-1).
        var csproj = RepoSources.Read("src", "MarkdownMidget", "MarkdownMidget.csproj");

        Assert.Matches(@"<Import\s+Project=""[^""]*LocalBuildNumber\.targets""", csproj);
    }

    [Fact]
    public void TheseTestsCleanUpAfterThemselvesIncludingGitsReadOnlyObjects()
    {
        // Git writes loose objects read-only. Directory.Delete(recursive) throws part
        // way through on the first one and leaves the rest, which is how round 1's
        // fix leaked one temp directory per suite run (review R2-F5). The attribute
        // is the cause: nothing holds these files.
        var fixture = Path.Combine(_probes.Root, "readonly-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(fixture, "objects", "ab"));
        var loose = Path.Combine(fixture, "objects", "ab", "cdef1234");
        File.WriteAllText(loose, "loose object");
        File.SetAttributes(loose, FileAttributes.ReadOnly);

        BuildNumberProbes.Delete(fixture);

        Assert.False(Directory.Exists(fixture), "a read-only file kept the temp directory alive");
    }

    [Fact]
    public void TheseTestsDoNotRunBesideTheWindowTests()
    {
        // They spawn four MSBuilds at once; the window tests wait on real Win32
        // focus timeouts. Whichever fails first, the cause would be this class.
        // xUnit's CollectionAttribute keeps the name in its constructor argument
        // rather than a property, so the attribute data is what carries it.
        var collection = CustomAttributeData.GetCustomAttributes(typeof(LocalBuildNumberTests))
            .SingleOrDefault(a => a.AttributeType == typeof(CollectionAttribute));

        Assert.NotNull(collection);
        Assert.Equal("LocalBuildNumber", (string)collection!.ConstructorArguments[0].Value!);
        var definition = typeof(LocalBuildNumberCollection).GetCustomAttribute<CollectionDefinitionAttribute>();
        Assert.True(definition!.DisableParallelization, "the collection runs in parallel with the rest of the suite");
    }

    // ===== helpers =====

    private static string ReadmeBuildSection()
    {
        var readme = RepoSources.Read("README.md");
        var build = readme[readme.IndexOf("## Build & run", StringComparison.Ordinal)..];
        return build[..build.IndexOf("\n## ", StringComparison.Ordinal)];
    }

    /// <summary>The fallback counter's name, read from the targets file's own default
    /// so this suite cannot go on pinning a name the build stopped using.</summary>
    private static string FallbackCounterName()
    {
        var targets = File.ReadAllText(BuildNumberProbes.TargetsFile);
        // The last quoted piece of the default's path expression is the file name:
        //   ...NormalizePath('$(MSBuildThisFileDirectory)..', '.local-build-number'))
        var match = Regex.Match(targets,
            @"<LocalBuildNumberFallbackFile[^>]*>[^<]*'([^']+)'\)*</LocalBuildNumberFallbackFile>");
        Assert.True(match.Success, "no default LocalBuildNumberFallbackFile in " + BuildNumberProbes.TargetsFile);
        return match.Groups[1].Value;
    }

    private string NewHarness(int rounds = 0)
    {
        var dir = Path.Combine(_probes.Root, "harness-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        var project = Path.Combine(dir, "harness.proj");
        var many = rounds <= 0 ? "" : $"""
              <ItemGroup>
                <Round Include="{string.Join(";", Enumerable.Range(1, rounds))}" />
              </ItemGroup>
              <Target Name="AllocateMany" Outputs="%(Round.Identity)">
                <AllocateLocalBuildNumber CounterFile="$(LocalBuildNumberFile)"
                                          TimeoutSeconds="$(LocalBuildNumberTimeoutSeconds)"
                                          BaseFileVersion="0.11.0" BaseInformationalVersion="0.11.0-dev">
                  <Output TaskParameter="Number" PropertyName="ProbeNumber" />
                </AllocateLocalBuildNumber>
                <WriteLinesToFile File="$(ProbeResult)" Lines="$(ProbeNumber)" Overwrite="false" />
              </Target>
            """;
        File.WriteAllText(project, $"""
            <Project>
              <Import Project="{BuildNumberProbes.TargetsFile}" />
              <Target Name="Allocate">
                <AllocateLocalBuildNumber CounterFile="$(LocalBuildNumberFile)"
                                          TimeoutSeconds="$(LocalBuildNumberTimeoutSeconds)"
                                          BaseFileVersion="$(ProbeFileVersion)"
                                          BaseInformationalVersion="$(ProbeInformationalVersion)">
                  <Output TaskParameter="Number" PropertyName="ProbeNumber" />
                  <Output TaskParameter="FileVersion" PropertyName="ProbeNewFileVersion" />
                  <Output TaskParameter="InformationalVersion" PropertyName="ProbeNewInformationalVersion" />
                </AllocateLocalBuildNumber>
                <WriteLinesToFile File="$(ProbeResult)" Overwrite="true"
                                  Lines="$(ProbeNumber);$(ProbeNewFileVersion);$(ProbeNewInformationalVersion)" />
              </Target>
            {many}
            </Project>
            """);
        return project;
    }

    private Allocated Allocate(string harness, string counter, int timeoutSeconds = 15,
                               string fileVersion = "0.11.0", string informationalVersion = "0.11.0-dev")
    {
        var (exit, output) = RunAllocate(harness, counter, [], timeoutSeconds, fileVersion, informationalVersion);
        Assert.True(exit == 0, output);
        return BuildNumberProbes.ReadAllocation(counter, Path.Combine(Path.GetDirectoryName(harness)!, "result.txt"),
                                                output);
    }

    /// <summary>An allocation whose exit code is the test's business — for the cases
    /// where failing (or not failing) IS the assertion.</summary>
    private (int Exit, string Output) RunAllocate(string harness, string counter, IEnumerable<string> extra,
                                                  int timeoutSeconds = 15, string fileVersion = "0.11.0",
                                                  string informationalVersion = "0.11.0-dev")
    {
        var dir = Path.GetDirectoryName(harness)!;
        return _probes.Run(dir, [
            harness, "-t:Allocate",
            "-p:LocalBuildNumberFile=" + counter,
            "-p:LocalBuildNumberTimeoutSeconds=" + timeoutSeconds,
            "-p:ProbeFileVersion=" + fileVersion,
            "-p:ProbeInformationalVersion=" + informationalVersion,
            "-p:ProbeResult=" + Path.Combine(dir, "result.txt"),
            .. extra,
        ]);
    }

    /// <summary>The exact command-line switch README tells the reader to use when a
    /// build runs with -warnaserror. Taken from the README so the test cannot pass
    /// while the documented line is something else.</summary>
    private static string DocumentedDemotionSwitch()
    {
        var match = Regex.Match(ReadmeBuildSection(), @"`(-p:MSBuildWarningsAsMessages=[^`\s]+)`");
        Assert.True(match.Success, "README's Build & run section prints no MSBuildWarningsAsMessages switch");
        return match.Groups[1].Value;
    }

    private static void Git(string workDir, string arguments) => BuildNumberProbes.Git(workDir, arguments);

    /// <summary>An MSBuild run whose failure is this test's failure — a run that
    /// silently exited 1 would leave a counter untouched and look like a verdict.</summary>
    private void RunOk(string workDir, IEnumerable<string> args)
    {
        var (exit, output) = _probes.Run(workDir, args);
        Assert.True(exit == 0, output);
    }
}

/// <summary>One allocation's answer: the number, the two version strings it produced,
/// and the build output that carried any warning.</summary>
public sealed record Allocated(int Number, string FileVersion, string InformationalVersion,
                               string CounterFile, string Output);

/// <summary>
/// The shared, expensive half of <see cref="LocalBuildNumberTests"/>: one NuGet
/// restore, and one MSBuild process each for the allocation cases, the
/// numbered-or-not decision, and the counter-path resolution. Every case is
/// batched into a single run rather than a process apiece — twelve extra MSBuild
/// starts cost more than everything they prove.
/// </summary>
public sealed class BuildNumberProbes : IDisposable
{
    public static string TargetsFile => Path.Combine(RepoSources.Root(), "build", "LocalBuildNumber.targets");

    /// <summary>The counter's name inside the repository's own directory. Read from
    /// the targets file so the tests and the build cannot disagree about it.</summary>
    public static string SharedCounterName { get; } = ReadSharedCounterName();

    public string Root { get; }
    public string ProbeDir { get; }
    public string GitRepo { get; }
    public string GitProbeDir { get; }
    public string FallbackCounter { get; }
    public IReadOnlyDictionary<string, Allocated> Allocations { get; }
    public string AllocationOutput { get; }
    public IReadOnlyDictionary<string, string> Decisions { get; }
    public IReadOnlyDictionary<string, string> Resolutions { get; }
    /// <summary>What git itself answers for the shapes git can be asked about.</summary>
    public IReadOnlyDictionary<string, string> GitCommonDirs { get; }
    public string ResolutionOutput { get; }

    public BuildNumberProbes()
    {
        Root = Path.Combine(Path.GetTempPath(), "mm-buildnumber-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        FallbackCounter = Path.Combine(Root, "fallback-counter");

        ProbeDir = NewProjectDir("probe");
        // One restore for every test that builds or asks for assembly attributes.
        Run(ProbeDir, [Path.Combine(ProbeDir, "Probe.csproj"), "-t:Restore"]);

        (Allocations, AllocationOutput) = RunAllocations();
        Decisions = RunDecisions();
        (GitRepo, GitProbeDir, Resolutions, GitCommonDirs, ResolutionOutput) = RunResolutions();
    }

    public void Dispose() => Delete(Root);

    // ===== building the throwaway SDK project =====

    public string NewCounter() => Path.Combine(Root, "counter-" + Guid.NewGuid().ToString("N")[..8]);

    public BuiltVersions BuildProbe(string counter, IEnumerable<string>? extra = null,
                                    IDictionary<string, string>? env = null)
    {
        RunProbe(counter, ["-t:Build", .. extra ?? []], env);
        var dll = Path.Combine(ProbeDir, "bin", "Debug", "net10.0", "Probe.dll");
        Assert.True(File.Exists(dll), "no built assembly at " + dll);
        var info = FileVersionInfo.GetVersionInfo(dll);
        return new BuiltVersions(info.FileVersion ?? "", info.ProductVersion ?? "");
    }

    public void RunProbe(string counter, IEnumerable<string> args, IDictionary<string, string>? env = null)
    {
        var (exit, output) = Run(ProbeDir,
            [Path.Combine(ProbeDir, "Probe.csproj"), "-p:LocalBuildNumberFile=" + counter, .. args], env);
        Assert.True(exit == 0, output);
    }

    private string NewProjectDir(string name)
    {
        var dir = Path.Combine(Root, name);
        Directory.CreateDirectory(dir);
        // Nothing above the temp folder gets to steer these builds.
        File.WriteAllText(Path.Combine(dir, "Directory.Build.props"), "<Project />\n");
        File.WriteAllText(Path.Combine(dir, "Directory.Build.targets"), "<Project />\n");
        File.WriteAllText(Path.Combine(dir, "Probe.csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Version>9.9.9-probe</Version>
                <Nullable>disable</Nullable>
                <AssemblyName>Probe</AssemblyName>
              </PropertyGroup>
              <Import Project="{TargetsFile}" />
            </Project>
            """);
        File.WriteAllText(Path.Combine(dir, "Probe.cs"), "public class Probe { }\n");
        return dir;
    }

    // ===== the three batched runs =====

    private (IReadOnlyDictionary<string, Allocated>, string) RunAllocations()
    {
        var dir = Path.Combine(Root, "allocations");
        Directory.CreateDirectory(dir);

        // name -> (base file version, base informational version, what the counter
        // already holds)
        var cases = new (string Name, string FileVersion, string Informational, string? Seed)[]
        {
            ("plain", "0.11.0", "0.11.0-dev", null),
            ("already-has-metadata", "0.11.0", "0.11.0-dev+abc1234", null),
            ("continuing", "0.11.0", "0.11.0-dev", "41"),
            ("not-a-version", "nightly", "0.11.0-dev", null),
            ("corrupt", "0.11.0", "0.11.0-dev", "banana"),
            ("at-the-ceiling", "0.11.0", "0.11.0-dev", "65534"),
        };

        var items = new StringBuilder();
        foreach (var (name, fileVersion, informational, seed) in cases)
        {
            var counter = Path.Combine(dir, name + ".counter");
            if (seed is not null) File.WriteAllText(counter, seed);
            items.AppendLine($"""
                    <Case Include="{name}">
                      <CounterFile>{counter}</CounterFile>
                      <BaseFileVersion>{fileVersion}</BaseFileVersion>
                      <BaseInformationalVersion>{informational}</BaseInformationalVersion>
                      <Result>{Path.Combine(dir, name + ".result")}</Result>
                    </Case>
                """);
        }

        var project = Path.Combine(dir, "allocations.proj");
        File.WriteAllText(project, $"""
            <Project>
              <Import Project="{TargetsFile}" />
              <ItemGroup>
            {items}
              </ItemGroup>
              <Target Name="AllocateAll" Outputs="%(Case.Identity)">
                <AllocateLocalBuildNumber CounterFile="%(Case.CounterFile)"
                                          TimeoutSeconds="5"
                                          BaseFileVersion="%(Case.BaseFileVersion)"
                                          BaseInformationalVersion="%(Case.BaseInformationalVersion)">
                  <Output TaskParameter="Number" PropertyName="ProbeNumber" />
                  <Output TaskParameter="FileVersion" PropertyName="ProbeNewFileVersion" />
                  <Output TaskParameter="InformationalVersion" PropertyName="ProbeNewInformationalVersion" />
                </AllocateLocalBuildNumber>
                <WriteLinesToFile File="%(Case.Result)" Overwrite="true"
                                  Lines="$(ProbeNumber);$(ProbeNewFileVersion);$(ProbeNewInformationalVersion)" />
              </Target>
            </Project>
            """);

        var (exit, output) = Run(dir, [project, "-t:AllocateAll"]);
        Assert.True(exit == 0, output);

        var results = new Dictionary<string, Allocated>(StringComparer.Ordinal);
        foreach (var (name, _, _, _) in cases)
            results[name] = ReadAllocation(Path.Combine(dir, name + ".counter"),
                                           Path.Combine(dir, name + ".result"), output);
        return (results, output);
    }

    private IReadOnlyDictionary<string, string> RunDecisions()
    {
        var dir = Path.Combine(Root, "decisions");
        Directory.CreateDirectory(dir);
        var project = Path.Combine(dir, "decisions.proj");
        // WPF's markup compiler builds a second, temporary copy of the project named
        // "<project>_<random>_wpftmp" (GenerateTemporaryTargetAssembly in
        // Microsoft.WinFX.targets); the name is the only thing that distinguishes it.
        var wpftmp = Path.Combine(dir, "decisions_a1b2c3d4_wpftmp.proj");

        // name -> the global properties that case sets. Each MSBuild call
        // re-evaluates the project with them, which is what the conditions in the
        // targets file see whether they come from the environment or the command
        // line; ABuildOnCiKeepsTheVersionStringsTheReleaseWorkflowPasses covers the
        // environment route end to end.
        var cases = new (string Name, string Properties, string Project)[]
        {
            ("plain", "MMProbe=plain", project),
            ("github-actions", "GITHUB_ACTIONS=true", project),
            ("ci", "CI=true", project),
            ("ci-false", "CI=false", project),
            ("ci-empty", "CI=", project),
            ("tf-build", "TF_BUILD=true", project),
            ("continuous-integration-build", "ContinuousIntegrationBuild=true", project),
            ("opted-out", "UseLocalBuildNumber=false", project),
            ("design-time", "DesignTimeBuild=true", project),
            ("not-building", "BuildingProject=false", project),
            ("wpftmp", "MMProbe=wpftmp", wpftmp),
        };

        var calls = new StringBuilder();
        foreach (var (name, properties, target) in cases)
            calls.AppendLine($"""
                    <MSBuild Projects="{target}" Targets="WriteDecision"
                             Properties="DecisionResult={Path.Combine(dir, name + ".decision")};{properties}" />
                """);

        var text = $"""
            <Project>
              <Import Project="{TargetsFile}" />
              <Target Name="WriteDecision">
                <WriteLinesToFile File="$(DecisionResult)" Lines="$(UseLocalBuildNumber)" Overwrite="true" />
              </Target>
              <Target Name="Decisions">
            {calls}
              </Target>
            </Project>
            """;
        File.WriteAllText(project, text);
        File.WriteAllText(wpftmp, text);

        var (exit, output) = Run(dir, [project, "-t:Decisions"]);
        Assert.True(exit == 0, output);

        var results = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, _, _) in cases)
            results[name] = File.ReadAllText(Path.Combine(dir, name + ".decision")).Trim();
        return results;
    }

    /// <summary>
    /// Every repository shape the resolver has to answer for, each one built by git
    /// itself where git is what creates the shape, and two built by hand for the
    /// link forms git writes only in situations a test cannot stage (a relative
    /// <c>gitdir:</c>, an absolute <c>commondir</c>).
    ///
    /// Every real-git case also records what `git rev-parse` answers for the
    /// git-common-dir option, so the walk is compared against the thing it replaced
    /// rather than against what this test's author believed git does.
    /// </summary>
    private (string Repo, string ProbeInRepo,
             IReadOnlyDictionary<string, string> Resolved,
             IReadOnlyDictionary<string, string> GitSays,
             string Output) RunResolutions()
    {
        var repo = NewGitRepo(Path.Combine(Root, "repo"));

        // A probe project inside the repository, for the end-to-end path test.
        var probeInRepo = Path.Combine(repo, "probe");
        Directory.CreateDirectory(probeInRepo);
        foreach (var file in Directory.EnumerateFiles(ProbeDir))
            File.Copy(file, Path.Combine(probeInRepo, Path.GetFileName(file)), overwrite: true);
        Run(probeInRepo, [Path.Combine(probeInRepo, "Probe.csproj"), "-t:Restore"]);

        // A linked worktree, and a worktree added from inside that worktree: both
        // carry a .git FILE, and both must land on the original repository.
        var worktree = Path.Combine(Root, "repo-worktree");
        Git(repo, "worktree add --detach \"" + worktree + "\"");
        var nested = Path.Combine(Root, "repo-worktree-nested");
        Git(worktree, "worktree add --detach \"" + nested + "\"");

        // A submodule is its own repository, kept at .git/modules/<name> of the
        // superproject: it gets its own counter, which is what git reports too.
        var sub = NewGitRepo(Path.Combine(Root, "sub"));
        var super = NewGitRepo(Path.Combine(Root, "super"));
        Git(super, "-c protocol.file.allow=always -c user.email=probe@example.com -c user.name=Probe " +
                   "submodule add \"" + sub.Replace('\\', '/') + "\" sub");

        // No work tree at all. `git rev-parse` answers "." there, and the walk has
        // to recognise a directory that IS a git directory.
        var bare = Path.Combine(Root, "bare.git");
        Directory.CreateDirectory(bare);
        Git(bare, "init --bare");

        // Paths with spaces broke every quoting scheme the spawned version had to
        // get right; the walk has no command line at all, which is the point.
        var spaced = NewGitRepo(Path.Combine(Root, "repo with spaces"));

        // Hand-built link forms. Relative first: a .git file pointing up and across,
        // whose gitdir then points somewhere else again through commondir - both
        // relative, each resolved against the file that names it.
        var relativeWorktree = Path.Combine(Root, "relative-link");
        var relativeGitDir = Path.Combine(Root, "relative-gitdir");
        var relativeCommon = Path.Combine(Root, "relative-common");
        Directory.CreateDirectory(relativeWorktree);
        Directory.CreateDirectory(relativeGitDir);
        Directory.CreateDirectory(relativeCommon);
        File.WriteAllText(Path.Combine(relativeWorktree, ".git"), "gitdir: ../relative-gitdir\n");
        File.WriteAllText(Path.Combine(relativeGitDir, "commondir"), "../relative-common\n");

        // And absolute: the shape git writes when the repository was named by an
        // absolute path.
        var absoluteWorktree = Path.Combine(Root, "absolute-link");
        var absoluteGitDir = Path.Combine(Root, "absolute-gitdir");
        var absoluteCommon = Path.Combine(Root, "absolute-common");
        Directory.CreateDirectory(absoluteWorktree);
        Directory.CreateDirectory(absoluteGitDir);
        Directory.CreateDirectory(absoluteCommon);
        File.WriteAllText(Path.Combine(absoluteWorktree, ".git"), "gitdir: " + absoluteGitDir + "\n");
        File.WriteAllText(Path.Combine(absoluteGitDir, "commondir"), absoluteCommon + "\n");

        var notARepository = Path.Combine(Root, "not-a-repository");
        Directory.CreateDirectory(notARepository);

        // name -> where the build would be, and whether git can be asked about it.
        var cases = new (string Name, string Start, bool AskGit)[]
        {
            ("repo", repo, true),
            ("subdirectory", probeInRepo, true),
            ("worktree", worktree, true),
            ("nested-worktree", nested, true),
            ("submodule", Path.Combine(super, "sub"), true),
            ("superproject", super, true),
            ("bare", bare, true),
            ("path-with-spaces", spaced, true),
            ("relative-link", relativeWorktree, false),
            ("absolute-link", absoluteWorktree, false),
            ("not-a-repository", notARepository, false),
        };

        var dir = Path.Combine(Root, "resolutions");
        Directory.CreateDirectory(dir);
        var items = new StringBuilder();
        foreach (var (name, start, _) in cases)
            items.AppendLine($"""
                    <Case Include="{name}">
                      <StartDirectory>{start}</StartDirectory>
                      <Result>{Path.Combine(dir, name + ".resolved")}</Result>
                    </Case>
                """);

        var project = Path.Combine(dir, "resolutions.proj");
        File.WriteAllText(project, $"""
            <Project>
              <Import Project="{TargetsFile}" />
              <ItemGroup>
            {items}
              </ItemGroup>
              <Target Name="ResolveAll" Outputs="%(Case.Identity)">
                <ResolveLocalBuildNumberFile StartDirectory="%(Case.StartDirectory)"
                                             FallbackFile="{FallbackCounter}"
                                             FileName="$(LocalBuildNumberSharedName)">
                  <Output TaskParameter="CounterFile" PropertyName="Resolved" />
                </ResolveLocalBuildNumberFile>
                <WriteLinesToFile File="%(Case.Result)" Lines="$(Resolved)" Overwrite="true" />
              </Target>
            </Project>
            """);

        var (exit, output) = Run(dir, [project, "-t:ResolveAll"]);
        Assert.True(exit == 0, output);

        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        var gitSays = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, start, askGit) in cases)
        {
            resolved[name] = File.ReadAllText(Path.Combine(dir, name + ".resolved")).Trim();
            if (askGit) gitSays[name] = GitCommonDir(start);
        }
        return (repo, probeInRepo, resolved, gitSays, output);
    }

    private static string NewGitRepo(string path)
    {
        Directory.CreateDirectory(path);
        Git(path, "init");
        File.WriteAllText(Path.Combine(path, "readme.txt"), "probe\n");
        Git(path, "add -A");
        Git(path, "-c user.email=probe@example.com -c user.name=Probe commit -m probe");
        return path;
    }

    /// <summary>What git itself calls the repository's common directory, as an
    /// absolute path — the oracle the walk is measured against.</summary>
    public static string GitCommonDir(string workDir)
    {
        var answer = GitOutput(workDir, "rev-parse --git-common-dir").Trim();
        return Path.GetFullPath(Path.IsPathRooted(answer) ? answer : Path.Combine(workDir, answer));
    }

    // ===== running things =====

    public (int Exit, string Output) Run(string workDir, IEnumerable<string> args,
                                         IDictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("msbuild");
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        psi.ArgumentList.Add("-nologo");
        psi.ArgumentList.Add("-v:m");
        // Leave no MSBuild worker processes behind: they live for minutes, hold
        // handles on the temp directories these tests delete, and on a CI runner
        // they outlive the job step.
        psi.ArgumentList.Add("-nodeReuse:false");

        // Most of these tests are about what a LOCAL build does, and a CI runner
        // exports the first three to every child process — so each case states its
        // own mode instead of inheriting the machine's. That part is load-bearing:
        // without it every "is this numbered" assertion inverts on CI.
        //
        // The MSBUILD* removal is a precaution, not a fix for an observed failure:
        // the outer `dotnet test` leaks its own SDK and node-reuse settings into this
        // process, and a nested build that inherits them is a known way to get a
        // build that only misbehaves on someone else's machine. No test here can
        // demonstrate it, and it costs nothing, so it stays.
        foreach (var name in new[]
                 {
                     "GITHUB_ACTIONS", "CI", "TF_BUILD", "ContinuousIntegrationBuild", "UseLocalBuildNumber",
                 })
            psi.Environment.Remove(name);
        foreach (var name in psi.Environment.Keys
                     .Where(k => k.StartsWith("MSBUILD", StringComparison.OrdinalIgnoreCase))
                     .ToList())
            psi.Environment.Remove(name);
        if (env is not null)
            foreach (var pair in env) psi.Environment[pair.Key] = pair.Value;

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        // A hang must fail the test rather than the whole `dotnet test` run. Five
        // minutes is absurd locally and merely generous on a two-core runner.
        Assert.True(process.WaitForExit(300_000), "dotnet msbuild did not finish within five minutes");
        Task.WaitAll(stdout, stderr);
        return (process.ExitCode, stdout.Result + stderr.Result);
    }

    public static void Git(string workDir, string arguments) => GitOutput(workDir, arguments);

    /// <summary>Runs git and returns its standard output. Both pipes are drained
    /// before the wait: a child that fills one while nobody reads the other never
    /// exits, and the wait's timeout never arrives.</summary>
    public static string GitOutput(string workDir, string arguments)
    {
        var psi = new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(60_000), "git " + arguments + " did not finish");
        Task.WaitAll(output, error);
        Assert.True(process.ExitCode == 0, "git " + arguments + " failed: " + error.Result + output.Result);
        return output.Result;
    }

    public static Allocated ReadAllocation(string counter, string result, string output)
    {
        var lines = File.ReadAllLines(result);
        Assert.True(lines.Length == 3, "the harness wrote " + lines.Length + " lines:\n" + output);
        return new Allocated(int.Parse(lines[0]), lines[1], lines[2], counter, output);
    }

    private static string ReadSharedCounterName()
    {
        var targets = File.ReadAllText(TargetsFile);
        var match = Regex.Match(targets,
            @"<LocalBuildNumberSharedName[^>]*>([^<]+)</LocalBuildNumberSharedName>");
        Assert.True(match.Success, "no LocalBuildNumberSharedName in " + TargetsFile);
        return match.Groups[1].Value.Trim();
    }

    /// <summary>
    /// Remove a throwaway directory, read-only files included.
    ///
    /// Git writes loose objects with the read-only attribute set, and
    /// Directory.Delete then throws UnauthorizedAccessException part way through and
    /// leaves the rest behind — which is how a suite that makes git repositories
    /// leaks one temp directory per run. The attribute is the whole cause; nothing is
    /// holding these files. A second attempt covers the one real race, an antivirus
    /// scanner still reading a file this run just wrote.
    /// </summary>
    public static void Delete(string path)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    {
                        try { File.SetAttributes(file, FileAttributes.Normal); } catch { /* deleted under us */ }
                    }
                    Directory.Delete(path, recursive: true);
                }
                else if (File.Exists(path))
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                    File.Delete(path);
                }
                return;
            }
            catch when (attempt < 2)
            {
                Thread.Sleep(100);
            }
            catch
            {
                // Still there: a leaked temp directory is not worth failing a test
                // run over, and TheseTestsCleanUpAfterThemselves is what stops the
                // ordinary cause from coming back.
            }
        }
    }
}

public sealed record BuiltVersions(string FileVersion, string ProductVersion);
