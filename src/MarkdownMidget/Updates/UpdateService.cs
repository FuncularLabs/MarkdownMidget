using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;

namespace MarkdownMidget.Updates;

/// <summary>
/// Checks GitHub for newer releases, downloads the signed single-file exe, and
/// swaps it in.
///
/// Two install shapes, mirroring the Windows-integration feature:
/// - **Installed** (running from the AppData install dir): swap in place via the
///   rename dance (a running exe can be renamed but not overwritten), refresh the
///   registration + any shortcuts, restart. Path stays canonical, so shortcuts
///   keep working even if the refresh fails.
/// - **Portable** (running from anywhere else): download the versioned exe into
///   the SAME directory the current instance runs from, launch it, exit. The old
///   exe stays behind as a file the user can delete — nothing is modified except
///   adding one file, which is what a portable app should do.
///
/// The downloaded file must carry a valid Authenticode signature whose subject is
/// Funcular Labs before it is ever started or copied — a failed HTTPS download,
/// a tampered asset, or a wrong file simply aborts the update.
/// </summary>
internal static class UpdateService
{
    private const string ReleasesApi =
        "https://api.github.com/repos/FuncularLabs/MarkdownMidget/releases?per_page=20";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        // GitHub's API requires a User-Agent.
        c.DefaultRequestHeaders.UserAgent.ParseAdd("MarkdownMidget-Updater");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    /// <summary>Newest stable + newest prerelease, or null when offline/rate-limited.</summary>
    public static async Task<UpdateCheck?> CheckAsync()
    {
        try
        {
            var json = await Http.GetStringAsync(ReleasesApi);
            return ReleaseFeed.Select(json);
        }
        catch { return null; }   // offline, rate-limited, DNS… — the caller shows "couldn't check"
    }

    /// <summary>Download a release's exe to a temp path (verified separately).</summary>
    public static async Task<string?> DownloadAsync(ReleaseInfo release)
    {
        if (release.AssetUrl is null || release.AssetName is null) return null;
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "MarkdownMidget-update");
            Directory.CreateDirectory(dir);
            var dest = Path.Combine(dir, release.AssetName);
            if (File.Exists(dest)) File.Delete(dest);

            using var response = await Http.GetAsync(release.AssetUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            await using (var fs = File.Create(dest))
                await response.Content.CopyToAsync(fs);

            // A truncated download must not proceed to the signature/swap steps.
            if (release.AssetSize > 0 && new FileInfo(dest).Length != release.AssetSize)
            {
                File.Delete(dest);
                return null;
            }
            return dest;
        }
        catch { return null; }
    }

    /// <summary>
    /// True only when the file carries a VALID Authenticode signature (full
    /// WinVerifyTrust chain + hash check) whose signer subject is Funcular Labs.
    /// </summary>
    public static bool VerifySignature(string filePath, out string signer)
    {
        signer = "";
        try
        {
            if (WinVerifyTrustFile(filePath) != 0) return false;
            var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(filePath));
            signer = cert.GetNameInfo(X509NameType.SimpleName, forIssuer: false) ?? "";
            return signer.Contains("Funcular Labs", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>Where the current process's exe lives (single-file publish safe).</summary>
    public static string CurrentExePath => Environment.ProcessPath
        ?? Process.GetCurrentProcess().MainModule?.FileName
        ?? throw new InvalidOperationException("Cannot determine the running exe path.");

    public static bool IsInstalled()
    {
        try { return RegistrationService.IsRunningFromAppDataInstall(); }
        catch { return false; }
    }

    /// <summary>
    /// Installed flow: in-place swap at the canonical AppData path, refresh
    /// registration/shortcuts, then restart. Throws with a readable message on
    /// failure (nothing destructive happens before the copy succeeds).
    /// </summary>
    public static void ApplyInstalledAndRestart(string verifiedNewExe)
    {
        var target = CurrentExePath;
        var old = target + ".old";
        try { if (File.Exists(old)) File.Delete(old); } catch { /* replaced below */ }

        File.Move(target, old);            // legal while running; the process keeps its image
        try
        {
            File.Copy(verifiedNewExe, target);
        }
        catch
        {
            File.Move(old, target);        // roll back — leave the install untouched
            throw;
        }

        // Same canonical path, so these are refreshes rather than repairs — but the
        // instruction is to point registrations/shortcuts at the new version.
        try
        {
            if (RegistrationService.IsRegistered()) RegistrationService.Register(target);
            if (RegistrationService.HasStartMenuShortcut()) RegistrationService.CreateStartMenuShortcut(target);
            if (RegistrationService.HasDesktopShortcut()) RegistrationService.CreateDesktopShortcut(target);
        }
        catch { /* the swap succeeded; a shortcut refresh failure isn't fatal */ }

        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
    }

    /// <summary>
    /// Portable flow: place the (already verified) versioned exe next to the
    /// running one and start it. Returns the new exe path.
    /// </summary>
    public static string ApplyPortableAndRestart(string verifiedNewExe, string assetName)
    {
        var dir = Path.GetDirectoryName(CurrentExePath)!;
        var dest = Path.Combine(dir, assetName);
        if (string.Equals(dest, CurrentExePath, StringComparison.OrdinalIgnoreCase))
            dest = Path.Combine(dir, Path.GetFileNameWithoutExtension(assetName) + ".new.exe");
        File.Copy(verifiedNewExe, dest, overwrite: true);
        Process.Start(new ProcessStartInfo(dest) { UseShellExecute = true });
        return dest;
    }

    /// <summary>Startup cleanup: remove the .old left by a previous installed-flow
    /// update (the old process held it; by now it has exited).</summary>
    public static void CleanupOldBinaries()
    {
        try
        {
            var old = CurrentExePath + ".old";
            if (File.Exists(old)) File.Delete(old);
        }
        catch { /* still locked — the next launch gets it */ }
    }

    // ---- WinVerifyTrust (full Authenticode policy check) ----

    private static readonly Guid ActionGenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private static int WinVerifyTrustFile(string filePath)
    {
        var fileInfo = new WINTRUST_FILE_INFO
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
            pcwszFilePath = filePath,
        };
        var data = new WINTRUST_DATA
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
            dwUIChoice = 2,           // WTD_UI_NONE
            fdwRevocationChecks = 0,  // WTD_REVOKE_NONE (offline-tolerant; chain still validated)
            dwUnionChoice = 1,        // WTD_CHOICE_FILE
            dwStateAction = 0,
        };
        var fileInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPtr, false);
            data.pFile = fileInfoPtr;
            var action = ActionGenericVerifyV2;
            return WinVerifyTrust(IntPtr.Zero, ref action, ref data);
        }
        finally { Marshal.FreeHGlobal(fileInfoPtr); }
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid actionId, ref WINTRUST_DATA data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }
}
