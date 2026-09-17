using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace MarkdownMidget;

/// <summary>
/// Windows' app mode (Settings ▸ Personalization ▸ Colors ▸ Choose your mode), and
/// when it changes.
///
/// Two members to use: <see cref="IsDark"/> and <see cref="Changed"/>. High contrast
/// counts as light — a contrast theme brings its own colours, and Windows leaves the
/// app-mode value as it was underneath.
/// </summary>
internal sealed class WindowsAppearance : IDisposable
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>How long after the first notification the mode is read again.</summary>
    private static readonly TimeSpan SettleTime = TimeSpan.FromMilliseconds(250);

    private readonly Func<object?> _appsUseLightTheme;
    private readonly Func<bool> _highContrast;
    private readonly Action<Action> _settle;
    private int _pending;          // 1 while a re-read is scheduled
    private bool _subscribed;     // only the live instance from Start
    private volatile bool _disposed;

    /// <param name="appsUseLightTheme">The Personalize AppsUseLightTheme value, null when
    /// missing. A seam, so tests never read the registry.</param>
    /// <param name="highContrast">Whether high contrast is on.</param>
    /// <param name="settle">Runs the re-read later, on the window's thread — live, 250 ms
    /// after the first notification. Notifications until it runs share it; one after it
    /// has run schedules another.</param>
    internal WindowsAppearance(Func<object?> appsUseLightTheme, Func<bool> highContrast, Action<Action>? settle = null)
    {
        _appsUseLightTheme = appsUseLightTheme;
        _highContrast = highContrast;
        _settle = settle ?? (refresh => refresh());
        IsDark = Read();
    }

    /// <summary>True when Windows' apps are in dark mode and high contrast is off.</summary>
    public bool IsDark { get; private set; }

    /// <summary>Raised on the window's thread, once, when <see cref="IsDark"/> flips — not
    /// for other preference changes, nor for each of the repeated broadcasts Windows
    /// sends for one switch.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// The live instance for a window, raising <see cref="Changed"/> on
    /// <paramref name="dispatcher"/>. Dispose it when the window closes: SystemEvents
    /// keeps a static reference to its handler, and through it to the window.
    /// </summary>
    public static WindowsAppearance Start(Dispatcher dispatcher)
    {
        // Background priority, after a short wait: by then WPF has handled the same
        // broadcast itself and refreshed SystemParameters.HighContrast.
        var appearance = new WindowsAppearance(ReadAppsUseLightTheme, () => SystemParameters.HighContrast,
            refresh => Task.Delay(SettleTime).ContinueWith(_ => dispatcher.BeginInvoke(DispatcherPriority.Background, refresh),
                                                           TaskScheduler.Default));
        // A mode switch arrives as WM_SETTINGCHANGE "ImmersiveColorSet", sent several
        // times, and high contrast as SPI_SETHIGHCONTRAST; SystemEvents raises
        // UserPreferenceChanged for both. Every category is taken rather than trusting
        // one: the re-read is cheap, and Refresh raises only on an actual flip.
        SystemEvents.UserPreferenceChanged += appearance.OnSystemPreferenceChanged;
        appearance._subscribed = true;
        return appearance;
    }

    private void OnSystemPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e) => OnPreferenceChanged();

    /// <summary>A Windows preference changed, maybe the mode: schedule a re-read, unless
    /// one is already scheduled and not yet run. May be called from any thread.</summary>
    internal void OnPreferenceChanged()
    {
        if (_disposed || Interlocked.Exchange(ref _pending, 1) == 1) return;
        _settle(() =>
        {
            Interlocked.Exchange(ref _pending, 0);
            Refresh();   // after Dispose there is no one left to tell: Changed is cleared
        });
    }

    /// <summary>Read the mode again, and raise <see cref="Changed"/> if it flipped.</summary>
    internal void Refresh()
    {
        var dark = Read();
        if (dark == IsDark) return;
        IsDark = dark;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // AppsUseLightTheme is a DWORD, 0 for dark. Missing or any other value: light,
    // Windows' own default.
    private bool Read() => !_highContrast() && _appsUseLightTheme() is int value && value == 0;

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
        Changed = null;
    }
}
