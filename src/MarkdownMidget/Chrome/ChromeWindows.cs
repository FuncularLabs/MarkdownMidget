using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;

namespace MarkdownMidget.Chrome;

/// <summary>
/// Title bars for every window, with no code in any window: a class handler sees each
/// <see cref="Window"/> (the main window, every dialog, the file picker, a window built in
/// code) as it is first laid out, which is after its handle exists and before it is shown, so
/// a dark window never flashes a light caption. It remembers the windows it has seen, weakly,
/// so a switch reaches the ones already open.
/// </summary>
internal static class ChromeWindows
{
    private static int _registered;
    private static volatile bool _dark;
    private static readonly List<WeakReference<Window>> Seen = [];

    /// <summary>The caption mode each window last received; unset is Windows' own, light.</summary>
    private static readonly DependencyProperty TitleBarDarkProperty =
        DependencyProperty.RegisterAttached("TitleBarDark", typeof(bool), typeof(ChromeWindows), new PropertyMetadata(false));

    private static readonly DependencyProperty TrackedProperty =
        DependencyProperty.RegisterAttached("Tracked", typeof(bool), typeof(ChromeWindows), new PropertyMetadata(false));

    internal static bool IsDark => _dark;

    /// <summary>Once per process; later calls do nothing.</summary>
    internal static void Register()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1) return;
        // SizeChanged comes with the first layout, before the window is visible (measured);
        // Loaded is the backstop for a window that somehow skipped it.
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.SizeChangedEvent,
            new SizeChangedEventHandler((sender, _) => Sync(sender)), handledEventsToo: true);
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => Sync(sender)), handledEventsToo: true);
    }

    /// <summary>Record the mode and bring every open window's title bar to it.</summary>
    internal static void SetDark(bool dark)
    {
        _dark = dark;
        var open = new List<Window>();
        lock (Seen)
        {
            Seen.RemoveAll(r => !r.TryGetTarget(out _));
            foreach (var r in Seen)
                if (r.TryGetTarget(out var w)) open.Add(w);
        }
        foreach (var window in open)
        {
            if (window.Dispatcher.HasShutdownStarted) continue;
            if (window.Dispatcher.CheckAccess()) SyncOpen(window);
            else window.Dispatcher.BeginInvoke(new Action(() => SyncOpen(window)));
        }
    }

    // A seen window with no handle has been closed: leave it alone rather than wait for a
    // handle it will never get again.
    private static void SyncOpen(Window window)
    {
        if (new System.Windows.Interop.WindowInteropHelper(window).Handle != IntPtr.Zero) Sync(window);
    }

    private static void Sync(object sender)
    {
        if (sender is not Window window) return;
        if (!(bool)window.GetValue(TrackedProperty))
        {
            window.SetValue(TrackedProperty, true);
            lock (Seen) Seen.Add(new WeakReference<Window>(window));
        }
        var dark = _dark;
        if ((bool)window.GetValue(TitleBarDarkProperty) == dark) return;
        if (DarkTitleBar.Apply(window, dark)) window.SetValue(TitleBarDarkProperty, dark);
    }
}
