using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using MarkdownMidget.Picker;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>
/// The clues the file-dialog crash notice gives. Everything here runs on captured or
/// invented inputs: no test reads the real event log, registry or clipboard.
/// </summary>
public class PickerCrashCluesTests
{
    private static readonly DateTime Now = new(2026, 9, 23, 15, 20, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(2);
    private const string Exe = "MarkdownMidget.exe";

    /// <summary>An "Application Error" event exactly as Get-WinEvent's ToXml() gave it on
    /// Windows 11 26200 for a real access violation (2026-09-23); only the values vary.</summary>
    private static string Event(string app, int pid, string module, string modulePath,
                                string code = "c0000005", string time = "2026-09-23T15:19:34.9027771Z") =>
        "<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='Application Error' Guid='{a0e9b465-b939-57d7-b27d-95d8e925ff57}'/><EventID>1000</EventID><Version>0</Version><Level>2</Level><Task>100</Task><Opcode>0</Opcode><Keywords>0x8000000000000000</Keywords>" +
        $"<TimeCreated SystemTime='{time}'/><EventRecordID>454439</EventRecordID><Correlation/><Execution ProcessID='73968' ThreadID='115544'/><Channel>Application</Channel><Computer>PC</Computer><Security UserID='S-1-5-21-1'/></System>" +
        $"<EventData><Data Name='AppName'>{app}</Data><Data Name='AppVersion'>1.0.0.864</Data><Data Name='AppTimeStamp'>6a0e0000</Data><Data Name='ModuleName'>{module}</Data><Data Name='ModuleVersion'>52.4.0.0</Data><Data Name='ModuleTimeStamp'>6a0e0671</Data><Data Name='ExceptionCode'>{code}</Data><Data Name='FaultingOffset'>0000000000356fef</Data><Data Name='ProcessId'>0x{pid:x}</Data><Data Name='ProcessCreationTime'>0x1dd4b6eccab86dc</Data><Data Name='AppPath'>C:\\Users\\me\\AppData\\Local\\Programs\\MarkdownMidget\\{app}</Data><Data Name='ModulePath'>{modulePath}</Data><Data Name='IntegratorReportId'>eaf58013-69e9-4d42-a1a4-c2b0d25b803a</Data><Data Name='PackageFullName'></Data><Data Name='PackageRelativeAppId'></Data></EventData></Event>";

    private const string DropboxDll = @"C:\Program Files (x86)\Dropbox\Client\DropboxExt64.52.dll";

    // ===== 1. Windows' own record =====

    [Fact]
    public void ReadsTheFaultingModuleFromARealApplicationErrorEvent()
    {
        var fault = PickerCrashClues.ParseApplicationError(Event(Exe, 0xb8a4, "DropboxExt64.52.dll", DropboxDll));
        Assert.NotNull(fault);
        Assert.Equal(Exe, fault!.AppName);
        Assert.Equal(0xb8a4, fault.ProcessId);
        Assert.Equal("DropboxExt64.52.dll", fault.Module);
        Assert.Equal(DropboxDll, fault.ModulePath);
        Assert.Equal("c0000005", fault.ExceptionCode);
        Assert.Equal(new DateTime(2026, 9, 23, 15, 19, 34, DateTimeKind.Utc), fault.TimeUtc.AddTicks(-(fault.TimeUtc.Ticks % TimeSpan.TicksPerSecond)));
        Assert.Equal(DateTimeKind.Utc, fault.TimeUtc.Kind);
    }

    [Fact]
    public void AModuleWindowsHadAlreadyUnloadedIsNamedWithoutWindowsSuffix()
    {
        // Verbatim from nextcloud/desktop#6566: "NCContextMenu.dll_unloaded".
        var fault = PickerCrashClues.ParseApplicationError(Event(Exe, 1, "NCContextMenu.dll_unloaded", "NCContextMenu.dll"));
        Assert.Equal("NCContextMenu.dll", fault!.Module);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<Event")]
    [InlineData("<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System/></Event>")]
    [InlineData("not xml at all")]
    public void AnEventThatIsNotAnApplicationErrorReadsAsNothing(string xml) =>
        Assert.Null(PickerCrashClues.ParseApplicationError(xml));

    [Fact]
    public void PicksTheNewestRecordForThisHelperWithinTheWindow()
    {
        var events = new[]
        {
            Event("curl.exe", 42, "ntdll.dll", @"C:\WINDOWS\SYSTEM32\ntdll.dll", time: "2026-09-23T15:19:59.0000000Z"),   // another program
            Event(Exe, 42, "old.dll", @"C:\x\old.dll", time: "2026-09-23T15:19:00.0000000Z"),
            Event(Exe, 42, "DropboxExt64.52.dll", DropboxDll, time: "2026-09-23T15:19:30.0000000Z"),
            Event(Exe, 42, "stale.dll", @"C:\x\stale.dll", time: "2026-09-23T15:10:00.0000000Z"),                          // too old
            Event(Exe, 43, "sibling.dll", @"C:\x\sibling.dll", time: "2026-09-23T15:19:50.0000000Z"),                     // another window's helper
            "<garbage",
        };
        var fault = PickerCrashClues.PickFault(events, "markdownmidget.EXE", 42, Now, Window);
        Assert.Equal("DropboxExt64.52.dll", fault!.Module);
    }

    [Fact]
    public void NoRecordForThisHelperIsNoRecord() =>
        Assert.Null(PickerCrashClues.PickFault([Event(Exe, 43, "x.dll", @"C:\x.dll")], Exe, 42, Now, Window));

    [Fact]
    public void ALogThatCannotBeReadMeansNoRecordNotAnError()
    {
        Assert.Null(PickerCrashClues.FindFault(() => throw new UnauthorizedAccessException("denied"), Exe, 42, Now, Window));
        Assert.Equal("DropboxExt64.52.dll",
            PickerCrashClues.FindFault(() => [Event(Exe, 42, "DropboxExt64.52.dll", DropboxDll)], Exe, 42, Now, Window)!.Module);
    }

    [Theory]
    [InlineData("DropboxExt64.52.dll", DropboxDll, "AddOn")]
    [InlineData("ntdll.dll", @"C:\WINDOWS\SYSTEM32\ntdll.dll", "SystemFile")]
    // What .NET 10 records for an access violation in native code under a managed frame
    // (measured 2026-09-23): the runtime, not the file that faulted.
    [InlineData("coreclr.dll", @"C:\Program Files\dotnet\shared\Microsoft.NETCore.App\10.0.9\coreclr.dll", "SystemFile")]
    [InlineData("MarkdownMidget.exe", @"C:\Users\me\AppData\Local\Programs\MarkdownMidget\MarkdownMidget.exe", "SystemFile")]
    // What Windows records for Environment.FailFast (measured 2026-09-23).
    [InlineData("unknown", "unknown", "Unnamed")]
    public void OnlyAFileThatIsNotWindowsDotNetOrOursNamesTheCulprit(string module, string path, string expected) =>
        Assert.Equal(expected, PickerCrashClues.KindOf(PickerCrashClues.ParseApplicationError(Event(Exe, 1, module, path)), @"C:\Windows", Exe).ToString());

    [Fact]
    public void NoRecordIsItsOwnKind() => Assert.Equal(FaultKind.None, PickerCrashClues.KindOf(null, @"C:\Windows", Exe));

    [Fact]
    public void TheFirstClueSaysHowFarTheRecordCanBeTrusted()
    {
        string Say(string module, string path)
        {
            var fault = PickerCrashClues.ParseApplicationError(Event(Exe, 1, module, path));
            return PickerCrashClues.FaultSentence(new PickerCrashFindings(1, fault, PickerCrashClues.KindOf(fault, @"C:\Windows", Exe), []));
        }
        Assert.StartsWith($"Windows recorded the crash in DropboxExt64.52.dll ({DropboxDll}). That file is the best lead", Say("DropboxExt64.52.dll", DropboxDll));
        Assert.StartsWith("Windows recorded the crash in coreclr.dll, which is part of Windows, .NET or Markdown Midget itself.",
                          Say("coreclr.dll", @"C:\Program Files\dotnet\shared\Microsoft.NETCore.App\10.0.9\coreclr.dll"));
        Assert.Contains("doesn't name the add-on", Say("ntdll.dll", @"C:\WINDOWS\SYSTEM32\ntdll.dll"));
        Assert.Equal("Windows recorded the crash (exception 80131623) but couldn't name the file it happened in.",
                     PickerCrashClues.FaultSentence(new PickerCrashFindings(1,
                         PickerCrashClues.ParseApplicationError(Event(Exe, 1, "unknown", "unknown", code: "80131623")), FaultKind.Unnamed, [])));
    }

    // ===== 2. Shell extensions installed here =====

    private sealed class FakeRegistry : IRegistryView
    {
        public readonly Dictionary<string, Dictionary<string, string>> Keys = new(StringComparer.OrdinalIgnoreCase);

        public FakeRegistry Key(string path, params (string Name, string Value)[] values)
        {
            if (!Keys.TryGetValue(path, out var v)) Keys[path] = v = new(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, value) in values) v[name] = value;
            // Every ancestor exists too, as in the real registry.
            var cut = path.LastIndexOf('\\');
            if (cut > 0 && !Keys.ContainsKey(path[..cut])) Key(path[..cut]);
            return this;
        }

        public IReadOnlyList<string> SubKeyNames(string path) =>
            Keys.Keys.Where(k => k.StartsWith(path + "\\", StringComparison.OrdinalIgnoreCase) && k.IndexOf('\\', path.Length + 1) < 0)
                .Select(k => k[(path.Length + 1)..]).ToList();

        public IReadOnlyList<string> ValueNames(string path) =>
            Keys.TryGetValue(path, out var v) ? v.Keys.Where(n => n.Length > 0).ToList() : [];

        public string? Value(string path, string name) =>
            Keys.TryGetValue(path, out var v) && v.TryGetValue(name, out var s) ? s : null;

        public FakeRegistry Server(string clsid, string dll) => Key($@"HKCR\CLSID\{clsid}\InprocServer32", ("", dll));
    }

    private const string DropboxA = "{FB314ED9-A251-47B7-93E1-CDD82E34AF8B}";
    private const string DropboxB = "{FB314EDA-A251-47B7-93E1-CDD82E34AF8B}";
    private const string SevenZip = "{23170F69-40C1-278A-1000-000100020000}";
    private const string Tortoise = "{30351346-7B7D-4FCC-81B4-1E394CA267EB}";
    private const string AdobePreview = "{DC6EFB56-9CFA-464D-8880-44885D7DC193}";
    private const string AdobeProps = "{F9DB5320-233E-11D1-9F84-707F02C10627}";
    private const string ThumbOnly = "{C5A40261-CD64-4CCF-84CB-C394DA41E0F0}";
    private const string Shell32 = "{09799AFB-AD67-11D1-ABCD-00C04FC30936}";
    private const string Gone = "{00000000-1111-2222-3333-444444444444}";
    private const string Uninstalled = "{00000000-1111-2222-3333-555555555555}";

    private static FakeRegistry AMachine() => new FakeRegistry()
        .Key(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\ShellIconOverlayIdentifiers\ DropboxExt01", ("", DropboxA))
        .Key(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\ShellIconOverlayIdentifiers\ DropboxExt02", ("", DropboxB))
        .Key(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\ShellIconOverlayIdentifiers\Junk", ("", "not a guid"))
        .Key(@"HKCR\*\shellex\ContextMenuHandlers\7-Zip", ("", SevenZip))
        .Key(@"HKCR\Directory\shellex\ContextMenuHandlers\7-Zip", ("", SevenZip))
        .Key($@"HKCR\Folder\shellex\ContextMenuHandlers\{Tortoise}")                        // the key's own name is the CLSID
        .Key(@"HKCR\Drive\shellex\ContextMenuHandlers\Missing", ("", Gone))                  // no InprocServer32 anywhere
        .Key(@"HKCR\Drive\shellex\ContextMenuHandlers\Uninstalled", ("", Uninstalled))       // a DLL that is no longer there
        .Server(Uninstalled, @"C:\Program Files\Old\DropboxExt64.1.dll")
        .Key(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\PreviewHandlers", (AdobePreview, "Adobe PDF Preview Handler"))
        .Key(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\PropertySystem\PropertyHandlers\.pdf", ("", AdobeProps))
        .Key(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved",
             (ThumbOnly, "Thumbnail"), (Shell32, "Shell"), (SevenZip, "7-Zip"), ("junk", "x"))
        .Server(DropboxA, "\"" + DropboxDll + "\"")                                          // quoted, as some installers write it
        .Server(DropboxB, DropboxDll)
        .Server(SevenZip, @"C:\Program Files\7-Zip\7-zip.dll")
        .Server(Tortoise, @"C:\Program Files\TortoiseSVN\bin\TortoiseStub.dll")
        .Server(AdobePreview, @"C:\Program Files\Adobe\Acrobat DC\Acrobat\pdf.dll")
        .Server(AdobeProps, @"C:\Program Files\Adobe\Acrobat DC\Acrobat\pdf.dll")
        .Server(ThumbOnly, @"C:\Tools\Thumbs\thumbs.dll")
        .Server(Shell32, "shell32.dll");                                                     // bare name: the system folder

    private static (bool, string?) Files(string path) => Path.GetFileName(path).ToLowerInvariant() switch
    {
        "dropboxext64.52.dll" => (true, "Dropbox, Inc."),
        "7-zip.dll" => (true, "Igor Pavlov"),
        "tortoisestub.dll" => (true, "https://tortoisesvn.net"),
        "pdf.dll" => (true, "Adobe Inc."),
        "thumbs.dll" => (true, null),
        "shell32.dll" when path.StartsWith(@"C:\Windows\System32\", StringComparison.OrdinalIgnoreCase) => (true, "Microsoft Corporation"),
        _ => (false, null),
    };

    [Fact]
    public void FindsEveryKindOfHandlerOncePerDllWithWhatItDoes()
    {
        var found = PickerCrashClues.FindShellExtensions(AMachine(), Files, @"C:\Windows\System32");
        var byName = found.ToDictionary(e => Path.GetFileName(e.Path), StringComparer.OrdinalIgnoreCase);

        Assert.Equal(new[] { "7-zip.dll", "DropboxExt64.52.dll", "pdf.dll", "shell32.dll", "thumbs.dll", "TortoiseStub.dll" },
                     byName.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase));
        Assert.Equal(6, found.Count);
        Assert.Equal(DropboxDll, byName["DropboxExt64.52.dll"].Path);                        // quotes gone, two CLSIDs, one DLL
        Assert.Equal(new[] { "icon overlay" }, byName["DropboxExt64.52.dll"].Kinds);
        Assert.Equal(new[] { "right-click menu" }, byName["7-zip.dll"].Kinds);               // the Approved entry adds nothing
        Assert.Equal(new[] { "right-click menu" }, byName["TortoiseStub.dll"].Kinds);
        Assert.Equal(new[] { "preview", "properties" }, byName["pdf.dll"].Kinds);
        Assert.Equal(new[] { "other" }, byName["thumbs.dll"].Kinds);
        Assert.Equal(@"C:\Windows\System32\shell32.dll", byName["shell32.dll"].Path);
        Assert.Equal("Dropbox", byName["DropboxExt64.52.dll"].Culprit);
        Assert.Equal("7-Zip", byName["7-zip.dll"].Culprit);
        Assert.Equal("TortoiseSVN or TortoiseGit", byName["TortoiseStub.dll"].Culprit);
        Assert.Equal("Adobe Acrobat or Reader", byName["pdf.dll"].Culprit);
        Assert.Null(byName["thumbs.dll"].Culprit);
        Assert.Null(byName["shell32.dll"].Culprit);
    }

    [Fact]
    public void AnEmptyOrUnreadableRegistryAndAFileReadThatThrowsFindNothingAndDoNotThrow()
    {
        Assert.Empty(PickerCrashClues.FindShellExtensions(new FakeRegistry(), Files, @"C:\Windows\System32"));
        var found = PickerCrashClues.FindShellExtensions(AMachine(), _ => throw new IOException("locked"), @"C:\Windows\System32");
        Assert.Empty(found);   // a DLL whose facts can't be read is treated as not there: it can't be shown or matched
    }

    [Theory]
    [InlineData(@"C:\x\DropboxExt64.52.dll", null, "Dropbox")]
    [InlineData(@"C:\x\anything.dll", "Dropbox, Inc.", "Dropbox")]
    [InlineData(@"C:\x\shellex.dll", "AO Kaspersky Lab", "Kaspersky")]
    [InlineData(@"C:\x\avgsea.dll", null, "Avast or AVG")]
    [InlineData(@"C:\x\ext.dll", "AVG Technologies CZ, s.r.o.", "Avast or AVG")]
    [InlineData(@"C:\Windows\System32\igfxDTCM.dll", "Intel Corporation", "Intel graphics")]
    [InlineData(@"C:\x\DBROverlayIconBackuped.dll", null, "Dell Backup and Recovery")]
    [InlineData(@"C:\x\ext.dll", "Box, Inc.", null)]                        // "Box" is not "Dropbox"
    [InlineData(@"C:\x\ext.dll", "Dropboxify Ltd", null)]                   // nor is a word that only contains a name
    [InlineData(@"C:\x\FileSyncShell64.dll", "Microsoft Corporation", null)]
    [InlineData(@"C:\x\ext.dll", null, null)]
    public void MatchesTheCuratedListByFileNameOrWholeVendorName(string dll, string? company, string? expected) =>
        Assert.Equal(expected, PickerCrashClues.MatchCulprit(dll, company));

    [Fact]
    public void EveryCuratedEntryCanBeMatchedAndNamesItsProduct() =>
        Assert.All(PickerCrashClues.Culprits, c =>
        {
            Assert.False(string.IsNullOrWhiteSpace(c.Product));
            Assert.True(c.FilePrefixes.Length > 0 || c.Company is not null, c.Product);
        });

    [Fact]
    public void TheOthersAreTheUnmatchedOnesNotFromMicrosoft()
    {
        var found = PickerCrashClues.FindShellExtensions(AMachine(), Files, @"C:\Windows\System32");
        var findings = new PickerCrashFindings(unchecked((int)0xC0000005), null, FaultKind.None, found);
        Assert.Equal(new[] { "DropboxExt64.52.dll", "7-zip.dll", "TortoiseStub.dll", "pdf.dll" }.Order(),
                     findings.Suspects.Select(e => Path.GetFileName(e.Path)).Order());
        Assert.Equal(new[] { "thumbs.dll" }, findings.Others.Select(e => Path.GetFileName(e.Path)));   // no vendor counts as not Microsoft
    }

    // ===== the copied details =====

    [Fact]
    public void TheDetailsCarryEveryClueForABugReport()
    {
        var fault = PickerCrashClues.ParseApplicationError(Event(Exe, 42, "DropboxExt64.52.dll", DropboxDll));
        var found = PickerCrashClues.FindShellExtensions(AMachine(), Files, @"C:\Windows\System32");
        var text = PickerCrashClues.Details(new PickerCrashFindings(unchecked((int)0xC0000005), fault, FaultKind.AddOn, found),
                                            "1.0.0-rc2+build.900", "Microsoft Windows NT 10.0.26200.0");
        foreach (var expected in new[]
                 {
                     "1.0.0-rc2+build.900", "Microsoft Windows NT 10.0.26200.0", "0xC0000005",
                     "DropboxExt64.52.dll", DropboxDll, "c0000005",
                     "Dropbox: " + DropboxDll + " (icon overlay; Dropbox, Inc.)",
                     "7-Zip: " + @"C:\Program Files\7-Zip\7-zip.dll (right-click menu; Igor Pavlov)",
                     @"C:\Tools\Thumbs\thumbs.dll (other; no vendor named)",
                     "commonly reported to crash Windows' file dialogs (4)", "not from Microsoft (1)",
                 })
            Assert.Contains(expected, text);
        Assert.DoesNotContain("shell32.dll", text);   // Windows' own are counted out, not listed
    }

    [Fact]
    public void TheDetailsSayWhenWindowsRecordedNothingAndNothingIsInstalled()
    {
        var text = PickerCrashClues.Details(new PickerCrashFindings(1, null, FaultKind.None, []), "v", "os");
        Assert.Contains("0x00000001", text);
        Assert.Contains("no record", text);
        Assert.Contains("commonly reported to crash Windows' file dialogs (0)", text);
    }

    [Fact]
    public void ALockedClipboardIsANoteNotACrash()
    {
        string? written = null;
        Assert.Null(PickerCrashClues.CopyTo("details", s => written = s));
        Assert.Equal("details", written);
        Assert.NotNull(PickerCrashClues.CopyTo("x", _ => throw new COMException("in use", unchecked((int)0x800401D0))));   // CLIPBRD_E_CANT_OPEN
    }

    // ===== 3. The folder to show in Explorer =====

    [Fact]
    public void TheFolderIsTheDialogsOwnThenTheFirstRecentOneThenDocuments()
    {
        var real = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\docs", @"C:\recent2" };
        bool Exists(string d) => real.Contains(d);
        Assert.Equal(@"C:\docs", PickerCrashClues.FolderToShow(
            new FilePickerRequest { InitialDirectory = @"C:\docs", RecentFolders = [@"C:\recent2"] }, Exists, @"C:\Documents"));
        Assert.Equal(@"C:\recent2", PickerCrashClues.FolderToShow(
            new FilePickerRequest { InitialDirectory = @"C:\gone", RecentFolders = [@"C:\gone2", @"C:\recent2"] }, Exists, @"C:\Documents"));
        Assert.Equal(@"C:\Documents", PickerCrashClues.FolderToShow(new FilePickerRequest(), Exists, @"C:\Documents"));
    }

    // ===== 4. The guide =====

    private static string Guide()
    {
        using var stream = typeof(PickerCrashClues).Assembly.GetManifestResourceStream(PickerCrashClues.GuideResource);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    [Fact]
    public void TheGuideShipsSelfContainedAndFollowsDarkMode()
    {
        var html = Guide();
        Assert.StartsWith("<!DOCTYPE html>", html);
        foreach (var external in new[] { "<script", "<img", "<link", "<iframe", "url(", "@import", "src=" })
            Assert.DoesNotContain(external, html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("prefers-color-scheme: dark", html);
    }

    [Fact]
    public void TheGuideIsWrittenOnceAndRewrittenOnlyWhenItChanges()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mdm-guide-" + Guid.NewGuid().ToString("N"));
        try
        {
            var content = "<!DOCTYPE html>v1";
            Stream Open() => new MemoryStream(Encoding.UTF8.GetBytes(content));
            var path = PickerCrashClues.ExtractGuide(Open, dir);
            Assert.Equal(Path.Combine(dir, PickerCrashClues.GuideResource), path);
            Assert.Equal("<!DOCTYPE html>v1", File.ReadAllText(path!));

            var stamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(path!, stamp);
            PickerCrashClues.ExtractGuide(Open, dir);
            Assert.Equal(stamp, File.GetLastWriteTimeUtc(path!));                       // same bytes: left alone

            content = "<!DOCTYPE html>v2";
            PickerCrashClues.ExtractGuide(Open, dir);
            Assert.Equal("<!DOCTYPE html>v2", File.ReadAllText(path!));                  // an update's guide replaces it

            Assert.Null(PickerCrashClues.ExtractGuide(() => null, dir));                 // no resource: nothing to open
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
