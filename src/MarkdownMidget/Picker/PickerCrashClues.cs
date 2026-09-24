using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MarkdownMidget.Picker;

/// <summary>What Windows wrote when a process died: an "Application Error" event, id 1000.</summary>
internal sealed record FaultRecord(DateTime TimeUtc, string AppName, int? ProcessId, string Module, string? ModulePath, string ExceptionCode);

/// <summary>How much a <see cref="FaultRecord"/> tells the user. Only <see cref="AddOn"/> names a
/// lead; every other kind with a record says why it doesn't.</summary>
internal enum FaultKind
{
    /// <summary>No record: Windows logged nothing, or the helper was ended rather than crashed.</summary>
    None,
    /// <summary>A file on the list, a shell extension found here, or one from another vendor.</summary>
    AddOn,
    /// <summary>A Microsoft file in the Windows folder.</summary>
    Windows,
    /// <summary>A Microsoft file elsewhere (.NET's runtime, Office).</summary>
    Microsoft,
    /// <summary>Markdown Midget's own exe.</summary>
    ThisApp,
    /// <summary>Not on disk where Windows or the system folder says, or naming no vendor: whose it
    /// is can't be told, so it may yet be the add-on.</summary>
    Unidentified,
    /// <summary>Windows couldn't name the file ("unknown").</summary>
    Unnamed,
}

/// <summary>A shell extension's DLL, what it hooks into, its vendor and the curated entry it matched.</summary>
internal sealed record ShellExtensionDll(string Path, string? Company, IReadOnlyList<string> Kinds, string? Culprit);

/// <summary>What a registry scan found, and whether it ran out of time first.</summary>
internal sealed record ShellScan(IReadOnlyList<ShellExtensionDll> Found, bool CutShort);

/// <summary>A curated entry: the report it rests on and the DLL names that report gives, matched
/// whole; <paramref name="Prefix"/> only where a name carries a version.</summary>
internal sealed record CulpritEntry(string Product, string Source, string[] SourceDlls, string? Prefix = null);

/// <summary>Everything the notice shows and Copy details writes.</summary>
internal sealed record PickerCrashFindings(int ExitCode, FaultRecord? Fault, FaultKind FaultKind,
                                           IReadOnlyList<ShellExtensionDll> Extensions, bool ScanCutShort = false)
{
    public IEnumerable<ShellExtensionDll> Suspects => Extensions.Where(e => e.Culprit is not null);
    public IEnumerable<ShellExtensionDll> Others => Extensions.Where(e => e.Culprit is null && !PickerCrashClues.IsMicrosoft(e.Company));
}

/// <summary>Read-only registry access by full path ("HKLM\...", "HKCR\..."); "" names the default value.
/// Every member answers empty or null rather than throwing.</summary>
internal interface IRegistryView
{
    IReadOnlyList<string> SubKeyNames(string path);
    IReadOnlyList<string> ValueNames(string path);
    string? Value(string path, string name);
}

/// <summary>
/// The clues the file-dialog crash notice gives (PickerCrashDialog), as pure functions over
/// what <see cref="PickerCrashSources"/> reads from the machine. Nothing here loads, calls
/// or instantiates a shell extension: it reads the registry and file version resources only.
/// </summary>
internal static class PickerCrashClues
{
    // ===== 1. Windows' own record of the crash =====

    private static readonly XNamespace EventNs = "http://schemas.microsoft.com/win/2004/08/events/event";

