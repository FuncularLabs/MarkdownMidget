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

/// <summary>How much a <see cref="FaultRecord"/> tells the user.</summary>
internal enum FaultKind
{
    /// <summary>No record: Windows logged nothing, or the helper was ended rather than crashed.</summary>
    None,
    /// <summary>A file of Windows, .NET or this app, or one Windows couldn't name: where the
    /// crash was reported, not where it began.</summary>
    SystemFile,
    /// <summary>Any other file: the best lead there is.</summary>
    AddOn,
}

/// <summary>A shell extension's DLL, what it hooks into, its vendor and the curated entry it matched.</summary>
internal sealed record ShellExtensionDll(string Path, string? Company, IReadOnlyList<string> Kinds, string? Culprit);

/// <summary>A curated entry: the report it rests on, the DLL names that report gives, and the
/// file-name prefixes (lower case) that match them.</summary>
internal sealed record CulpritEntry(string Product, string[] FilePrefixes, string Source, string[] SourceDlls);

/// <summary>Everything the notice shows and Copy details writes.</summary>
internal sealed record PickerCrashFindings(int ExitCode, FaultRecord? Fault, FaultKind FaultKind, IReadOnlyList<ShellExtensionDll> Extensions)
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
    /// Microsoft's. <paramref name="company"/> is the file's vendor, null when unreadable.
    /// .NET 10 records an access violation in native code under a managed frame against its own
    /// runtime (measured 2026-09-23), which is Microsoft's and so counts as a reporter, not a culprit.
    /// </summary>
    public static FaultKind KindOf(FaultRecord? fault, string windowsDir, string exeName, IEnumerable<ShellExtensionDll> found, string? company)
    {
        if (fault is null) return FaultKind.None;
        if (Unnamed(fault) || fault.Module.Equals(exeName, StringComparison.OrdinalIgnoreCase)) return FaultKind.SystemFile;
        var path = fault.ModulePath ?? fault.Module;
        var inWindows = path.StartsWith(windowsDir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
        if (MatchCulprit(path) is not null
            || !inWindows && found.Any(e => e.Path.Equals(path, StringComparison.OrdinalIgnoreCase))
            || company is not null && !IsMicrosoft(company))
            return FaultKind.AddOn;
        return IsMicrosoft(company) || inWindows ? FaultKind.SystemFile : FaultKind.AddOn;
    }

    /// <summary>The notice's first clue, in words.</summary>
    public static string FaultSentence(PickerCrashFindings f) => (f.FaultKind, f.Fault) switch
    {
        (FaultKind.AddOn, { } fault) =>
            $"Windows recorded the crash in {fault.Module}{(fault.ModulePath is { } p ? $" ({p})" : "")}. That file is the best lead: " +
            (MatchCulprit(fault.ModulePath ?? fault.Module) is { } product
                ? $"it comes with {product}."
                : "the Details tab of its Properties in Explorer names the program it came with."),
        (FaultKind.SystemFile, { } fault) =>
            $"Windows recorded the crash in {(Unnamed(fault) ? $"a file it couldn't name (exception {fault.ExceptionCode})" : $"{fault.Module}, part of Windows, .NET or Markdown Midget itself")}. " +
            "That doesn't name the add-on: Windows records where a crash was reported, which is not always where it began, so the list below is the better lead.",
        _ => "Windows has no record of this crash in its Application log. If you closed the helper yourself, " +
             "or ended it in Task Manager, nothing crashed.",
    };

    // ===== 2. Shell extensions installed here =====

    private const string CurrentVersion = @"SOFTWARE\Microsoft\Windows\CurrentVersion";
    private static readonly string[] ContextMenuClasses = ["*", "AllFilesystemObjects", "Directory", @"Directory\Background", "Folder", "Drive"];

    /// <summary>
    /// Every shell extension DLL the registry names in the places a file dialog reaches — icon
    /// overlays, right-click menu handlers, preview and property handlers, and the Approved list
    /// for the rest, each for the machine (HKLM) and for this user (HKCU, where per-user installs
    /// register) — one entry per DLL that exists, with what it hooks into. <paramref name="file"/>
    /// answers whether a DLL is there and its vendor (the version resource), and is never asked to load it.
    /// </summary>
    public static IReadOnlyList<ShellExtensionDll> FindShellExtensions(IRegistryView reg, Func<string, (bool Exists, string? Company)> file, string systemDir)
    {
        var kindsByClsid = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
        void Add(string? clsid, string kind)
        {
            if (!Guid.TryParse(clsid?.Trim(), out var guid)) return;
            var key = guid.ToString("B");
            if (!kindsByClsid.TryGetValue(key, out var kinds)) kindsByClsid[key] = kinds = new(StringComparer.Ordinal);
            kinds.Add(kind);
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
        foreach (var cls in ContextMenuClasses)
        {
            var handlers = $@"HKCR\{cls}\shellex\ContextMenuHandlers";
            foreach (var name in reg.SubKeyNames(handlers))
            {
                var value = reg.Value($@"{handlers}\{name}", "");
                Add(Guid.TryParse(value, out _) ? value : name, "right-click menu");   // some use the key's own name
            }
        }

        var byDll = new Dictionary<string, (SortedSet<string> Kinds, string? Company)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (clsid, kinds) in kindsByClsid)
        {
            if (DllPath(reg.Value($@"HKCR\CLSID\{clsid}\InprocServer32", ""), systemDir) is not { } dll) continue;
            if (!byDll.TryGetValue(dll, out var entry))
            {
                (bool Exists, string? Company) facts;
                try { facts = file(dll); } catch { continue; }   // unreadable: can't be shown or matched
                if (!facts.Exists) continue;                      // a DLL that isn't there can't crash anything
                byDll[dll] = entry = (new SortedSet<string>(StringComparer.Ordinal), facts.Company);
            }
            entry.Kinds.UnionWith(kinds);
        }

        return byDll
            .Select(p =>
            {
                if (p.Value.Kinds.Count > 1) p.Value.Kinds.Remove("other");   // "other" only when nothing more specific
                return new ShellExtensionDll(p.Key, p.Value.Company, p.Value.Kinds.ToList(), MatchCulprit(p.Key));
            })
            .OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? DllPath(string? value, string systemDir)
    {
        var path = Environment.ExpandEnvironmentVariables(value?.Trim().Trim('"') ?? "");
        if (path.Length == 0) return null;
        try { return Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.Combine(systemDir, path); }
        catch { return null; }
    }

    private const string NirSoft = "https://shellfix.nirsoft.net/shell_problems_list.html";

    /// <summary>
    /// Shell extensions REPORTED TO CRASH EXPLORER OR PROGRAMS THAT LOAD THEM — never "known
    /// faulty": a match says only that the file has that public history. The rule for a place on
    /// the list: a public report names the DLL, by file, in a crash (a slowdown or a missing menu
    /// is not enough), and the name is distinctive enough to match on by itself. Matching is by
    /// file name only, so a vendor's other DLLs are never given this product's name. By that rule
    /// these are out: 7-Zip (NirSoft reports 7-zip32.dll only for delays; the SourceForge thread's
    /// faulting module was ntdll.dll), Kaspersky (its row is a missing menu, and shellex.dll is too
    /// generic), TortoiseOverlays.dll, igfxDTCM.dll and pdfprevhndlr.dll (delays or menus only),
    /// and OneDrive, Box, MEGA and WinRAR (no report found naming their DLL in a crash).
    /// </summary>
    public static readonly IReadOnlyList<CulpritEntry> Culprits =
    [
        new("Dropbox", ["dropboxext"], "https://bugzilla.mozilla.org/show_bug.cgi?id=1330991",
            ["dropboxext.8.0.dll", "dropboxext64.8.0.dll", "dropboxext.10.0.dll", "dropboxext64.10.0.dll", "DropboxExt64.27.dll"]),
        new("Google Drive", ["drivefsext", "googledrivesync"], "https://support.google.com/drive/thread/102923206; " + NirSoft,
            ["drivefsext.dll", "googledrivesync32.dll"]),
        new("iCloud", ["shellstreams"], NirSoft, ["ShellStreams64.dll"]),
        new("Nextcloud", ["nccontextmenu"], "https://github.com/nextcloud/desktop/issues/6566", ["NCContextMenu.dll"]),
        new("TortoiseSVN", ["tortoisestub"], NirSoft, ["TortoiseStub.dll"]),
        new("Adobe Acrobat or Reader", ["pdfshell", "contextmenushim"], NirSoft, ["PDFShell.dll", "ContextMenuShim64.dll"]),
        new("Foxit PDF", ["converttopdfshellextension"], NirSoft, ["ConvertToPDFShellExtension_x64.dll"]),
        new("NVIDIA", ["nvshext", "nv3dappshext"], NirSoft, ["nvshext.dll", "nv3dappshext.dll"]),
        new("Intel graphics", ["igfxpph"], NirSoft, ["igfxpph.dll"]),
        // Also Adobe's Substance 3D Painter page "Crash when opening or saving a file", which names these overlays.
        new("Dell Backup and Recovery", ["dbroverlayicon"], NirSoft, ["DBROverlayIconBackuped.dll"]),
        new("Norton", ["navshext", "tpshell"], NirSoft, ["NavShExt.dll", "tpShell.dll"]),
        new("McAfee", ["mcctxmenufrmwrk"], NirSoft, ["McCtxMenuFrmWrk.dll"]),
        new("Avast or AVG", ["ashshell", "avgsea"], NirSoft, ["ashShell.dll", "avgsea.dll"]),
        new("Bitdefender", ["bdshellext"], NirSoft, ["bdshellext.dll"]),
        new("Malwarebytes", ["mbshlext"], "https://forums.malwarebytes.com/topic/236763-mbshlextdll-crashing-explorerexe-in-windows-7/",
            ["mbshlext.dll"]),
    ];

    /// <summary>The curated product a DLL is, by its file name alone, or null.</summary>
    public static string? MatchCulprit(string dllPath)
    {
        var name = Path.GetFileName(dllPath);
        return Culprits.FirstOrDefault(c => c.FilePrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))?.Product;
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
        return text.ToString();
    }

    /// <summary>The notice's list: one line per match, product first.</summary>
    public static string SuspectLines(PickerCrashFindings f) =>
        f.Suspects.Any()
            ? string.Join("\n", f.Suspects.Select(s => $"• {s.Culprit}: {Path.GetFileName(s.Path)} ({string.Join(", ", s.Kinds)})"))
            : "None of the add-ons on our list were found.";

    /// <summary>The notice's count of the rest; Copy details names them.</summary>
    public static string OthersLine(PickerCrashFindings f) => f.Others.Count() switch
    {
        0 => "No other add-ons from outside Microsoft were found.",
        1 => "One other add-on from outside Microsoft was found; Copy details names it.",
        var n => $"{n} other add-ons from outside Microsoft were found; Copy details names them all.",
    };

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
