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

    public AboutDialog()
    {
        InitializeComponent();
        var info = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        _current = UpdateVersion.Parse(info);
        CurrentVersionText.Text = $"Version {info}" + (UpdateService.IsInstalled() ? "  (installed)" : "  (portable)");
        Loaded += async (_, _) => await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        var check = await UpdateService.CheckAsync();
        if (check is null)
        {
            StableText.Text = "Newest release: couldn't check (offline?)";
            PreText.Text = "Newest prerelease: couldn't check";
            return;
        }
        _stable = check.Stable;
        _prerelease = check.PrereleaseRelease;

        StableText.Text = _stable is null
            ? "Newest release: none published"
            : $"Newest release: {_stable.Tag}";
        PreText.Text = _prerelease is null
            ? "Newest prerelease: none published"
            : $"Newest prerelease: {_prerelease.Tag}";

        var stableNewer = _stable is not null && _current is not null && _stable.Version.CompareTo(_current) > 0;
        var preNewer = _prerelease is not null && _current is not null && _prerelease.Version.CompareTo(_current) > 0;
        StableUpdateBtn.Visibility = stableNewer ? Visibility.Visible : Visibility.Collapsed;
        PreUpdateBtn.Visibility = preNewer ? Visibility.Visible : Visibility.Collapsed;

        if (!stableNewer && !preNewer)
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
                UpdateService.ApplyInstalledAndRestart(file);
            }
            else
            {
                UpdateService.ApplyPortableAndRestart(file, release.AssetName ?? "MarkdownMidget.exe");
            }
            Application.Current.Shutdown();
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
