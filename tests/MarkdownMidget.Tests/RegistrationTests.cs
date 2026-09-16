using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>Installed updates and repeat Registers run Register again: it must write only what is missing
/// or different, in place, and delete nothing, or Windows resets the user's .md default. All against a fake
/// registry. Not covered: the one-line public wrappers, and the stray cleanup Unregister runs for real.</summary>
public class RegistrationTests
{
    private const string Exe = @"C:\Users\u\AppData\Local\Programs\MarkdownMidget\MarkdownMidget.exe";

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
        reg.Values[@"Software\Classes\Applications\MarkdownMidget-v1.0.0-beta1-win-x64-net10.exe\shell\open\command|"] = @"""D:\MarkdownMidget-v1.0.0-beta1-win-x64-net10.exe"" ""%1""";
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
}
