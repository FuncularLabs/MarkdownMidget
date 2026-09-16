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

    private sealed class Recorder : RegistrationService.IRegistryValues
    {
        public readonly List<(string Key, string Name)> Written = [];
        public object? Get(string key, string name) => null;
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
