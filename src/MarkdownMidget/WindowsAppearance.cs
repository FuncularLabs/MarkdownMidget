using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace MarkdownMidget;

/// <summary>
/// The effective light or dark mode — Windows' app mode (Settings ▸ Personalization ▸
/// Colors ▸ Choose your mode) unless View ▸ Mode says Light or Dark — and when it changes.
///
/// Members to use: <see cref="IsDark"/>, <see cref="Mode"/>, <see cref="Changed"/>,
/// <see cref="SetMode"/> and <see cref="Follow"/>. High contrast counts as light, over
/// View ▸ Mode too — a contrast theme brings its own colours, and Windows leaves the
/// app-mode value as it was underneath (<see cref="AppearanceModes.IsDark"/>).
/// </summary>
internal sealed class WindowsAppearance : IDisposable
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>How long after the first notification the mode is read again.</summary>
    private static readonly TimeSpan SettleTime = TimeSpan.FromMilliseconds(250);

    private readonly Func<object?> _appsUseLightTheme;
    private readonly Func<bool> _highContrast;
    private readonly Action<Action> _settle;
    private readonly Func<AppearanceMode?> _savedMode;
    private FileSystemWatcher? _settingsWatcher;
    private int _pending;          // 1 while a re-read is scheduled
    private bool _subscribed;     // only the live instance from Start
    private volatile bool _disposed;

    /// <param name="appsUseLightTheme">The Personalize AppsUseLightTheme value, null when
    /// missing. A seam, so tests never read the registry.</param>
    /// <param name="highContrast">Whether high contrast is on.</param>
    /// <param name="settle">Runs the re-read later, on the window's thread — live, 250 ms
    /// after the first notification. Notifications until it runs share it; one after it
    /// has run schedules another.</param>
    /// <param name="savedMode">View ▸ Mode as settings.json holds it now; null when it can't
    /// be read, which keeps the mode last known (System at the start).</param>
    internal WindowsAppearance(Func<object?> appsUseLightTheme, Func<bool> highContrast, Action<Action>? settle = null,
                               Func<AppearanceMode?>? savedMode = null)
    {
        _appsUseLightTheme = appsUseLightTheme;
        _highContrast = highContrast;
        _settle = settle ?? (refresh => refresh());
        _savedMode = savedMode ?? (() => null);
        _lastSaved = _savedMode();
        Mode = _lastSaved ?? AppearanceMode.System;
        IsDark = Read();
    }

    private AppearanceMode? _lastSaved;   // the last mode read from settings.json; null before a read succeeds

    /// <summary>True when the effective mode is dark: View ▸ Mode is Dark, or System with
    /// Windows' apps in dark mode; and high contrast is off.</summary>
    public bool IsDark { get; private set; }

    /// <summary>View ▸ Mode, as picked in this window or last read from settings.json.</summary>
    public AppearanceMode Mode { get; private set; }

    /// <summary>Raised on the window's thread, once, when <see cref="IsDark"/> flips — not
    /// for other preference changes, nor for each of the repeated broadcasts Windows
    /// sends for one switch, nor for a View ▸ Mode change that leaves the mode as it was.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// The live instance for a window, raising <see cref="Changed"/> on
    /// <paramref name="dispatcher"/>, with View ▸ Mode read from
    /// <paramref name="settingsPath"/>. Dispose it when the window closes: SystemEvents
    /// keeps a static reference to its handler, and through it to the window.
    /// </summary>
    public static WindowsAppearance Start(Dispatcher dispatcher, string settingsPath)
    {
        // Background priority, after a short wait: by then WPF has handled the same
        // broadcast itself and refreshed SystemParameters.HighContrast.
        var appearance = new WindowsAppearance(ReadAppsUseLightTheme, () => SystemParameters.HighContrast,
            refresh => Task.Delay(SettleTime).ContinueWith(_ => dispatcher.BeginInvoke(DispatcherPriority.Background, refresh),
                                                           TaskScheduler.Default),
            () => AppearanceModes.ReadSetting(settingsPath));
        // A mode switch arrives as WM_SETTINGCHANGE "ImmersiveColorSet", sent several
        // times, and high contrast as SPI_SETHIGHCONTRAST; SystemEvents raises
        // UserPreferenceChanged for both. Every category is taken rather than trusting
        // one: the re-read is cheap, and Refresh raises only on an actual flip.
        SystemEvents.UserPreferenceChanged += appearance.OnSystemPreferenceChanged;
        appearance._subscribed = true;
        appearance.FollowSettingsFile(settingsPath);
        return appearance;
    }

    /// <summary>Re-read after every change to <paramref name="settingsPath"/>, until Dispose.</summary>
    internal void FollowSettingsFile(string settingsPath) =>
        _settingsWatcher = WatchSettings(settingsPath, OnPreferenceChanged);

    /// <summary>
    /// A View ▸ Mode pick in another window arrives as a write to settings.json, which every
    /// window watches. Any write, or the file going, is treated like a Windows notification:
    /// the same settled re-read, which raises only on a flip and never writes. A watcher
    /// that can't start leaves this window following Windows and its own picks only.
    /// Measured: a save over no file raises Renamed only; over a file, Deleted then Renamed;
    /// a write in place, Changed; a delete, Deleted; an empty file created, Created.
    /// </summary>
    internal static FileSystemWatcher? WatchSettings(string settingsPath, Action changed)
    {
        try
        {
            var directory = Path.GetDirectoryName(settingsPath)!;
            Directory.CreateDirectory(directory);   // a first run, before anything has saved
            var watcher = new FileSystemWatcher(directory, Path.GetFileName(settingsPath))
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            watcher.Changed += (_, _) => changed();
            watcher.Created += (_, _) => changed();
            watcher.Deleted += (_, _) => changed();
            watcher.Renamed += (_, _) => changed();
            watcher.Error += (_, _) => changed();   // events lost: read anyway
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch { return null; }
    }

    private void OnSystemPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e) => OnPreferenceChanged();

    /// <summary>A Windows preference or settings.json changed, maybe the mode: schedule a
    /// re-read, unless one is already scheduled and not yet run. May be called from any thread.</summary>
    internal void OnPreferenceChanged()
    {
        if (_disposed || Interlocked.Exchange(ref _pending, 1) == 1) return;
        _settle(() =>
        {
            Interlocked.Exchange(ref _pending, 0);
            Refresh();   // after Dispose there is no one left to tell: Changed is cleared
        });
    }

    /// <summary>Read Windows' mode and View ▸ Mode again, and raise <see cref="Changed"/> if
    /// the effective mode flipped. The saved mode is taken only when it differs from the one
    /// read last: a pick elsewhere. A pick here whose save didn't land then survives other
    /// windows' saves and Windows' broadcasts, until a pick elsewhere replaces it.</summary>
    internal void Refresh()
    {
        if (_savedMode() is { } saved && saved != _lastSaved) (Mode, _lastSaved) = (saved, saved);
        Update();
    }

    /// <summary>View ▸ Mode picked in this window: applies at once. The caller saves it first,
    /// and settings.json is read now, so a save that didn't land leaves the value on disk as
    /// the one read last (see <see cref="Refresh"/>).</summary>
    internal void SetMode(AppearanceMode mode)
    {
        if (_savedMode() is { } saved) _lastSaved = saved;
        Mode = mode;
        Update();
    }

    /// <summary>Call <paramref name="apply"/> with <see cref="IsDark"/> now and after every
    /// change: the window chrome (ChromePalette.Apply), before the window is first shown.</summary>
    internal void Follow(Action<bool> apply)
    {
        apply(IsDark);
        Changed += (_, _) => apply(IsDark);
    }

    private void Update()
    {
        var dark = Read();
        if (dark == IsDark) return;
        IsDark = dark;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // AppsUseLightTheme is a DWORD, 0 for dark. Missing or any other value: light,
    // Windows' own default.
    private bool Read() =>
        AppearanceModes.IsDark(Mode, _appsUseLightTheme() is int value && value == 0, _highContrast());

    private static object? ReadAppsUseLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("AppsUseLightTheme");
        }
        catch { return null; }   // unreadable: light
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_subscribed) SystemEvents.UserPreferenceChanged -= OnSystemPreferenceChanged;
        _settingsWatcher?.Dispose();
        Changed = null;
    }
}
