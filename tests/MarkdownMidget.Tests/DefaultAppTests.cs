using System.ComponentModel;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>DefaultApp, through pure decisions and fakes: no test queries an association, starts a process, or reads the registry or
/// settings. Not covered: the AssocQueryString and Process.Start lines, the notice window, MainWindow's wiring (INST-03, Human).</summary>
public class DefaultAppTests
{
    [Theory]
    [InlineData(null, false, true, true, false, true)]     // an RC1 user: no record yet
    [InlineData(true, false, true, true, false, true)]     // was the default, now isn't
    [InlineData(false, false, true, true, false, false)]   // never was
    [InlineData(true, true, true, true, false, false)]     // still is
    [InlineData(null, false, true, false, false, false)]   // a portable copy
    [InlineData(true, false, true, true, true, false)]     // Don't show this again
    [InlineData(true, false, false, true, false, false)]   // the same version as last run
    public void The_notice_shows_only_when_an_update_of_the_installed_copy_lost_the_default(bool? before, bool now, bool updated, bool installed, bool suppressed, bool expected)
        => Assert.Equal(expected, DefaultApp.ShouldOfferDefault(before, now, updated, installed, suppressed));

    [Theory]   // The startup file is not an input: a plain launch and a clean file launch (Apply update's relaunch, Open with) are one row.
    [InlineData(true, false, false, false, false, "Show")]
    [InlineData(true, false, false, true, false, "Wait")]     // --recover
    [InlineData(true, false, false, false, true, "Wait")]     // unsaved or recovered work
    [InlineData(true, true, false, false, false, "Wait")]     // Help
    [InlineData(true, false, true, false, false, "Wait")]     // a closing window
    [InlineData(false, true, true, true, true, "Record")]     // nothing to say: record, whatever the window
    public void The_notice_shows_over_a_clean_window_and_waits_over_work_Help_or_a_closing_one(bool offer, bool help, bool closing, bool recovering, bool unsaved, string expected)
        => Assert.Equal(expected, DefaultApp.Step(offer, help, closing, recovering, unsaved).ToString());

    [Theory]
    [InlineData(null, null, true)]                                              // none: rc1's Register deleted it
    [InlineData("UserChoice", "MarkdownMidget.Document", true)]
    [InlineData("UserChoice", @"applications\MarkdownMidget-1.0.0-rc1.exe", true)]
    [InlineData("UserChoice", "VSCode.md", false)]                              // the user chose another app
    [InlineData("UserChoiceLatest", "VSCode.md", false)]                        // and the newer key, either shape, wins over an older UserChoice of ours
    [InlineData(@"UserChoiceLatest\ProgId", "VSCode.md", false)]
    [InlineData("UserChoice", @"Applications\mkm.exe", false)]
    public void With_no_record_the_UserChoice_ProgId_says_whether_the_default_was_ours(string? key, string? progId, bool expected)
    {
        const string FileExts = @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.md\";
        var reg = new Recorder();
        if (key is not null) reg.Values[FileExts + key + "|ProgId"] = progId!;
        if (key?.StartsWith("UserChoiceLatest") == true) reg.Values[FileExts + "UserChoice|ProgId"] = "MarkdownMidget.Document";
        Assert.Equal(expected, DefaultApp.DefaultWasOurs(reg));
    }

    [Fact]
    public void One_notice_at_a_time_and_the_next_can_take_it_once_released()
    {
        var name = @"Local\MarkdownMidget.Tests." + Guid.NewGuid().ToString("N");
        Mutex? Elsewhere() { Mutex? m = null; var t = new Thread(() => m = DefaultApp.TryHoldNotice(name)); t.Start(); t.Join(); return m; }   // another window's process; not Task.Run, which can run inline on this owner
        var held = DefaultApp.TryHoldNotice(name);
        Assert.NotNull(held);
        Assert.Null(Elsewhere());
        held.ReleaseMutex(); held.Dispose();
        using var next = Elsewhere();
        Assert.NotNull(next);
    }

    private sealed class Recorder : RegistrationService.IRegistryValues
    {
        public readonly List<(string Key, string Name)> Written = [];
        public readonly Dictionary<string, object> Values = [];
        public object? Get(string key, string name) => Values.GetValueOrDefault(key + "|" + name);
        public void Set(string key, string name, object value) => Written.Add((key, name));
        public void DeleteTree(string key) { }
        public void DeleteValue(string key, string name) { }
        public IEnumerable<string> SubKeyNames(string key) => [];
        public IEnumerable<string> ValueNames(string key) => [];
    }

    [Theory]
    [InlineData(22621, true)]
    [InlineData(22000, false)]   // Windows 11 21H2
    [InlineData(19045, false)]   // Windows 10 22H2
    public void Settings_opens_on_the_app_Register_lists_from_Windows_11_22H2_and_on_Default_apps_before(int build, bool ownPage)
    {
        var reg = new Recorder();
        RegistrationService.Register(@"C:\Users\u\AppData\Local\Programs\MarkdownMidget\MarkdownMidget.exe", reg, () => { });
        var name = Assert.Single(reg.Written, w => w.Key == @"Software\RegisteredApplications").Name;
        Assert.Equal(ownPage ? "ms-settings:defaultapps?registeredAppUser=" + Uri.EscapeDataString(name) : "ms-settings:defaultapps", DefaultApp.SettingsUri(build));
    }

    [Fact]
    public void Make_default_launches_only_when_registered_and_says_so_when_it_cannot()
    {
        List<string> launched = [], told = [];
        DefaultApp.OpenSettings(true, launched.Add, told.Add);
        DefaultApp.OpenSettings(false, launched.Add, told.Add);
        DefaultApp.OpenSettings(true, _ => throw new Win32Exception(1155, "No application is associated with the specified file for this operation"), told.Add);
        Assert.Equal([DefaultApp.SettingsUri(Environment.OSVersion.Version.Build)], launched);
        Assert.Equal([DefaultApp.ButtonTip(registered: false), DefaultApp.LaunchFailed], told);
    }

    [Fact]
    public void The_button_tooltip_says_to_register_first_while_the_button_is_greyed_out()
    {
        Assert.Contains("Register as .md editor", DefaultApp.ButtonTip(registered: false));
        Assert.DoesNotContain("first", DefaultApp.ButtonTip(registered: true));
    }

    [Theory]
    [InlineData(@"c:\users\u\appdata\local\programs\markdownmidget\markdownmidget.exe", true)]
    [InlineData(@"C:\Users\u\AppData\Local\Programs\MarkdownMidget\.\MarkdownMidget.exe", true)]
    [InlineData(@"C:\Users\u\Downloads\MarkdownMidget.exe", false)]
    [InlineData(@"C:\WINDOWS\system32\OpenWith.exe", false)]
    [InlineData(null, false)]
    public void Opens_with_us_compares_full_paths_in_any_case(string? resolved, bool expected)
        => Assert.Equal(expected, DefaultApp.IsSameExe(resolved, @"C:\Users\u\AppData\Local\Programs\MarkdownMidget\MarkdownMidget.exe"));
}
