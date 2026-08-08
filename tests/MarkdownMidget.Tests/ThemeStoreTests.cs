using System;
using System.IO;
using System.Linq;
using System.Reflection;
using MarkdownMidget.Themes;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>
/// The theme folder. Most of what can go wrong here is not "the wrong colours" —
/// it is a built-in silently reverting to an older version's copy, a user's file
/// being overwritten by ours, a name in settings.json reaching a file it has no
/// business reaching, or a folder we can't write to leaving the app with nothing.
/// </summary>
public class ThemeStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "mm-theme-tests-" + Guid.NewGuid().ToString("N"));

    private static readonly Assembly Res = typeof(ThemeStoreTests).Assembly;

    private ThemeStore New()
    {
        Directory.CreateDirectory(_dir);
        return new ThemeStore(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string Stamp => Path.Combine(_dir, ".version");

    // ===== where the folder lives =====

    [Fact]
    public void AnInstalledBuildKeepsThemesInTheProfile()
    {
        var (root, fellBack) = ThemeStore.ResolveRoot(installed: true, @"C:\somewhere\else", _dir);
        Assert.Equal(Path.Combine(_dir, "MarkdownMidget", "themes"), root);
        Assert.False(fellBack);
    }

    [Fact]
    public void APortableExeKeepsThemesBesideItself()
    {
        // The point of portable: a copy on a stick carries its themes along.
        var exeDir = Path.Combine(_dir, "portable");
        Directory.CreateDirectory(exeDir);

        var (root, fellBack) = ThemeStore.ResolveRoot(installed: false, exeDir, _dir);
        Assert.Equal(Path.Combine(exeDir, "themes"), root);
        Assert.False(fellBack);
    }

    [Fact]
    public void APortableExeSomewhereUnwritableFallsBackRatherThanHavingNoThemes()
    {
        // A read-only share, a CD, a locked-down folder. Starting with no themes at
        // all would be the worse answer, so the profile takes over — and the caller
        // is told, because it has to say so once in the status bar.
        var blocked = Path.Combine(_dir, "blocked");
        // A FILE where the themes directory would go: CreateDirectory fails on it the
        // same way a denied ACL does, without needing to be an administrator to set up.
        Directory.CreateDirectory(blocked);
        File.WriteAllText(Path.Combine(blocked, "themes"), "not a directory");

        var (root, fellBack) = ThemeStore.ResolveRoot(installed: false, blocked, _dir);
        Assert.Equal(Path.Combine(_dir, "MarkdownMidget", "themes"), root);
        Assert.True(fellBack);
    }

    [Fact]
    public void APortableFolderWeCanCreateButNotWriteIntoAlsoFallsBack()
    {
        // Creating a directory and writing a file into it are different permissions,
        // and the pair comes apart more often than it looks: a deny-write ACL on files
        // specifically, a quota, an AV product that allows the mkdir and blocks the
        // write. Probing with a mkdir alone would report success and then fail on the
        // first thing that mattered — so the probe writes, because writing is what we
        // are about to need.
        //
        // Staged with a DIRECTORY sitting where the probe file goes, which fails a
        // file write while leaving the directory perfectly creatable.
        var exeDir = Path.Combine(_dir, "half-writable");
        Directory.CreateDirectory(Path.Combine(exeDir, "themes", ".write-probe"));

        var (root, fellBack) = ThemeStore.ResolveRoot(installed: false, exeDir, _dir);
        Assert.Equal(Path.Combine(_dir, "MarkdownMidget", "themes"), root);
        Assert.True(fellBack);
    }

    // ===== refreshing built-ins =====

    [Fact]
    public void RefreshWritesTheBuiltInsAndStampsTheVersion()
    {
        var store = New();
        Assert.True(store.Refresh("0.7.0", Res));

        Assert.True(File.Exists(Path.Combine(_dir, "apple.css")));
        Assert.True(File.Exists(Path.Combine(_dir, "zebra.css")));
        Assert.Equal("0.7.0", File.ReadAllText(Stamp));
    }

    [Fact]
    public void ARefreshOverwritesABuiltInSoAnUpdateCanFixOne()
    {
        // The whole reason built-ins live outside the exe and are rewritten anyway:
        // a bad colour shipped in 0.7.0 has to be fixable by 0.7.1 without asking the
        // user to delete anything.
        var store = New();
        store.Refresh("0.7.0", Res);
        File.WriteAllText(Path.Combine(_dir, "apple.css"), "/* hand-edited */");

        Assert.True(store.Refresh("0.7.1", Res));
        Assert.DoesNotContain("hand-edited", File.ReadAllText(Path.Combine(_dir, "apple.css")));
    }

    [Fact]
    public void AnOlderExeLeavesANewerVersionsBuiltInsAlone()
    {
        // The portable-thrash case, and the reason the gate compares versions instead
        // of just noticing they differ. A portable update leaves the old exe in place,
        // so both share one themes folder. Run the old one once and — under a
        // different-from-ours rule — it would quietly undo every theme fix the new
        // version delivered. Running last must not mean winning.
        var store = New();
        store.Refresh("0.8.0", Res);
        var marker = Path.Combine(_dir, "apple.css");
        File.WriteAllText(marker, "/* written by 0.8.0 */");

        Assert.False(store.Refresh("0.7.0", Res));
        Assert.Contains("written by 0.8.0", File.ReadAllText(marker));
        Assert.Equal("0.8.0", File.ReadAllText(Stamp));
    }

    [Fact]
    public void TheSameVersionLaunchingTwiceDoesNotRewriteEveryTime()
    {
        var store = New();
        store.Refresh("0.7.0", Res);
        Assert.False(store.Refresh("0.7.0", Res));
    }

    [Fact]
    public void APrereleaseIsOlderThanItsOwnStable()
    {
        // 0.7.0-beta1 < 0.7.0, so the stable build refreshes over the beta's copies
        // and the beta, run afterwards, does not undo it.
        var store = New();
        store.Refresh("0.7.0-beta1", Res);
        Assert.True(store.Refresh("0.7.0", Res));
        Assert.False(store.Refresh("0.7.0-beta1", Res));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a version")]
    public void AnUnreadableStampMeansRefresh(string junk)
    {
        // Fail towards doing the work. A stamp we can't parse tells us nothing about
        // what is in the folder, and shipping the built-ins again is harmless.
        var store = New();
        store.Refresh("0.7.0", Res);
        File.WriteAllText(Stamp, junk);
        Assert.True(store.Refresh("0.7.0", Res));
    }

    [Fact]
    public void AStampWithNoBuiltInsBesideItIsNotEnoughToSkip_WhenTheVersionMoves()
    {
        var store = New();
        File.WriteAllText(Stamp, "0.1.0");
        Assert.True(store.Refresh("0.7.0", Res));
        Assert.True(File.Exists(Path.Combine(_dir, "apple.css")));
    }

    [Fact]
    public void AnExtractionThatFailsHalfwayDoesNotStampTheVersion()
    {
        // The completion-marker rule, and the one that bites on the SECOND run rather
        // than the first: stamp before copying and a half-finished extraction leaves
        // behind a marker saying the folder is current. Nothing ever re-examines it,
        // so a partial set of built-ins becomes permanent — and the failure surfaces
        // as "one of my themes disappeared" weeks later, with no launch left to blame.
        var store = New();
        // A directory where a built-in has to be written: File.Create fails on it, the
        // same way a locked or denied file would, without needing special permissions.
        Directory.CreateDirectory(Path.Combine(_dir, "zebra.css"));

        Assert.False(store.Refresh("0.7.0", Res));
        Assert.False(File.Exists(Stamp));

        // And the repair: with the obstruction gone, the SAME version must still
        // extract. Under a stamp-first rule this second call is the one that does
        // nothing, and the folder stays broken for good.
        Directory.Delete(Path.Combine(_dir, "zebra.css"));
        Assert.True(store.Refresh("0.7.0", Res));
        Assert.True(File.Exists(Path.Combine(_dir, "apple.css")));
        Assert.True(File.Exists(Path.Combine(_dir, "zebra.css")));
    }

    [Fact]
    public void AResourceNameWithASeparatorInItDoesNotEscapeTheFolder()
    {
        // `themes/sub/nested.css` is a resource name, not a path, and combining it
        // would write outside the themes folder. Skipped rather than flattened,
        // because flattening invents a filename nobody chose.
        var store = New();
        store.Refresh("0.7.0", Res);

        Assert.False(Directory.Exists(Path.Combine(_dir, "sub")));
        Assert.False(File.Exists(Path.Combine(_dir, "nested.css")));
    }

    [Fact]
    public void TheStampIsNotAThemeInTheMenu()
        // It has no .css extension, so enumeration passes over it — but that is a
        // property of the name we chose, and worth pinning.
        => Assert.DoesNotContain(NewRefreshed().List(), t => t.Name.Contains("version"));

    // ===== the sample =====

    [Fact]
    public void TheSampleLandsInCustomOnFirstRun()
    {
        var store = NewRefreshed();
        Assert.True(File.Exists(Path.Combine(store.CustomDir, "sample.css")));
        Assert.Contains(store.List(), t => t.IsCustom && t.Name == "Sample");
    }

    [Fact]
    public void TheSampleIsNotWrittenBackOverTheUsersEdits()
    {
        // "Written once" has to survive the user editing it in place — the sample is
        // meant to be copied, but someone will edit it directly, and this folder is
        // the one place the app promises not to touch.
        var store = NewRefreshed();
        var sample = Path.Combine(store.CustomDir, "sample.css");
        File.WriteAllText(sample, "/* mine now */");

        store.Refresh("0.9.0", Res);
        Assert.Equal("/* mine now */", File.ReadAllText(sample));
    }

    [Fact]
    public void TheSampleDoesNotComeBackWhileTheUserHasThemesOfTheirOwn()
    {
        var store = NewRefreshed();
        File.Delete(Path.Combine(store.CustomDir, "sample.css"));
        File.WriteAllText(Path.Combine(store.CustomDir, "mine.css"), ":root { --mdm-text: #333; }");

        store.Refresh("0.9.0", Res);
        Assert.False(File.Exists(Path.Combine(store.CustomDir, "sample.css")));
    }

    [Fact]
    public void TheShippedSampleIsAThemeTheValidatorAccepts()
    {
        // It is the first CSS most people will edit, and an example that the app
        // itself refuses would be a poor introduction.
        using var stream = typeof(ThemeStore).Assembly.GetManifestResourceStream("themes-sample.css");
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        Assert.Null(CssValidator.Validate(reader.ReadToEnd()));
    }

    // ===== the menu =====

    [Fact]
    public void DefaultIsAlwaysThereAndAlwaysFirst()
    {
        // It is not a file — it is the palette already in the bundle — so it survives
        // an empty folder, an unwritable one, and a user who deleted everything.
        var store = new ThemeStore(Path.Combine(_dir, "never-created"));
        var list = store.List();
        Assert.Single(list);
        Assert.Equal("Default", list[0].Name);
        Assert.Null(list[0].Path);
        Assert.True(list[0].IsUsable);
    }

    [Fact]
    public void BuiltInsComeBeforeCustomAndEachGroupIsAlphabetical()
    {
        var store = NewRefreshed();
        File.WriteAllText(Path.Combine(store.CustomDir, "aardvark.css"), ":root { --mdm-text: #333; }");

        var names = store.List().Select(t => t.Name).ToArray();
        Assert.Equal(new[] { "Default", "Apple", "Zebra", "Aardvark", "Sample" }, names);
    }

    [Fact]
    public void ACustomFileWinsANameCollisionWithABuiltIn()
    {
        // Putting apple.css in custom\ is how you say "mine, not yours" — and it has
        // to win, because the built-in copy is overwritten by the next update anyway.
        var store = NewRefreshed();
        File.WriteAllText(Path.Combine(store.CustomDir, "apple.css"), ":root { --mdm-text: #abcdef; }");

        var apples = store.List().Where(t => t.Key.Equals("apple.css", StringComparison.OrdinalIgnoreCase)).ToArray();
        var apple = Assert.Single(apples);
        Assert.True(apple.IsCustom);
        Assert.Contains("#abcdef", File.ReadAllText(apple.Path!));
    }

    [Fact]
    public void ACollisionIsDecidedOnTheNameRegardlessOfCase()
    {
        var store = NewRefreshed();
        File.WriteAllText(Path.Combine(store.CustomDir, "APPLE.CSS"), ":root { --mdm-text: #abcdef; }");
        Assert.Single(store.List().Where(t => t.Name.Equals("Apple", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void AThemeIsAFileInTheFolder_NotATreeToWalk()
    {
        // Recursion looks harmless here — `custom\` is filtered out by the collision
        // rule, which hides the change in the obvious test — so the case that shows it
        // is any OTHER subdirectory. Two things go wrong at once if we walk.
        //
        // The menu key is a bare filename, so two files with the same leaf in
        // different subfolders become one entry and shadow each other by enumeration
        // order. And "the themes folder" stops being a boundary: a junction dropped in
        // it points anywhere, and every .css under wherever that is gets read,
        // validated and offered as a theme. Not walking is what makes the junction
        // uninteresting rather than what makes it safe.
        var store = NewRefreshed();
        Directory.CreateDirectory(Path.Combine(_dir, "archive"));
        File.WriteAllText(Path.Combine(_dir, "archive", "buried.css"), ":root { --mdm-text: #333; }");
        File.WriteAllText(Path.Combine(_dir, "archive", "apple.css"), ":root { --mdm-text: #999999; }");

        var list = store.List();
        Assert.DoesNotContain(list, t => t.Name == "Buried");
        Assert.Contains("#fafafa", File.ReadAllText(list.Single(t => t.Name == "Apple").Path!));
    }

    [Fact]
    public void ABrokenThemeIsListedWithItsReasonRatherThanHidden()
    {
        // Hidden would read as "my theme vanished". Greyed out with a line number
        // reads as "line 2 is wrong", which is the difference between a bug report
        // and a fix.
        var store = NewRefreshed();
        File.WriteAllText(Path.Combine(store.CustomDir, "broken.css"),
            ":root {\n  --mdm-text #333;\n}\n");

        var broken = store.List().Single(t => t.Name == "Broken");
        Assert.False(broken.IsUsable);
        Assert.Contains("line 2", broken.Unusable);
    }

    [Fact]
    public void AThemeThatPhonesHomeIsRefusedInTheMenuNotAtClickTime()
    {
        var store = NewRefreshed();
        File.WriteAllText(Path.Combine(store.CustomDir, "beacon.css"),
            ".x { background-image: url(https://evil.example/a.png); }");

        var beacon = store.List().Single(t => t.Name == "Beacon");
        Assert.False(beacon.IsUsable);
        Assert.Contains("outside the app", beacon.Unusable);
    }

    [Fact]
    public void AThemeTooBigToBeAPaletteIsRefusedWithoutBeingLoaded()
    {
        var store = NewRefreshed();
        var fat = Path.Combine(store.CustomDir, "fat.css");
        File.WriteAllText(fat, new string('/', ThemeStore.MaxBytes + 1));

        var listed = store.List().Single(t => t.Name == "Fat");
        Assert.False(listed.IsUsable);
        Assert.Contains("larger than", listed.Unusable);
    }

    [Fact]
    public void AFileExactlyAtTheCapIsStillATheme()
    {
        // Off-by-one at a boundary nobody would notice until the day a theme is
        // exactly 256 KB, which is the day it would be blamed on something else.
        var store = NewRefreshed();
        var body = "/*" + new string('x', ThemeStore.MaxBytes - 4) + "*/";
        Assert.Equal(ThemeStore.MaxBytes, body.Length);
        File.WriteAllText(Path.Combine(store.CustomDir, "edge.css"), body);

        Assert.True(store.List().Single(t => t.Name == "Edge").IsUsable);
    }

    [Fact]
    public void AUtf16FileIsReadAsTextRatherThanAsNulls()
    {
        // Notepad still offers UTF-16, and a theme saved that way would otherwise be
        // rejected for a reason that says nothing about what is wrong with it.
        var store = NewRefreshed();
        File.WriteAllText(Path.Combine(store.CustomDir, "wide.css"),
            ":root { --mdm-text: #123456; }", new System.Text.UnicodeEncoding(false, true));

        var wide = store.List().Single(t => t.Name == "Wide");
        Assert.True(wide.IsUsable);
        Assert.Contains("#123456", store.Read(wide, out _)!);
    }

    // ===== reading =====

    [Fact]
    public void DefaultReadsAsNothingToInject()
    {
        var store = NewRefreshed();
        var css = store.Read(store.List()[0], out var failure);
        Assert.Equal("", css);
        Assert.Null(failure);
    }

    [Fact]
    public void AThemeIsValidatedAgainAtReadTimeNotJustWhenTheMenuWasBuilt()
    {
        // The submenu is built when it opens and clicked some time later. The file is
        // the user's to change in between — and "it passed when we listed it" is not
        // a statement about the bytes we are about to inject.
        var store = NewRefreshed();
        var path = Path.Combine(store.CustomDir, "swap.css");
        File.WriteAllText(path, ":root { --mdm-text: #333; }");

        var listed = store.List().Single(t => t.Name == "Swap");
        Assert.True(listed.IsUsable);

        File.WriteAllText(path, ".x { background-image: url(https://evil.example/a.png); }");

        Assert.Null(store.Read(listed, out var failure));
        Assert.Contains("outside the app", failure);
    }

    [Fact]
    public void AThemeThatGrewPastTheCapAfterListingIsStillRefused()
    {
        var store = NewRefreshed();
        var path = Path.Combine(store.CustomDir, "grow.css");
        File.WriteAllText(path, ":root { --mdm-text: #333; }");
        var listed = store.List().Single(t => t.Name == "Grow");

        File.WriteAllText(path, new string('/', ThemeStore.MaxBytes + 1));
        Assert.Null(store.Read(listed, out var failure));
        Assert.Contains("larger than", failure);
    }

    [Fact]
    public void AThemeDeletedBetweenListingAndClickingFailsWithAReason()
    {
        var store = NewRefreshed();
        var listed = store.List().Single(t => t.Name == "Apple");
        File.Delete(listed.Path!);

        Assert.Null(store.Read(listed, out var failure));
        Assert.False(string.IsNullOrWhiteSpace(failure));
    }

    // ===== the persisted key =====

    [Fact]
    public void ThePersistedNameIsMatchedAgainstWhatIsThere_NotJoinedOntoAPath()
    {
        // settings.json is a text file the user can edit. If a theme name were
        // combined into a path, this would read a file outside the themes folder and
        // hand its contents to the page. Matching against the enumerated list is the
        // only lookup that cannot be talked into it.
        var store = NewRefreshed();
        foreach (var attack in new[]
        {
            @"..\..\..\Windows\win.ini",
            "../../secrets.css",
            @"C:\Windows\win.ini",
            @"\\server\share\evil.css",
            "apple.css ",           // trailing space: Win32 would trim it, we won't
        })
        {
            Assert.Null(store.Find(attack));
        }
    }

    [Fact]
    public void APersistedNameThatIsGoneFallsBackRatherThanThrowing()
    {
        var store = NewRefreshed();
        Assert.Null(store.Find("deleted-last-week.css"));
        Assert.Equal("Default", store.Find(ThemeStore.DefaultKey)!.Name);
        Assert.Equal("Default", store.Find(null)!.Name);
    }

    [Fact]
    public void APersistedNameMatchesRegardlessOfCase()
        => Assert.Equal("Apple", NewRefreshed().Find("APPLE.CSS")!.Name);

    // ===== display names =====

    [Theory]
    [InlineData("solarized-light.css", "Solarized Light")]
    [InlineData("theme-default.css", "Default")]
    [InlineData("dracula.css", "Dracula")]
    [InlineData("my_own_thing.css", "My Own Thing")]
    [InlineData("GITHUB.css", "GITHUB")]          // already capitalised: left alone
    [InlineData("-.css", "-.css")]                // nothing left to show: use the filename
    public void ThemeNamesComeFromTheFilename(string file, string expected)
        // From the filename and nowhere else. A name declared inside the file would be
        // a second parser over untrusted text — one whose output goes in the menu bar.
        => Assert.Equal(expected, ThemeStore.DisplayName(file));

    private ThemeStore NewRefreshed()
    {
        var store = New();
        store.Refresh("0.7.0", Res);
        return store;
    }
}
