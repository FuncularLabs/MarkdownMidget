using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Navigation;
using MarkdownMidget.Updates;

namespace MarkdownMidget;

/// <summary>
/// About box: identity (copyright/license links), the running version, and the
/// newest available versions from GitHub — with one-click update. Stable and
/// prerelease are shown separately so the user knowingly chooses a prerelease.
/// </summary>
public partial class AboutDialog : Window
{
    private readonly UpdateVersion? _current;
    private ReleaseInfo? _stable;
    private ReleaseInfo? _prerelease;
    private bool _updating;

    // What the restarted instance should reopen — the owning window's document and
    // view flags, captured when the dialog opens. An update should not cost the
    // user their place; the startup argument parser already honours all of these.
    private readonly System.Collections.Generic.List<string> _relaunchArgs = new();

    // Whether the owning window's Help menu actually offers Apply-update. Help
    // viewer windows suppress the item, so advice pointing at it there would name
    // a control that does not exist for the reader.
    private readonly bool _hasApplyMenu;

    public AboutDialog(string? currentDocumentPath = null, bool readOnly = false,
                       bool sourceMode = false, bool hasApplyMenu = true)
    {
        InitializeComponent();
        _hasApplyMenu = hasApplyMenu;
        if (currentDocumentPath is not null) _relaunchArgs.Add(currentDocumentPath);
        if (readOnly) _relaunchArgs.Add("--readonly");
        if (sourceMode) _relaunchArgs.Add("--source");
        var info = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        _current = UpdateVersion.Parse(info);
        var installed = UpdateService.IsInstalled();
        CurrentVersionText.Text = $"Version {info}" + (installed ? "  (installed)" : "  (portable)");

        // When the exe on disk differs from what this window is running — in either
        // direction — say so HERE, permanently, not only as transient status text
        // during a manual update check. "Running" is what the first line already
        // shows; this line is the other half. Newer-on-disk is the ordinary case
        // (another window updated); older-on-disk is a rollback or a window left
        // open across one, and hiding the difference there would be exactly the
        // confusion this line exists to prevent. Installed mode only: a portable
        // instance's own path still holds its own exe, so the two can never differ.
        if (installed)
        {
            try
            {
                var onDisk = UpdateService.VersionOnDisk();
                if (onDisk is not null && _current is not null && onDisk.CompareTo(_current) != 0)
                {
                    var newer = onDisk.CompareTo(_current) > 0;
                    var action = !newer
                        ? "(older than this window — the installed copy was rolled back)"
                        : _hasApplyMenu
                            ? $"Use Help ▸ Apply v{onDisk} Update to switch."
                            : "Close and reopen this window to switch.";
                    OnDiskVersionText.Text =
                        $"Installed on disk: {onDisk} — this window is running {_current}. {action}";
                    OnDiskVersionText.Visibility = Visibility.Visible;
                }
            }
            catch { /* unreadable file version — the line just stays hidden */ }
        }

        Loaded += async (_, _) => await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        var check = await UpdateService.CheckAsync();
        if (check is null)
        {
            StableText.Text = "Newest release: couldn't check (offline?)";
            return;   // the prerelease row stays hidden; one failure message is enough
        }
        _stable = check.Stable;
        _prerelease = check.PrereleaseRelease;

        StableText.Text = _stable is null
            ? "Newest release: none published"
            : $"Newest release: {_stable.Tag}";

        var stableNewer = UpdateOffer.ShowStableUpdate(_stable, _current);
        StableUpdateBtn.Visibility = stableNewer ? Visibility.Visible : Visibility.Collapsed;

        // Only surface a prerelease while it still leads. A superseded one - say
        // 0.6.0-beta2 alongside a shipped 0.6.2 - would read as the more advanced
        // build when it is really older code, so the row is hidden entirely rather
        // than shown with the button disabled.
        var showPre = UpdateOffer.ShowPrerelease(_prerelease, _stable, _current);
        if (showPre)
        {
            PreText.Text = $"Newest prerelease: {_prerelease!.Tag}";
            PreText.Visibility = Visibility.Visible;
            PreUpdateBtn.Visibility = Visibility.Visible;
        }
        else
        {
            _prerelease = null;   // nothing offered, so nothing installable from here
            // Reset the view too, not just the model: a visible row left over from an
            // earlier refresh would be a live-looking button that quietly does nothing.
            PreText.Visibility = Visibility.Collapsed;
            PreUpdateBtn.Visibility = Visibility.Collapsed;
        }

        if (!stableNewer && !showPre)
        {
            StatusText.Text = "You're up to date.";
            StatusText.Visibility = Visibility.Visible;
        }
    }

