using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>
/// The local build counter — <c>build/LocalBuildNumber.targets</c> — which stamps a
/// number into the exe so a copy of it can be identified at a glance.
///
/// Everything here drives the REAL targets file with a real MSBuild, against
/// throwaway projects in the temp folder and a counter file per test, so no test
/// ever touches the counter of the clone it is running in. Two shapes are used:
///
/// - a throwaway SDK project, built for real, whose BUILT DLL is then read with
///   <see cref="FileVersionInfo"/> — the same fields Explorer shows in
///   Properties ▸ Details, which is where the owner reads the number; and
/// - a plain MSBuild project that calls the allocation task directly, for the
///   file-level behaviour (missing, corrupt, locked, concurrent) where a full
///   compile would only add seconds.
/// </summary>
public class LocalBuildNumberTests : IDisposable
{
    private static string TargetsFile => Path.Combine(RepoSources.Root(), "build", "LocalBuildNumber.targets");

    private readonly List<string> _dirs = [];

    public void Dispose()
    {
        foreach (var dir in _dirs)
            try { Directory.Delete(dir, recursive: true); } catch { /* a locked obj file is not a test failure */ }
    }

    // ===== the counter reaches the binary =====

    [Fact]
    public void TwoBuildsOfTheSameProjectProduceTwoDifferentlyNumberedBinaries()
    {
        var probe = NewSdkProbe();

        var first = probe.Build(restore: true);
        File.AppendAllText(Path.Combine(probe.Dir, "Probe.cs"), "// edited\n");
        var second = probe.Build();

        Assert.Equal("9.9.9.1", first.FileVersion);
        Assert.Equal("9.9.9-probe+build.1", first.ProductVersion);
        Assert.Equal("9.9.9.2", second.FileVersion);
        Assert.Equal("9.9.9-probe+build.2", second.ProductVersion);
        Assert.Equal("2", File.ReadAllText(probe.CounterFile).Trim());
    }

    [Fact]
    public void ABuildWithNothingChangedStillProducesANewNumber()
    {
        // The rule this feature picked, and the one the owner asked for: every local
        // build invocation takes a number, so an exe that was published a moment ago
        // can never carry the number of the one before it. The cost is that the
        // version attribute changes every time, so the compiler runs every time —
        // about two seconds, measured on this project, and the reason CI is excluded.
        var probe = NewSdkProbe();

        var first = probe.Build(restore: true);
        var second = probe.Build();   // not one byte of the project has changed

        Assert.Equal("9.9.9.1", first.FileVersion);
        Assert.Equal("9.9.9.2", second.FileVersion);
        // The number is in the BINARY, not merely in a generated source file: an
        // incremental build that skipped the compile would leave the dll on 1.
        Assert.Equal("9.9.9-probe+build.2", second.ProductVersion);
    }

    [Fact]
    public void APublishTakesItsOwnNumber()
    {
        // The exe the owner actually copies comes from `dotnet publish`, and publish
        // runs the build — so it must take a number of its own rather than shipping
        // whatever the last `dotnet build` stamped.
        var probe = NewSdkProbe();
        probe.Build(restore: true);

        var publishDir = Path.Combine(probe.Dir, "published");
        probe.Run(["-t:Publish", "-p:PublishDir=" + publishDir + Path.DirectorySeparatorChar]);

        var published = FileVersionInfo.GetVersionInfo(Path.Combine(publishDir, "Probe.dll"));
        Assert.Equal("9.9.9.2", published.FileVersion);
        Assert.Equal("9.9.9-probe+build.2", published.ProductVersion);
        Assert.Equal("2", File.ReadAllText(probe.CounterFile).Trim());
    }

    // ===== CI and release builds are left exactly as they were =====

    [Theory]
    [InlineData("GITHUB_ACTIONS")]
    [InlineData("CI")]
    public void ABuildOnCiIgnoresTheLocalCounter(string variable)
    {
        // Both workflows run on GitHub Actions, which exports both of these. A
        // release asset must carry the version the tag named and nothing else.
        var probe = NewSdkProbe();

        var built = probe.Build(restore: true, env: new Dictionary<string, string> { [variable] = "true" });

        Assert.Equal("9.9.9.0", built.FileVersion);
        Assert.Equal("9.9.9-probe", built.ProductVersion);
        Assert.False(File.Exists(probe.CounterFile), "a CI build wrote a counter file");
    }

