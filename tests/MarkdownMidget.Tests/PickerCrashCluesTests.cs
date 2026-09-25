using System;
using System.Collections.Generic;
using System.Globalization;
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
    private const string DriverStore = @"C:\Windows\System32\DriverStore\FileRepository\nv_dispig.inf_amd64_8d1c5c2e1f0a3b7e\";
    private const string OneDriveDll = @"C:\Users\me\AppData\Local\Microsoft\OneDrive\25.160.0817.0003\FileSyncShell64.dll";

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
            Event(Exe, 43, "sibling.dll", @"C:\x\sibling.dll", time: "2026-09-23T15:19:50.0000000Z"),                     // another window's helper
            "<garbage",
        };
        var fault = PickerCrashClues.PickFault(events, "markdownmidget.EXE", 42, Now, Window);
        Assert.Equal("DropboxExt64.52.dll", fault!.Module);
    }

    [Fact]
    public void ARecordFromAnotherHelperOrOlderThanTheWindowIsNoRecord()
    {
        Assert.Null(PickerCrashClues.PickFault([Event(Exe, 43, "x.dll", @"C:\x.dll")], Exe, 42, Now, Window));
        // This helper's process id, but from a crash 10 minutes ago: an earlier helper that got the same id.
        Assert.Null(PickerCrashClues.PickFault([Event(Exe, 42, "stale.dll", @"C:\x\stale.dll", time: "2026-09-23T15:10:00.0000000Z")],
                                               Exe, 42, Now, Window));
    }

    [Fact]
    public void ALogThatCannotBeReadMeansNoRecordNotAnError()
    {
        Assert.Null(PickerCrashClues.FindFault(() => throw new UnauthorizedAccessException("denied"), Exe, 42, Now, Window));
        Assert.Equal("DropboxExt64.52.dll",
            PickerCrashClues.FindFault(() => [Event(Exe, 42, "DropboxExt64.52.dll", DropboxDll)], Exe, 42, Now, Window)!.Module);
    }

    private static readonly ShellExtensionDll OneDrive = new(OneDriveDll, "Microsoft Corporation", ["icon overlay"], null);
    private static readonly ShellExtensionDll Shell32 = new(@"C:\WINDOWS\System32\shell32.dll", "Microsoft Corporation", ["other"], null);
    private const string FoxitThumbDll = @"C:\Program Files (x86)\Foxit Software\Foxit PDF Reader\FoxitThumbnailHndlr_x64.dll";
    private static readonly ShellExtensionDll FoxitThumbnail = new(FoxitThumbDll, "Foxit Software Inc.", ["thumbnail"], null);
    private static readonly ShellExtensionDll[] Known = [OneDrive, Shell32, FoxitThumbnail];

    private const string Missing = "-";   // a company value meaning "no such file"
    private const string Throws = "!";    // a company value meaning "the lookup throws"

    /// <summary>Facts for one row: only a rooted path can be read, so a bare module name that
    /// is not first resolved against the system folder is "not found".</summary>
    private static Func<string, (bool, string?)> FileWith(string? company) =>
        p => company == Throws ? throw new UnauthorizedAccessException("denied")
           : !Path.IsPathRooted(p) || company == Missing ? (false, null) : (true, company);

    private static FaultKind Kind(string module, string path, string? company) =>
        PickerCrashClues.KindOf(PickerCrashClues.ParseApplicationError(Event(Exe, 1, module, path)),
                                @"C:\Windows\System32", Exe, Known, FileWith(company));

    [Theory]
    [InlineData("DropboxExt64.52.dll", DropboxDll, "Dropbox, Inc.", "AddOn")]
    // A graphics driver's shell extension lives under the Windows folder; the file decides, not the folder.
    [InlineData("nvshext.dll", DriverStore + "nvshext.dll", "NVIDIA Corporation", "AddOn")]
    [InlineData("nvxdapix.dll", DriverStore + "nvxdapix.dll", "NVIDIA Corporation", "AddOn")]      // not on the list: its vendor decides
    [InlineData("igfxpph.dll", DriverStore + "igfxpph.dll", Missing, "AddOn")]                    // on the list, even unreadable
    [InlineData("NCContextMenu.dll_unloaded", "NCContextMenu.dll", Missing, "AddOn")]             // on the list, bare and gone
    [InlineData("FileSyncShell64.dll", OneDriveDll, "Microsoft Corporation", "AddOn")]         // Microsoft's, but an add-on found here
    [InlineData("FileSyncShell64.dll", @"C:\Program Files\Other\FileSyncShell64.dll", "Microsoft Corporation", "Microsoft")]   // same name, not the one found
    // Bare and not in System32, but the scan found a DLL of that name: that one is the add-on.
    [InlineData("FoxitThumbnailHndlr_x64.dll_unloaded", "FoxitThumbnailHndlr_x64.dll", Missing, "AddOn")]
    // Bare, and the scan found Windows' own DLL of that name: that doesn't make it an add-on.
    [InlineData("shell32.dll_unloaded", "shell32.dll", "Microsoft Corporation", "Windows")]
    [InlineData("shell32.dll", @"C:\WINDOWS\System32\shell32.dll", "Microsoft Corporation", "Windows")]   // Windows' own shell extension
    [InlineData("ntdll.dll", @"C:\WINDOWS\SYSTEM32\ntdll.dll", "Microsoft Corporation", "Windows")]
    // The Windows folder, not just System32: side-by-side assemblies are a common crash site.
    [InlineData("comctl32.dll", @"C:\Windows\WinSxS\amd64_microsoft.windows.common-controls_6595b64144ccf1df_6.0.26100.1_none_1\comctl32.dll",
                "Microsoft Corporation", "Windows")]
    // Windows names an already-unloaded DLL by its bare file name: resolved against System32 first.
    [InlineData("thumbcache.dll_unloaded", "thumbcache.dll", "Microsoft Corporation", "Windows")]
    // A bare name can't climb out of System32 into another folder's verdict.
    [InlineData("evil.dll", @"..\..\Tools\evil.dll", "Microsoft Corporation", "Microsoft")]
    // What .NET 10 records for an access violation in native code under a managed frame
    // (measured 2026-09-23): the runtime, a Microsoft file, not the file that faulted.
    [InlineData("coreclr.dll", @"C:\Program Files\dotnet\shared\Microsoft.NETCore.App\10.0.9\coreclr.dll", "Microsoft Corporation", "Microsoft")]
    [InlineData("msoshext.dll", @"C:\Program Files\Microsoft Office\root\VFS\ProgramFilesCommonX64\Microsoft Shared\OFFICE16\msoshext.dll",
                "Microsoft Corporation", "Microsoft")]                                            // Microsoft's, not Windows'
    [InlineData("coreclr.dll", "coreclr.dll", Missing, "Unidentified")]                        // bare, and not in System32 either
    [InlineData("gone.dll", @"C:\WINDOWS\System32\gone.dll", Missing, "Unidentified")]
    [InlineData("boom.dll", @"C:\Tools\boom.dll", Throws, "Unidentified")]                     // a lookup that throws
    // No vendor named: every Windows file names Microsoft, so a vendorless one in System32 isn't Windows'.
    [InlineData("oddhook.dll", @"C:\Windows\System32\oddhook.dll", null, "Unidentified")]
    [InlineData("hook.dll", @"C:\Tools\hook.dll", null, "Unidentified")]
    [InlineData("MarkdownMidget.exe", @"C:\Users\me\AppData\Local\Programs\MarkdownMidget\MarkdownMidget.exe", "Funcular Labs", "ThisApp")]
    // What Windows records for Environment.FailFast (measured 2026-09-23).
    [InlineData("unknown", "unknown", null, "Unnamed")]
    public void TheFaultingFileItselfDecidesWhetherItNamesTheCulprit(string module, string path, string? company, string expected) =>
        Assert.Equal(expected, Kind(module, path, company).ToString());

    [Fact]
    public void NoRecordIsItsOwnKind() =>
        Assert.Equal(FaultKind.None, PickerCrashClues.KindOf(null, @"C:\Windows\System32", Exe, [], FileWith(null)));

    [Fact]
    public void TheFirstClueSaysHowFarTheRecordCanBeTrusted()
    {
        string Say(string module, string path, string? company)
        {
            var fault = PickerCrashClues.ParseApplicationError(Event(Exe, 1, module, path, code: "80131623"));
            return PickerCrashClues.FaultSentence(new PickerCrashFindings(1, fault, Kind(module, path, company), Known));
        }
        Assert.Equal($"Windows recorded the crash in DropboxExt64.52.dll ({DropboxDll}). That file is the best lead: it comes with Dropbox.",
                     Say("DropboxExt64.52.dll", DropboxDll, "Dropbox, Inc."));
        Assert.Equal($"Windows recorded the crash in FoxitThumbnailHndlr_x64.dll ({FoxitThumbDll}). That file is the best lead: " +
                     "the Details tab of its Properties in Explorer names the program it came with.",
                     Say("FoxitThumbnailHndlr_x64.dll_unloaded", "FoxitThumbnailHndlr_x64.dll", Missing));   // the path the scan found
        Assert.Equal("Windows recorded the crash in NCContextMenu.dll. That file is the best lead: it comes with Nextcloud.",
                     Say("NCContextMenu.dll_unloaded", "NCContextMenu.dll", Missing));                        // bare, and no path known
        Assert.StartsWith("Windows recorded the crash in coreclr.dll, a Microsoft file. That doesn't name the add-on:",
                          Say("coreclr.dll", @"C:\Program Files\dotnet\shared\Microsoft.NETCore.App\10.0.9\coreclr.dll", "Microsoft Corporation"));
        Assert.StartsWith("Windows recorded the crash in thumbcache.dll, part of Windows.", Say("thumbcache.dll_unloaded", "thumbcache.dll", "Microsoft Corporation"));
        Assert.Equal("Windows recorded the crash in coreclr.dll, but Markdown Midget can't tell whose file that is. " +
                     "If it isn't part of Windows, it may be the add-on.", Say("coreclr.dll", "coreclr.dll", Missing));
        Assert.StartsWith("Windows recorded the crash in MarkdownMidget.exe, Markdown Midget itself.",
                          Say("MarkdownMidget.exe", @"C:\x\MarkdownMidget.exe", "Funcular Labs"));
        Assert.StartsWith("Windows recorded the crash in a file it couldn't name (exception 80131623).", Say("unknown", "unknown", null));
        Assert.Contains("doesn't name the add-on", Say("unknown", "unknown", null));
        Assert.StartsWith("Windows has no record of this crash",
                          PickerCrashClues.FaultSentence(new PickerCrashFindings(1, null, FaultKind.None, [])));
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
    private const string Shell32Clsid = "{09799AFB-AD67-11D1-ABCD-00C04FC30936}";
    private const string PerUser = "{45769BCC-E8FD-42D0-947E-02BEEF77A1F5}";
    private const string FoxitThumb = "{A5B1A5C2-4D2E-4B55-9B9E-2F4C9A5E0B11}";
    private const string PdnThumb = "{0B2C7E1A-3F4D-4E6A-8C9B-7D1E2F3A4B5C}";
    private const string Thumbnails = "{e357fccd-a995-4576-b01f-234630154e96}";
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
        .Server(Uninstalled, @"C:\Program Files\Old\DropboxExt64.1.dll")        .Key(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\PreviewHandlers", (AdobePreview, "Adobe PDF Preview Handler"))
        // A per-user install registers under HKCU, as PowerToys' preview handlers do.
        .Key(@"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\PreviewHandlers", (PerUser, "Markdown preview"))
        .Key(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\PropertySystem\PropertyHandlers\.pdf", ("", AdobeProps))
        .Key(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved",
             (ThumbOnly, "Thumbnail"), (Shell32Clsid, "Shell"), (SevenZip, "7-Zip"), ("junk", "x"))
        .Server(DropboxA, "\"" + DropboxDll + "\"")                                          // quoted, as some installers write it
        .Server(DropboxB, DropboxDll)
        .Server(SevenZip, @"C:\Program Files\7-Zip\7-zip.dll")
        .Server(Tortoise, @"C:\Program Files\TortoiseSVN\bin\TortoiseStub.dll")
        .Server(AdobePreview, @"C:\Program Files\Adobe\Acrobat DC\Acrobat\pdf.dll")
        .Server(AdobeProps, @"C:\Program Files\Adobe\Acrobat DC\Acrobat\pdf.dll")
        .Server(ThumbOnly, @"C:\Tools\Thumbs\thumbs.dll")
        .Server(PerUser, @"C:\Users\me\AppData\Local\Viewer\MarkdownPreview.dll")
        // Thumbnail handlers are registered per file type, and are not on the Approved list.
        .Key($@"HKCR\.pdf\ShellEx\{Thumbnails}", ("", FoxitThumb))
        .Key($@"HKCR\.PDF_Other\ShellEx\{Thumbnails}", ("", "not a guid"))
        .Key($@"HKCR\pdffile\ShellEx\{Thumbnails}", ("", AdobeProps))                          // not a file type: never read
        .Key($@"HKCR\SystemFileAssociations\.pdn\ShellEx\{Thumbnails}", ("", PdnThumb))
        .Server(FoxitThumb, @"C:\Program Files (x86)\Foxit Software\Foxit PDF Reader\FoxitThumbnailHndlr_x64.dll")
        .Server(PdnThumb, @"C:\Program Files\paint.net\PaintDotNet.ShellExtension.x64.dll")
        .Server(Shell32Clsid, "shell32.dll");                                                // bare name: the system folder

    private static (bool, string?) Files(string path) => Path.GetFileName(path).ToLowerInvariant() switch
    {
        "dropboxext64.52.dll" => (true, "Dropbox, Inc."),
        "7-zip.dll" => (true, "Igor Pavlov"),
        "tortoisestub.dll" => (true, "https://tortoisesvn.net"),
        "pdf.dll" => (true, "Adobe Inc."),
        "thumbs.dll" => (true, null),
        "markdownpreview.dll" => (true, "Viewer Co"),
        "foxitthumbnailhndlr_x64.dll" => (true, "Foxit Software Inc."),
        "paintdotnet.shellextension.x64.dll" => (true, "dotPDN LLC"),
        "shell32.dll" when path.StartsWith(@"C:\Windows\System32\", StringComparison.OrdinalIgnoreCase) => (true, "Microsoft Corporation"),
        _ => (false, null),
    };

    /// <summary>Each call to the scan's deadline factory takes the next budget from
    /// <paramref name="checks"/>: how many "out of time?" checks that phase passes before
    /// time is up (null: never). The scan starts one deadline per phase.</summary>
    private static ShellScan Scan(IRegistryView reg, Func<string, (bool, string?)> file, params int?[] checks)
    {
        var phase = 0;
        return PickerCrashClues.FindShellExtensions(reg, file, @"C:\Windows\System32", () =>
        {
            var budget = phase < checks.Length ? checks[phase] : null;
            phase++;
            var used = 0;
            return () => budget is { } b && ++used > b;
        });
    }

    private static IReadOnlyList<ShellExtensionDll> Found() => Scan(AMachine(), Files).Found;

    [Fact]
    public void FindsEveryKindOfHandlerOncePerDllWithWhatItDoes()
    {
        var scan = Scan(AMachine(), Files);
        var found = scan.Found;
        var byName = found.ToDictionary(e => Path.GetFileName(e.Path), StringComparer.OrdinalIgnoreCase);

        Assert.Equal(new[] { "7-zip.dll", "DropboxExt64.52.dll", "FoxitThumbnailHndlr_x64.dll", "MarkdownPreview.dll",
                             "PaintDotNet.ShellExtension.x64.dll", "pdf.dll", "shell32.dll", "thumbs.dll", "TortoiseStub.dll" },
                     byName.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase));
        Assert.Equal(9, found.Count);
        Assert.False(scan.CutShort);
        Assert.Equal(new[] { "thumbnail" }, byName["FoxitThumbnailHndlr_x64.dll"].Kinds);      // HKCR\.pdf
        Assert.Equal(new[] { "thumbnail" }, byName["PaintDotNet.ShellExtension.x64.dll"].Kinds); // HKCR\SystemFileAssociations\.pdn
        Assert.Equal(DropboxDll, byName["DropboxExt64.52.dll"].Path);                        // quotes gone, two CLSIDs, one DLL
        Assert.Equal(new[] { "icon overlay" }, byName["DropboxExt64.52.dll"].Kinds);
        Assert.Equal(new[] { "right-click menu" }, byName["7-zip.dll"].Kinds);               // the Approved entry adds nothing
        Assert.Equal(new[] { "right-click menu" }, byName["TortoiseStub.dll"].Kinds);
        Assert.Equal(new[] { "preview", "properties" }, byName["pdf.dll"].Kinds);            // HKCR\pdffile is not a file type
        Assert.Equal(new[] { "preview" }, byName["MarkdownPreview.dll"].Kinds);              // from HKCU
        Assert.Equal(new[] { "other" }, byName["thumbs.dll"].Kinds);
        Assert.Equal(@"C:\Windows\System32\shell32.dll", byName["shell32.dll"].Path);
        Assert.Equal("Dropbox", byName["DropboxExt64.52.dll"].Culprit);
        Assert.Equal("TortoiseSVN", byName["TortoiseStub.dll"].Culprit);
        Assert.All(new[] { "7-zip.dll", "pdf.dll", "thumbs.dll", "shell32.dll", "MarkdownPreview.dll" },
                   name => Assert.Null(byName[name].Culprit));                              // file name only: Adobe Inc. is not Acrobat
    }

    [Fact]
    public void AnEmptyOrUnreadableRegistryAndAFileReadThatThrowsFindNothingAndDoNotThrow()
    {
        Assert.Empty(Scan(new FakeRegistry(), Files).Found);
        Assert.Empty(Scan(AMachine(), _ => throw new IOException("locked")).Found);   // unreadable facts: can't be shown or matched
    }

    private static string[] Names(ShellScan scan) =>
        scan.Found.Select(e => Path.GetFileName(e.Path)).Order(StringComparer.OrdinalIgnoreCase).ToArray();

    [Fact]
    public void AScanOutOfTimeKeepsWhatItFoundContextMenusFirstAndStillLooksAtThumbnails()
    {
        // Three checks pass: 7-Zip (*), 7-Zip again (Directory), TortoiseSVN (Folder); then time is up,
        // before Dropbox's overlays. The thumbnail step has a deadline of its own and finishes.
        var scan = Scan(AMachine(), Files, 3, null);
        Assert.True(scan.CutShort);
        Assert.Equal(new[] { "7-zip.dll", "FoxitThumbnailHndlr_x64.dll", "PaintDotNet.ShellExtension.x64.dll", "TortoiseStub.dll" }, Names(scan));

        var findings = new PickerCrashFindings(1, null, FaultKind.None, scan.Found, scan.CutShort);
        Assert.EndsWith("The search ran out of time before it finished, so there may be more.", PickerCrashClues.SuspectLines(findings));
        Assert.EndsWith("The search ran out of time before it finished, so there may be more.", PickerCrashClues.OthersLine(findings));
        Assert.Contains("The search ran out of time before it finished", PickerCrashClues.Details(findings, "v", "os"));
        Assert.Equal("None of the add-ons on our list were found. The search ran out of time before it finished, so there may be more.",
                     PickerCrashClues.SuspectLines(new PickerCrashFindings(1, null, FaultKind.None, [], true)));
        Assert.DoesNotContain("out of time", PickerCrashClues.SuspectLines(new PickerCrashFindings(1, null, FaultKind.None, scan.Found)));
    }

    [Fact]
    public void TheThumbnailStepStopsOnItsOwnDeadlineAndKeepsTheRest()
    {
        var scan = Scan(AMachine(), Files, null, 0);
        Assert.True(scan.CutShort);
        Assert.Equal(new[] { "7-zip.dll", "DropboxExt64.52.dll", "MarkdownPreview.dll", "pdf.dll", "shell32.dll", "thumbs.dll", "TortoiseStub.dll" },
                     Names(scan));
    }

    [Theory]
    [InlineData(@"C:\x\DropboxExt64.52.dll", "Dropbox")]          // Dropbox's name carries its version: matched by prefix
    [InlineData(@"C:\x\dropboxext.11.0.dll", "Dropbox")]
    [InlineData(@"C:\x\avgsea.dll", "Avast or AVG")]
    [InlineData(@"C:\Windows\System32\IGFXPPH.DLL", "Intel graphics")]
    [InlineData(@"C:\x\DBROverlayIconBackuped.dll", "Dell Backup and Recovery")]
    [InlineData(@"C:\x\PDFShell.dll", "Adobe Acrobat or Reader")]
    [InlineData(@"C:\x\AIPreviewHandler.dll", "Adobe Illustrator")]
    [InlineData(@"C:\x\pdfshelltools.dll", null)]               // every other name is matched whole
    [InlineData(@"C:\x\shellex.dll", null)]                     // Kaspersky's name is too generic to match on
    [InlineData(@"C:\x\7-zip.dll", null)]                       // reported for delays, not crashes
    [InlineData(@"C:\Windows\System32\igfxDTCM.dll", null)]     // likewise
    [InlineData(@"C:\x\FileSyncShell64.dll", null)]
    public void MatchesTheCuratedListByFileNameOnly(string dll, string? expected) =>
        Assert.Equal(expected, PickerCrashClues.MatchCulprit(dll));

    /// <summary>Self-consistency only: every DLL an entry's record cites is matched to that entry,
    /// and a prefix covers every cited name. That each record says what its source says was
    /// checked by hand against the sources (2026-09-23); no test can re-read them.</summary>
    [Fact]
    public void EveryCuratedEntryNamesTheDllsItsRecordCites() =>
        Assert.All(PickerCrashClues.Culprits, c =>
        {
            Assert.False(string.IsNullOrWhiteSpace(c.Product));
            Assert.StartsWith("http", c.Source);
            Assert.NotEmpty(c.SourceDlls);
            foreach (var dll in c.SourceDlls) Assert.Equal(c.Product, PickerCrashClues.MatchCulprit(dll));
            if (c.Prefix is { } prefix) Assert.All(c.SourceDlls, dll => Assert.StartsWith(prefix, dll, StringComparison.OrdinalIgnoreCase));
        });

    [Fact]
    public void TheOthersAreTheUnmatchedOnesNotFromMicrosoft()
    {
        var findings = new PickerCrashFindings(unchecked((int)0xC0000005), null, FaultKind.None, Found());
        Assert.Equal(new[] { "DropboxExt64.52.dll", "TortoiseStub.dll" }.Order(),
                     findings.Suspects.Select(e => Path.GetFileName(e.Path)).Order());
        Assert.Equal(new[] { "7-zip.dll", "FoxitThumbnailHndlr_x64.dll", "MarkdownPreview.dll", "PaintDotNet.ShellExtension.x64.dll",
                             "pdf.dll", "thumbs.dll" },                                     // no vendor counts as not Microsoft
                     findings.Others.Select(e => Path.GetFileName(e.Path)).Order(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheNoticeListsTheMatchesAndCountsTheRest()
    {
        var findings = new PickerCrashFindings(1, null, FaultKind.None, Found());
        Assert.Equal("• Dropbox: DropboxExt64.52.dll (icon overlay)\n• TortoiseSVN: TortoiseStub.dll (right-click menu)",
                     PickerCrashClues.SuspectLines(findings));
        Assert.Equal("6 other add-ons from outside Microsoft were found; the log names them all.", PickerCrashClues.OthersLine(findings));

        var none = new PickerCrashFindings(1, null, FaultKind.None, []);
        Assert.Equal("None of the add-ons on our list were found.", PickerCrashClues.SuspectLines(none));
        Assert.Equal("No other add-ons from outside Microsoft were found.", PickerCrashClues.OthersLine(none));
        var one = new PickerCrashFindings(1, null, FaultKind.None, [new(@"C:\x\a.dll", null, ["other"], null)]);
        Assert.Equal("One other add-on from outside Microsoft was found; the log names it.", PickerCrashClues.OthersLine(one));
    }

    [Fact]
    public void WithoutALogTheCountLineSendsYouToCopyDetails()
    {
        Assert.Equal("6 other add-ons from outside Microsoft were found; Copy details names them all.",
                     PickerCrashClues.OthersLine(new PickerCrashFindings(1, null, FaultKind.None, Found()), logSaved: false));
        Assert.Equal("One other add-on from outside Microsoft was found; Copy details names it.",
                     PickerCrashClues.OthersLine(new PickerCrashFindings(1, null, FaultKind.None, [new(@"C:\x\a.dll", null, ["other"], null)]), logSaved: false));
    }

    // ===== the copied details =====

    [Fact]
    public void TheDetailsCarryEveryClueForABugReport()
    {
        var fault = PickerCrashClues.ParseApplicationError(Event(Exe, 42, "DropboxExt64.52.dll", DropboxDll));
        var text = PickerCrashClues.Details(new PickerCrashFindings(unchecked((int)0xC0000005), fault, FaultKind.AddOn, Found()),
                                            "1.0.0-rc2+build.900", "Microsoft Windows NT 10.0.26200.0");
        foreach (var expected in new[]
                 {
                     "1.0.0-rc2+build.900", "Microsoft Windows NT 10.0.26200.0", "0xC0000005",
                     "DropboxExt64.52.dll", DropboxDll, "c0000005",
                     "Dropbox: " + DropboxDll + " (icon overlay; Dropbox, Inc.)",
                     @"C:\Program Files\7-Zip\7-zip.dll (right-click menu; Igor Pavlov)",
                     @"C:\Tools\Thumbs\thumbs.dll (other; no vendor named)",
                     @"C:\Program Files\paint.net\PaintDotNet.ShellExtension.x64.dll (thumbnail; dotPDN LLC)",
                     "reported to crash Explorer or programs that load them (2)", "not from Microsoft (6)",
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
        Assert.Contains("reported to crash Explorer or programs that load them (0)", text);
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

    [Theory]
    // Explorer splits its command line on commas, so a folder is always quoted, spaces or not.
    [InlineData(@"D:\Clients\Smith,J", "\"D:\\Clients\\Smith,J\"")]
    [InlineData(@"C:\My Documents\", "\"C:\\My Documents\"")]
    [InlineData(@"C:\", "\"C:\\\"")]                              // a root keeps its backslash
    public void ExplorerIsGivenTheFolderQuoted(string folder, string expected) =>
        Assert.Equal(expected, PickerCrashClues.ExplorerArguments(folder));

    [Fact]
    public void OpenTheLogHandsTheLogQuotedToExplorerItself()
    {
        // Nothing is started: the test reads what the button would start.
        const string log = @"C:\Users\Jo Smith,Jr\AppData\Local\MarkdownMidget\logs\file-dialog-crash-2026-09-25-093700.txt";
        var start = PickerCrashClues.ExplorerStart(@"C:\Windows", log);
        Assert.Equal(@"C:\Windows\explorer.exe", start.FileName);
        Assert.Equal("\"" + log + "\"", start.Arguments);
        Assert.False(start.UseShellExecute);   // Explorer opens it with the .txt program, in that program's process
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

    // ===== 5. The log =====

    private static readonly DateTimeOffset Crashed = new(2026, 9, 25, 9, 37, 0, TimeSpan.FromHours(-5));

    /// <summary>Runs <paramref name="test"/> on a new temp folder standing in for the logs folder, under a
    /// culture whose calendar isn't the Gregorian one (Thai Buddhist: this year is 2569), then deletes it.</summary>
    private static void InLogsFolder(Action<string> test)
    {
        var dir = Path.Combine(Path.GetTempPath(), "mdm-logs-" + Guid.NewGuid().ToString("N"));
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("th-TH");
        try { test(dir); }
        finally
        {
            CultureInfo.CurrentCulture = culture;
            if (Directory.Exists(dir))
            {
                foreach (var file in Directory.GetFiles(dir)) File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(dir, true);
            }
        }
    }

    private static string OldLog(string dir, string stamp)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"file-dialog-crash-{stamp}.txt");
        File.WriteAllText(path, stamp);
        return path;
    }

    [Fact]
    public void TheLogHoldsTheDetailsUnderTheDateAndTime() => InLogsFolder(dir =>
    {
        var details = PickerCrashClues.Details(new PickerCrashFindings(unchecked((int)0xC0000005), null, FaultKind.None, Found()),
                                               "1.0.0-rc4", "Microsoft Windows NT 10.0.26200.0");
        var text = PickerCrashClues.LogText(details, Crashed);
        Assert.Equal("2026-09-25 09:37:00 UTC-05:00" + Environment.NewLine + details, text);
        var (path, note) = PickerCrashClues.SaveLog(dir, text, Crashed);
        Assert.Equal(text, File.ReadAllText(path!));
        Assert.Equal("The log was saved as " + path, note);
    });

    [Fact]
    public void CopyDetailsCopiesExactlyWhatTheLogHolds() => InLogsFolder(dir =>
    {
        var text = PickerCrashClues.LogText(PickerCrashClues.Details(new PickerCrashFindings(1, null, FaultKind.None, []), "v", "os"), Crashed);
        string? copied = null;
        Assert.Null(PickerCrashClues.CopyTo(text, s => copied = s));
        Assert.Equal(File.ReadAllText(PickerCrashClues.SaveLog(dir, text, Crashed).Path!), copied);
    });

    [Fact]
    public void TheLogIsNamedForTheLocalTimeOfTheCrashSoTheNamesSortByAge() => InLogsFolder(dir =>
        Assert.Equal(Path.Combine(dir, "file-dialog-crash-2026-09-25-093700.txt"), PickerCrashClues.SaveLog(dir, "x", Crashed).Path));

    [Fact]
    public void TwoCrashesInOneSecondKeepBothLogs() => InLogsFolder(dir =>
    {
        var first = PickerCrashClues.SaveLog(dir, "first", Crashed).Path;
        var second = PickerCrashClues.SaveLog(dir, "second", Crashed).Path;
        Assert.Equal(Path.Combine(dir, "file-dialog-crash-2026-09-25-093700.txt"), first);
        Assert.Equal(Path.Combine(dir, "file-dialog-crash-2026-09-25-093700-02.txt"), second);
        Assert.EndsWith("first", File.ReadAllText(first!));
        Assert.EndsWith("second", File.ReadAllText(second!));
    });

    [Fact]
    public void OnlyTheNewestTwentyOfOurOwnLogsAreKeptAndNothingElseIsTouched() => InLogsFolder(dir =>
    {
        var days = Enumerable.Range(1, 25).Select(day => OldLog(dir, $"2026-09-{day:00}-120000")).ToList();
        var sameSecond = OldLog(dir, "2026-09-07-120000-02");   // after 09-07's first log, before 09-08's
        string[] others = ["notes.txt", "file-dialog-crash-notes.txt", "file-dialog-crash-2026-09-01-120000.log",
                           "file-dialog-crash-2026-09-01-120000.txt.bak", "file-dialog-crash-2026-09-01-120000.txtx", "file-dialog-crashes.html",
                           "FILE-DIALOG-CRASH-2026-08-01-120000.TXT"];   // older than all of ours, so a case-blind match would delete it
        foreach (var other in others) File.WriteAllText(Path.Combine(dir, other), other);

        var saved = PickerCrashClues.SaveLog(dir, "x", Crashed).Path;

        Assert.All(days.Take(7), p => Assert.False(File.Exists(p), p));                          // 09-01 to 09-07
        Assert.All(days.Skip(7).Append(sameSecond).Append(saved!), p => Assert.True(File.Exists(p), p));   // 19 and the new one
        Assert.All(others, other => Assert.True(File.Exists(Path.Combine(dir, other)), other));
    });

    [Fact]
    public void ALogFromBeforeTheClockWentBackIsNotKeptInPlaceOfTheNewOne() => InLogsFolder(dir =>
    {
        for (var day = 1; day <= 20; day++) OldLog(dir, $"2030-01-{day:00}-120000");   // named later than now
        Assert.True(File.Exists(PickerCrashClues.SaveLog(dir, "x", Crashed).Path));
    });

    [Fact]
    public void ALogThatCannotBeDeletedStaysAndNeitherStopsTheRestNorThrows() => InLogsFolder(dir =>
    {
        var days = Enumerable.Range(1, 23).Select(day => OldLog(dir, $"2026-09-{day:00}-120000")).ToList();
        File.SetAttributes(days[2], FileAttributes.ReadOnly);                                // 09-03
        using (File.Open(days[1], FileMode.Open, FileAccess.Read, FileShare.Read))           // 09-02, open without FileShare.Delete
        {
            var (path, note) = PickerCrashClues.SaveLog(dir, "x", Crashed);
            Assert.True(File.Exists(path));
            Assert.Equal("The log was saved as " + path, note);
        }
        Assert.False(File.Exists(days[0]));                                                  // the ones either side still go
        Assert.False(File.Exists(days[3]));
        Assert.True(File.Exists(days[1]));
        Assert.True(File.Exists(days[2]));
    });

    [Fact]
    public void AnUnwritableLogsFolderFallsBackToCopyDetailsWithAMessage() => InLogsFolder(dir =>
    {
        Directory.CreateDirectory(dir);
        var blocked = Path.Combine(dir, "logs");
        File.WriteAllText(blocked, "a file where the folder should be");
        foreach (var logs in new[] { blocked, Path.Combine(blocked, "logs"), @"MarkdownMidget\logs" })   // the last: no app data folder
        {
            var (path, note) = PickerCrashClues.SaveLog(logs, "details", Crashed);
            Assert.Null(path);
            Assert.StartsWith("The log couldn't be saved (", note);
            Assert.EndsWith("). Copy details copies what it would have held.", note);
        }
        Assert.Equal(new[] { blocked }, Directory.GetFileSystemEntries(dir));   // nothing half-written left behind
    });
}