    private async void StableUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_stable is not null) await UpdateToAsync(_stable, prerelease: false);
    }

    private async void PreUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_prerelease is not null) await UpdateToAsync(_prerelease, prerelease: true);
    }

    private async Task UpdateToAsync(ReleaseInfo release, bool prerelease)
    {
        if (_updating) return;

        var installed = UpdateService.IsInstalled();

        // Another window may already have applied this exact update. Check before
        // asking anything: there is nothing to download, nothing to install, and the
        // swap would fail on the rename because the target name is this process's own
        // locked image — surfacing "Cannot create a file when that file already
        // exists", which tells the user nothing about what to do. Only the installed
        // flow renames, so only it can be detected this way; the portable flow is
        // handled inside ApplyPortableAndRestart.
        if (installed && UpdateService.AlreadyUpdatedOnDisk(release.Version, _current, out var onDisk))
        {
            var howToSwitch = _hasApplyMenu
                ? $"Use Help ▸ Apply v{onDisk} Update to switch to it in one click, " +
                  "or close and reopen the window."
                : "Close and reopen the window to pick it up.";
            MessageBox.Show(this,
                $"Markdown Midget on this machine is already at {onDisk} — most likely " +
                "updated by another window.\n\nThis window is still running the older " +
                $"version. {howToSwitch}",
                "Restart to finish updating", MessageBoxButton.OK, MessageBoxImage.Information);
            StatusText.Text = _hasApplyMenu
                ? $"Already at {onDisk} on disk — Help ▸ Apply v{onDisk} Update switches to it."
                : $"Already at {onDisk} on disk — close and reopen this window to pick it up.";
            StatusText.Visibility = Visibility.Visible;
            StableUpdateBtn.Visibility = PreUpdateBtn.Visibility = Visibility.Collapsed;
            return;
        }

        var what = prerelease
            ? $"{release.Tag} is a PRERELEASE — early access, may contain rough edges."
            : $"{release.Tag} is the newest stable release.";
        var how = installed
            ? "The installed copy will be replaced, shortcuts refreshed, and the app restarted."
            : "The new version will be downloaded next to the current one and started; the current exe stays behind.";
        if (MessageBox.Show(this, $"{what}\n\n{how}\n\nUpdate now?",
                "Markdown Midget update", MessageBoxButton.YesNo,
                prerelease ? MessageBoxImage.Warning : MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        _updating = true;
        StableUpdateBtn.IsEnabled = PreUpdateBtn.IsEnabled = false;
        Progress.Visibility = Visibility.Visible;
        StatusText.Visibility = Visibility.Visible;
        try
        {
            StatusText.Text = $"Downloading {release.AssetName}…";
            var file = await UpdateService.DownloadAsync(release);
            if (file is null) { Fail("Download failed — check your connection and try again."); return; }

            StatusText.Text = "Verifying signature…";
            var ok = await Task.Run(() => UpdateService.VerifySignature(file, out _));
            if (!ok) { Fail("The downloaded file failed signature verification and was NOT installed."); return; }

            StatusText.Text = "Installing…";
            if (installed)
            {
                UpdateService.ApplyInstalledAndRestart(file, _relaunchArgs);
            }
            else
            {
                UpdateService.ApplyPortableAndRestart(file, release.AssetName ?? "MarkdownMidget.exe", _relaunchArgs);
            }
            Application.Current.Shutdown();
        }
        catch (InvalidOperationException ex)
        {
            // Ours, and already a full sentence about what happened and what to do —
            // prefixing it produces "Update failed: Markdown Midget is updated, but…".
            Fail(ex.Message);
        }
        catch (Exception ex)
        {
            Fail($"Update failed: {ex.Message}");
        }
    }

    private void Fail(string message)
    {
        _updating = false;
        Progress.Visibility = Visibility.Collapsed;
        StatusText.Text = message;
        StableUpdateBtn.IsEnabled = PreUpdateBtn.IsEnabled = true;
    }

    private void Link_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch { /* no browser — nothing sane to do */ }
        e.Handled = true;
    }
}