    [Fact]
    public void ABuildOnCiKeepsTheVersionStringsTheReleaseWorkflowPasses()
    {
        // .github/workflows/release.yml publishes with -p:Version=<base> and
        // -p:InformationalVersion=<tag minus v>. What the released exe reports must
        // be byte-for-byte what it reported before this feature existed.
        var probe = NewSdkProbe();

        var built = probe.Build(restore: true,
            extra: ["-p:Version=0.12.0", "-p:InformationalVersion=0.12.0-beta1"],
            env: new Dictionary<string, string> { ["GITHUB_ACTIONS"] = "true" });

        Assert.Equal("0.12.0.0", built.FileVersion);
        Assert.Equal("0.12.0-beta1", built.ProductVersion);
        Assert.False(File.Exists(probe.CounterFile));
    }

    // ===== builds that are not a build of the app =====

    [Fact]
    public void TheMarkupCompilersTemporaryProjectDoesNotTakeANumber()
    {
        // WPF's markup compiler builds a second, temporary copy of the project to
        // resolve local types — named "<project>_<random>_wpftmp" in
        // Microsoft.WinFX.targets — and that copy imports everything this one does.
        // Left alone it would consume a number per build for an assembly nobody
        // ships, so one `dotnet build` of the app would advance the counter by two.
        var probe = NewSdkProbe(name: "Probe_a1b2c3d4_wpftmp");

        var built = probe.Build(restore: true);

        Assert.Equal("9.9.9.0", built.FileVersion);
        Assert.Equal("9.9.9-probe", built.ProductVersion);
        Assert.False(File.Exists(probe.CounterFile), "the markup compiler's temporary project took a number");
    }

    [Fact]
    public void ADesignTimeBuildDoesNotTakeANumber()
    {
        // An IDE runs design-time builds constantly — on every edit, every file
        // added. They compile nothing, so a number spent there is a number no exe
        // ever carries, and the counter would race ahead while the editor sat idle.
        var probe = NewSdkProbe();

        probe.Run(["-t:GetAssemblyAttributes", "-p:DesignTimeBuild=true"], restore: true);
        Assert.False(File.Exists(probe.CounterFile), "a design-time build took a number");

        // The control: the same invocation without the design-time flag DOES take
        // one, so the assertion above is the guard working and not a broken command.
        probe.Run(["-t:GetAssemblyAttributes"]);
        Assert.Equal("1", File.ReadAllText(probe.CounterFile).Trim());
    }

    // ===== what the number looks like =====

    [Fact]
    public void TheBuildNumberIsTheFourthComponentOfTheFileVersion()
    {
        // Properties ▸ Details ▸ File version is the pre-launch check, and a file
        // version has exactly four components — so the number goes in the one the
        // project does not use.
        var allocated = Allocate(NewHarness(), fileVersion: "0.11.0", informationalVersion: "0.11.0-dev");

        Assert.Equal(1, allocated.Number);
        Assert.Equal("0.11.0.1", allocated.FileVersion);
    }

    [Fact]
    public void TheInformationalVersionCarriesTheNumberAsSemVerBuildMetadata()
    {
        var allocated = Allocate(NewHarness(), fileVersion: "0.11.0", informationalVersion: "0.11.0-dev");

        Assert.Equal("0.11.0-dev+build.1", allocated.InformationalVersion);
    }

    [Fact]
    public void AnInformationalVersionThatAlreadyHasMetadataGetsAFurtherIdentifier()
    {
        // The SDK appends "+<commit sha>" when IncludeSourceRevisionInInformationalVersion
        // is on. SemVer allows one '+' only, so the number joins the existing
        // metadata with a dot — exactly as the SDK itself does when it finds a '+'.
        var allocated = Allocate(NewHarness(), fileVersion: "0.11.0", informationalVersion: "0.11.0-dev+abc1234");

        Assert.Equal("0.11.0-dev+abc1234.build.1", allocated.InformationalVersion);
    }

    [Fact]
    public void AFileVersionThatIsNotAVersionIsLeftAloneAndTheBuildIsWarned()
    {
        // A file version has to parse as one before a component can be replaced.
        // Refusing to guess costs the Explorer half of the feature for that build;
        // guessing would write a version string nobody chose.
        var harness = NewHarness();
        var allocated = Allocate(harness, fileVersion: "nightly", informationalVersion: "0.11.0-dev");

        Assert.Equal("nightly", allocated.FileVersion);
        Assert.Equal("0.11.0-dev+build.1", allocated.InformationalVersion);   // the half that still works
        Assert.Contains("warning", allocated.Output, StringComparison.OrdinalIgnoreCase);
    }

    // ===== the counter file itself =====

    [Fact]
    public void AMissingCounterFileStartsAtOne()
    {
        // Deleting the file is how the owner resets numbering, so a missing file is
        // a normal state, not an error.
        var harness = NewHarness();

        var allocated = Allocate(harness);

        Assert.Equal(1, allocated.Number);
        Assert.Equal("1", File.ReadAllText(harness.CounterFile).Trim());
    }

