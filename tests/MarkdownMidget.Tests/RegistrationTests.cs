using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>Installed updates and repeat Registers run Register again: it must write only what is missing
/// or different, in place, and delete nothing, or Windows resets the user's .md default. All against a fake
/// registry. Not covered: the one-line public wrappers, and the stray cleanup Unregister runs for real.</summary>
public class RegistrationTests
{
    private const string Exe = @"C:\Users\u\AppData\Local\Programs\MarkdownMidget\MarkdownMidget.exe";
    private const string Command = $"\"{Exe}\" \"%1\"", Icon = $"\"{Exe}\",0", Classes = @"Software\Classes\";

    private sealed class FakeRegistry : RegistrationService.IRegistryValues
    {
        public Dictionary<string, object> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Log { get; } = new();
        public int Notified;
        public void Register(string exe) => RegistrationService.Register(exe, this, () => Notified++);
        // A new array per read, as the real registry returns for REG_NONE (ToArray would reuse Array.Empty).
        public object? Get(string key, string name) => Values.GetValueOrDefault(key + "|" + name) is byte[] b ? b.Clone() : Values.GetValueOrDefault(key + "|" + name);
        public void Set(string key, string name, object value) { Log.Add("set " + key + "|" + name); Values[key + "|" + name] = value; }
        public void DeleteValue(string key, string name) { Log.Add("delete " + key + "|" + name); Values.Remove(key + "|" + name); }
        public IEnumerable<string> ValueNames(string key) => Values.Keys.Where(k => k.StartsWith(key + "|", StringComparison.OrdinalIgnoreCase)).Select(k => k[(key.Length + 1)..]);
        public IEnumerable<string> SubKeyNames(string key) => Values.Keys.Where(k => k.StartsWith(key + @"\", StringComparison.OrdinalIgnoreCase))
            .Select(k => k[(key.Length + 1)..].Split('\\', '|')[0]).Distinct(StringComparer.OrdinalIgnoreCase);
        public void DeleteTree(string key)
        {
            Log.Add("delete " + key);
            foreach (var k in Values.Keys.Where(k => k.StartsWith(key + "|", StringComparison.OrdinalIgnoreCase) || k.StartsWith(key + @"\", StringComparison.OrdinalIgnoreCase)).ToList()) Values.Remove(k);
        }
    }

    [Fact]
    public void A_repeat_registration_writes_deletes_and_notifies_nothing()
    {
        var reg = new FakeRegistry();
        reg.Register(Exe);
        Assert.Equal($"\"{Exe}\" \"%1\"", reg.Values[@"Software\Classes\MarkdownMidget.Document\shell\open\command|"]);
        reg.Log.Clear();
        reg.Register(Exe);
        Assert.Empty(reg.Log);
        Assert.Equal(1, reg.Notified);
    }

    [Fact]
    public void An_update_to_a_new_exe_path_rewrites_only_the_path_values_in_place()
    {
        var reg = new FakeRegistry();
        reg.Register(@"D:\old\MarkdownMidget.exe");
        var keys = reg.Values.Keys.ToList(); reg.Log.Clear();
        reg.Register(Exe);
        Assert.Equal(Enumerable.Repeat(true, 6), reg.Log.Select(l => l.StartsWith("set ")));   // command and icon: both ProgIDs, Applications
        Assert.Equal(keys, reg.Values.Keys.ToList());
        Assert.DoesNotContain(reg.Values.Values, v => v is string s && s.Contains(@"D:\old"));
        Assert.Equal(2, reg.Notified);
    }

    [Fact]
    public void Registration_keeps_a_users_default_and_entries_older_versions_left()
    {
        var reg = new FakeRegistry();
        reg.Values[@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.md\UserChoice|ProgId"] = @"Applications\MarkdownMidget.exe";
        reg.Values[@"Software\Classes\MarkdownMidget.Document|"] = "Markdown Document";
        var before = reg.Values.ToList();
        reg.Register(Exe);
        Assert.All(before, kv => Assert.Equal(kv.Value, reg.Values[kv.Key]));
        Assert.DoesNotContain(reg.Log, l => l.StartsWith("delete ") || l == @"set Software\Classes\MarkdownMidget.Document|");
    }

    [Fact]
    public void Unregister_still_removes_everything_registration_wrote()
    {
        var reg = new FakeRegistry();
        reg.Register(Exe);
        RegistrationService.Unregister(reg);
        Assert.Equal([@"Software\Classes\.mdenc|"], reg.Values.Keys);   // its default, reset to empty
        Assert.Equal("", reg.Values[@"Software\Classes\.mdenc|"]);
    }

    [Fact]
    public void A_first_registration_writes_exactly_these_24_values_and_kinds()
    {
        var reg = new FakeRegistry(); reg.Register(Exe);
        const string Apps = Classes + @"Applications\MarkdownMidget.exe", Caps = @"Software\Funcular Labs\Markdown Midget\Capabilities";
        List<string> expected = new([$"{Apps}|FriendlyAppName=SZ Markdown Midget", $@"{Apps}\SupportedTypes|.md=SZ ", $@"{Apps}\SupportedTypes|.mdenc=SZ ",
            $@"{Apps}\shell\open\command|=SZ {Command}", $@"{Apps}\DefaultIcon|=SZ {Icon}", $@"{Classes}.md\OpenWithProgids|MarkdownMidget.Document=NONE",
            $@"{Classes}.mdenc|=SZ MarkdownMidget.SecureDocument", $@"{Classes}.mdenc\OpenWithProgids|MarkdownMidget.SecureDocument=NONE",
            $"{Caps}|ApplicationName=SZ Markdown Midget", $"{Caps}|ApplicationDescription=SZ WYSIWYG markdown editor — WordPad-style editing with markdown as the native format.",
            $@"{Caps}\FileAssociations|.md=SZ MarkdownMidget.Document", $@"{Caps}\FileAssociations|.markdown=SZ MarkdownMidget.Document",
            $@"{Caps}\FileAssociations|.mdenc=SZ MarkdownMidget.SecureDocument", $@"Software\RegisteredApplications|Markdown Midget=SZ {Caps}"]);
        foreach (var (pid, type) in new[] { ("MarkdownMidget.Document", "Markdown Document"), ("MarkdownMidget.SecureDocument", "Markdown Midget Encrypted Document") })
            expected.AddRange([$@"{Classes}{pid}|=SZ {type}", $@"{Classes}{pid}|FriendlyTypeName=SZ {type}", $@"{Classes}{pid}\DefaultIcon|=SZ {Icon}",
                $@"{Classes}{pid}\shell\open|FriendlyAppName=SZ Markdown Midget", $@"{Classes}{pid}\shell\open\command|=SZ {Command}"]);
        Assert.Equal(expected.Order(), reg.Values.Select(kv => kv.Key + "=" + (kv.Value is byte[] { Length: 0 } ? "NONE" : "SZ " + kv.Value)).Order());
    }

    [Fact]
    public void Entries_a_hand_picked_older_copy_left_are_pointed_at_the_registered_exe_in_place_once()
    {
        var reg = new FakeRegistry();
        const string Stray = Classes + @"Applications\MarkdownMidget-v1.0.0-rc1-win-x64-net10.exe", Old = Classes + "Old.MarkdownMidget";
        reg.Values[Stray + @"\shell\open\command|"] = @"""C:\Users\u\Downloads\MarkdownMidget-v1.0.0-rc1-win-x64-net10.exe"" ""%1""";
        reg.Values[Stray + @"\DefaultIcon|"] = @"C:\Users\u\Downloads\MarkdownMidget-v1.0.0-rc1-win-x64-net10.exe,0";
        reg.Values[@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.md\UserChoice|ProgId"] = @"Applications\MarkdownMidget-v1.0.0-rc1-win-x64-net10.exe";
        reg.Values[Classes + @".md\OpenWithProgids|Old.MarkdownMidget"] = Array.Empty<byte>();   // a ProgID an Open with list names
        reg.Values[Old + @"\shell\open\command|"] = @"D:\Tools\MarkdownMidget.exe %1";
        reg.Register(Exe);
        Assert.Equal([Command, Icon, Command, null], new[] { reg.Values[Stray + @"\shell\open\command|"], reg.Values[Stray + @"\DefaultIcon|"], reg.Values[Old + @"\shell\open\command|"], reg.Get(Old + @"\DefaultIcon", "") });
        Assert.DoesNotContain(reg.Log, l => l.StartsWith("delete ") || l.Contains("UserChoice"));
        reg.Log.Clear(); reg.Register(Exe);
        Assert.Empty(reg.Log);
    }

    [Fact]
    public void Another_app_that_merely_mentions_MarkdownMidget_is_left_untouched()
    {
        var reg = new FakeRegistry();
        reg.Values[Classes + @"Applications\SomeOtherEditor.exe\shell\open\command|"] = @"""C:\Tools\MarkdownMidget\SomeOtherEditor.exe"" ""%1""";
        reg.Values[Classes + @"Applications\Viewer.exe\shell\open\command|"] = @"""C:\Tools\Viewer.exe"" ""C:\Tools\MarkdownMidget.exe"" ""%1""";
        reg.Register(Exe);
        Assert.DoesNotContain(reg.Log, l => l.Contains("SomeOtherEditor") || l.Contains("Viewer"));   // the fake changes values only through a logged call
    }
}
