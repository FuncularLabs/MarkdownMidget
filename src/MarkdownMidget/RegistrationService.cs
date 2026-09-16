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
    internal const string DisplayName = "Markdown Midget";   // also the value name under RegisteredApplications (Settings' registeredAppUser)
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

    /// <summary>The HKEY_CURRENT_USER calls registration makes, so tests can pass a fake. Get is null
    /// when missing; Set writes a string as REG_SZ, a byte[] as REG_NONE; deletes are best-effort; the
    /// name lists are empty when the key is missing.</summary>
    internal interface IRegistryValues
    {
        object? Get(string key, string name);
        void Set(string key, string name, object value);
        void DeleteTree(string key);
        void DeleteValue(string key, string name);
        IEnumerable<string> SubKeyNames(string key);
        IEnumerable<string> ValueNames(string key);
    }

    private sealed class CurrentUserRegistry : IRegistryValues
    {
        public static readonly CurrentUserRegistry Instance = new();
        public object? Get(string key, string name)
        {
            using var k = Registry.CurrentUser.OpenSubKey(key);
            return k?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        }
        public void Set(string key, string name, object value)
        {
            using var k = Registry.CurrentUser.CreateSubKey(key);
            k.SetValue(name, value, value is byte[] ? RegistryValueKind.None : RegistryValueKind.String);
        }
        public void DeleteTree(string key) { try { Registry.CurrentUser.DeleteSubKeyTree(key, false); } catch { } }
        public void DeleteValue(string key, string name)
        {
            try { using var k = Registry.CurrentUser.OpenSubKey(key, writable: true); k?.DeleteValue(name, false); } catch { }
        }
        public IEnumerable<string> SubKeyNames(string key) { using var k = Registry.CurrentUser.OpenSubKey(key); return k?.GetSubKeyNames() ?? []; }
        public IEnumerable<string> ValueNames(string key) { using var k = Registry.CurrentUser.OpenSubKey(key); return k?.GetValueNames() ?? []; }
    }

    /// <summary>
    /// Register <paramref name="exePath"/> as an editor for .md. Installed updates and repeat
    /// Registers run this again, so it writes only missing or different values, in place, and
    /// deletes nothing: deleting what a user's default (UserChoice) points at makes Windows reset
    /// it to another app. The stray cleanup that did so now runs only in <see cref="Unregister()"/>;
    /// Register points those entries at <paramref name="exePath"/> instead.
    /// </summary>
    public static void Register(string exePath) => Register(exePath, CurrentUserRegistry.Instance, NotifyShellAssocChanged);

    /// <summary>As above, against <paramref name="reg"/>; <paramref name="notifyShell"/> only if a value was written.</summary>
    internal static void Register(string exePath, IRegistryValues reg, Action notifyShell)
    {
        string command = $"\"{exePath}\" \"%1\"", icon = $"\"{exePath}\",0", classes = @"Software\Classes\";
        var changed = false;
        void Put(string key, string name, object value)
        {
            var now = reg.Get(key, name);
            if (value is byte[] bytes ? now is byte[] had && had.AsSpan().SequenceEqual(bytes) : Equals(now, value)) return;
            reg.Set(key, name, value);
            changed = true;
        }
        void PutProgId(string progId, string typeName)
        {
            Put(classes + progId, string.Empty, typeName);
            Put(classes + progId, "FriendlyTypeName", typeName);
            Put(classes + progId + @"\DefaultIcon", string.Empty, icon);
            Put(classes + progId + @"\shell\open", "FriendlyAppName", DisplayName);
            Put(classes + progId + @"\shell\open\command", string.Empty, command);
        }

        // Our ProgID (the modern, canonical entry).
        PutProgId(ProgId, DocTypeName);

        // Also mirror under Applications\<exe> — some older Open With code paths
        // look here for the display name.
        var apps = classes + @"Applications\" + ExeCanonicalName;
        Put(apps, "FriendlyAppName", DisplayName);
        Put(apps + @"\SupportedTypes", ".md", string.Empty);
        Put(apps + @"\SupportedTypes", ".mdenc", string.Empty);
        Put(apps + @"\shell\open\command", string.Empty, command);
        Put(apps + @"\DefaultIcon", string.Empty, icon);

        // Link our ProgID into the .md extension's OpenWithProgids.
        Put(classes + @".md\OpenWithProgids", ProgId, Array.Empty<byte>());

        // Secure Markdown (.mdenc) rides the same registration: its own ProgID so
        // Explorer names the type honestly, same open command. Registered here
        // rather than opt-in because nothing else on the system can open one, and
        // the docs promise double-click works.
        PutProgId(SecureProgId, SecureDocTypeName);
        // .mdenc has no other claimants, so the ProgID can be the default
        // handler outright - no Default-apps ceremony needed for double-click.
        Put(classes + ".mdenc", string.Empty, SecureProgId);
        Put(classes + @".mdenc\OpenWithProgids", SecureProgId, Array.Empty<byte>());

        // The Windows 11 "Default apps" page builds its list of choosable apps from
        // HKCU\Software\RegisteredApplications -> a Capabilities key. Everything
        // above covers "Open with" (which is why that worked), but WITHOUT these two
        // writes the app never appears in the Default-apps chooser — the very panel
        // the register flow opens and tells the user to pick Markdown Midget from.
        // Reported in the field as "it says to set the value, but it isn't there".
        Put(CapabilitiesKeyPath, "ApplicationName", DisplayName);
        Put(CapabilitiesKeyPath, "ApplicationDescription",
            "WYSIWYG markdown editor — WordPad-style editing with markdown as the native format.");
        Put(CapabilitiesKeyPath + @"\FileAssociations", ".md", ProgId);
        Put(CapabilitiesKeyPath + @"\FileAssociations", ".markdown", ProgId);
        Put(CapabilitiesKeyPath + @"\FileAssociations", ".mdenc", SecureProgId);
        Put(@"Software\RegisteredApplications", DisplayName, CapabilitiesKeyPath);

        // Entries an older copy left running a MarkdownMidget*.exe elsewhere: the Applications\<file> key
        // "Choose an app on your PC" writes, or a ProgID an Open with list names. A default may name one,
        // so point it here, in place: deleting it resets that default, and leaving it breaks once Move
        // deletes the download it runs. Best-effort, entry by entry, as the cleanup it replaces was.
        try
        {
            var strays = reg.SubKeyNames(classes + "Applications").Select(n => @"Applications\" + n).ToList();
            foreach (var ext in new[] { ".md", ".markdown", ".mdenc" })
                strays.AddRange(reg.ValueNames(classes + ext + @"\OpenWithProgids").Concat(reg.ValueNames(
                    @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\" + ext + @"\OpenWithProgids")));
            foreach (var stray in strays.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if (!RunsMarkdownMidgetExe(reg.Get(classes + stray + @"\shell\open\command", string.Empty) as string)) continue;
                    Put(classes + stray + @"\shell\open\command", string.Empty, command);
                    if (reg.Get(classes + stray + @"\DefaultIcon", string.Empty) is not null) Put(classes + stray + @"\DefaultIcon", string.Empty, icon);
                }
                catch { /* the next entry */ }
            }
        }
        catch { /* the registration above is complete */ }

        if (changed) notifyShell();
    }

    /// <summary>Whether <paramref name="command"/> runs a file named MarkdownMidget*.exe: judged by the file
    /// name of its quoted path, or of its first word if unquoted, so a folder or an argument naming us doesn't count.</summary>
    private static bool RunsMarkdownMidgetExe(string? command)
    {
        var c = command?.TrimStart() ?? string.Empty;
        var exe = c.StartsWith('"') ? c[1..].Split('"')[0] : c.Split(' ')[0];
        return System.IO.Enumeration.FileSystemName.MatchesSimpleExpression("MarkdownMidget*.exe", Path.GetFileName(exe));
    }

    /// <summary>Remove all registration and dedupe strays. Safe to call twice. Explicit uninstall only.</summary>
    public static void Unregister()
    {
        Unregister(CurrentUserRegistry.Instance);
        DedupeStrays(keepOurProgId: false);
        NotifyShellAssocChanged();
    }

    /// <summary>The removals <see cref="Unregister()"/> makes before its stray cleanup, each best-effort.</summary>
    internal static void Unregister(IRegistryValues reg)
    {
        reg.DeleteTree(@"Software\Classes\" + ProgId);
        reg.DeleteTree(@"Software\Classes\Applications\" + ExeCanonicalName);
        reg.DeleteValue(@"Software\RegisteredApplications", DisplayName);   // the Default-apps listing
        reg.DeleteTree(@"Software\Funcular Labs\Markdown Midget");
        reg.DeleteTree(@"Software\Classes\" + SecureProgId);
        try { if (reg.Get(@"Software\Classes\.mdenc", string.Empty) as string == SecureProgId) reg.Set(@"Software\Classes\.mdenc", string.Empty, string.Empty); }
        catch { }
        reg.DeleteValue(@"Software\Classes\.mdenc\OpenWithProgids", SecureProgId);
        reg.DeleteValue(@"Software\Classes\.md\OpenWithProgids", ProgId);
    }

    /// <summary>
    /// Clean up any left-over "MarkdownMidget" references from previous manual
    /// "Open with → Choose another app" pickings so the extension menu ends up
    /// with only our controlled entry. Unregister only: it deletes a UserChoice,
    /// and keys one can point at, which resets the user's default.
    /// </summary>
    /// <param name="keepOurProgId">
    /// When true, don't strip our <see cref="ProgId"/> from Explorer's per-user
    /// OpenWithProgids MRU. When false (Unregister), strip it too.
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