    /// <summary>One event's XML (EventRecord.ToXml) as a fault record, or null for anything else.</summary>
    public static FaultRecord? ParseApplicationError(string xml)
    {
        try
        {
            var root = XDocument.Parse(xml).Root;
            var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in root?.Element(EventNs + "EventData")?.Elements(EventNs + "Data") ?? [])
                if (d.Attribute("Name")?.Value is { Length: > 0 } name) data[name] = d.Value.Trim();
            var time = root?.Element(EventNs + "System")?.Element(EventNs + "TimeCreated")?.Attribute("SystemTime")?.Value;
            if (!data.TryGetValue("AppName", out var app) || !data.TryGetValue("ModuleName", out var module)
                || !DateTime.TryParse(time, CultureInfo.InvariantCulture,
                                      DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var utc))
                return null;
            // Windows appends "_unloaded" when the DLL had already been unloaded (nextcloud/desktop#6566).
            if (module.EndsWith("_unloaded", StringComparison.OrdinalIgnoreCase)) module = module[..^"_unloaded".Length];
            int? pid = null;
            if (data.GetValueOrDefault("ProcessId") is { } p
                && (p.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                        ? int.TryParse(p[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var n)
                        : int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)))
                pid = n;
            var path = data.GetValueOrDefault("ModulePath");
            if (string.IsNullOrEmpty(path) || path.Equals("unknown", StringComparison.OrdinalIgnoreCase)) path = null;
            return new FaultRecord(utc, app, pid, module, path, data.GetValueOrDefault("ExceptionCode") ?? "");
        }
        catch { return null; }   // malformed XML, or anything else: not a record
    }

    /// <summary>The newest record for THIS helper — its exe, its process id when the event has one,
    /// no older than <paramref name="window"/>. Another window's helper is the same exe, so the id matters.</summary>
    public static FaultRecord? PickFault(IEnumerable<string> eventXml, string exeName, int processId, DateTime nowUtc, TimeSpan window)
    {
        FaultRecord? best = null;
        foreach (var xml in eventXml)
        {
            if (ParseApplicationError(xml) is not { } f
                || !f.AppName.Equals(exeName, StringComparison.OrdinalIgnoreCase)
                || f.ProcessId is { } pid && pid != processId
                || nowUtc - f.TimeUtc > window)
                continue;
            if (best is null || f.TimeUtc > best.TimeUtc) best = f;
        }
        return best;
    }

    /// <summary><see cref="PickFault"/> over a reader that may fail: a missing log or a denied read is "no record".</summary>
    public static FaultRecord? FindFault(Func<IEnumerable<string>> read, string exeName, int processId, DateTime nowUtc, TimeSpan window)
    {
        try { return PickFault(read(), exeName, processId, nowUtc, window); }
        catch { return null; }
    }

    private static bool Unnamed(FaultRecord fault) =>
        fault.Module.Length == 0 || fault.Module.Equals("unknown", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the faulting file names the culprit. The file decides before its folder: a graphics
    /// driver's shell extension lives under the Windows folder (DriverStore), and OneDrive's is
    /// Microsoft's. A bare module path (Windows names an unloaded DLL that way) is resolved against
    /// the system folder first, as registry entries are. <paramref name="file"/> gives whether the
    /// file is there and its vendor. .NET 10 records an access violation in native code under a
    /// managed frame against its own runtime (measured 2026-09-23): a Microsoft file, not a culprit.
    /// </summary>
    public static FaultKind KindOf(FaultRecord? fault, string systemDir, string exeName, IEnumerable<ShellExtensionDll> found,
                                   Func<string, (bool Exists, string? Company)> file)
    {
        if (fault is null) return FaultKind.None;
        if (Unnamed(fault)) return FaultKind.Unnamed;
        if (fault.Module.Equals(exeName, StringComparison.OrdinalIgnoreCase)) return FaultKind.ThisApp;
        var bare = !Path.IsPathRooted(fault.ModulePath ?? fault.Module);
        var path = DllPath(fault.ModulePath ?? fault.Module, systemDir) ?? fault.Module;
        if (MatchCulprit(path) is not null) return FaultKind.AddOn;          // the list wins, even for a file that's gone
        var windowsDir = Path.GetDirectoryName(systemDir.TrimEnd('\\')) ?? systemDir;
        bool InWindows(string p) => p.StartsWith(windowsDir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
        // A shell extension this scan found, other than Windows' own, is an add-on; a bare name
        // (the file may be gone from where Windows loaded it) is matched by file name.
        if (found.Any(e => !(IsMicrosoft(e.Company) && InWindows(e.Path))
                           && (bare ? Path.GetFileName(e.Path).Equals(Path.GetFileName(path), StringComparison.OrdinalIgnoreCase)
                                    : e.Path.Equals(path, StringComparison.OrdinalIgnoreCase))))
            return FaultKind.AddOn;
        (bool Exists, string? Company) facts;
        try { facts = file(path); } catch { facts = (false, null); }
        // Every Windows file names Microsoft as its vendor, so a file that names none, even in
        // System32, can't be called Windows'; nor can one that isn't there (it names no vendor).
        if (facts.Company is null) return FaultKind.Unidentified;
        if (IsMicrosoft(facts.Company)) return InWindows(path) ? FaultKind.Windows : FaultKind.Microsoft;
        return FaultKind.AddOn;
    }

    /// <summary>The notice's first clue, in words.</summary>
    public static string FaultSentence(PickerCrashFindings f)
    {
        if (f.Fault is not { } fault || f.FaultKind == FaultKind.None)
            return "Windows has no record of this crash in its Application log. If you closed the helper yourself, " +
                   "or ended it in Task Manager, nothing crashed.";
        if (f.FaultKind == FaultKind.AddOn)
        {
            // A bare module path shows the path the scan found for that file name, if any.
            var shown = fault.ModulePath is { } p && Path.IsPathRooted(p) ? p
                : f.Extensions.FirstOrDefault(e => Path.GetFileName(e.Path).Equals(fault.Module, StringComparison.OrdinalIgnoreCase))?.Path;
            return $"Windows recorded the crash in {fault.Module}{(shown is null ? "" : $" ({shown})")}. That file is the best lead: " +
                   (MatchCulprit(fault.Module) is { } product
                       ? $"it comes with {product}."
                       : "the Details tab of its Properties in Explorer names the program it came with.");
        }
        if (f.FaultKind == FaultKind.Unidentified)
            return $"Windows recorded the crash in {fault.Module}, but Markdown Midget can't tell whose file that is. " +
                   "If it isn't part of Windows, it may be the add-on.";
        var what = f.FaultKind switch
        {
            FaultKind.Unnamed => $"a file it couldn't name (exception {fault.ExceptionCode})",
            FaultKind.Windows => $"{fault.Module}, part of Windows",
            FaultKind.Microsoft => $"{fault.Module}, a Microsoft file",
            _ => $"{fault.Module}, Markdown Midget itself",
        };
        return $"Windows recorded the crash in {what}. That doesn't name the add-on: Windows records where a crash was reported, " +
               "which is not always where it began, so the list below is the better lead.";
    }

    // ===== 2. Shell extensions installed here =====

    private const string CurrentVersion = @"SOFTWARE\Microsoft\Windows\CurrentVersion";
    private static readonly string[] ContextMenuClasses = ["*", "AllFilesystemObjects", "Directory", @"Directory\Background", "Folder", "Drive"];
    private const string ThumbnailHandler = "{e357fccd-a995-4576-b01f-234630154e96}";

    /// <summary>How long each of the registry scan's two steps may take before it stops and says so.</summary>
    public static readonly TimeSpan ScanBudget = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Every shell extension DLL the registry names in the places a file dialog reaches — icon
    /// overlays, right-click menu handlers, preview and property handlers, and the Approved list,
    /// each for the machine (HKLM) and for this user (HKCU, where per-user installs register), and
    /// the thumbnail handler of every file type (HKCR\.ext and HKCR\SystemFileAssociations\*),
    /// which the Approved list does not reliably hold. A thumbnail handler that reads a stream
    /// usually runs in a COM Surrogate (dllhost.exe), and only the in-process kind can take the
    /// helper down, but the registry doesn't say which kind a handler is, so all are listed.
    /// One entry per DLL that exists, with what it hooks into. <paramref name="file"/> answers
    /// whether a DLL is there and its vendor (the version resource), and is never asked to load
    /// it. The listed handlers are resolved first, right-click menus first (most entries live
    /// there), on one deadline from <paramref name="startDeadline"/>; the thumbnail step then gets
    /// a deadline of its own, so neither can spend the other's time. Whatever was found before a
    /// deadline passed is kept, and the scan says it was cut short.
    /// </summary>
    public static ShellScan FindShellExtensions(IRegistryView reg, Func<string, (bool Exists, string? Company)> file, string systemDir,
                                                Func<Func<bool>> startDeadline)
    {
        var handlers = new List<(string Clsid, string Kind)>();
        void Add(string? clsid, string kind)
        {
            if (Guid.TryParse(clsid?.Trim(), out var guid)) handlers.Add((guid.ToString("B"), kind));
        }
        foreach (var cls in ContextMenuClasses)
        {
            var menus = $@"HKCR\{cls}\shellex\ContextMenuHandlers";
            foreach (var name in reg.SubKeyNames(menus))
            {
                var value = reg.Value($@"{menus}\{name}", "");
                Add(Guid.TryParse(value, out _) ? value : name, "right-click menu");   // some use the key's own name
            }
        }
        foreach (var hive in new[] { "HKLM", "HKCU" })
        {
            var overlays = $@"{hive}\{CurrentVersion}\Explorer\ShellIconOverlayIdentifiers";
            foreach (var name in reg.SubKeyNames(overlays)) Add(reg.Value($@"{overlays}\{name}", ""), "icon overlay");
            foreach (var clsid in reg.ValueNames($@"{hive}\{CurrentVersion}\PreviewHandlers")) Add(clsid, "preview");
            var properties = $@"{hive}\{CurrentVersion}\PropertySystem\PropertyHandlers";
            foreach (var ext in reg.SubKeyNames(properties)) Add(reg.Value($@"{properties}\{ext}", ""), "properties");
            foreach (var clsid in reg.ValueNames($@"{hive}\{CurrentVersion}\Shell Extensions\Approved")) Add(clsid, "other");
        }

        var byDll = new Dictionary<string, (SortedSet<string> Kinds, string? Company)>(StringComparer.OrdinalIgnoreCase);
        void Record(string clsid, string kind)
        {
            if (DllPath(reg.Value($@"HKCR\CLSID\{clsid}\InprocServer32", ""), systemDir) is not { } dll) return;
            if (!byDll.TryGetValue(dll, out var entry))
            {
                (bool Exists, string? Company) facts;
                try { facts = file(dll); } catch { return; }   // unreadable: can't be shown or matched
                if (!facts.Exists) return;                      // a DLL that isn't there can't crash anything
                byDll[dll] = entry = (new SortedSet<string>(StringComparer.Ordinal), facts.Company);
            }
            entry.Kinds.Add(kind);
        }

        var cutShort = false;
        var outOfTime = startDeadline();
        foreach (var (clsid, kind) in handlers)
        {
            if (outOfTime()) { cutShort = true; break; }
            Record(clsid, kind);
        }
        outOfTime = startDeadline();
        var fileTypes = reg.SubKeyNames("HKCR").Where(n => n.StartsWith('.')).Select(n => $@"HKCR\{n}")
            .Concat(reg.SubKeyNames(@"HKCR\SystemFileAssociations").Select(n => $@"HKCR\SystemFileAssociations\{n}"));
        foreach (var type in fileTypes)
        {
            if (outOfTime()) { cutShort = true; break; }
            if (Guid.TryParse(reg.Value($@"{type}\ShellEx\{ThumbnailHandler}", "")?.Trim(), out var guid)) Record(guid.ToString("B"), "thumbnail");
        }

        var found = byDll
            .Select(p =>
            {
                if (p.Value.Kinds.Count > 1) p.Value.Kinds.Remove("other");   // "other" only when nothing more specific
                return new ShellExtensionDll(p.Key, p.Value.Company, p.Value.Kinds.ToList(), MatchCulprit(p.Key));
            })
            .OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new ShellScan(found, cutShort);
    }

    private static string? DllPath(string? value, string systemDir)
    {
        var path = Environment.ExpandEnvironmentVariables(value?.Trim().Trim('"') ?? "");
        if (path.Length == 0) return null;
        try { return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(systemDir, path)); }   // no "..\" escapes
        catch { return null; }
    }

    private const string NirSoft = "https://shellfix.nirsoft.net/shell_problems_list.html";

    /// <summary>
    /// Shell extensions REPORTED TO CRASH EXPLORER OR PROGRAMS THAT LOAD THEM — never "known
    /// faulty": a match says only that the file has that public history. The rule for a place on
    /// the list: a public report names the DLL, by file, in a crash (a slowdown or a missing menu
    /// is not enough); the name is distinctive enough to match on by itself; and the product is
    /// widely installed on home and office PCs, because the list's job is to name what the user
    /// probably has. Names are matched whole, except Dropbox's, which carries its version
    /// (DropboxExt64.52.dll) and so is matched by prefix; a vendor's other DLLs never get the name.
    /// Out by that rule: 7-Zip (NirSoft reports 7-zip32.dll only for delays; the SourceForge
    /// thread's faulting module was ntdll.dll); Kaspersky (NirSoft has an Explorer crash for its
    /// shellex.dll, but the name is too generic to match on); TortoiseOverlays.dll, igfxDTCM.dll
    /// and pdfprevhndlr.dll (delays or menus only); WinCDEmu, VirtualCloneDrive, F-Secure and ABBYY
    /// FineReader (crash reports, but less widely installed); OneDrive, Box, MEGA and WinRAR (no
    /// report found naming their DLL in a crash). Each record was checked against its source by hand.
    /// </summary>
    public static readonly IReadOnlyList<CulpritEntry> Culprits =
    [
        new("Dropbox", "https://bugzilla.mozilla.org/show_bug.cgi?id=1330991; " + NirSoft,
            ["dropboxext.8.0.dll", "dropboxext64.8.0.dll", "dropboxext.10.0.dll", "dropboxext64.10.0.dll", "DropboxExt64.27.dll"], "dropboxext"),
        new("Google Drive", "https://support.google.com/drive/thread/102923206; " + NirSoft, ["drivefsext.dll", "googledrivesync32.dll"]),
        new("iCloud", NirSoft, ["ShellStreams64.dll"]),
        new("Nextcloud", "https://github.com/nextcloud/desktop/issues/6566", ["NCContextMenu.dll"]),
        new("TortoiseSVN", NirSoft, ["TortoiseStub.dll"]),
        new("Adobe Acrobat or Reader", NirSoft, ["PDFShell.dll", "ContextMenuShim64.dll"]),
        new("Adobe Illustrator", NirSoft, ["AIPreviewHandler.dll"]),
        new("Foxit PDF", NirSoft, ["ConvertToPDFShellExtension_x64.dll"]),
        new("NVIDIA", NirSoft, ["nvshext.dll", "nv3dappshext.dll"]),
        new("Intel graphics", NirSoft, ["igfxpph.dll"]),
        // Also Adobe's Substance 3D Painter page "Crash when opening or saving a file", which names these overlays.
        new("Dell Backup and Recovery", NirSoft, ["DBROverlayIconBackuped.dll"]),
        new("Norton", NirSoft, ["NavShExt.dll", "tpShell.dll"]),
        new("McAfee", NirSoft, ["McCtxMenuFrmWrk.dll"]),
        new("Avast or AVG", NirSoft, ["ashShell.dll", "avgsea.dll"]),
        new("Bitdefender", NirSoft, ["bdshellext.dll"]),
        new("Malwarebytes", "https://forums.malwarebytes.com/topic/236763-mbshlextdll-crashing-explorerexe-in-windows-7/", ["mbshlext.dll"]),
    ];

    /// <summary>The curated product a DLL is, by its file name alone, or null.</summary>
    public static string? MatchCulprit(string dllPath)
    {
        var name = Path.GetFileName(dllPath);
        return Culprits.FirstOrDefault(c => c.SourceDlls.Any(d => d.Equals(name, StringComparison.OrdinalIgnoreCase))
                                            || c.Prefix is { } prefix && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?.Product;
    }

    public static bool IsMicrosoft(string? company) =>
        company is not null && Regex.IsMatch(company, @"\bMicrosoft\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // ===== Copy details =====

    private static string Line(ShellExtensionDll e) =>
        $"{e.Path} ({string.Join(", ", e.Kinds)}; {e.Company ?? "no vendor named"})";

    /// <summary>All of it as plain text, for a bug report.</summary>
    public static string Details(PickerCrashFindings f, string appVersion, string osVersion)
    {
        var text = new StringBuilder()
            .AppendLine($"Markdown Midget {appVersion}: Windows' file dialog closed unexpectedly")
            .AppendLine($"Windows: {osVersion}")
            .AppendLine($"The helper's exit code: 0x{f.ExitCode:X8}")
            .AppendLine()
            .AppendLine("What Windows recorded:")
            .AppendLine("  " + FaultSentence(f));
        if (f.Fault is { } fault)
            text.AppendLine($"  Module {fault.Module}, path {fault.ModulePath ?? "not given"}, exception {fault.ExceptionCode}, " +
                            $"at {fault.TimeUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} UTC");
        var suspects = f.Suspects.ToList();
        text.AppendLine().AppendLine($"Add-ons found here that are reported to crash Explorer or programs that load them ({suspects.Count}):");
        foreach (var s in suspects) text.AppendLine($"  {s.Culprit}: {Line(s)}");
        var others = f.Others.ToList();
        text.AppendLine().AppendLine($"Other add-ons, not from Microsoft ({others.Count}):");
        foreach (var o in others) text.AppendLine("  " + Line(o));
        if (f.ScanCutShort) text.AppendLine().AppendLine(CutShortNote(f).Trim());
        return text.ToString();
    }

    /// <summary>The notice's list: one line per match, product first.</summary>
    public static string SuspectLines(PickerCrashFindings f) =>
        (f.Suspects.Any()
            ? string.Join("\n", f.Suspects.Select(s => $"• {s.Culprit}: {Path.GetFileName(s.Path)} ({string.Join(", ", s.Kinds)})"))
            : "None of the add-ons on our list were found.") + CutShortNote(f);

    /// <summary>The notice's count of the rest; Copy details names them.</summary>
    public static string OthersLine(PickerCrashFindings f) => f.Others.Count() switch
    {
        0 => "No other add-ons from outside Microsoft were found.",
        1 => "One other add-on from outside Microsoft was found; Copy details names it.",
        var n => $"{n} other add-ons from outside Microsoft were found; Copy details names them all.",
    } + CutShortNote(f);

    // No number: each step has a deadline of its own, so the search can run past one budget.
    private static string CutShortNote(PickerCrashFindings f) => f.ScanCutShort
        ? " The search ran out of time before it finished, so there may be more."
        : "";

    /// <summary>Copy details' clipboard write, <paramref name="setText"/> being Clipboard.SetText outside
    /// tests. Null when copied; otherwise the note to show (the same seam as MainWindow.CopyLinkTo).</summary>
    public static string? CopyTo(string text, Action<string> setText)
    {
        try { setText(text); return null; }
        catch (ExternalException) { return "The details weren't copied: another program is using the clipboard. Try again."; }
    }

    // ===== 3. The folder to open in Explorer =====

    /// <summary>The folder the dialog opened in; else the first recent folder that exists (the open
    /// document's folder leads that list); else Documents.</summary>
    public static string FolderToShow(FilePickerRequest request, Func<string, bool> dirExists, string documents) =>
        new[] { request.InitialDirectory }.Concat(request.RecentFolders)
            .FirstOrDefault(d => !string.IsNullOrEmpty(d) && dirExists(d)) ?? documents;

    /// <summary>explorer.exe's command line for a folder: always quoted, because Explorer splits its
    /// own command line on commas as well as spaces. A trailing backslash goes, except on a root.</summary>
    public static string ExplorerArguments(string folder) =>
        "\"" + (folder.Length > 3 ? folder.TrimEnd('\\') : folder) + "\"";

    // ===== 4. The guide =====

    /// <summary>The embedded HTML guide's resource name, and its file name once extracted.</summary>
    public const string GuideResource = "file-dialog-crashes.html";

    /// <summary>Write the guide into <paramref name="dataDir"/> unless the same bytes are already
    /// there, and return its path; null when the resource is missing.</summary>
    public static string? ExtractGuide(Func<Stream?> openResource, string dataDir)
    {
        using var stream = openResource();
        if (stream is null) return null;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var bytes = buffer.ToArray();
        Directory.CreateDirectory(dataDir);
        var path = Path.Combine(dataDir, GuideResource);
        if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) File.WriteAllBytes(path, bytes);
        return path;
    }
}
