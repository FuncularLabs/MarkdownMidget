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

    [Theory]
    [InlineData(@"\\server\share\Beta", DriveType.Fixed, true, false, false, "")]
    [InlineData(@"Z:\Work", DriveType.Network, true, false, false, "type")]
    [InlineData(@"E:\Stick", DriveType.NoRootDirectory, true, false, false, "type")]
    [InlineData(@"E:\Stick", DriveType.Removable, false, false, false, "type ready")]
    [InlineData(@"C:\Kept", DriveType.Fixed, true, true, false, "type ready exists")]
    [InlineData(@"C:\Gone", DriveType.Fixed, true, false, true, "type ready exists")]
    [InlineData(@"E:\Gone", DriveType.Removable, true, false, true, "type ready exists")]
    public void CleanupForgetsOnlyAMissingFolderOnAReadyLocalDrive(string folder, DriveType type, bool ready, bool exists, bool forget, string probes)
    {
        var asked = new List<string>();
        var gone = PickerViews.Gone(folder,
            root => { asked.Add("type"); return type; },
            root => { asked.Add("ready"); return ready; },
            path => { asked.Add("exists"); return exists; });
        Assert.Equal(forget, gone);
        Assert.Equal(probes, string.Join(" ", asked));   // a UNC path is never probed at all
    }

    [Fact]
    public void AProbeThatFailsKeepsTheEntry() =>
        Assert.False(PickerViews.Gone(@"C:\Odd", _ => DriveType.Fixed, _ => true, _ => throw new IOException("device not ready")));

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
