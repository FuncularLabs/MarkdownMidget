using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MarkdownMidget;

/// <summary>
/// Per-user Windows integration for Markdown Midget: register the current .exe
/// as an editor for .md files, dedupe stale references, optionally copy to
/// AppData and create a Start-menu shortcut.
///
/// Everything writes to HKCU / LocalAppData / per-user Start Menu — no admin.
/// </summary>
internal static class RegistrationService
{
    // Stable ProgID: re-registering overwrites the same key, so we can't
    // accidentally create duplicate "MarkdownMidget" entries in Open With.
    private const string ProgId = "MarkdownMidget.Document";
    private const string DisplayName = "Markdown Midget";
    private const string DocTypeName = "Markdown Document";
    private const string ExeCanonicalName = "MarkdownMidget.exe";
    private const string StartMenuLinkName = "Markdown Midget.lnk";

    public static string CurrentExePath =>
        Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine current exe path.");

    public static string AppDataInstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "MarkdownMidget");

    public static string AppDataInstallExe => Path.Combine(AppDataInstallDir, ExeCanonicalName);

    public static string StartMenuLinkPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), StartMenuLinkName);

    public static bool IsInstalledToAppData() => File.Exists(AppDataInstallExe);

    public static bool IsRunningFromAppDataInstall()
    {
        try
        {
            var here = Path.GetFullPath(CurrentExePath);
            return string.Equals(here, Path.GetFullPath(AppDataInstallExe),
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static bool IsRegistered()
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + ProgId);
        return k is not null;
    }

    // ===== Register / Unregister =====

    /// <summary>
    /// Register <paramref name="exePath"/> as an editor for .md. Idempotent —
    /// existing entries under our ProgID (and stale strays) are cleaned first.
    /// </summary>
    public static void Register(string exePath)
    {
        DedupeStrays();

        // Our ProgID (the modern, canonical entry).
        using (var progKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + ProgId)!)
        {
            progKey.SetValue(string.Empty, DocTypeName);
            progKey.SetValue("FriendlyTypeName", DocTypeName);
            using (var icon = progKey.CreateSubKey("DefaultIcon")!)
                icon.SetValue(string.Empty, $"\"{exePath}\",0");
            using (var open = progKey.CreateSubKey(@"shell\open"))
                open!.SetValue("FriendlyAppName", DisplayName);
            using (var cmd = progKey.CreateSubKey(@"shell\open\command")!)
                cmd.SetValue(string.Empty, $"\"{exePath}\" \"%1\"");
        }

        // Also mirror under Applications\<exe> — some older Open With code paths
        // look here for the display name.
        using (var appsKey = Registry.CurrentUser.CreateSubKey(
            @"Software\Classes\Applications\" + ExeCanonicalName)!)
        {
            appsKey.SetValue("FriendlyAppName", DisplayName);
            using (var supported = appsKey.CreateSubKey("SupportedTypes")!)
                supported.SetValue(".md", string.Empty);
            using (var cmd = appsKey.CreateSubKey(@"shell\open\command")!)
                cmd.SetValue(string.Empty, $"\"{exePath}\" \"%1\"");
            using (var icon = appsKey.CreateSubKey("DefaultIcon")!)
                icon.SetValue(string.Empty, $"\"{exePath}\",0");
        }

        // Link our ProgID into the .md extension's OpenWithProgids.
        using (var mdKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\.md")!)
        using (var pids = mdKey.CreateSubKey("OpenWithProgids")!)
        {
            pids.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
        }

        NotifyShellAssocChanged();
    }

    /// <summary>Remove all registration and dedupe strays. Safe to call twice.</summary>
    public static void Unregister()
    {
        // Remove our ProgID entry.
        try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + ProgId, throwOnMissingSubKey: false); } catch { }

        // Remove our Applications\<exe> entry.
        try { Registry.CurrentUser.DeleteSubKeyTree(
            @"Software\Classes\Applications\" + ExeCanonicalName, throwOnMissingSubKey: false); } catch { }

        // Remove our ProgID from .md OpenWithProgids.
        try
        {
            using var pids = Registry.CurrentUser.OpenSubKey(
                @"Software\Classes\.md\OpenWithProgids", writable: true);
            pids?.DeleteValue(ProgId, throwOnMissingValue: false);
        }
        catch { }

        DedupeStrays();
        NotifyShellAssocChanged();
    }

    /// <summary>
    /// Clean up any left-over "MarkdownMidget" references from previous manual
    /// "Open with → Choose another app" pickings by the user, so the extension
    /// menu ends up with only our controlled entry.
    /// </summary>
    private static void DedupeStrays()
    {
        // The Explorer-controlled MRU list under FileExts.
        var mruPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.md\OpenWithList";
        try
        {
            using var mru = Registry.CurrentUser.OpenSubKey(mruPath, writable: true);
            if (mru is null) return;

            var order = (mru.GetValue("MRUList") as string) ?? string.Empty;
            var toRemove = new List<char>();

            foreach (var name in mru.GetValueNames())
            {
                if (name == "MRUList") continue;
                if (name.Length != 1) continue;
                var val = mru.GetValue(name) as string ?? string.Empty;
                if (LooksLikeUs(val))
                {
                    toRemove.Add(name[0]);
                    try { mru.DeleteValue(name); } catch { }
                }
            }

            if (toRemove.Count > 0)
            {
                foreach (var ch in toRemove) order = order.Replace(ch.ToString(), string.Empty);
                mru.SetValue("MRUList", order);
            }
        }
        catch { /* best-effort */ }

        // Also clean any other exe-filename registrations that were pointing at
        // our tool via a shortcut with a different filename (e.g. mkm.exe).
        var appsRoot = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Applications", writable: true);
        if (appsRoot is null) return;
        try
        {
            foreach (var name in appsRoot.GetSubKeyNames())
            {
                if (string.Equals(name, ExeCanonicalName, StringComparison.OrdinalIgnoreCase)) continue;
                using var sub = appsRoot.OpenSubKey(name);
                var friendly = sub?.GetValue("FriendlyAppName") as string;
                var cmd = sub?.OpenSubKey(@"shell\open\command")?.GetValue(string.Empty) as string;
                if ((friendly is not null && friendly.Contains("Markdown Midget", StringComparison.OrdinalIgnoreCase))
                    || (cmd is not null && cmd.Contains("MarkdownMidget", StringComparison.OrdinalIgnoreCase)))
                {
                    try { appsRoot.DeleteSubKeyTree(name, throwOnMissingSubKey: false); } catch { }
                }
            }
        }
        finally { appsRoot.Dispose(); }
    }

    private static bool LooksLikeUs(string s) =>
        s.Contains("MarkdownMidget", StringComparison.OrdinalIgnoreCase)
        || s.Contains("mkm.exe", StringComparison.OrdinalIgnoreCase)
        || s.Contains("mkm.lnk", StringComparison.OrdinalIgnoreCase);

    // ===== AppData install + Start Menu =====

    /// <summary>Copy the current exe to %LocalAppData%\Programs\MarkdownMidget.</summary>
    public static string InstallToAppData()
    {
        Directory.CreateDirectory(AppDataInstallDir);
        var source = CurrentExePath;
        // If we're already running from the target location, skip the copy —
        // the file is locked anyway.
        if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(AppDataInstallExe),
                StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(source, AppDataInstallExe, overwrite: true);
        }
        return AppDataInstallExe;
    }

    public static void UninstallFromAppData()
    {
        RemoveStartMenuShortcut();
        try
        {
            if (Directory.Exists(AppDataInstallDir))
                Directory.Delete(AppDataInstallDir, recursive: true);
        }
        catch { /* usually file-in-use; user needs to close first */ }
    }

    public static void CreateStartMenuShortcut(string targetExe)
    {
        try
        {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            if (t is null) return;
            dynamic shell = Activator.CreateInstance(t)!;
            dynamic link = shell.CreateShortcut(StartMenuLinkPath);
            link.TargetPath = targetExe;
            link.WorkingDirectory = Path.GetDirectoryName(targetExe) ?? string.Empty;
            link.IconLocation = targetExe + ",0";
            link.Description = "Markdown Midget — a WordPad-style, markdown-native WYSIWYG editor.";
            link.Save();
        }
        catch { /* best-effort — Start menu is bonus, not critical */ }
    }

    public static void RemoveStartMenuShortcut()
    {
        try { if (File.Exists(StartMenuLinkPath)) File.Delete(StartMenuLinkPath); } catch { }
    }

    // ===== Windows default-apps deep link =====

    /// <summary>
    /// Open Windows Settings on the Default Apps page filtered to .md. Win10/11
    /// won't let us set the default programmatically (they guard UserChoice
    /// with a hash), so this is the honest path — user confirms with one click.
    /// </summary>
    public static void OpenDefaultAppsSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:defaultapps?registeredAppOrFileExtension=.md")
            { UseShellExecute = true });
        }
        catch
        {
            try { Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true }); } catch { }
        }
    }

    // ===== Notify Explorer that associations changed =====

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int wEventId, int uFlags, IntPtr dwItem1, IntPtr dwItem2);

    private static void NotifyShellAssocChanged()
    {
        const int SHCNE_ASSOCCHANGED = 0x08000000;
        try { SHChangeNotify(SHCNE_ASSOCCHANGED, 0, IntPtr.Zero, IntPtr.Zero); } catch { }
    }
}
