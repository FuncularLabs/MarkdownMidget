using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
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
            // AssetName comes from the release JSON — never let it steer the path.
            var dest = Path.Combine(dir, Path.GetFileName(release.AssetName));
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

    // The identity gate. Azure Trusted Signing issues short-lived LEAF certs that
    // rotate every release, so the leaf thumbprint can't be pinned. Two things are
    // stable and together constitute a real identity check (not a spoofable subject
    // string): the leaf's Organization, and the chain root — Microsoft only issues a
    // cert under this root with O="Funcular Labs, Inc." after verifying that identity.
    private const string ExpectedOrg = "Funcular Labs, Inc.";
    private const string TrustedRootThumbprint = "F40042E2E5F7E8EF8189FED15519AECE42C3BFA2"; // MS Identity Verification Root CA 2020

    /// <summary>
    /// True only when the file carries a valid embedded Authenticode signature
    /// (WinVerifyTrust: hash + trust chain), the signer's Organization is exactly
    /// Funcular Labs, Inc., the chain roots at Microsoft's identity-verification
    /// root, and no chain cert is known-revoked.
    /// </summary>
    public static bool VerifySignature(string filePath, out string signer)
    {
        signer = "";
        try
        {
            if (WinVerifyTrustFile(filePath) != 0) return false;   // invalid/untrusted/tampered

            // Extract the embedded SIGNER cert. CreateFromSignedFile is flagged
            // SYSLIB0057, but its steer (X509CertificateLoader) only loads certificate
            // *files* — there is no non-obsolete managed API to pull a signer out of a
            // signed PE. So use it, then re-load the raw bytes via the non-obsolete
            // loader to hold an X509Certificate2 without the obsolete file ctor.
#pragma warning disable SYSLIB0057
            var signed = X509Certificate.CreateFromSignedFile(filePath);
#pragma warning restore SYSLIB0057
            using var leaf = X509CertificateLoader.LoadCertificate(signed.GetRawCertData());
            signer = leaf.Subject;

            // Organization must be EXACTLY ours (not a Contains — "Funcular Labs Fan
            // Club" and the like must not pass).
            if (!OrganizationIs(leaf, ExpectedOrg)) return false;

            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.Online;
            chain.ChainPolicy.RevocationFlag = X509RevocationFlag.EntireChain;
            // WinVerifyTrust already established trust; here we only need the chain to
            // read the root and to surface a definitive revocation. Transient CRL/OCSP
            // unavailability must NOT block a legitimate update, so those are ignored;
            // a genuine "Revoked" is not.
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.IgnoreCertificateAuthorityRevocationUnknown
                | X509VerificationFlags.IgnoreEndRevocationUnknown
                | X509VerificationFlags.IgnoreRootRevocationUnknown;
            chain.Build(leaf);

            var rootPinned = false;
            foreach (var el in chain.ChainElements)
            {
                foreach (var st in el.ChainElementStatus)
                    if (st.Status.HasFlag(X509ChainStatusFlags.Revoked)) return false;
                if (string.Equals(el.Certificate.Thumbprint, TrustedRootThumbprint, StringComparison.OrdinalIgnoreCase))
                    rootPinned = true;
            }
            return rootPinned;
        }
        catch { return false; }
    }

    private static bool OrganizationIs(X509Certificate2 cert, string org)
    {
        foreach (var rdn in cert.SubjectName.EnumerateRelativeDistinguishedNames())
        {
            if (rdn.GetSingleElementType().FriendlyName is "O" or "Organization" ||
                rdn.GetSingleElementType().Value == "2.5.4.10") // OID for O
            {
                return string.Equals(rdn.GetSingleElementValue(), org, StringComparison.Ordinal);
            }
        }
        return false;
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
    /// The version of the exe sitting at <see cref="CurrentExePath"/> right now, which
    /// is NOT necessarily the version this process is running.
    ///
    /// Windows lets a running exe be renamed, and the installed update flow does
    /// exactly that — but Environment.ProcessPath and MainModule.FileName keep
    /// reporting the original path, so after another window updates, this path
    /// resolves to the NEW exe while we go on executing the old image from
    /// "<c>…exe.old</c>". That asymmetry is what makes this check possible.
    /// </summary>
    public static UpdateVersion? VersionOnDisk()
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(CurrentExePath);
            return UpdateVersion.Parse(info.ProductVersion ?? info.FileVersion);
        }
        catch { return null; }
    }

    /// <summary>
    /// True when the exe on disk already satisfies <paramref name="wanted"/> — i.e.
    /// some other window has already applied this update and there is nothing to
    /// download. The caller should tell the user to restart rather than attempt a
    /// swap, which would fail with a bare "Cannot create a file when that file
    /// already exists" (the rename target is this process's own locked image).
    /// </summary>
    public static bool AlreadyUpdatedOnDisk(
        UpdateVersion wanted, UpdateVersion? running, out UpdateVersion? onDisk)
    {
        onDisk = VersionOnDisk();
        return UpdateOffer.NeedsRestartNotUpdate(onDisk, wanted, running);
    }

    /// <summary>
    /// Installed flow: in-place swap at the canonical AppData path, refresh
    /// registration/shortcuts, then restart. Throws with a readable message on
    /// failure (nothing destructive happens before the copy succeeds).
    /// </summary>
    public static void ApplyInstalledAndRestart(string verifiedNewExe)
    {
        var target = CurrentExePath;
        var dir = Path.GetDirectoryName(target)!;
        // Per-process staging name: two windows updating within the same second would
        // otherwise share one file, and the cleanup below would delete a sibling's
        // copy out from under its own rename.
        var staged = Path.Combine(dir, $"{StagedPrefix}-{Environment.ProcessId}.exe");

        // Do the slow cross-volume copy from %TEMP% BEFORE touching the running exe,
        // so the only window where `target` is absent spans two fast same-volume
        // renames rather than a full copy. Clean up after ourselves if it fails
        // partway — CleanupOldBinaries would eventually get it, but only on a launch
        // whose process id happens not to collide, and a half-written 6.5 MB file
        // shouldn't wait for that.
        try
        {
            File.Copy(verifiedNewExe, staged, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(staged)) File.Delete(staged); } catch { /* best effort */ }
            throw;
        }

        // The chooser, not one chosen name: a sibling can park at `…exe.old` while
        // those 6.5 MB are being written, and even after the choice moved down here
        // two windows can still both be told the plain name is free. Re-deciding per
        // attempt is what actually settles it.
        SwapInPlace(target, staged, () => ChooseParkingName(target));

        // Same canonical path, so these are refreshes rather than repairs — but the
        // instruction is to point registrations/shortcuts at the new version.
        try
        {
            if (RegistrationService.IsRegistered()) RegistrationService.Register(target);
            if (RegistrationService.HasStartMenuShortcut()) RegistrationService.CreateStartMenuShortcut(target);
            if (RegistrationService.HasDesktopShortcut()) RegistrationService.CreateDesktopShortcut(target);
        }
        catch { /* the swap succeeded; a shortcut refresh failure isn't fatal */ }

        // The swap is done and the shortcuts point at it, so the update HAS happened —
        // but a sibling parking the file in this same instant makes this throw "The
        // system cannot find the file specified", and reporting that as a failed update
        // would be wrong twice over: it succeeded, and the sentence names no file.
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Markdown Midget is updated, but this window couldn't start the new " +
                "version — most likely another window was busy with its own update at " +
                "that moment. Close this window and start Markdown Midget again from " +
                "your shortcut or the Start menu.", ex);
        }
    }

    /// <summary>
    /// Where to park the exe we're replacing.
    ///
    /// Normally `…exe.old`. But that name may already exist AND be undeletable,
    /// because it is the running image of a window that was open during an earlier
    /// update — and then the rename onto it fails with "Cannot create a file when
    /// that file already exists", which is the failure this release exists to stop
    /// showing people. That window can't be detected by version (this instance
    /// genuinely does need the update it is asking for), so the answer is simply not
    /// to collide: park at `…exe.old-1234` instead. The startup sweep reclaims both
    /// shapes once their owners exit.
    /// </summary>
    internal static string ChooseParkingName(string target)
    {
        // Take the first name that is free, or that we can free. Process ids get
        // reused, so `…old-<pid>` can be occupied by a window still executing an
        // image an *earlier* holder of this pid parked there — assuming it's
        // available is how the original collision comes back by another route.
        foreach (var candidate in ParkingCandidates(target + ".old"))
        {
            // Directory.Exists as well as File.Exists: a *directory* sitting at the
            // name is not free either, and File.Exists alone would call it free three
            // times over and then blame a race that isn't happening.
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
            try { File.Delete(candidate); return candidate; }   // stale; now free
            catch { /* someone is running it, or it isn't a file — try the next name */ }
        }
        // Everything probed is taken, which shouldn't happen. Returning the plain name
        // here would hand back one just proven undeletable — re-delivering the exact
        // error this release exists to remove. A timestamped name has no such history:
        // it is very likely free, and it still matches the `.old*` sweep glob.
        return $"{target}.old-{Environment.ProcessId}-{DateTime.UtcNow.Ticks:x}";
    }

    private static IEnumerable<string> ParkingCandidates(string preferred)
    {
        yield return preferred;
        var pid = Environment.ProcessId;
        yield return $"{preferred}-{pid}";
        for (var n = 1; n <= 20; n++) yield return $"{preferred}-{pid}-{n}";
    }

    /// <summary>
    /// Rename the running exe out of the way, taking a fresh name each attempt.
    /// Returns the name it landed on.
    ///
    /// Retried when waiting could plausibly change the answer: a sibling mid-swap (the
    /// two codes below) or something holding the file open. A denied rename is caught
    /// but not retried, only rephrased — see IsParkFailureWeExplain, which has the last
    /// word on both questions. The two codes:
    ///
    /// - ERROR_ALREADY_EXISTS: a sibling took the name between the probe and the
    ///   rename. A different name works, so re-decide.
    /// - ERROR_FILE_NOT_FOUND: `target` is momentarily absent because a sibling has
    ///   parked it and not yet moved its own copy in. This is the *more likely* of the
    ///   two in a two-window race, and the same name works once that lands.
    ///
    /// A locked target is retried too — see IsParkFailureWeExplain, which also has the
    /// last word on which failures get their text replaced. A full disk and the rest
    /// fail identically next time and keep their own, which does say something.
    /// </summary>
    private static string ParkTarget(string target, Func<string> chooseOld)
    {
        for (var attempt = 1; ; attempt++)
        {
            var old = chooseOld();
            try { if (File.Exists(old)) File.Delete(old); } catch { /* replaced below */ }
            try
            {
                File.Move(target, old);    // legal while running; we keep our image
                return old;
            }
            catch (Exception ex) when (IsParkFailureWeExplain(ex))
            {
                if (ShouldRetryPark(ex, attempt)) { Thread.Sleep(RetryPause); continue; }
                throw new InvalidOperationException(ParkFailureMessage(target, ex), ex);
            }
        }
    }

    /// <summary>
    /// Failures of the first rename that we take responsibility for explaining, rather
    /// than letting Windows' own text through.
    ///
    /// Both arms matter. The IOException arm is the sibling mid-swap. The other is a
    /// denied rename, which File.Move raises as UnauthorizedAccessException — NOT an
    /// IOException — so a filter that only named IOException would let the one message
    /// with no path in it ("Access to the path is denied") straight through to a dialog.
    ///
    /// A sharing violation is in for the same reason as the denial, and I had this
    /// wrong: its text — "The process cannot access the file because it is being used
    /// by another process" — names no file either. File.Move's two-argument overload
    /// passes no path to the Win32 error translator, so nothing it raises here carries
    /// one. It is also common at THIS rename specifically, because InstallToAppData
    /// holds the destination with neither read nor delete sharing for as long as it
    /// takes to copy 6.5 MB.
    ///
    /// A full disk and the rest keep their own text, which does say something specific.
    /// </summary>
    internal static bool IsParkFailureWeExplain(Exception ex)
        => ex is UnauthorizedAccessException
           || (ex is IOException io && (IsSiblingMidSwap(io) || IsSharingViolation(io)));

    /// <summary>
    /// Whether another attempt could change the answer. A denial can't: the same call
    /// will be denied again, and spending the full park budget on it only adds a second
    /// of frozen UI before the same report.
    /// </summary>
    internal static bool ShouldRetryPark(Exception ex, int attempt)
        => ex is IOException io && (IsSiblingMidSwap(io) || IsSharingViolation(io))
           && attempt < ParkAttempts;

    /// <summary>
    /// What to tell someone whose update couldn't move the running program aside.
    ///
    /// A pure function of the failure and whether a program file is there, because the
    /// state that produces the first case can't be staged in-process — it needs a real
    /// ACL denial or a security product — so the routing is what gets pinned. The rule
    /// each arm exists for: never hand over the bare Win32 text (the denial's does not
    /// even name a path), and never blame a race that hasn't been established.
    /// </summary>
    internal static string ParkFailureMessage(string target, Exception ex) => ex switch
    {
        UnauthorizedAccessException =>
            $"Windows refused to rename {target}, so the update can't be applied. That is " +
            "usually a security product guarding the folder, or the folder's own " +
            "permissions — not another Markdown Midget window.",
        // Before the "another window" arm: something holding the file is not the same
        // as something racing us for a name, and the remedy is different.
        IOException sharing when IsSharingViolation(sharing) =>
            $"Something else has {target} open, so it couldn't be moved aside and the " +
            "update wasn't applied. Nothing has changed. That is usually a virus scanner, " +
            "or another copy of Markdown Midget being installed at the same time — either " +
            "way it passes, so trying again shortly normally works.",
        _ when File.Exists(target) =>
            "Couldn't move the running program out of the way — another Markdown Midget " +
            "window kept getting there first. Close the other windows and try the update " +
            "again.",
        _ =>
            $"There is no Markdown Midget program file at {target} to update. Another " +
            "window may be partway through its own update — if so, wait a moment and try " +
            "again. Otherwise reinstall from the website, or run the copy you have from " +
            "wherever it actually lives.",
    };

    /// <summary>
    /// Attempts at the first rename. More than the recovery below gets, deliberately:
    /// the sibling this waits for may itself be in a recovery that holds the gap for
    /// ~600ms, and giving up in 300ms would announce a missing program file while the
    /// window next door is putting one back.
    /// </summary>
    private const int ParkAttempts = 8;

    /// <summary>Attempts at each of the two recovery renames.</summary>
    private const int RecoveryAttempts = 3;

    /// <summary>
    /// Long enough for a sibling's renames to finish, short enough not to read as a
    /// hang. A swap that exhausts the park (7 waits) and both recovery loops (2 each)
    /// stalls for about 1.6s, on the UI thread, on top of the synchronous 6.5 MB copy
    /// that already runs there. Only failing updates pay any of it.
    /// </summary>
    private static readonly TimeSpan RetryPause = TimeSpan.FromMilliseconds(150);

    /// <summary>183 (the name is taken) or 2/3 (the target isn't there yet) — the two
    /// shapes a sibling's in-progress swap presents, and the only two worth another
    /// go. Everything else means waiting changes nothing.</summary>
    internal static bool IsSiblingMidSwap(IOException ex) => (ex.HResult & 0xFFFF) is 183 or 2 or 3;

    /// <summary>
    /// A failed rename that the recovery should handle rather than let escape.
    ///
    /// The second type is the one that is easy to miss and expensive to get wrong:
    /// File.Move surfaces ERROR_ACCESS_DENIED as UnauthorizedAccessException, which is
    /// NOT an IOException — so a catch of IOException alone lets exactly the security
    /// products that cause this (Defender ASR, Controlled Folder Access, an EDR filter
    /// holding a just-written 6.5 MB PE) skip the retries, skip the fallback, skip the
    /// instructions, and leave the folder with no exe.
    /// </summary>
    internal static bool IsRecoverableMoveFailure(Exception ex)
        => ex is IOException or UnauthorizedAccessException;

    /// <summary>
    /// Move <paramref name="staged"/> onto <paramref name="target"/>, parking the
    /// existing target at <paramref name="old"/>. Two same-volume renames, so the
    /// window in which nothing sits at <paramref name="target"/> is as short as the
    /// filesystem can make it.
    ///
    /// Separated from the download and the restart so the one sequence here that can
    /// leave a directory with no usable exe is testable on ordinary files.
    /// </summary>
    internal static void SwapInPlace(string target, string staged, string old)
        => SwapInPlace(target, staged, () => old);

    /// <summary>
    /// As above, but re-deciding the parking name on each attempt.
    ///
    /// <see cref="ChooseParkingName"/> reports what was free at the instant it looked,
    /// and nothing reserves it. The plain `…exe.old` is the one candidate two processes
    /// can both pick — every other shape carries a process id, and two live processes
    /// can't share one — so two windows updating at once can each be told it is free
    /// and the second still collide. Hoisting the choice next to the rename shrank that
    /// gap to a couple of syscalls but cannot close it; only re-choosing can.
    /// </summary>
    internal static void SwapInPlace(string target, string staged, Func<string> chooseOld)
        => SwapInPlace(target, staged, chooseOld, PutSomethingBack);

    /// <summary>
    /// As above, with the recovery step injectable. Which of its three outcomes counts
    /// as a failure is the single decision here that the user actually sees — whether
    /// they are told the update failed — and the states that produce the other two
    /// can't be staged from outside without a race. So the branch gets a seam.
    /// </summary>
    internal static void SwapInPlace(string target, string staged, Func<string> chooseOld,
                                     Func<string, string, string, Recovery> recover)
    {
        // Set whenever the recovery gave up rather than returned. One of those messages
        // tells the user to go and rename `staged` by hand, and deleting it moments
        // later would make that a lie about the only copy of the verified download; the
        // others don't, and keeping it costs one file that the next launch sweeps
        // anyway. Erring towards keeping is the cheap direction.
        var keepStaged = false;
        try
        {
            // The name it actually parked at, which after a retry is not the name the
            // first call chose — the rollback below has to move back the right file.
            var parked = ParkTarget(target, chooseOld);
            try
            {
                File.Move(staged, target); // same volume — effectively atomic
            }
            catch (Exception moveFailure)
            {
                // The dangerous instant: nothing sits at `target` at all. Getting
                // SOMETHING back there matters more than which something — and only
                // one of the three ways that can end is a failure worth reporting.
                // Putting the OLD version back means the update didn't happen; the
                // other two mean a good binary is at the canonical path, which is what
                // a successful swap produces, so the caller should carry on and start
                // it rather than being told about an error that changed nothing.
                Recovery outcome;
                try { outcome = recover(target, parked, staged); }
                catch { keepStaged = true; throw; }

                if (outcome != Recovery.OldVersionRestored) return;

                // Rolled back: the install is exactly as it was. Say that, and say it
                // without handing over a Win32 sentence that names no file — the same
                // pathless text the portable flow already replaces, and the same shape
                // as the report this release started from. Nothing here sends the user
                // looking for the staged copy, so it is still litter and still swept.
                throw new InvalidOperationException(
                    $"The update couldn't be applied: the new copy could not be moved into " +
                    $"place. Nothing has changed — {Path.GetFileName(target)} is still the " +
                    "version you were running. This is usually a virus scanner holding the " +
                    "freshly written file for a moment, so trying again shortly often works.",
                    moveFailure);
            }
        }
        finally
        {
            // On success the staged file has been moved away and this is a no-op. On
            // failure it is a ~6.5 MB copy nothing else would ever remove.
            //
            // Not when the rollback ALSO failed, though: nothing sits at `target` then
            // and this verified copy is the best recovery artifact left. Nor when the
            // failure was reported with a message naming it. Leaking a file is
            // recoverable; deleting the only working binary is not.
            try
            {
                if (!keepStaged && File.Exists(staged) && File.Exists(target)) File.Delete(staged);
            }
            catch { /* best effort */ }
        }
    }

    /// <summary>How the recovery below ended. Only the first is a failed update; the
    /// other two leave a good binary at the canonical path.</summary>
    internal enum Recovery
    {
        /// <summary>The previous version went back. The install is as it was.</summary>
        OldVersionRestored,
        /// <summary>The canonical path already holds a byte-for-byte copy of the
        /// binary this window verified, so the update is done however it got there.
        ///
        /// Deliberately NOT "another window beat us to it": the park is a mutual
        /// exclusion — a process can only install after succeeding at the rename, which
        /// needs the target to exist, and it doesn't for the whole of our gap. So no
        /// sibling update can land here. What can is the un-synchronised File.Copy in
        /// RegistrationService.InstallToAppData, which is why the bytes are checked
        /// rather than assumed.</summary>
        VerifiedCopyAlreadyThere,
        /// <summary>The old one wouldn't go back, so the verified new one went in
        /// instead. The update landed after all.</summary>
        StagedInstalled,
    }

    /// <summary>
    /// Put a working binary back at <paramref name="target"/> after the second rename
    /// failed.
    ///
    /// This is the only place in the app that can leave a directory with no exe. The
    /// ordinary reason for getting here — a scanner holding a freshly-renamed 6.5 MB
    /// binary — is also a reason the rollback itself fails, and it clears on its own.
    /// So it waits and retries, then falls back to the staged copy, which is verified,
    /// newer, and an entirely acceptable resident. Only if neither will go does it
    /// throw, and then it names the files to rename by hand: an install with nothing to
    /// launch must not be reported with a sentence about processes and file handles.
    /// </summary>
    internal static Recovery PutSomethingBack(string target, string parked, string staged)
    {
        // Something refilled `target` in this same gap — which is one of the reasons
        // the second move failed. Moving anything onto it would only fail again, so
        // don't; judge what is there instead.
        //
        // Checking here rather than only at the bottom is an optimisation: the
        // look-again reaches the same verdict, but after 600ms of renames that cannot
        // succeed, aimed at a file somebody else just wrote.
        if (File.Exists(target)) return JudgeWhatIsThere(target, parked, staged);

        for (var attempt = 1; attempt <= RecoveryAttempts; attempt++)
        {
            try { File.Move(parked, target); return Recovery.OldVersionRestored; }
            catch (Exception ex) when (IsRecoverableMoveFailure(ex))
            {
                if (attempt < RecoveryAttempts) Thread.Sleep(RetryPause);
            }
        }

        // The old one won't go back. Try the new one — the same move that just failed,
        // but whatever blocked it may well have been the transient thing above.
        for (var attempt = 1; ; attempt++)
        {
            try { File.Move(staged, target); return Recovery.StagedInstalled; }
            catch (Exception ex) when (IsRecoverableMoveFailure(ex))
            {
                if (attempt < RecoveryAttempts) { Thread.Sleep(RetryPause); continue; }

                // Look once more before declaring a catastrophe: ~600ms have passed,
                // and a program file appearing in that time turns this from the worst
                // outcome into something else entirely. Saying otherwise would send the
                // user to rename an old version over a working newer one.
                if (File.Exists(target)) return JudgeWhatIsThere(target, parked, staged);

                throw new InvalidOperationException(
                    $"The update failed partway and left no program at {target}. Nothing is " +
                    $"lost: in that folder, rename either \"{Path.GetFileName(parked)}\" (the " +
                    $"version you were running) or \"{Path.GetFileName(staged)}\" (the new one, " +
                    $"already verified) to \"{Path.GetFileName(target)}\", and Markdown Midget " +
                    "will start again.", ex);
            }
        }
    }

    /// <summary>
    /// Something is at <paramref name="target"/> that this window did not put there.
    /// It is the update only if it is byte-for-byte the file we downloaded and checked
    /// the signature of; anything else is a stranger, and reporting a stranger as a
    /// successful update would restart the app into an unverified binary — quite
    /// possibly an older one, since the caller goes on to repoint every shortcut at it
    /// and the sweep then removes the copy the user was running.
    ///
    /// Nothing is moved or deleted in the mismatch case: both our binaries are still
    /// beside it, and the honest report is that this update did not happen.
    /// </summary>
    private static Recovery JudgeWhatIsThere(string target, string parked, string staged)
    {
        // One verdict, used three ways. Asking twice would be a race against itself: a
        // scanner releasing its handle between the two calls turns "couldn't read" into
        // "same", the second answer no longer matches the branch that let it through,
        // and control falls to the mismatch throw — which is exactly the claim the
        // three-valued answer exists to prevent, made about two identical files.
        var verdict = Compare(staged, target);

        if (verdict == FileMatch.Same) return Recovery.VerifiedCopyAlreadyThere;

        // Either side being unreadable lands here, and both are reachable: the lock that
        // stopped the rename is an excellent reason our own copy can't be opened, and a
        // file still being written — the shape a half-finished File.Copy has — can't be
        // opened either. Asserting a mismatch on that would state something this window
        // has no way to know.
        if (verdict == FileMatch.CouldNotRead)
            throw new InvalidOperationException(
                $"A program file appeared at {target} while this update was being applied, " +
                "and this window couldn't read the two files to compare them — so it has " +
                "changed nothing and nothing is lost. Close every Markdown Midget window " +
                "and try the update again.");

        throw new InvalidOperationException(
            $"Something else wrote a program file to {target} while this update was being " +
            $"applied, and it is not the version that was just downloaded and verified. " +
            $"Nothing has been lost and nothing unverified has been started: the version " +
            $"you were running is beside it as \"{Path.GetFileName(parked)}\", and the " +
            $"new one as \"{Path.GetFileName(staged)}\". Close every Markdown Midget " +
            "window and try the update again.");
    }

    /// <summary>
    /// Portable flow: place the (already verified) versioned exe next to the
    /// running one and start it. Returns the new exe path.
    /// </summary>
    public static string ApplyPortableAndRestart(string verifiedNewExe, string assetName)
    {
        var dir = Path.GetDirectoryName(CurrentExePath)!;
        var safeName = Path.GetFileName(assetName);   // never let the release name escape `dir`
        var dest = Path.Combine(dir, safeName);
        if (string.Equals(dest, CurrentExePath, StringComparison.OrdinalIgnoreCase))
            dest = Path.Combine(dir, Path.GetFileNameWithoutExtension(safeName) + ".new.exe");

        // The portable collision: another window already updated, so `dest` exists and
        // may be the running image of the instance it started. Copying onto it throws
        // "The process cannot access the file … because it is being used by another
        // process" — a confusing failure for a file that is already exactly what we
        // were about to write. Skip the copy when the bytes already match.
        if (!SameFile(verifiedNewExe, dest))
        {
            try
            {
                File.Copy(verifiedNewExe, dest, overwrite: true);
            }
            catch (IOException) when (SameFile(verifiedNewExe, dest))
            {
                // Lost a race with a sibling writing the identical file. Nothing to do.
            }
            catch (IOException ex) when (File.Exists(dest) && IsSharingViolation(ex))
            {
                // Narrow deliberately: only a sharing/lock violation means "something
                // else has this file open". Anything else (disk full, for instance)
                // keeps its own message, because claiming another window is running
                // it would send the user looking for a window that isn't there — and
                // possibly at a half-written binary.
                throw new InvalidOperationException(
                    $"{Path.GetFileName(dest)} is already here and in use — most likely " +
                    "another Markdown Midget window is running it. Close that window and " +
                    "try again.", ex);
            }
        }
        Process.Start(new ProcessStartInfo(dest) { UseShellExecute = true });
        return dest;
    }

    /// <summary>Same length and same SHA-256 — cheap enough for a one-off 6 MB check,
    /// and the only honest way to say "that file is already the one I was going to
    /// write" when we can't open it for writing.</summary>
    internal static bool SameFile(string a, string b) => Compare(a, b) == FileMatch.Same;

    /// <summary>Whether two files are the same, or whether we couldn't tell.</summary>
    internal enum FileMatch { Same, Different, CouldNotRead }

    /// <summary>
    /// The three-valued form. "Couldn't tell" is a distinct answer from "not the same",
    /// and the difference decides whether it is honest to tell someone the file at the
    /// install path isn't what they downloaded — an unreadable file on EITHER side, and
    /// a file being written right now is unreadable on the target side, makes that a
    /// claim we have no basis for.
    /// </summary>
    internal static FileMatch Compare(string a, string b)
    {
        try
        {
            if (!File.Exists(a) || !File.Exists(b)) return FileMatch.Different;
            if (new FileInfo(a).Length != new FileInfo(b).Length) return FileMatch.Different;
            using var sa = File.OpenRead(a);
            using var sb = File.OpenRead(b);   // read share is enough even while running
            return System.Security.Cryptography.SHA256.HashData(sa)
                .AsSpan().SequenceEqual(System.Security.Cryptography.SHA256.HashData(sb))
                ? FileMatch.Same : FileMatch.Different;
        }
        catch { return FileMatch.CouldNotRead; }
    }

    /// <summary>Prefix of the temporary copy the installed swap stages next to the
    /// target. Everything after it is the owning process id.</summary>
    private const string StagedPrefix = ".mdm-update-staged";

    /// <summary>
    /// Startup cleanup: the `.old` left by a previous installed-flow update (the old
    /// process held it; by now it has exited), plus any staged copy abandoned by a
    /// failed one.
    ///
    /// Staged files are named per-process so two windows updating at once can't
    /// delete each other's, which means nothing reclaims them implicitly any more —
    /// a leftover would sit there forever. Includes the un-suffixed name written by
    /// versions before 0.6.4, since anyone who hit the bug this release fixes has one.
    /// </summary>
    public static void CleanupOldBinaries()
    {
        string exePath;
        // Not hoisted out of a try: CurrentExePath throws when the process path can't
        // be determined, and this runs bare in the MainWindow constructor.
        try { exePath = CurrentExePath; } catch { return; }
        CleanupOldBinaries(exePath);
    }

    /// <summary>
    /// As above, against an explicit exe path. Split out because the branch that
    /// declines to sweep is the one that decides whether a broken install keeps its
    /// last working binary, and that is worth pinning rather than reasoning about.
    /// </summary>
    internal static void CleanupOldBinaries(string exePath)
    {
        // Two independent sweeps, deliberately in separate try blocks: enumeration is
        // lazy, so a failure partway through the first would otherwise skip the second.
        string dir;
        try
        {
            dir = Path.GetDirectoryName(exePath)!;

            // With nothing at the canonical path, an `…exe.old` may be the only
            // working binary left — a failed swap AND a failed rollback. Leaking a file
            // is recoverable; deleting the last exe is not, so don't sweep at all.
            if (!File.Exists(exePath)) return;
        }
        catch { return; }

        try
        {
            // `…exe.old` and the `…exe.old-1234` variant used when an update had to
            // step around a still-running window's image. Both are just files by now
            // if their owner has exited; if it hasn't, the delete fails and the next
            // launch tries again.
            var exe = Path.GetFileName(exePath);
            foreach (var file in Directory.EnumerateFiles(dir, exe + ".old*"))
            {
                try { File.Delete(file); } catch { /* still in use — next launch */ }
            }
        }
        catch { /* best effort */ }

        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, StagedPrefix + "*.exe"))
            {
                if (!IsReclaimableStagedFile(file)) continue;
                try { File.Delete(file); } catch { /* next launch */ }
            }
        }
        catch { /* best effort */ }
    }

    /// <summary>
    /// Is this abandoned staging copy safe to delete?
    ///
    /// "<c>.mdm-update-staged.exe</c>" exactly: written only by versions before 0.6.4,
    /// which carry no process id, so there is nothing to test and no way to tell an
    /// abandoned one from a live one. Reclaimed unconditionally — the narrow cost being
    /// that a 0.6.3 window mid-update during the 0.6.3-to-0.6.4 transition loses its
    /// staging copy and its update fails and rolls back. "<c>…-1234.exe</c>": only once
    /// 1234 is gone, because during the swap the owner has the file closed rather than
    /// locked, and deleting it then would break that window's update instead of tidying
    /// up after it. That the pid may since have been reused by some unrelated program
    /// only makes this refuse to delete — it never deletes something live.
    /// </summary>
    internal static bool IsReclaimableStagedFile(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (!name.StartsWith(StagedPrefix, StringComparison.Ordinal)) return false;
        if (name.Length == StagedPrefix.Length) return true;              // legacy name
        var suffix = name[(StagedPrefix.Length + 1)..];
        return int.TryParse(suffix, out var pid) && !IsProcessAlive(pid);
    }

    /// <summary>ERROR_SHARING_VIOLATION (32) / ERROR_LOCK_VIOLATION (33) — the file is
    /// open somewhere else, as opposed to any other reason a write can fail.</summary>
    internal static bool IsSharingViolation(IOException ex) => (ex.HResult & 0xFFFF) is 32 or 33;

    private static bool IsProcessAlive(int pid)
    {
        try { using var _ = Process.GetProcessById(pid); return true; }
        catch { return false; }
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
            fdwRevocationChecks = 0,  // WTD_REVOKE_NONE here; VerifySignature does explicit revocation
            dwUnionChoice = 1,        // WTD_CHOICE_FILE
            dwStateAction = 1,        // WTD_STATEACTION_VERIFY
        };
        var fileInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPtr, false);
            data.pFile = fileInfoPtr;
            var action = ActionGenericVerifyV2;
            var result = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            // Always release the provider state the VERIFY action allocated.
            data.dwStateAction = 2;   // WTD_STATEACTION_CLOSE
            WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            return result;
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
