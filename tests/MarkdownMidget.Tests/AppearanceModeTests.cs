using System;
using System.IO;
using System.Text;
using System.Text.Json;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>
/// View ▸ Mode: Light, Dark or System over Windows' own mode. What regresses here is an
/// override high contrast doesn't beat, a saved value this build doesn't know turning into
/// anything but System, another window's save erasing the pick, and a window that writes
/// settings.json while following a change another window made. Temp files only.
/// </summary>
public sealed class AppearanceModeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mdm-appearance-tests", Guid.NewGuid().ToString("N"));

    public AppearanceModeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* a temp folder */ }
    }

    private string Settings(string json, Encoding? encoding = null)
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, json, encoding ?? new UTF8Encoding(false));
        return path;
    }

    [Theory]
    [InlineData("System", false, false, false)]
    [InlineData("System", true, false, true)]
    [InlineData("System", false, true, false)]
    [InlineData("System", true, true, false)]
    [InlineData("Light", false, false, false)]
    [InlineData("Light", true, false, false)]
    [InlineData("Light", false, true, false)]
    [InlineData("Light", true, true, false)]
    [InlineData("Dark", false, false, true)]
    [InlineData("Dark", true, false, true)]
    [InlineData("Dark", false, true, false)]
    [InlineData("Dark", true, true, false)]
    public void TheEffectiveModeForEveryChoiceWindowsModeAndContrast(string mode, bool windowsDark, bool highContrast, bool dark) =>
        Assert.Equal(dark, AppearanceModes.IsDark(Enum.Parse<AppearanceMode>(mode), windowsDark, highContrast));

    [Theory]
    [InlineData(null, "System")]
    [InlineData("", "System")]
    [InlineData("System", "System")]
    [InlineData("Light", "Light")]
    [InlineData("Dark", "Dark")]
    [InlineData("dark", "Dark")]           // edited by hand
    [InlineData("Auto", "System")]         // a value from a later build
    [InlineData("1", "System")]            // not the enum's number
    [InlineData("Light, Dark", "System")]  // nor a flags list
    [InlineData(" Dark", "System")]
    public void MissingOrUnknownValuesMeanSystem(string? saved, string mode) =>
        Assert.Equal(Enum.Parse<AppearanceMode>(mode), AppearanceModes.Parse(saved));

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    [InlineData("System")]
    public void APickWritesOnlyAppearanceMode(string mode)
    {
        var disk = new MainWindow.AppSettings
        {
            Theme = "Dracula.css", ThemeDark = "Dracula.css", LinkThemes = false, WordWrap = true, RecentLimit = 7,
            WindowLeft = 10, WindowMaximized = true, LastSeenChangelogVersion = "1.0.0-rc2", MdOpensWithUs = true,
            AppearanceMode = "Light",
        };
        var before = JsonSerializer.SerializeToNode(disk)!.AsObject();

        MainWindow.RememberMode(Enum.Parse<AppearanceMode>(mode))(disk);   // what SavePersistentField applies

        var after = JsonSerializer.SerializeToNode(disk)!.AsObject();
        Assert.Equal(mode, (string?)after["AppearanceMode"]);
        before.Remove("AppearanceMode");
        after.Remove("AppearanceMode");
        Assert.Equal(before.ToJsonString(), after.ToJsonString());
    }

    [Fact]
    public void AnotherWindowsSaveKeepsTheModeAsItIsOnDisk()
    {
        // Window B launched before window A picked Dark, and toggles word wrap. B's save
        // restates B's preferences, which never hold the mode, and takes it from disk.
        var save = new MainWindow.AppSettings { WordWrap = true };
        MainWindow.CarryFromDisk(new MainWindow.AppSettings { AppearanceMode = "Dark" }, save);
        Assert.Equal("Dark", save.AppearanceMode);

        MainWindow.CarryFromDisk(new MainWindow.AppSettings(), save);   // never picked: stays unwritten
        Assert.Null(save.AppearanceMode);

        var first = new MainWindow.AppSettings();
        MainWindow.CarryFromDisk(null, first);                          // no file yet
        Assert.Null(first.AppearanceMode);
    }

    [Theory]
    [InlineData("{\"AppearanceMode\":\"Dark\",\"Theme\":\"\"}", "Dark")]
    [InlineData("{\"Theme\":\"Dracula.css\"}", "System")]   // never picked, or saved by rc2, which drops the field
    [InlineData("{\"AppearanceMode\":null}", "System")]
    [InlineData("{\"AppearanceMode\":\"Auto\"}", "System")]
    [InlineData("{\"AppearanceMode\":1}", "System")]
    [InlineData("null", "System")]
    public void ASettingsFileIsReadForItsMode(string json, string mode) =>
        Assert.Equal(Enum.Parse<AppearanceMode>(mode), AppearanceModes.ReadSetting(Settings(json)));

    [Fact]
    public void TheModeAWindowWritesIsTheModeTheOthersRead()
    {
        var saved = new MainWindow.AppSettings();
        MainWindow.RememberMode(AppearanceMode.Light)(saved);
        Assert.Equal(AppearanceMode.Light, AppearanceModes.ReadSetting(Settings(JsonSerializer.Serialize(saved))));

        // Saved from PowerShell with a byte-order mark, as the test plan's steps do.
        Assert.Equal(AppearanceMode.Dark, AppearanceModes.ReadSetting(Settings("{\"AppearanceMode\":\"Dark\"}", new UTF8Encoding(true))));
    }

    [Fact]
    public void NoSettingsFileMeansSystem()
    {
        Assert.Equal(AppearanceMode.System, AppearanceModes.ReadSetting(Path.Combine(_dir, "settings.json")));
        Assert.Equal(AppearanceMode.System, AppearanceModes.ReadSetting(Path.Combine(_dir, "gone", "settings.json")));
    }

    [Fact]
    public void AnUnreadableFileIsNoAnswerAndIsLeftExactlyAsItIs()
    {
        // A launch moves a corrupt settings.json aside. Following another window's change must
        // not: that would be a write, and a file half-way through a hand edit would be lost.
        const string corrupt = "{\"AppearanceMode\":\"Da";
        var path = Settings(corrupt);
        Assert.Null(AppearanceModes.ReadSetting(path));
        Assert.Equal(corrupt, File.ReadAllText(path));
        Assert.False(File.Exists(path + ".bad"));

        var fine = Settings("{\"AppearanceMode\":\"Dark\"}");
        using (new FileStream(fine, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Null(AppearanceModes.ReadSetting(fine));   // locked: no answer, not System
    }

    [Fact]
    public void ASaveWaitsOutAWindowReadingTheSettingsFile()
    {
        // Measured: a reader's open handle, even one sharing delete, makes the replace fail
        // with access denied. Every window reads settings.json 250 ms after each save.
        var target = Settings("{\"AppearanceMode\":\"Light\"}");
        var source = Path.Combine(_dir, "settings.json.1.tmp");
        File.WriteAllText(source, "{\"AppearanceMode\":\"Dark\"}");
        var reader = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var waits = 0;

        MainWindow.ReplaceFile(source, target, () => { if (++waits == 2) reader.Dispose(); });

        Assert.Equal(2, waits);
        Assert.Equal(AppearanceMode.Dark, AppearanceModes.ReadSetting(target));
        Assert.False(File.Exists(source));
    }

    [Fact]
    public void AReplaceThatKeepsFailingGivesUpAfterAFewTries()
    {
        var target = Settings("{}");
        var source = Path.Combine(_dir, "settings.json.1.tmp");
        File.WriteAllText(source, "{}");
        using var reader = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var waits = 0;

        Assert.Throws<UnauthorizedAccessException>(() => MainWindow.ReplaceFile(source, target, () => waits++));
        Assert.Equal(3, waits);
    }

    // ===== the settings watcher: a real FileSystemWatcher on this test's temp folder =====

    private static readonly TimeSpan EventWait = TimeSpan.FromSeconds(10);

    /// <summary>What WriteSettings does: a temporary file, moved over settings.json.</summary>
    private static void Save(string path, string mode)
    {
        var tmp = $"{path}.{Environment.ProcessId}.tmp";
        File.WriteAllText(tmp, "{\"AppearanceMode\":\"" + mode + "\"}");
        MainWindow.ReplaceFile(tmp, path, () => System.Threading.Thread.Sleep(25));
    }

    [Fact]
    public void TheSettingsWatcherSeesEveryWayTheFileChanges()
    {
        // Measured: each step raises only the event named, so each pins one handler.
        var path = Path.Combine(_dir, "settings.json");
        var raised = new System.Threading.ManualResetEventSlim();   // not disposed: a late event may still set it
        using var watcher = WindowsAppearance.WatchSettings(path, raised.Set);
        Assert.NotNull(watcher);

        void Expect(string step, Action change)
        {
            System.Threading.Thread.Sleep(250);   // a late event from the step before
            raised.Reset();
            change();
            Assert.True(raised.Wait(EventWait), step);
        }

        Expect("the first save: Renamed", () => Save(path, "Dark"));
        Expect("a later save: Deleted, then Renamed", () => Save(path, "Light"));
        Expect("written in place: Changed", () => File.WriteAllText(path, "{}"));
        Expect("deleted: Deleted", () => File.Delete(path));
        Expect("created empty: Created", () => File.Create(path).Dispose());
    }

    [Fact]
    public void AWatchedSaveIsFollowedAndNothingIsReadAfterDispose()
    {
        var path = Path.Combine(_dir, "settings.json");
        var reads = 0;
        var settled = new System.Threading.AutoResetEvent(false);   // not disposed, as above
        var appearance = new WindowsAppearance(() => 1, () => false,
            refresh => { refresh(); settled.Set(); },
            () => { System.Threading.Interlocked.Increment(ref reads); return AppearanceModes.ReadSetting(path); });
        appearance.FollowSettingsFile(path);

        Save(path, "Dark");
        // IsDark is the last thing a refresh sets (Mode comes first), so wait on it.
        Assert.True(System.Threading.SpinWait.SpinUntil(() => appearance.IsDark, EventWait));
        Assert.Equal(AppearanceMode.Dark, appearance.Mode);

        appearance.Dispose();
        System.Threading.Thread.Sleep(250);
        settled.Reset();
        var readsBefore = reads;
        Save(path, "Light");
        Assert.False(settled.WaitOne(TimeSpan.FromSeconds(1)));
        Assert.Equal((readsBefore, AppearanceMode.Dark), (reads, appearance.Mode));
    }

    [Theory]
    [InlineData("System", false, "For Windows light mode")]
    [InlineData("System", true, "For Windows dark mode")]
    [InlineData("Light", false, "For light mode")]
    [InlineData("Dark", true, "For dark mode")]
    [InlineData("Dark", false, "For light mode")]   // Dark under high contrast: the light mode's theme
    public void TheThemeCaptionNamesWindowsOnlyUnderSystem(string mode, bool dark, string caption) =>
        Assert.Equal(caption, AppearanceModes.ThemeCaption(Enum.Parse<AppearanceMode>(mode), dark));
}
