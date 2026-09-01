using System;
using System.IO;
using System.Linq;
using MarkdownMidget.Picker;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>
/// The built-in picker's rules. The dialog is a shell over these, so this is
/// where its behaviour is actually pinned — the same split as CssValidator and
/// SecureUi.
/// </summary>
public class FilePickerModelTests
{
    // ---- filter parsing ----

    [Fact]
    public void ParsesAWin32FilterIntoLabelledGroups()
    {
        var groups = FilePickerModel.ParseFilter(
            "Markdown (*.md;*.markdown)|*.md;*.markdown|All files (*.*)|*.*");
        Assert.Equal(2, groups.Count);
        Assert.Equal("Markdown (*.md;*.markdown)", groups[0].Label);
        Assert.Equal(new[] { "*.md", "*.markdown" }, groups[0].Patterns);
        Assert.True(groups[1].IsCatchAll);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyFilterYieldsNoGroups(string? filter) =>
        Assert.Empty(FilePickerModel.ParseFilter(filter));

    [Fact]
    public void ATrailingLabelWithNoPatternsIsDropped()
    {
        // A half-written filter is a caller bug; inventing a pattern for it would
        // silently show the wrong files.
        var groups = FilePickerModel.ParseFilter("Markdown|*.md|Orphan label");
        Assert.Single(groups);
        Assert.Equal("Markdown", groups[0].Label);
    }

    // ---- matching ----

    [Theory]
    [InlineData("notes.md", true)]
    [InlineData("NOTES.MD", true)]
    [InlineData("notes.markdown", true)]
    [InlineData("notes.txt", false)]
    [InlineData("md", false)]
    public void MatchesExtensionPatternsCaseInsensitively(string name, bool expected)
    {
        var group = FilePickerModel.ParseFilter("Markdown|*.md;*.markdown")[0];
        Assert.Equal(expected, FilePickerModel.MatchesFilter(name, group));
    }

    [Fact]
    public void TheCatchAllGroupAcceptsEverything()
    {
        var group = FilePickerModel.ParseFilter("All files (*.*)|*.*")[0];
        Assert.True(FilePickerModel.MatchesFilter("anything.at.all", group));
        Assert.True(FilePickerModel.MatchesFilter("no-extension", group));
    }

    [Fact]
    public void AnExactNamePatternMatchesOnlyThatName()
    {
        var group = FilePickerModel.ParseFilter("Word dictionary|CUSTOM.DIC")[0];
        Assert.True(FilePickerModel.MatchesFilter("custom.dic", group));
        Assert.False(FilePickerModel.MatchesFilter("other.dic", group));
    }

    // ---- typed paths ----

    [Fact]
    public void ResolvesAbsoluteRelativeAndQuotedPaths()
    {
        var root = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        Assert.Equal(Path.GetFullPath(root), FilePickerModel.ResolveTypedPath(root, @"C:\elsewhere"));
        Assert.Equal(Path.Combine(root, "sub"), FilePickerModel.ResolveTypedPath("sub", root));
        Assert.Equal(Path.Combine(root, "sub"), FilePickerModel.ResolveTypedPath("\"sub\"", root));
    }

    [Fact]
    public void ExpandsEnvironmentVariablesAndTilde()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(Path.GetFullPath(profile), FilePickerModel.ResolveTypedPath("%USERPROFILE%", @"C:\"));
        Assert.Equal(Path.GetFullPath(profile), FilePickerModel.ResolveTypedPath("~", @"C:\"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("what?.md")]     // wildcards and reserved characters are not paths
    [InlineData("a<b")]
    [InlineData("a|b")]
    public void RefusesTextThatCannotBeAPath(string? typed) =>
        Assert.Null(FilePickerModel.ResolveTypedPath(typed, @"C:\"));

    // ---- save extension rules ----

    [Fact]
    public void AddsTheFilterExtensionWhenTheNameHasNone()
    {
        var group = FilePickerModel.ParseFilter("Markdown (*.md)|*.md")[0];
        Assert.Equal("notes.md", FilePickerModel.EnsureExtension("notes", group, null));
    }

    [Fact]
    public void LeavesAnExistingExtensionAlone()
    {
        // Silently rewriting "notes.v2" to "notes.v2.md" is the kind of
        // helpfulness users curse at.
        var group = FilePickerModel.ParseFilter("Markdown (*.md)|*.md")[0];
        Assert.Equal("notes.v2", FilePickerModel.EnsureExtension("notes.v2", group, null));
        Assert.Equal("notes.txt", FilePickerModel.EnsureExtension("notes.txt", group, null));
    }

    [Fact]
    public void AMultiPatternOrCatchAllGroupFallsBackToTheDefaultExtension()
    {
        // "*.md;*.markdown" gives no single right answer, and "*.*" gives none at
        // all — so the caller's DefaultExt decides.
        var multi = FilePickerModel.ParseFilter("Markdown|*.md;*.markdown")[0];
        var all = FilePickerModel.ParseFilter("All files (*.*)|*.*")[0];
        Assert.Equal("notes.md", FilePickerModel.EnsureExtension("notes", multi, ".md"));
        Assert.Equal("notes.md", FilePickerModel.EnsureExtension("notes", all, ".md"));
        Assert.Equal("notes", FilePickerModel.EnsureExtension("notes", all, null));
    }

    [Fact]
    public void AnExtensionWithoutItsDotStillWorks() =>
        Assert.Equal("notes.md", FilePickerModel.EnsureExtension("notes", null, "md"));

    [Fact]
    public void WorksOnTheFULLPATHTheDialogActuallyPassesIt()
    {
        // Accept_Click resolves the typed text to an absolute path FIRST and
        // applies the extension to that — so this, not the bare name, is the
        // production input shape.
        var group = FilePickerModel.ParseFilter("Markdown (*.md)|*.md")[0];
        Assert.Equal(@"C:\docs\notes.md", FilePickerModel.EnsureExtension(@"C:\docs\notes", group, null));
        Assert.Equal(@"C:\docs\notes.txt", FilePickerModel.EnsureExtension(@"C:\docs\notes.txt", group, null));
        // A dot in a DIRECTORY name must not read as the file having an extension.
        Assert.Equal(@"C:\my.folder\notes.md", FilePickerModel.EnsureExtension(@"C:\my.folder\notes", group, null));
    }

    // ---- presentation ----

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1024, "1.0 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1048576, "1.0 MB")]
    public void FormatsSizesLikeExplorer(long bytes, string expected) =>
        Assert.Equal(expected, FilePickerModel.FormatSize(bytes));

    [Fact]
    public void SortsFoldersFirstThenByName()
    {
        Assert.True(FilePickerModel.CompareEntries(true, "zeta", false, "alpha") < 0);
        Assert.True(FilePickerModel.CompareEntries(false, "alpha", true, "zeta") > 0);
        Assert.True(FilePickerModel.CompareEntries(false, "Apple", false, "banana") < 0);
    }

    // ---- type-ahead ----

    [Fact]
    public void TypeAheadWalksEveryMatchAndWraps()
    {
        string[] names = ["alpha", "readme", "notes", "release", "zeta"];
        var first = FilePickerModel.FindByPrefix(names, "re", -1);
        Assert.Equal(1, first);                                     // readme
        var second = FilePickerModel.FindByPrefix(names, "re", first);
        Assert.Equal(3, second);                                    // release
        // ...and back round to the first match rather than stopping.
        Assert.Equal(1, FilePickerModel.FindByPrefix(names, "re", second));
    }

    [Fact]
    public void TypeAheadReportsNoMatch()
    {
        string[] names = ["alpha", "beta"];
        Assert.Equal(-1, FilePickerModel.FindByPrefix(names, "zz", 0));
        Assert.Equal(-1, FilePickerModel.FindByPrefix([], "a", 0));
    }

    // ---- retyping on a filter change ----

    [Fact]
    public void ChangingTheFileTypeRetypesTheName()
    {
        // Not cosmetic: the CALLER decides what to write from the returned
        // path's extension, so a name left as .md after the user picked Secure
        // Markdown would write plaintext into a file they believe is encrypted.
        var mdenc = FilePickerModel.ParseFilter("Secure Markdown (*.mdenc)|*.mdenc")[0];
        Assert.Equal("notes.mdenc", FilePickerModel.RetypeForFilter("notes.md", mdenc));
        Assert.Equal("notes.mdenc", FilePickerModel.RetypeForFilter("notes", mdenc));
    }

    [Fact]
    public void RetypingLeavesANameThatAlreadyFits()
    {
        var mdenc = FilePickerModel.ParseFilter("Secure Markdown (*.mdenc)|*.mdenc")[0];
        Assert.Equal("notes.mdenc", FilePickerModel.RetypeForFilter("notes.mdenc", mdenc));
    }

    [Fact]
    public void RetypingDoesNothingWithoutASingleExtensionToApply()
    {
        // "*.*" has no extension to impose, and "*.md;*.markdown" has no single
        // right answer — in both cases the user's name is left alone.
        var all = FilePickerModel.ParseFilter("All files (*.*)|*.*")[0];
        var multi = FilePickerModel.ParseFilter("Markdown|*.md;*.markdown")[0];
        Assert.Equal("notes.md", FilePickerModel.RetypeForFilter("notes.md", all));
        Assert.Equal("notes.txt", FilePickerModel.RetypeForFilter("notes.txt", multi));
        Assert.Equal("notes.md", FilePickerModel.RetypeForFilter("notes.md", null));
        Assert.Equal("", FilePickerModel.RetypeForFilter("", all));
    }
}

/// <summary>
/// The parent/child command line for the out-of-process native dialog. A drift
/// here silently turns every native pick into a "crash" and permanently
/// switches users to the built-in picker, so it is worth pinning.
/// </summary>
public class PickerChildTests
{
    [Theory]
    [InlineData(new[] { "--pick-open" }, true)]
    [InlineData(new[] { "--pick-save" }, true)]
    [InlineData(new[] { "C:\\doc.md" }, false)]
    [InlineData(new string[0], false)]
    public void RecognisesOnlyPickerInvocations(string[] args, bool expected) =>
        Assert.Equal(expected, PickerChild.IsPickerInvocation(args));

    [Fact]
    public void ParsesEveryArgumentTheServiceSends()
    {
        var request = PickerChild.Parse([
            "--pick-save",
            "--filter", "Markdown (*.md)|*.md",
            "--filter-index", "2",
            "--dir", @"C:\docs",
            "--name", "notes.md",
            "--default-ext", ".md",
            "--title", "Save As",
            "--check-exists",
        ]);
        Assert.True(request.Save);
        Assert.Equal("Markdown (*.md)|*.md", request.Filter);
        Assert.Equal(2, request.FilterIndex);
        Assert.Equal(@"C:\docs", request.InitialDirectory);
        Assert.Equal("notes.md", request.FileName);
        Assert.Equal(".md", request.DefaultExt);
        Assert.Equal("Save As", request.Title);
        Assert.True(request.CheckFileExists);
    }

    [Fact]
    public void OpenIsTheDefaultAndBadValuesDoNotThrow()
    {
        var request = PickerChild.Parse(["--pick-open", "--filter-index", "not-a-number", "--unknown"]);
        Assert.False(request.Save);
        Assert.Equal(1, request.FilterIndex);      // kept, not zeroed
        Assert.Equal("", request.Filter);
    }

    [Fact]
    public void ASwitchMissingItsValueAtTheEndIsIgnoredRatherThanFatal() =>
        Assert.Null(PickerChild.Parse(["--pick-open", "--name"]).FileName);
}
