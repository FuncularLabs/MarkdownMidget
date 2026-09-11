using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>
/// File ▸ Windows Integration ▸ Register as .md editor… copies the running exe, on
/// its own, into %LocalAppData%\Programs\MarkdownMidget. A `dotnet build` output's
/// exe is only the .NET apphost: copied without MarkdownMidget.dll beside it, it
/// cannot start, and with Move checked the app handed off to that copy and
/// vanished. These pin the decision that refuses such an exe, the refusal's text,
/// the refusal inside the copy itself, that the text's command and folder match
/// the repository, and that the README tells a developer the same thing first.
///
/// Nothing here touches the real install or the registry: the decision takes its
/// file probe as a parameter, and every install test passes its own destination
/// folder, a temp one.
///
/// Of the copy's refusal, the tests pin the two internal overloads: the one that
/// takes the source, the folder and the probe, against temp folders and injected
/// probes; and the one that takes only the folder and supplies the running exe and
/// File.Exists itself, live against this test run's own exe. The public
/// InstallToAppData() is not called here, since its folder is the real install. It
/// is a one-line delegation to the folder-only overload with the folder as its only
/// argument, and a change to that line fails no test here.
///
/// Not covered: the click handler's own check before the dialog is UI with no seam,
/// so dropping it, or moving it after the dialog, fails no test here; the question it
/// asks, CurrentExeNeedsFilesBesideIt, is the live install test's precondition. What
/// still stands between a dropped check and a broken install is the copy's own
/// refusal, pinned as above up to that one line.
/// </summary>
public class InstallGuardTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mdm-installguard-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* a handle outlived the test */ }
        GC.SuppressFinalize(this);
    }

    // ===== the decision =====

    /// <summary>A file probe over a fixed set of paths that records every path it is asked about.</summary>
    private sealed class Probe
    {
        private readonly HashSet<string> _present;
        public Probe(params string[] present) => _present = new(present, StringComparer.OrdinalIgnoreCase);
        public List<string> Asked { get; } = new();
        public bool Exists(string path) { Asked.Add(path); return _present.Contains(path); }
    }

    [Fact]
    public void NeedsFilesBesideIt_WhenTheAppsDllSitsBesideTheExe()
    {
        var probe = new Probe(@"C:\dev\bin\MarkdownMidget.dll");
        Assert.True(RegistrationService.NeedsFilesBesideIt(@"C:\dev\bin\MarkdownMidget.exe", "MarkdownMidget", probe.Exists));
    }

    [Fact]
    public void DoesNotNeedFilesBesideIt_WhenNoAppDllSitsBesideTheExe()
    {
        // The single-file bundle's shape: the exe alone in its folder, under the
        // release asset's name.
        var exe = @"C:\Downloads\MarkdownMidget-v0.10.0-win-x64-net10.exe";
        Assert.False(RegistrationService.NeedsFilesBesideIt(exe, "MarkdownMidget", new Probe(exe).Exists));
    }

    [Fact]
    public void OnlyTheExesOwnFolderCounts()
    {
        // The app's DLL one level up, or one level down, is not beside the exe.
        var probe = new Probe(@"C:\dev\MarkdownMidget.dll", @"C:\dev\bin\sub\MarkdownMidget.dll");
        Assert.False(RegistrationService.NeedsFilesBesideIt(@"C:\dev\bin\MarkdownMidget.exe", "MarkdownMidget", probe.Exists));
        // And the one path it asked about is the DLL in the exe's own folder.
        Assert.Equal(new[] { @"C:\dev\bin\MarkdownMidget.dll" }, probe.Asked);
    }

    [Fact]
    public void TheDllNameComesFromTheParameter()
    {
        // Not from the exe's file name (a renamed download is the everyday case, and
        // the apphost looks for the ASSEMBLY), and not written into the method.
        var exe = @"C:\dev\bin\renamed.exe";
        Assert.True(RegistrationService.NeedsFilesBesideIt(exe, "Other", new Probe(@"C:\dev\bin\Other.dll").Exists));
        Assert.False(RegistrationService.NeedsFilesBesideIt(exe, "Other",
            new Probe(@"C:\dev\bin\MarkdownMidget.dll", @"C:\dev\bin\renamed.dll").Exists));
    }

    [Theory]
    [InlineData("MarkdownMidget.exe")]
    [InlineData(@"bin\MarkdownMidget.exe")]
    [InlineData(@"C:\")]
    public void APathWithoutAFolderIsRefusedRatherThanGuessed(string exePath)
    {
        // Relative to what? The working directory is not the exe's folder, and a
        // guess would answer for the wrong folder.
        var probe = new Probe();
        Assert.Throws<ArgumentException>(() => RegistrationService.NeedsFilesBesideIt(exePath, "MarkdownMidget", probe.Exists));
        Assert.Empty(probe.Asked);
    }

    [Fact]
    public void AnEmptyAssemblyNameIsRefusedRatherThanProbedAsDotDll()
    {
        // An empty name would probe "<folder>\.dll", find nothing, and let every exe
        // through: the check would fail open.
        var probe = new Probe();
        Assert.Throws<ArgumentException>(
            () => RegistrationService.NeedsFilesBesideIt(@"C:\dev\bin\MarkdownMidget.exe", "", probe.Exists));
        Assert.Empty(probe.Asked);
    }

    [Fact]
    public void ThisTestRunsOwnBuildOutputNeedsFilesBesideIt()
    {
        // LIVE. This folder is a real `dotnet build` output: the test project
        // references the app, so MarkdownMidget.dll sits here beside the app's
        // apphost exe, the exact shape that broke. The app's real assembly name and
        // the real File.Exists must call it what it is.
        var exe = Path.Combine(AppContext.BaseDirectory, "MarkdownMidget.exe");
        Assert.True(RegistrationService.NeedsFilesBesideIt(exe, RegistrationService.AppAssemblyName, File.Exists));
    }

    // ===== what the refusal says =====

    [Fact]
    public void TheRefusalSaysWhatThisIsAndWhatToDoInstead()
    {
        var message = RegistrationService.DevelopmentBuildRefusal;
        Assert.Contains("development build", message);
        Assert.Contains("needs the files beside it", message);
        Assert.Contains("can't be installed on its own", message);
        // The command and folder character for character: a developer copies them.
        // Changing either is a deliberate edit here, and the pins further down check
        // both against the repository.
        Assert.Contains("dotnet publish src/MarkdownMidget/MarkdownMidget.csproj -c Release -p:PublishProfile=win-x64-fxdependent", message);
        Assert.Contains(@"src\MarkdownMidget\bin\Release\publish\framework-dependent\", message);
        Assert.Contains("register from there", message);
        // The pinned parts are the parts the message shows.
        Assert.Contains(RegistrationService.PublishCommand, message);
        Assert.Contains(RegistrationService.PublishedExeFolder, message);
    }

    // ===== the copy refuses too =====

    private string MakeDir(string name) => Directory.CreateDirectory(Path.Combine(_dir, name)).FullName;

    private static string WriteFile(string dir, string name, string content)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>A `dotnet build` output: the apphost, and the app's own DLL beside it.</summary>
    private string BuildOutput()
    {
        var bin = MakeDir("bin");
        WriteFile(bin, RegistrationService.AppAssemblyName + ".dll", "the app");
        return WriteFile(bin, "MarkdownMidget.exe", "apphost");
    }

    [Fact]
    public void InstallingABuildOutputThrowsTheRefusalAndCreatesNothing()
    {
        var source = BuildOutput();
        var installDir = Path.Combine(_dir, "Programs", "MarkdownMidget");   // not there yet

        var ex = Assert.Throws<InvalidOperationException>(
            () => RegistrationService.InstallToAppData(source, installDir, File.Exists));

        Assert.Equal(RegistrationService.DevelopmentBuildRefusal, ex.Message);
        Assert.False(Directory.Exists(installDir));
    }

    [Fact]
    public void ARefusedInstallLeavesTheCopyAlreadyThereUntouched()
    {
        // A working install from a release must survive a developer's click.
        var source = BuildOutput();
        var installDir = MakeDir("install");
        var installed = WriteFile(installDir, "MarkdownMidget.exe", "the release that works");

        Assert.Throws<InvalidOperationException>(
            () => RegistrationService.InstallToAppData(source, installDir, File.Exists));

        Assert.Equal("the release that works", File.ReadAllText(installed));
        Assert.Equal(new[] { installed }, Directory.GetFiles(installDir));
    }

    [Fact]
    public void ABundleShapedExeIsCopiedUnderTheCanonicalName()
    {
        // The release download: one file, under its versioned asset name, with
        // nothing beside it.
        var downloads = MakeDir("Downloads");
        var source = WriteFile(downloads, "MarkdownMidget-v0.10.0-win-x64-net10.exe", "single-file bundle");
        var installDir = Path.Combine(_dir, "Programs", "MarkdownMidget");   // the copy creates it

        var installed = RegistrationService.InstallToAppData(source, installDir, File.Exists);

        Assert.Equal(Path.Combine(installDir, "MarkdownMidget.exe"), installed);
        Assert.Equal("single-file bundle", File.ReadAllText(installed));
        Assert.Equal(new[] { installed }, Directory.GetFiles(installDir));
        Assert.True(File.Exists(source));   // a copy; removing the download is the Move hand-off's business
    }

    [Fact]
    public void RunningTheInstalledCopyIsNotRefusedAndCopiesNothing()
    {
        // No copy happens, so there is nothing to refuse: the installed exe is
        // registered in place, and whatever sits beside it stays beside it.
        var installDir = MakeDir("install");
        var installed = WriteFile(installDir, "MarkdownMidget.exe", "running");
        WriteFile(installDir, RegistrationService.AppAssemblyName + ".dll", "beside it");

        Assert.Equal(installed, RegistrationService.InstallToAppData(installed, installDir, File.Exists));
        Assert.Equal("running", File.ReadAllText(installed));
    }

    [Fact]
    public void TheCopyAsksTheProbeItWasGiven()
    {
        // No DLL on disk, but the probe says there is one: refused. The answer is the
        // injected probe's, not a second look at the disk.
        var downloads = MakeDir("Downloads");
        var source = WriteFile(downloads, "MarkdownMidget.exe", "exe");
        var installDir = Path.Combine(_dir, "install");

        Assert.Throws<InvalidOperationException>(
            () => RegistrationService.InstallToAppData(source, installDir, _ => true));
        Assert.False(Directory.Exists(installDir));
    }

    // ===== the running exe, through the real probe =====

    [Fact]
    public void InstallingThisTestRunsOwnExeThrowsTheRefusalAndCreatesNothing()
    {
        // LIVE. The one install test that leaves the source and the probe to the code:
        // the overload that takes only the folder supplies the running exe and
        // File.Exists itself, and the public method Register calls hands it nothing
        // but the real install folder. Under `dotnet test` the running exe is
        // testhost.exe in this build output, beside the app's DLL: exactly the exe
        // Register must refuse. Only the folder is ours.
        Assert.True(RegistrationService.CurrentExeNeedsFilesBesideIt(),
            $"This test needs the running exe to be a build output, and {RegistrationService.CurrentExePath} " +
            $"has no {RegistrationService.AppAssemblyName}.dll beside it. Under `dotnet test` the running exe is " +
            "testhost.exe in the test output folder, where that DLL is. If tests now start some other way " +
            "(through dotnet.exe, say), this test cannot see the refusal and needs another build output to run; " +
            "if the exe above IS in the test output folder, CurrentExeNeedsFilesBesideIt is broken, and so is " +
            "Register's check before its dialog.");
        var installDir = Path.Combine(_dir, "Programs", "MarkdownMidget");
        Assert.False(Directory.Exists(installDir));

        var ex = Assert.Throws<InvalidOperationException>(() => RegistrationService.InstallToAppData(installDir));

        Assert.Equal(RegistrationService.DevelopmentBuildRefusal, ex.Message);
        Assert.False(Directory.Exists(installDir));
    }

    // ===== the command and folder match the repository =====

    /// <summary>
    /// Found from the test assembly, as <see cref="MenuPathsInDocsTests"/> does: the
    /// working directory of `dotnet test` is not a fixed distance from bin/.
    /// </summary>
    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (dir.EnumerateFiles("MarkdownMidget.sln*").Any())
                return dir.FullName;
        throw new InvalidOperationException($"No MarkdownMidget.sln[x] above {AppContext.BaseDirectory}");
    }

    /// <summary>The project file and publish profile the refusal's command names, read
    /// out of the command itself rather than restated here.</summary>
    private static (string ProjectFile, string ProfileFile) WhatTheCommandNames()
    {
        const string profileSwitch = "-p:PublishProfile=";
        var tokens = RegistrationService.PublishCommand.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var project = Assert.Single(tokens, t => t.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase));
        var profile = Assert.Single(tokens, t => t.StartsWith(profileSwitch, StringComparison.Ordinal))[profileSwitch.Length..];

        var projectFile = Path.Combine(RepoRoot(), project.Replace('/', Path.DirectorySeparatorChar));
        var profileFile = Path.Combine(Path.GetDirectoryName(projectFile)!, "Properties", "PublishProfiles", profile + ".pubxml");
        return (projectFile, profileFile);
    }

    private static string PubxmlProperty(string profileFile, string name) =>
        XDocument.Load(profileFile).Descendants().Single(e => e.Name.LocalName == name).Value.Trim();

    /// <summary>One separator, no trailing one: "a/b/" and "a\b" are the same folder.</summary>
    private static string Normalised(string path) => path.Replace('/', '\\').TrimEnd('\\');

    [Fact]
    public void TheCommandNamesAProjectAndAPublishProfileThatExist()
    {
        var (projectFile, profileFile) = WhatTheCommandNames();

        Assert.True(File.Exists(projectFile), $"The refusal's command names {projectFile}, which isn't there.");
        Assert.True(File.Exists(profileFile), $"The refusal's command names the profile {profileFile}, which isn't there.");
        Assert.Equal(
            Path.Combine(RepoRoot(), "src", "MarkdownMidget", "Properties", "PublishProfiles", "win-x64-fxdependent.pubxml"),
            profileFile);
    }

    [Fact]
    public void TheFolderIsWhereThatProfilePublishes()
    {
        // PublishDir is relative to the project's folder; the message's folder is
        // relative to the repository root, where the command is run.
        var (projectFile, profileFile) = WhatTheCommandNames();
        var projectDir = Path.GetRelativePath(RepoRoot(), Path.GetDirectoryName(projectFile)!);
        var publishDir = PubxmlProperty(profileFile, "PublishDir");

        Assert.Equal(Normalised(Path.Combine(projectDir, publishDir)), Normalised(RegistrationService.PublishedExeFolder));
    }

    [Fact]
    public void ThatProfilePublishesASingleFile()
    {
        // The advice holds only while the profile bundles: a profile that published
        // the DLL beside the exe would be refused by the very check that sends the
        // developer there.
        var (_, profileFile) = WhatTheCommandNames();
        Assert.Equal("true", PubxmlProperty(profileFile, "PublishSingleFile"), ignoreCase: true);
    }

    // ===== the README says it first =====

    [Fact]
    public void TheReadmesBuildSectionSaysToPublishBeforeRegistering()
    {
        // A developer should read this before meeting the refusal, not after.
        var lines = File.ReadAllLines(Path.Combine(RepoRoot(), "README.md"));
        var start = Array.FindIndex(lines, l => l.StartsWith("## Build & run", StringComparison.Ordinal));
        Assert.True(start >= 0, "README has no \"## Build & run\" section.");
        var end = Array.FindIndex(lines, start + 1, l => l.StartsWith("## ", StringComparison.Ordinal));
        // Every run of whitespace becomes one space, so a wrapped sentence reads whole.
        var section = string.Join(" ", string.Join(" ", lines[start..(end < 0 ? lines.Length : end)])
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        Assert.Contains("publish a single-file build first", section);
        Assert.Contains("Register as .md editor", section);
        Assert.Contains("Register refuses a plain `dotnet build`", section);
    }
}
