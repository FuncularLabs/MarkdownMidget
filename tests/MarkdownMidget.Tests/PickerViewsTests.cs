using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using MarkdownMidget.Picker;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>
/// The built-in picker remembers its sort and column widths (#12, item 4): the first change
/// anywhere becomes the default, a later one belongs to its folder. Every file here is in a
/// temp folder; nothing reads or writes the real %LocalAppData%\MarkdownMidget.
/// </summary>
public sealed class PickerViewsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mdm-views-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime T0 = new(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string ViewsPath => Path.Combine(_dir, PickerViewStore.FileName);

    private static PickerView View(PickerColumn column, bool descending = false, double nameWidth = 250) =>
        new(new PickerSort(column, descending), [nameWidth, 130, 110, 70]);

    private static void Same(PickerView expected, PickerView? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.Sort, actual!.Sort);
        Assert.Equal(expected.Widths, actual.Widths);
    }

    // ---- the two layers ----

    [Fact]
    public void WithNothingSavedEveryFolderGetsTheBuiltInDefault() =>
        Assert.Null(PickerViews.Resolve(new PickerViewsFile(), @"C:\Projects\Alpha"));

    [Fact]
    public void TheFirstChangeAnywhereBecomesHowEveryFolderOpens()
    {
        var file = new PickerViewsFile();
        PickerViews.Apply(file, @"C:\Projects\Alpha", View(PickerColumn.Modified, true), T0);
        Assert.Empty(file.Folders);
        Same(View(PickerColumn.Modified, true), PickerViews.Resolve(file, @"C:\Projects\Alpha"));
        Same(View(PickerColumn.Modified, true), PickerViews.Resolve(file, @"D:\Elsewhere"));
    }

    [Fact]
    public void ALaterChangeBelongsToItsFolderOnly()
    {
        var file = new PickerViewsFile();
        PickerViews.Apply(file, @"C:\Projects\Alpha", View(PickerColumn.Modified, true), T0);
        PickerViews.Apply(file, @"C:\Projects\Alpha", View(PickerColumn.Size, nameWidth: 400), T0);
        Same(View(PickerColumn.Size, nameWidth: 400), PickerViews.Resolve(file, @"C:\Projects\Alpha"));
        Same(View(PickerColumn.Modified, true), PickerViews.Resolve(file, @"C:\Projects\Beta"));
    }

    [Fact]
    public void AFolderIsOneEntryHoweverItsPathIsSpelt()
    {
        var file = new PickerViewsFile();
        PickerViews.Apply(file, @"C:\Seed", View(PickerColumn.Name), T0);
        PickerViews.Apply(file, @"C:\Projects\Alpha\", View(PickerColumn.Size), T0);
        PickerViews.Apply(file, @"c:\projects\beta\..\ALPHA", View(PickerColumn.Type), T0);
        Assert.Single(file.Folders);
        Same(View(PickerColumn.Type), PickerViews.Resolve(file, @"C:/Projects/Alpha"));
    }

    [Fact]
    public void ThreeHundredFoldersAreKeptAndTheLeastRecentlyUsedGoesFirst()
    {
        var file = new PickerViewsFile();
        PickerViews.Apply(file, @"C:\Seed", View(PickerColumn.Name), T0);
        for (var i = 0; i < PickerViews.MaxFolders; i++)
            PickerViews.Apply(file, $@"C:\F{i}", View(PickerColumn.Size), T0.AddMinutes(i));
        // F0 is the oldest change, but opening it two days on makes F1 the least recently used.
        Assert.True(PickerViews.Touch(file, @"C:\F0", T0.AddDays(2)));
        PickerViews.Apply(file, @"C:\New", View(PickerColumn.Type), T0.AddDays(2));
        Assert.Equal(PickerViews.MaxFolders, file.Folders.Count);
        Assert.NotNull(PickerViews.Resolve(file, @"C:\F0"));
        Assert.DoesNotContain(file.Folders, e => e.Folder == @"C:\F1");
        Assert.Contains(file.Folders, e => e.Folder == @"C:\New");
    }

    [Fact]
    public void OpeningARememberedFolderMarksItUsedAtMostOnceADay()
    {
        var file = new PickerViewsFile();
        PickerViews.Apply(file, @"C:\Seed", View(PickerColumn.Name), T0);
        PickerViews.Apply(file, @"C:\Alpha", View(PickerColumn.Size), T0);
        Assert.False(PickerViews.Touch(file, @"C:\Alpha", T0.AddHours(23)));   // nothing worth a write
        Assert.True(PickerViews.Touch(file, @"C:\Alpha", T0.AddHours(25)));
        Assert.Equal(T0.AddHours(25), file.Folders[0].Used);
        Assert.False(PickerViews.Touch(file, @"C:\Beta", T0.AddDays(9)));     // the default isn't a folder's
    }

    // ---- the file ----

    [Fact]
    public void EveryViewSurvivesAWriteAndARead()
    {
        var file = new PickerViewsFile();
        PickerViews.Apply(file, @"C:\Seed", View(PickerColumn.Modified, true, 300), T0);
        PickerViews.Apply(file, @"\\server\share\Beta", View(PickerColumn.Type, nameWidth: 180.5), T0);
        var back = PickerViews.Parse(PickerViews.Serialize(file));
        Same(View(PickerColumn.Modified, true, 300), PickerViews.Resolve(back, @"C:\Anywhere"));
        Same(View(PickerColumn.Type, nameWidth: 180.5), PickerViews.Resolve(back, @"\\server\share\Beta"));
        Assert.Equal(T0, back.Folders[0].Used);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"Default\":")]
    [InlineData("null")]
    [InlineData("[1,2]")]
    public void TextThatIsNotAViewsFileReadsAsEmpty(string text)
    {
        var file = PickerViews.Parse(text);
        Assert.Null(file.Default);
        Assert.Empty(file.Folders);
    }

    [Fact]
    public void AnEntryThatMakesNoSenseIsDroppedAndTheRestAreKept()
    {
        const string text = """
            {"Version":1,"Default":{"Column":"Bogus","Descending":false,"Widths":[1,2,3,4]},"Folders":[
             {"Folder":"C:\\Ok","Column":"Size","Descending":true,"Widths":[250,130,110,70],"Used":"2026-10-08T12:00:00Z"},
             {"Folder":"c:\\OK","Column":"Type","Widths":[250,130,110,70],"Used":"2026-10-09T12:00:00Z"},
             {"Folder":"C:\\ThreeWidths","Column":"Size","Widths":[250,130,110]},
             {"Folder":"C:\\Negative","Column":"Size","Widths":[-1,130,110,70]},
             {"Folder":"","Column":"Size","Widths":[250,130,110,70]},
             {"Column":"Size","Widths":[250,130,110,70]}]}
            """;
        var file = PickerViews.Parse(text);
        Assert.Null(file.Default);
        var kept = Assert.Single(file.Folders);   // one folder written twice: the newer stands
        Assert.Equal((@"c:\OK", "Type"), (kept.Folder, kept.Column));
    }

    [Fact]
    public void ChangesFromTwoWindowsAreBothKeptAndTheLastWriterWinsAnEntry()
    {
        var one = new PickerViewStore(_dir);
        var two = new PickerViewStore(_dir);
        Assert.True(one.Update(f => PickerViews.Apply(f, @"C:\Seed", View(PickerColumn.Name), T0)));
        Assert.True(one.Update(f => PickerViews.Apply(f, @"C:\Alpha", View(PickerColumn.Size), T0)));
        Assert.True(two.Update(f => PickerViews.Apply(f, @"C:\Beta", View(PickerColumn.Type), T0)));
        Assert.True(two.Update(f => PickerViews.Apply(f, @"C:\Alpha", View(PickerColumn.Modified, true), T0)));
        var file = one.Load();
        Same(View(PickerColumn.Modified, true), PickerViews.Resolve(file, @"C:\Alpha"));
        Same(View(PickerColumn.Type), PickerViews.Resolve(file, @"C:\Beta"));
    }

    [Fact]
    public void WritersAtTheSameTimeLoseNoEntry()
    {
        new PickerViewStore(_dir).Update(f => PickerViews.Apply(f, @"C:\Seed", View(PickerColumn.Name), T0));
        Parallel.For(0, 6, w =>
        {
            var store = new PickerViewStore(_dir);   // one per window: each is its own process
            for (var i = 0; i < 10; i++)
                Assert.True(store.Update(f => PickerViews.Apply(f, $@"C:\W{w}\F{i}", View(PickerColumn.Size), T0)));
        });
        Assert.Equal(60, new PickerViewStore(_dir).Load().Folders.Count);
    }

    [Fact]
    public void ACorruptFileIsRebuiltNotThrown()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(ViewsPath, "{\"Folders\": [ oops");
        var store = new PickerViewStore(_dir);
        Assert.Null(store.Load().Default);
        Assert.True(store.Update(f => PickerViews.Apply(f, @"C:\Seed", View(PickerColumn.Size), T0)));
        Same(View(PickerColumn.Size), PickerViews.Resolve(PickerViews.Parse(File.ReadAllText(ViewsPath)), @"C:\X"));
    }

    [Fact]
    public void AFileThatCannotBeReadIsLeftAloneAndNothingThrows()
    {
        var store = new PickerViewStore(_dir, _ => { });
        store.Update(f => PickerViews.Apply(f, @"C:\Seed", View(PickerColumn.Size), T0));
        var before = File.ReadAllText(ViewsPath);
        using (new FileStream(ViewsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Null(store.Load().Default);
            Assert.False(store.Update(f => PickerViews.Apply(f, @"C:\Alpha", View(PickerColumn.Type), T0)));
        }
        Assert.Equal(before, File.ReadAllText(ViewsPath));
        // Denied reading but not replacing: only the failed read can stop the write.
        var file = new FileInfo(ViewsPath);
        var deny = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ReadData, AccessControlType.Deny);
        var acl = file.GetAccessControl();
        acl.AddAccessRule(deny);
        file.SetAccessControl(acl);
        Assert.False(store.Update(f => PickerViews.Apply(f, @"C:\Alpha", View(PickerColumn.Type), T0)));
        acl.RemoveAccessRule(deny);
        file.SetAccessControl(acl);
        Assert.Equal(before, File.ReadAllText(ViewsPath));
    }

    [Fact]
    public void AReplaceBlockedForAMomentIsTriedAgain()
    {
        var holder = (FileStream?)null;
        var store = new PickerViewStore(_dir, _ => { holder?.Dispose(); holder = null; });
        store.Update(f => PickerViews.Apply(f, @"C:\Seed", View(PickerColumn.Size), T0));
        // Readable, but no delete share: the move over it fails until the wait lets go.
        holder = new FileStream(ViewsPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        Assert.True(store.Update(f => PickerViews.Apply(f, @"C:\Alpha", View(PickerColumn.Type), T0)));
        Assert.Null(holder);
        Same(View(PickerColumn.Type), PickerViews.Resolve(store.Load(), @"C:\Alpha"));
    }

    [Fact]
    public async Task ChangesQueuedFromTheWindowLandInOrder()
    {
        // The first change finds the lock taken and waits; the second is queued during that wait
        // and given half a second to get in first. Queued behind the first, it can't.
        new PickerViewStore(_dir).Update(f => PickerViews.Apply(f, @"C:\Seed", View(PickerColumn.Name), T0));
        var held = new FileStream(ViewsPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var secondApplied = new ManualResetEventSlim();
        PickerViewStore? store = null;
        Task? second = null;
        store = new PickerViewStore(_dir, _ =>
        {
            if (second is not null) return;
            second = store!.UpdateLater(f => { PickerViews.Apply(f, @"C:\Alpha", View(PickerColumn.Size), T0); secondApplied.Set(); });
            held.Dispose();
            secondApplied.Wait(500);
        });
        await store.UpdateLater(f => PickerViews.Apply(f, @"C:\Alpha", View(PickerColumn.Type), T0));
        await second!;
        Same(View(PickerColumn.Size), PickerViews.Resolve(store.Load(), @"C:\Alpha"));
    }

    // ---- cleanup ----

    /// <summary>A stand-in file system: the paths that exist, with their attributes; a path in
    /// <paramref name="denied"/> can't be looked at. Logs each probe.</summary>
    private static bool Gone(string folder, DriveType type, bool ready, List<string> asked, string denied = "", params (string Path, FileAttributes Attributes)[] existing) =>
        PickerViews.Gone(folder,
            root => { asked.Add("type"); return type; },
            root => { asked.Add("ready"); return ready; },
            path =>
            {
                asked.Add(path);
                if (path == denied) throw new UnauthorizedAccessException("Access to the path is denied.");
                return existing.FirstOrDefault(e => e.Path == path) is { Path: not null } hit ? hit.Attributes : null;
            });

    [Theory]
    [InlineData(@"\\server\share\Beta", DriveType.Fixed, true, false, "")]
    [InlineData(@"Z:\Work", DriveType.Network, true, false, "type")]
    [InlineData(@"E:\Stick", DriveType.NoRootDirectory, true, false, "type")]
    [InlineData(@"E:\Stick", DriveType.Removable, false, false, "type ready")]
    [InlineData(@"C:\Kept", DriveType.Fixed, true, false, @"type ready C:\Kept")]
    [InlineData(@"C:\Gone", DriveType.Fixed, true, true, @"type ready C:\Gone")]
    [InlineData(@"E:\Gone", DriveType.Removable, true, true, @"type ready E:\Gone")]
    public void CleanupForgetsOnlyAMissingFolderOnAReadyLocalDrive(string folder, DriveType type, bool ready, bool forget, string probes)
    {
        var asked = new List<string>();
        Assert.Equal(forget, Gone(folder, type, ready, asked, "", (@"C:\Kept", FileAttributes.Directory)));
        Assert.Equal(probes, string.Join(" ", asked));   // a UNC path is never probed at all
    }

    [Fact]
    public void CleanupWalksDownToTheFolderAndKeepsItBehindALinkOrAFolderItCannotRead()
    {
        var asked = new List<string>();
        var link = (@"C:\Link", FileAttributes.Directory | FileAttributes.ReparsePoint);
        Assert.False(Gone(@"C:\Link\Gone", DriveType.Fixed, true, asked, "", link));          // a junction, symlink or mount
        Assert.False(Gone(@"C:\Denied\Inner", DriveType.Fixed, true, asked, @"C:\Denied"));   // access denied is not gone
        Assert.DoesNotContain(@"C:\Link\Gone", asked);
        asked.Clear();
        Assert.True(Gone(@"C:\Parent\Gone", DriveType.Fixed, true, asked));                   // missing from the first step down
        Assert.Equal(["type", "ready", @"C:\Parent"], asked);
    }

    [Fact]
    public void AProbeThatFailsKeepsTheEntry() =>
        Assert.False(PickerViews.Gone(@"C:\Odd", _ => DriveType.Fixed, _ => true, _ => throw new IOException("The device is not ready.")));

    [Fact]
    public void ANewerVersionsFileIsNeitherReadNorOverwritten()
    {
        Directory.CreateDirectory(_dir);
        const string newer = """{"Version":2,"Default":{"Column":"Size","Widths":[250,130,110,70]},"Folders":[]}""";
        File.WriteAllText(ViewsPath, newer);
        var store = new PickerViewStore(_dir, _ => { });
        Assert.Null(store.Load().Default);
        Assert.False(store.Update(f => PickerViews.Apply(f, @"C:\Alpha", View(PickerColumn.Type), T0)));
        Assert.Equal(newer, File.ReadAllText(ViewsPath));
    }

    [Theory]
    [InlineData("""{"Version":2,"Default":{"Column":"Size","Widths":{"Name":250}}}""")]   // shapes version 1 can't parse
    [InlineData("""{"Version":2,"Folders":{"C:\\A":{"Column":"Size"}}}""")]
    [InlineData("""{"Version":"2.0","Folders":[]}""")]
    [InlineData("""{"Version":1.5,"Folders":[]}""")]
    [InlineData("""{"Folders":[]}""")]   // every file this code writes says "Version":1, so this one is someone else's
    public void AFileThatIsNotVersion1IsRefusedBeforeItIsReadWhateverItsShape(string text)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(ViewsPath, text);
        var store = new PickerViewStore(_dir, _ => { });
        Assert.Null(store.Load().Default);
        Assert.False(store.Update(f => PickerViews.Apply(f, @"C:\Alpha", View(PickerColumn.Type), T0)));
        Assert.Equal(text, File.ReadAllText(ViewsPath));
    }

    [Fact]
    public void AnEmptyFileIsStartedAgain()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(ViewsPath, "");
        Assert.True(new PickerViewStore(_dir).Update(f => PickerViews.Apply(f, @"C:\Alpha", View(PickerColumn.Type), T0)));
    }

    [Fact]
    public void FolderViewsWithoutADefaultMeanTheFirstChangeWasMadeAndAChangeStillBelongsToItsFolder()
    {
        // A default dropped as unreadable, its folders kept: the next change is not a new default.
        var file = PickerViews.Parse("""{"Default":{"Column":"Bogus"},"Folders":[{"Folder":"C:\\Alpha","Column":"Size","Widths":[250,130,110,70]}]}""");
        PickerViews.Apply(file, @"C:\Beta", View(PickerColumn.Type), T0);
        Assert.Null(file.Default);
        Same(View(PickerColumn.Type), PickerViews.Resolve(file, @"C:\Beta"));
        Assert.Null(PickerViews.Resolve(file, @"C:\Gamma"));   // the built-in view
    }

    [Fact]
    public async Task TheWindowsCopyBecomesTheFileAsWrittenWhereTheDefaultWasDecided()
    {
        // The window's copy had no default; another window set one meanwhile. On disk the change
        // belongs to its folder, and the written file comes back for the window to take.
        new PickerViewStore(_dir).Update(f => PickerViews.Apply(f, @"C:\Other", View(PickerColumn.Size), T0));
        PickerViewsFile? written = null;
        await new PickerViewStore(_dir).UpdateLater(f => PickerViews.Apply(f, @"C:\Alpha", View(PickerColumn.Type), T0), f => written = f);
        Same(View(PickerColumn.Size), PickerViews.Resolve(written!, @"C:\Elsewhere"));   // the other window's default
        Same(View(PickerColumn.Type), PickerViews.Resolve(written, @"C:\Alpha"));
        var failed = false;
        await new PickerViewStore(Path.Combine(_dir, "\0bad"), _ => { }).UpdateLater(_ => { }, _ => failed = true);
        Assert.False(failed);   // nothing written, nothing handed back
    }

    [Fact]
    public async Task CleanupRunsOnceAndKeepsAnEntryAnotherWindowChangedMeanwhile()
    {
        var store = new PickerViewStore(_dir);
        store.Update(f =>
        {
            PickerViews.Apply(f, @"C:\Seed", View(PickerColumn.Name), T0);
            foreach (var folder in new[] { @"C:\GoneA", @"C:\Here", @"C:\GoneC" }) PickerViews.Apply(f, folder, View(PickerColumn.Size), T0);
        });
        var other = new PickerViewStore(_dir);
        await store.CleanOnce(folder =>
        {
            // Another window saves C again while cleanup is still looking at the folders.
            if (folder == @"C:\GoneC") other.Update(f => PickerViews.Apply(f, @"C:\GoneC", View(PickerColumn.Type), T0.AddHours(1)));
            return folder != @"C:\Here";
        });
        Assert.Equal([@"C:\Here", @"C:\GoneC"], store.Load().Folders.Select(e => e.Folder));
        var probedAgain = false;
        await store.CleanOnce(_ => probedAgain = true);
        Assert.False(probedAgain);
    }
}