    [Fact]
    public void ACorruptCounterFileLeavesTheBuildUnnumberedAndWarnsRatherThanFailing()
    {
        // A half-written file (a machine that lost power mid-build) must not stop
        // anyone from building. It must also not be silently reset to 1: that would
        // hand out numbers an older exe already carries.
        var harness = NewHarness();
        File.WriteAllText(harness.CounterFile, "banana");

        var allocated = Allocate(harness);

        Assert.Equal(0, allocated.Number);
        Assert.Equal("0.11.0", allocated.FileVersion);              // unnumbered, as before this feature
        Assert.Equal("0.11.0-dev", allocated.InformationalVersion);
        Assert.Equal("banana", File.ReadAllText(harness.CounterFile).Trim());
        Assert.Contains("warning", allocated.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ACounterAnotherProcessIsHoldingLeavesTheBuildUnnumberedAndWarns()
    {
        // Two builds at once, or a backup tool with the lock file open. Waiting
        // forever would hang the build; failing would stop it; so it gives up after
        // its timeout, says so, and builds.
        var harness = NewHarness();
        using (File.Open(harness.CounterFile + ".lock", FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            var allocated = Allocate(harness, timeoutSeconds: 1);

            Assert.Equal(0, allocated.Number);
            Assert.Contains("warning", allocated.Output, StringComparison.OrdinalIgnoreCase);
        }
        Assert.False(File.Exists(harness.CounterFile), "a build that could not take the lock wrote the counter anyway");
    }

    [Fact]
    public void TheCeilingIsRefusedRatherThanOverflowingTheFileVersion()
    {
        // A file-version component cannot exceed 65534 — the compiler refuses one
        // that does. Wrapping round would re-issue numbers that are already out
        // there on real exes, so the counter stops and says how to restart it.
        var harness = NewHarness();
        File.WriteAllText(harness.CounterFile, "65534");

        var allocated = Allocate(harness);

        Assert.Equal(0, allocated.Number);
        Assert.Equal("65534", File.ReadAllText(harness.CounterFile).Trim());
        Assert.Contains("warning", allocated.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConcurrentBuildsNeverTakeTheSameNumber()
    {
        // Parallel project builds, or two terminals. Read-then-write without a lock
        // hands the same number to two different binaries, which is the one thing
        // this feature must never do.
        var harness = NewHarness();
        const int builds = 5;

        var numbers = Enumerable.Range(0, builds)
            .AsParallel().WithDegreeOfParallelism(builds)
            .Select(i => Allocate(harness, resultName: "result" + i + ".txt").Number)
            .ToList();

        Assert.Equal(builds, numbers.Distinct().Count());
        Assert.DoesNotContain(0, numbers);
        Assert.Equal(builds.ToString(), File.ReadAllText(harness.CounterFile).Trim());
    }

    // ===== the repository says where it lives =====

    [Fact]
    public void TheCounterFileIsGitIgnored()
    {
        // It is per clone and per worktree, so it must never be committed — and the
        // name in .gitignore has to be the name the targets file actually uses.
        var name = CounterFileName();
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
        // The one question the number raises: "where does it come from, and how do I
        // start again?" A number with no documented reset is a number nobody trusts.
        var readme = RepoSources.Read("README.md");
        var build = readme[readme.IndexOf("## Build & run", StringComparison.Ordinal)..];
        build = build[..build.IndexOf("\n## ", StringComparison.Ordinal)];

        Assert.Contains(CounterFileName(), build, StringComparison.Ordinal);
        Assert.Contains("Properties", build, StringComparison.Ordinal);       // where to read it before launching
        Assert.Contains("Help ▸ About", build, StringComparison.Ordinal);     // and after
        Assert.Contains("delete", build, StringComparison.OrdinalIgnoreCase); // how to reset it
    }

    [Fact]
    public void TheAppProjectImportsTheBuildNumberTargets()
    {
        // The targets file only numbers the project that imports it. Imported from
        // the app project rather than a Directory.Build.targets so the test project
        // — built far more often than the app is published — is left alone.
        var csproj = RepoSources.Read("src", "MarkdownMidget", "MarkdownMidget.csproj");

        Assert.Contains("LocalBuildNumber.targets", csproj, StringComparison.Ordinal);
    }

    /// <summary>The counter file's name, read from the targets file's own default so
    /// this suite cannot go on pinning a name the build stopped using.</summary>
    private static string CounterFileName()
    {
        var targets = File.ReadAllText(TargetsFile);
        // The last quoted piece of the default's path expression is the file name:
        //   ...NormalizePath('$(MSBuildThisFileDirectory)..', '.local-build-number'))
        var match = Regex.Match(targets, @"<LocalBuildNumberFile[^>]*>[^<]*'([^']+)'\)*</LocalBuildNumberFile>");
        Assert.True(match.Success, "no default LocalBuildNumberFile in " + TargetsFile);
        return match.Groups[1].Value;
    }

    // ===== harnesses =====

    private string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mm-buildnumber-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        // Nothing above the temp folder gets to steer these builds.
        File.WriteAllText(Path.Combine(dir, "Directory.Build.props"), "<Project />\n");
        File.WriteAllText(Path.Combine(dir, "Directory.Build.targets"), "<Project />\n");
        _dirs.Add(dir);
        return dir;
    }

    private SdkProbe NewSdkProbe(string name = "Probe")
    {
        var dir = NewDir();
        File.WriteAllText(Path.Combine(dir, name + ".csproj"), $"""
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
        return new SdkProbe(dir, name, Path.Combine(dir, "counter"));
    }

    private Harness NewHarness()
    {
        var dir = NewDir();
        // A plain MSBuild project: it imports the real targets file and calls the
        // real task, without an SDK, a restore or a compile.
        File.WriteAllText(Path.Combine(dir, "harness.proj"), $"""
            <Project>
              <Import Project="{TargetsFile}" />
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
            </Project>
            """);
        return new Harness(dir, Path.Combine(dir, "counter"));
    }

    private sealed record SdkProbe(string Dir, string Name, string CounterFile)
    {
        public BuiltVersions Build(bool restore = false, IEnumerable<string>? extra = null,
                                   IDictionary<string, string>? env = null)
        {
            Run(["-t:Build", .. extra ?? []], restore, env);
            var dll = Path.Combine(Dir, "bin", "Debug", "net10.0", "Probe.dll");
            Assert.True(File.Exists(dll), "no built assembly at " + dll);
            var info = FileVersionInfo.GetVersionInfo(dll);
            return new BuiltVersions(info.FileVersion ?? "", info.ProductVersion ?? "");
        }

        public void Run(IEnumerable<string> args, bool restore = false, IDictionary<string, string>? env = null)
        {
            List<string> all = [Path.Combine(Dir, Name + ".csproj"), "-nologo", "-v:m",
                                "-p:LocalBuildNumberFile=" + CounterFile, .. args];
            if (restore) all.Insert(1, "-restore");
            var (exit, output) = Msbuild(Dir, all, env);
            Assert.True(exit == 0, output);
        }
    }

    private sealed record BuiltVersions(string FileVersion, string ProductVersion);

    private sealed record Harness(string Dir, string CounterFile);

    private sealed record Allocated(int Number, string FileVersion, string InformationalVersion, string Output);

    private static Allocated Allocate(Harness harness, string fileVersion = "0.11.0",
                                      string informationalVersion = "0.11.0-dev",
                                      int timeoutSeconds = 15, string resultName = "result.txt")
    {
        var result = Path.Combine(harness.Dir, resultName);
        var (exit, output) = Msbuild(harness.Dir, [
            Path.Combine(harness.Dir, "harness.proj"), "-nologo", "-v:m", "-t:Allocate",
            "-p:LocalBuildNumberFile=" + harness.CounterFile,
            "-p:LocalBuildNumberTimeoutSeconds=" + timeoutSeconds,
            "-p:ProbeFileVersion=" + fileVersion,
            "-p:ProbeInformationalVersion=" + informationalVersion,
            "-p:ProbeResult=" + result,
        ]);
        Assert.True(exit == 0, output);
        var lines = File.ReadAllLines(result);
        Assert.True(lines.Length == 3, "the harness wrote " + lines.Length + " lines:\n" + output);
        return new Allocated(int.Parse(lines[0]), lines[1], lines[2], output);
    }

    private static (int Exit, string Output) Msbuild(string workDir, IEnumerable<string> args,
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
        // Half of these tests are about what a LOCAL build does, and a CI runner
        // exports these to every child process — so each test states its own mode
        // instead of inheriting the machine's.
        foreach (var name in new[] { "GITHUB_ACTIONS", "CI", "TF_BUILD" }) psi.Environment.Remove(name);
        if (env is not null)
            foreach (var pair in env) psi.Environment[pair.Key] = pair.Value;

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        // A hang must fail this test rather than the whole `dotnet test` run.
        Assert.True(process.WaitForExit(300_000), "dotnet msbuild did not finish within five minutes");
        Task.WaitAll(stdout, stderr);
        return (process.ExitCode, stdout.Result + stderr.Result);
    }
}
