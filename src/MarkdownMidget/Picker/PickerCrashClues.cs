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
    /// <summary>Recorded, but Windows couldn't name the file ("unknown").</summary>
    Unnamed,
    /// <summary>A file of Windows, .NET or this app: where the crash was reported, not where it began.</summary>
    SystemFile,
    /// <summary>Any other file: the best lead there is.</summary>
    AddOn,
}

/// <summary>A shell extension's DLL, what it hooks into, and the curated entry it matched.</summary>
internal sealed record ShellExtensionDll(string Path, string? Company, IReadOnlyList<string> Kinds, string? Culprit);

/// <summary>A curated entry: file-name prefixes (lower case) and/or a vendor regex matched as whole words.</summary>
internal sealed record CulpritEntry(string Product, string[] FilePrefixes, string? Company);

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

    private static readonly HashSet<string> RuntimeFiles = new(StringComparer.OrdinalIgnoreCase)
        { "coreclr.dll", "clrjit.dll", "hostfxr.dll", "hostpolicy.dll", "clr.dll" };

    public static FaultKind KindOf(FaultRecord? fault, string windowsDir, string exeName)
    {
        if (fault is null) return FaultKind.None;
        if (fault.Module.Length == 0 || fault.Module.Equals("unknown", StringComparison.OrdinalIgnoreCase)) return FaultKind.Unnamed;
        // An access violation in native code below a managed frame is recorded against .NET's own
        // runtime (measured on .NET 10, 2026-09-23), so these name the reporter, not the culprit.
        var path = fault.ModulePath ?? "";
        return RuntimeFiles.Contains(fault.Module) || fault.Module.Equals(exeName, StringComparison.OrdinalIgnoreCase)
               || path.StartsWith(windowsDir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)
               || path.Contains(@"\dotnet\shared\", StringComparison.OrdinalIgnoreCase)
            ? FaultKind.SystemFile
            : FaultKind.AddOn;
    }

    /// <summary>The notice's first clue, in words.</summary>
    public static string FaultSentence(PickerCrashFindings f) => f.FaultKind switch
    {
        FaultKind.AddOn =>
            $"Windows recorded the crash in {f.Fault!.Module}{(f.Fault.ModulePath is { } p ? $" ({p})" : "")}. " +
            "That file is the best lead: look for the program it belongs to below.",
        FaultKind.SystemFile =>
            $"Windows recorded the crash in {f.Fault!.Module}, which is part of Windows, .NET or Markdown Midget itself. " +
            "That is where the crash was reported, not where it began, so it doesn't name the add-on; the list below is the better lead.",
        FaultKind.Unnamed =>
            $"Windows recorded the crash (exception {f.Fault!.ExceptionCode}) but couldn't name the file it happened in.",
        _ => "Windows has no record of this crash in its Application log. If you closed the helper yourself, " +
             "or ended it in Task Manager, nothing crashed.",
    };

    // ===== 2. Shell extensions installed here =====

    private const string Overlays = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\ShellIconOverlayIdentifiers";
    private const string PreviewHandlers = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\PreviewHandlers";
    private const string PropertyHandlers = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\PropertySystem\PropertyHandlers";
    private const string Approved = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved";
    private static readonly string[] ContextMenuClasses = ["*", "AllFilesystemObjects", "Directory", @"Directory\Background", "Folder", "Drive"];

    /// <summary>
    /// Every shell extension DLL the registry names in the places a file dialog reaches — icon
    /// overlays, right-click menu handlers, preview and property handlers, and the Approved list
    /// for the rest — one entry per DLL that exists, with what it hooks into. <paramref name="file"/>
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

        foreach (var name in reg.SubKeyNames(Overlays)) Add(reg.Value($@"{Overlays}\{name}", ""), "icon overlay");
        foreach (var cls in ContextMenuClasses)
        {
            var handlers = $@"HKCR\{cls}\shellex\ContextMenuHandlers";
            foreach (var name in reg.SubKeyNames(handlers))
            {
                var value = reg.Value($@"{handlers}\{name}", "");
                Add(Guid.TryParse(value, out _) ? value : name, "right-click menu");   // some use the key's own name
            }
        }
        foreach (var clsid in reg.ValueNames(PreviewHandlers)) Add(clsid, "preview");
        foreach (var ext in reg.SubKeyNames(PropertyHandlers)) Add(reg.Value($@"{PropertyHandlers}\{ext}", ""), "properties");
        foreach (var clsid in reg.ValueNames(Approved)) Add(clsid, "other");

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
                return new ShellExtensionDll(p.Key, p.Value.Company, p.Value.Kinds.ToList(), MatchCulprit(p.Key, p.Value.Company));
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

    /// <summary>
    /// Shell extensions COMMONLY REPORTED to crash the programs that load them — never "known
    /// faulty": a match says only that this product has a public history of doing it. Each entry
    /// cites the report it rests on. Considered and left out for want of a report naming a crash:
    /// OneDrive, Box, MEGA, WinRAR, Bitdefender (NirSoft's only entry is the 2010 edition).
    /// NirSoft = "Shell extensions causing problems", https://shellfix.nirsoft.net/shell_problems_list.html
    /// </summary>
    public static readonly IReadOnlyList<CulpritEntry> Culprits =
    [
        // bugzilla.mozilla.org/show_bug.cgi?id=1330991 (Firefox and Thunderbird crashing in dropboxext*.dll); NirSoft
        new("Dropbox", ["dropboxext"], "Dropbox"),
        // support.google.com/drive/thread/102923206 "Access violation crash in drivefsext.dll"
        new("Google Drive", ["drivefsext", "googledrivesync"], "Google"),
        // NirSoft: ShellStreams64.dll, "Explorer crashes when you right-click on a file"
        new("iCloud", ["shellstreams"], "Apple"),
        // github.com/nextcloud/desktop/issues/6566: faulting module NCContextMenu.dll, c0000005
        new("Nextcloud", ["nccontextmenu", "ncoverlays"], "Nextcloud"),
        // NirSoft: TortoiseOverlays.dll and TortoiseStub.dll, "large delays and crashes"
        new("TortoiseSVN or TortoiseGit", ["tortoiseoverlays", "tortoisestub", "tortoisesvn", "tortoisegit"], null),
        // sourceforge.net/p/sevenzip/discussion/45797/thread/e38aca6c: Explorer crashes stopped with 7-Zip's shell integration off
        new("7-Zip", ["7-zip"], "Igor Pavlov"),
        // NirSoft: PDShell.dll, AIPreviewHandler.dll and Acrobat's ContextMenu*.dll crash Explorer
        new("Adobe Acrobat or Reader", ["pdshell", "pdfprevhndlr", "contextmenushim"], "Adobe"),
        // NirSoft: nvshext.dll and nv3dappshext.dll, "multiple crashes during right-click operations"
        new("NVIDIA", ["nvshext", "nv3dappshext"], "NVIDIA"),
        // NirSoft: igfxpph.dll and igfxDTCM.dll, "large delays and crashes"
        new("Intel graphics", ["igfxpph", "igfxdtcm"], null),
        // NirSoft: DBROverlayIconBackuped.dll, "Applications crash when opening or saving files"; Adobe's
        // Substance 3D Painter support page "Crash when opening or saving a file" names the same overlays
        new("Dell Backup and Recovery", ["dbroverlayicon"], null),
        // NirSoft: NavShExt.dll and Norton 360's tpShell.dll, "multiple crashes"
        new("Norton", ["navshext", "tpshell"], "Symantec|NortonLifeLock|Norton"),
        // NirSoft: McCtxMenuFrmWrk.dll, "Outlook and Explorer crashes during attachment operations"
        new("McAfee", ["mcctxmenufrmwrk"], "McAfee"),
        // NirSoft: ashShell.dll and avgsea.dll crash Explorer on right-click
        new("Avast or AVG", ["ashshell", "avgsea"], "AVAST|AVG"),
        // NirSoft: Kaspersky's shellex.dll — a name too generic to match on, so by vendor only
        new("Kaspersky", [], "Kaspersky"),
        // forums.malwarebytes.com/topic/236763 "mbshlext.dll crashing explorer.exe"
        new("Malwarebytes", ["mbshlext"], "Malwarebytes"),
    ];

    /// <summary>The curated product a DLL belongs to, by file name or by vendor as whole words
    /// ("Box, Inc." is not Dropbox), or null.</summary>
    public static string? MatchCulprit(string dllPath, string? company)
    {
        var name = Path.GetFileName(dllPath);
        foreach (var c in Culprits)
            if (c.FilePrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                || c.Company is not null && company is not null
                   && Regex.IsMatch(company, $@"\b(?:{c.Company})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                return c.Product;
        return null;
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
        text.AppendLine().AppendLine($"Add-ons installed here that are commonly reported to crash Windows' file dialogs ({suspects.Count}):");
        foreach (var s in suspects) text.AppendLine($"  {s.Culprit}: {Line(s)}");
        var others = f.Others.ToList();
        text.AppendLine().AppendLine($"Other add-ons, not from Microsoft ({others.Count}):");
        foreach (var o in others) text.AppendLine("  " + Line(o));
        return text.ToString();
    }

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
