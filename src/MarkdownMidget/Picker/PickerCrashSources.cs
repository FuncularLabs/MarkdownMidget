using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Threading;
using Microsoft.Win32;

namespace MarkdownMidget.Picker;

/// <summary>
/// The only code that reads the real machine for the crash notice: the Application event log,
/// the registry (read-only) and DLL version resources. Tests never run it; they drive
/// <see cref="PickerCrashClues"/> with captured data instead. Every read here degrades to
/// "nothing found" — a notice that throws would be worse than one with fewer clues.
/// </summary>
internal static class PickerCrashSources
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(2);

    /// <summary>Gather every clue; slow enough (registry walk, version resources) to belong off the UI thread.</summary>
    public static PickerCrashFindings Gather(int exitCode, int processId)
    {
        var exe = Path.GetFileName(Environment.ProcessPath ?? "MarkdownMidget.exe");
        var fault = PickerCrashClues.FindFault(RecentApplicationErrors, exe, processId, DateTime.UtcNow, Window);
        if (fault is null)
        {
            // Windows Error Reporting usually writes before the process is gone (measured), but it
            // can queue: one more look before settling on "no record".
            Thread.Sleep(1500);
            fault = PickerCrashClues.FindFault(RecentApplicationErrors, exe, processId, DateTime.UtcNow, Window);
        }
        IReadOnlyList<ShellExtensionDll> found;
        try { found = PickerCrashClues.FindShellExtensions(new RegistryView(), FileFacts, Environment.SystemDirectory); }
        catch { found = []; }
        var windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var company = fault?.ModulePath is { } path ? FileFacts(path).Item2 : null;
        return new PickerCrashFindings(exitCode, fault, PickerCrashClues.KindOf(fault, windowsDir, exe, found, company), found);
    }

    /// <summary>The newest "Application Error" (1000) events of the last few minutes, as XML.</summary>
    private static IEnumerable<string> RecentApplicationErrors()
    {
        var xml = new List<string>();
        var query = new EventLogQuery("Application", PathType.LogName,
            "*[System[Provider[@Name='Application Error'] and (EventID=1000) and " +
            $"TimeCreated[timediff(@SystemTime) <= {(long)Window.TotalMilliseconds}]]]") { ReverseDirection = true };
        using var reader = new EventLogReader(query);
        for (var i = 0; i < 50 && reader.ReadEvent() is { } record; i++)
            using (record)
                try { xml.Add(record.ToXml()); } catch { /* one unreadable event must not hide the rest */ }
        return xml;
    }

    /// <summary>Whether the DLL is there, and the vendor its version resource names. The file is
    /// mapped as data by Windows to read that resource; its code is never loaded or run.</summary>
    private static (bool, string?) FileFacts(string path)
    {
        if (!File.Exists(path)) return (false, null);
        try { return (true, FileVersionInfo.GetVersionInfo(path).CompanyName?.Trim() is { Length: > 0 } c ? c : null); }
        catch { return (true, null); }
    }

    private sealed class RegistryView : IRegistryView
    {
        private static RegistryKey? Open(string path)
        {
            var cut = path.IndexOf('\\');
            var hive = path[..cut] switch
            {
                "HKLM" => Registry.LocalMachine,
                "HKCR" => Registry.ClassesRoot,
                "HKCU" => Registry.CurrentUser,
                _ => null,
            };
            return hive?.OpenSubKey(path[(cut + 1)..], writable: false);
        }

        public IReadOnlyList<string> SubKeyNames(string path)
        {
            try { using var key = Open(path); return key?.GetSubKeyNames() ?? []; }
            catch { return []; }
        }

        public IReadOnlyList<string> ValueNames(string path)
        {
            try { using var key = Open(path); return Array.FindAll(key?.GetValueNames() ?? [], n => n.Length > 0); }
            catch { return []; }
        }

        public string? Value(string path, string name)
        {
            try { using var key = Open(path); return key?.GetValue(name) as string; }
            catch { return null; }
        }
    }
}
