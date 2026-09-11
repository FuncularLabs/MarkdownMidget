using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
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
    private const string SecureProgId = "MarkdownMidget.SecureDocument";
    private const string DisplayName = "Markdown Midget";
    private const string DocTypeName = "Markdown Document";
    private const string SecureDocTypeName = "Markdown Midget Encrypted Document";
    private const string ExeCanonicalName = "MarkdownMidget.exe";
    private const string ShortcutLinkName = "Markdown Midget.lnk";
    private const string InstallInfoName = "install-info.json";
    // Where the Windows "Default apps" page learns we exist — see Register().
    private const string CapabilitiesKeyPath = @"Software\Funcular Labs\Markdown Midget\Capabilities";

    public static string CurrentExePath =>
        Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine current exe path.");

    public static string AppDataInstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "MarkdownMidget");

    public static string AppDataInstallExe => Path.Combine(AppDataInstallDir, ExeCanonicalName);

    public static string StartMenuLinkPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), ShortcutLinkName);

    public static string DesktopLinkPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ShortcutLinkName);

    private static string InstallInfoPath => Path.Combine(AppDataInstallDir, InstallInfoName);

    public static bool IsInstalledToAppData() => File.Exists(AppDataInstallExe);
    public static bool HasStartMenuShortcut() => File.Exists(StartMenuLinkPath);
    public static bool HasDesktopShortcut() => File.Exists(DesktopLinkPath);

    /// <summary>Where the exe originally came from, and whether we moved (deleted) it.</summary>
    public sealed record InstallInfo(string? OriginalPath, bool Moved);

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
        DedupeStrays(keepOurProgId: true);

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
            {
                supported.SetValue(".md", string.Empty);
                supported.SetValue(".mdenc", string.Empty);
            }
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

        // Secure Markdown (.mdenc) rides the same registration: its own ProgID so
        // Explorer names the type honestly, same open command. Registered here
        // rather than opt-in because nothing else on the system can open one, and
        // the docs promise double-click works.
        using (var progKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + SecureProgId)!)
        {
            progKey.SetValue(string.Empty, SecureDocTypeName);
            progKey.SetValue("FriendlyTypeName", SecureDocTypeName);
            using (var icon = progKey.CreateSubKey("DefaultIcon")!)
                icon.SetValue(string.Empty, $"\"{exePath}\",0");
            using (var open = progKey.CreateSubKey(@"shell\open"))
                open!.SetValue("FriendlyAppName", DisplayName);
            using (var cmd = progKey.CreateSubKey(@"shell\open\command")!)
                cmd.SetValue(string.Empty, $"\"{exePath}\" \"%1\"");
        }
        using (var encKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\.mdenc")!)
        {
            // .mdenc has no other claimants, so the ProgID can be the default
            // handler outright - no Default-apps ceremony needed for double-click.
            encKey.SetValue(string.Empty, SecureProgId);
            using var pids = encKey.CreateSubKey("OpenWithProgids")!;
            pids.SetValue(SecureProgId, Array.Empty<byte>(), RegistryValueKind.None);
        }

        // The Windows 11 "Default apps" page builds its list of choosable apps from
        // HKCU\Software\RegisteredApplications -> a Capabilities key. Everything
        // above covers "Open with" (which is why that worked), but WITHOUT these two
        // writes the app never appears in the Default-apps chooser — the very panel
        // the register flow opens and tells the user to pick Markdown Midget from.
        // Reported in the field as "it says to set the value, but it isn't there".
        using (var caps = Registry.CurrentUser.CreateSubKey(CapabilitiesKeyPath)!)
        {
            caps.SetValue("ApplicationName", DisplayName);
            caps.SetValue("ApplicationDescription",
                "WYSIWYG markdown editor — WordPad-style editing with markdown as the native format.");
            using var assoc = caps.CreateSubKey("FileAssociations")!;
            assoc.SetValue(".md", ProgId);
            assoc.SetValue(".markdown", ProgId);
            assoc.SetValue(".mdenc", SecureProgId);
        }
        using (var registered = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications")!)
        {
            registered.SetValue(DisplayName, CapabilitiesKeyPath);
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

        // Remove the Default-apps listing (RegisteredApplications + Capabilities).
        try
        {
            using var registered = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", writable: true);
            registered?.DeleteValue(DisplayName, throwOnMissingValue: false);
        }
        catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Funcular Labs\Markdown Midget", throwOnMissingSubKey: false); } catch { }

        // Remove the Secure Markdown registration.
        try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + SecureProgId, throwOnMissingSubKey: false); } catch { }
        try
        {
            using var encKey = Registry.CurrentUser.OpenSubKey(@"Software\Classes\.mdenc", writable: true);
            if (encKey is not null)
            {
                if (encKey.GetValue(string.Empty) as string == SecureProgId)
                    encKey.SetValue(string.Empty, string.Empty);
                using var pids = encKey.OpenSubKey("OpenWithProgids", writable: true);
                pids?.DeleteValue(SecureProgId, throwOnMissingValue: false);
            }
        }
        catch { }

        // Remove our ProgID from .md OpenWithProgids.
        try
        {
            using var pids = Registry.CurrentUser.OpenSubKey(
                @"Software\Classes\.md\OpenWithProgids", writable: true);
            pids?.DeleteValue(ProgId, throwOnMissingValue: false);
        }
        catch { }

        DedupeStrays(keepOurProgId: false);
        NotifyShellAssocChanged();
    }

    /// <summary>
    /// Clean up any left-over "MarkdownMidget" references from previous manual
    /// "Open with → Choose another app" pickings so the extension menu ends up
    /// with only our controlled entry. Called on both Register and Unregister.
    /// </summary>
    /// <param name="keepOurProgId">
    /// When true (during Register), don't strip our fresh <see cref="ProgId"/>
    /// from Explorer's per-user OpenWithProgids MRU. When false (Unregister),
    /// strip it too.
    /// </param>
    private static void DedupeStrays(bool keepOurProgId = false)
    {
        // BOTH extensions the app can own a default for. Register advertises
        // .markdown in the Default-apps Capabilities, so its per-user FileExts
        // state needs exactly the cleanup .md gets - one loop, so the two can
        // never drift apart again.
        foreach (var ext in new[] { ".md", ".markdown", ".mdenc" })
        {

            // 1) Explorer-controlled per-user MRU of app EXE FILENAMES.
            var mruListPath = @$"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\{ext}\OpenWithList";
            try
            {
                using var mru = Registry.CurrentUser.OpenSubKey(mruListPath, writable: true);
                if (mru is not null)
                {
                    var order = (mru.GetValue("MRUList") as string) ?? string.Empty;
                    var toRemove = new List<char>();
                    foreach (var name in mru.GetValueNames())
                    {
                        if (name == "MRUList" || name.Length != 1) continue;
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
            }
            catch { /* best-effort */ }

            // 2) Explorer-controlled per-user PROGID MRU. Windows tracks the user's
            // manually-selected ProgIDs here separately from the Classes hive — this
            // was the missing dedupe target that let stale "Markdown Midget" entries
            // survive on re-registration.
            var mruPidsPath = @$"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\{ext}\OpenWithProgids";
            try
            {
                using var mruPids = Registry.CurrentUser.OpenSubKey(mruPidsPath, writable: true);
                if (mruPids is not null)
                {
                    foreach (var name in mruPids.GetValueNames())
                    {
                        // If it's our ProgID and we're registering, leave it alone (we'll add
                        // it back below). If we're unregistering, drop it.
                        if (string.Equals(name, ProgId, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(name, SecureProgId, StringComparison.OrdinalIgnoreCase))
                        {
                            if (!keepOurProgId) try { mruPids.DeleteValue(name); } catch { }
                            continue;
                        }
                        if (LooksLikeUs(name)
                            || name.StartsWith(@"Applications\MarkdownMidget", StringComparison.OrdinalIgnoreCase)
                            || name.StartsWith(@"Applications\mkm", StringComparison.OrdinalIgnoreCase))
                        {
                            try { mruPids.DeleteValue(name); } catch { }
                        }
                    }
                }
            }
            catch { /* best-effort */ }

            // 3) UserChoice — if it was pointing at a stale/old ProgID of ours, remove
            // it so Windows falls back to prompting. (This is the only key we can
            // safely delete but not write; write is hash-protected.)
            try
            {
                using var uc = Registry.CurrentUser.OpenSubKey(
                    @$"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\{ext}\UserChoice", writable: true);
                if (uc is not null)
                {
                    var pid = uc.GetValue("ProgId") as string ?? string.Empty;
                    if (pid.StartsWith(@"Applications\MarkdownMidget", StringComparison.OrdinalIgnoreCase)
                        || pid.StartsWith(@"Applications\mkm", StringComparison.OrdinalIgnoreCase)
                        || (LooksLikeUs(pid) && !string.Equals(pid, ProgId, StringComparison.OrdinalIgnoreCase)
                                          && !string.Equals(pid, SecureProgId, StringComparison.OrdinalIgnoreCase))
                        // On UNregister the current ProgID is about to be deleted, so a
                        // per-user default pointing at it must go too, or Explorer is
                        // left resolving .md/.markdown through a ProgID that no longer
                        // exists. On register it stays: it is a valid choice of us.
                        || (!keepOurProgId && (string.Equals(pid, ProgId, StringComparison.OrdinalIgnoreCase)
                                            || string.Equals(pid, SecureProgId, StringComparison.OrdinalIgnoreCase))))
                    {
                        Registry.CurrentUser.DeleteSubKey(
                            @$"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\{ext}\UserChoice",
                            throwOnMissingSubKey: false);
                    }
                }
            }
            catch { /* best-effort */ }
        }


        // 4) Applications\ subkeys with different exe filenames pointing at our
        // tool via shortcuts (e.g. mkm.exe entries left over from earlier tests).
        try
        {
            using var appsRoot = Registry.CurrentUser.OpenSubKey(
                @"Software\Classes\Applications", writable: true);
            if (appsRoot is not null)
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
        }
        catch { /* best-effort */ }
    }

    private static bool LooksLikeUs(string s) =>
        s.Contains("MarkdownMidget", StringComparison.OrdinalIgnoreCase)
        || s.Contains("mkm.exe", StringComparison.OrdinalIgnoreCase)
        || s.Contains("mkm.lnk", StringComparison.OrdinalIgnoreCase);

    // ===== AppData install + Start Menu =====

    /// <summary>
    /// The name of the app's own assembly, whose <c>.dll</c> a development build's exe
    /// needs beside it. Read from <see cref="App"/>'s assembly rather than written out,
    /// so renaming the assembly moves it too. <c>GetName()</c> is safe in a single-file
    /// bundle, where <c>Assembly.Location</c> would raise IL3000; and <c>typeof(App)</c>
    /// rather than the entry assembly, which under a test host is the test host.
    /// </summary>
    internal static string AppAssemblyName =>
        typeof(App).Assembly.GetName().Name
        ?? throw new InvalidOperationException("The app's assembly has no name.");

    /// <summary>
    /// Whether the exe at <paramref name="exePath"/> needs files beside it to start, and
    /// so cannot be installed by copying it on its own: true when the app's own assembly
    /// file, <paramref name="appAssemblyName"/>.dll, is in the exe's folder.
    /// </summary>
    /// <remarks>
    /// Why this test and not another:
    /// <list type="bullet">
    /// <item><description>It is exactly the condition the .NET apphost fails on. A
    /// <c>dotnet build</c> output's exe is only the apphost, which starts the runtime on
    /// that DLL from its own folder; a copy without it exits at once with "The
    /// application to execute does not exist" (Application event log, .NET Runtime
    /// 1023), with no window and no message.</description></item>
    /// <item><description>A single-file bundle never has it: the assembly is inside the
    /// exe. Even with <c>IncludeAllContentForSelfExtract</c>, extraction goes to a temp
    /// folder, not beside the exe. The release workflow publishes the single-file
    /// win-x64-fxdependent profile, so a release download is never refused.</description></item>
    /// <item><description>Not <c>Assembly.Location</c>: it raises IL3000 under the
    /// single-file analyzer, and under self-extract it returns the extracted copy's path,
    /// which is not empty, so it cannot tell a bundle from a build output.</description></item>
    /// </list>
    /// It errs toward refusing: a bundle dropped into a folder that also holds a build's
    /// DLL is refused too, which only a developer can arrange.
    /// </remarks>
    /// <param name="fileExists">The probe. The app passes <see cref="File.Exists(string)"/>;
    /// a test can answer for paths that are not on disk.</param>
    /// <exception cref="ArgumentException"><paramref name="exePath"/> is not a full path
    /// with a folder (a relative one would be read against the working directory, which
    /// is not the exe's folder), or <paramref name="appAssemblyName"/> is empty (which
    /// would probe for ".dll", find nothing, and let every exe through).</exception>
    public static bool NeedsFilesBesideIt(string exePath, string appAssemblyName, Func<string, bool> fileExists)
    {
        ArgumentException.ThrowIfNullOrEmpty(appAssemblyName);
        var folder = Path.IsPathFullyQualified(exePath) ? Path.GetDirectoryName(exePath) : null;
        if (string.IsNullOrEmpty(folder))
            throw new ArgumentException($"Expected the full path of an exe, not \"{exePath}\".", nameof(exePath));
        return fileExists(Path.Combine(folder, appAssemblyName + ".dll"));
    }

    /// <summary>
    /// <see cref="NeedsFilesBesideIt"/> for the running exe, with the real probe: the
    /// question the Register click handler asks before its dialog. InstallGuardTests
    /// asserts it as the precondition of its live install test.
    /// </summary>
    internal static bool CurrentExeNeedsFilesBesideIt() =>
        NeedsFilesBesideIt(CurrentExePath, AppAssemblyName, File.Exists);

    /// <summary>The command <see cref="DevelopmentBuildRefusal"/> gives, run from the
    /// repository root. InstallGuardTests pins its project and publish profile against
    /// the repository.</summary>
    internal const string PublishCommand =
        "dotnet publish src/MarkdownMidget/MarkdownMidget.csproj -c Release -p:PublishProfile=win-x64-fxdependent";

    /// <summary>Where <see cref="PublishCommand"/> writes the exe, from the repository
    /// root: that profile's PublishDir under the project's folder (pinned by
    /// InstallGuardTests).</summary>
    internal const string PublishedExeFolder = @"src\MarkdownMidget\bin\Release\publish\framework-dependent\";

    /// <summary>
    /// What Register shows, and what <see cref="InstallToAppData()"/> throws, for an exe
    /// that needs files beside it (<see cref="NeedsFilesBesideIt"/>). Only a developer
    /// ever sees it, since a release download is a single file, so it speaks to one.
    /// </summary>
    public const string DevelopmentBuildRefusal =
        "This is a development build. Its exe needs the files beside it, so it can't be installed on its own.\n\n" +
        "To try an installed copy, publish a single-file build from the repository root:\n\n" +
        PublishCommand + "\n\n" +
        "Then run the exe it writes to " + PublishedExeFolder + " and register from there.";

    /// <summary>Copy the current exe to %LocalAppData%\Programs\MarkdownMidget.</summary>
    /// <exception cref="InvalidOperationException">The current exe is not the installed
    /// copy and needs files beside it; the message is <see cref="DevelopmentBuildRefusal"/>.</exception>
    public static string InstallToAppData() => InstallToAppData(AppDataInstallDir);

    /// <summary>
    /// Copy the running exe into <paramref name="installDir"/>, judged by the real
    /// <see cref="File.Exists(string)"/>: <see cref="InstallToAppData()"/> with only the
    /// folder left to the caller. The public method passes the real install folder;
    /// InstallGuardTests passes a temp one, so a test runs the source and the probe
    /// Register uses without touching the real install.
    /// </summary>
    /// <exception cref="InvalidOperationException">As for <see cref="InstallToAppData()"/>.</exception>
    internal static string InstallToAppData(string installDir) =>
        InstallToAppData(CurrentExePath, installDir, File.Exists);

    /// <summary>
    /// Copy <paramref name="sourceExe"/> into <paramref name="installDir"/> as
    /// MarkdownMidget.exe and return the installed path. Refused, before anything is
    /// created or copied, when a copy would happen and the source needs files beside it
    /// (<see cref="NeedsFilesBesideIt"/>): the copy would be an apphost with nothing to
    /// start, and a Move would then launch it and exit, leaving no app and no message.
    /// The Register click handler checks first and explains; this refusal is for any
    /// caller that does not.
    /// </summary>
    internal static string InstallToAppData(string sourceExe, string installDir, Func<string, bool> fileExists)
    {
        var installExe = Path.Combine(installDir, ExeCanonicalName);
        // Running from the target location already: no copy (the file is locked
        // anyway), and so nothing to refuse.
        var copies = !string.Equals(Path.GetFullPath(sourceExe), Path.GetFullPath(installExe),
            StringComparison.OrdinalIgnoreCase);
        if (copies && NeedsFilesBesideIt(sourceExe, AppAssemblyName, fileExists))
            throw new InvalidOperationException(DevelopmentBuildRefusal);

        Directory.CreateDirectory(installDir);
        if (copies) File.Copy(sourceExe, installExe, overwrite: true);
        return installExe;
    }

    public static void UninstallFromAppData()
    {
        try
        {
            if (Directory.Exists(AppDataInstallDir))
                Directory.Delete(AppDataInstallDir, recursive: true);
        }
        catch { /* usually file-in-use; user needs to close first */ }
    }

    private static void CreateShortcut(string linkPath, string targetExe)
    {
        try
        {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            if (t is null) return;
            dynamic shell = Activator.CreateInstance(t)!;
            dynamic link = shell.CreateShortcut(linkPath);
            link.TargetPath = targetExe;
            link.WorkingDirectory = Path.GetDirectoryName(targetExe) ?? string.Empty;
            link.IconLocation = targetExe + ",0";
            link.Description = "Markdown Midget — a WordPad-style, markdown-native WYSIWYG editor.";
            link.Save();
        }
        catch { /* best-effort — shortcuts are a bonus, not critical */ }
    }

    public static void CreateStartMenuShortcut(string targetExe) => CreateShortcut(StartMenuLinkPath, targetExe);
    public static void CreateDesktopShortcut(string targetExe) => CreateShortcut(DesktopLinkPath, targetExe);

    public static void RemoveStartMenuShortcut()
    {
        try { if (File.Exists(StartMenuLinkPath)) File.Delete(StartMenuLinkPath); } catch { }
    }

    public static void RemoveDesktopShortcut()
    {
        try { if (File.Exists(DesktopLinkPath)) File.Delete(DesktopLinkPath); } catch { }
    }

    // ===== Original-location memorialization + move / restore =====

    /// <summary>Record where the exe came from (so unregister can offer to restore it).</summary>
    public static void SaveInstallInfo(string? originalPath, bool moved)
    {
        try
        {
            Directory.CreateDirectory(AppDataInstallDir);
            File.WriteAllText(InstallInfoPath,
                JsonSerializer.Serialize(new InstallInfo(originalPath, moved)));
        }
        catch { /* best-effort */ }
    }

    public static InstallInfo? ReadInstallInfo()
    {
        try
        {
            if (!File.Exists(InstallInfoPath)) return null;
            return JsonSerializer.Deserialize<InstallInfo>(File.ReadAllText(InstallInfoPath));
        }
        catch { return null; }
    }

    /// <summary>Copy the installed exe back to <paramref name="destPath"/>.</summary>
    public static bool RestoreToOriginal(string destPath)
    {
        try
        {
            if (!File.Exists(AppDataInstallExe)) return false;
            var dir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.Copy(AppDataInstallExe, destPath, overwrite: true);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Delete the original downloaded exe after a "move" install. Called by the
    /// freshly-launched AppData copy (passed <c>--finish-move &lt;path&gt;</c>);
    /// the original process needs a moment to exit and release the file lock.
    /// </summary>
    public static void FinishMove(string originalPath)
    {
        _ = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 40; attempt++) // ~10s total
            {
                try
                {
                    if (!File.Exists(originalPath)) return;
                    File.Delete(originalPath);
                    return;
                }
                catch { await Task.Delay(250); }
            }
        });
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
