using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace MarkdownMidget;

/// <summary>
/// Keeps every dialog (a window with a WPF <see cref="Window.Owner"/>) on its owner's monitor:
/// it opens with the whole title bar inside the work area and no bigger than it, and a size-to-content
/// dialog stays capped there so it scrolls. WPF's
/// CenterOwner does not do this for a maximised or minimised owner: it centres on the work
/// area with no clamp, so a dialog taller than the work area opens with its title bar above
/// the top of the screen. Attached to every window by <see cref="Chrome.ChromeWindows"/>.
/// </summary>
internal static class DialogPlacement
{
    private static readonly DependencyProperty PlacedProperty =
        DependencyProperty.RegisterAttached("Placed", typeof(bool), typeof(DialogPlacement), new PropertyMetadata(false));

    /// <summary>The work area of the monitor nearest a window, in physical pixels; null when
    /// Windows can't say. Replaced by tests.</summary>
    internal static Func<IntPtr, Rect?> WorkAreaOf = WorkAreaNative;

    /// <summary>
    /// Where the dialog goes, in physical pixels, and its largest size in device-independent
    /// units: centred on <paramref name="owner"/> (on the work area when null, as for a minimised
    /// owner), shifted inside <paramref name="work"/>, and the top-left corner wins when it can't
    /// all fit. <paramref name="dialog"/> is in device-independent units; <paramref name="scale"/>
    /// is the DPI scale of the monitor <paramref name="work"/> belongs to.
    /// </summary>
    internal static (Point Position, Size MaxSize) Fit(Rect? owner, Size dialog, Rect work, DpiScale scale)
    {
        var (w, h) = (dialog.Width * scale.DpiScaleX, dialog.Height * scale.DpiScaleY);
        var around = owner ?? work;
        // Min, then Max: a dialog too big to fit ends up at the left and top edges, never past them.
        var x = Math.Max(Math.Min(around.X + (around.Width - w) / 2, work.Right - w), work.Left);
        var y = Math.Max(Math.Min(around.Y + (around.Height - h) / 2, work.Bottom - h), work.Top);
        return (new Point(Math.Floor(x), Math.Floor(y)), new Size(work.Width / scale.DpiScaleX, work.Height / scale.DpiScaleY));
    }

    /// <summary>A window opened with nothing saved (the main window's first launch, Help): its default
    /// <paramref name="size"/> shrunk to <paramref name="work"/> but not below <paramref name="min"/> (both DIPs),
    /// centred there by <see cref="Fit"/>; the top-left wins when even the minimum doesn't fit. Physical pixels.</summary>
    internal static Rect FitDefault(Size size, Size min, Rect work, DpiScale scale)
    {
        var max = Fit(null, size, work, scale).MaxSize;
        var fitted = new Size(Math.Max(Math.Min(size.Width, max.Width), min.Width), Math.Max(Math.Min(size.Height, max.Height), min.Height));
        return new Rect(Fit(null, fitted, work, scale).Position, new Size(Math.Round(fitted.Width * scale.DpiScaleX), Math.Round(fitted.Height * scale.DpiScaleY)));
    }

    /// <summary>
    /// The first layout (handle made, not yet visible) places the dialog on its owner's monitor;
    /// a later size change made by its content (SizeToContent) keeps it inside the monitor it is on.
    /// A size the user set, which turns SizeToContent off, is left alone.
    /// </summary>
    internal static void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not Window { Owner: { } owner, WindowState: WindowState.Normal } dialog) return;
        var placed = (bool)dialog.GetValue(PlacedProperty);
        if (placed && dialog.SizeToContent == SizeToContent.Manual) return;
        var handle = new WindowInteropHelper(dialog).Handle;
        var near = placed ? handle : new WindowInteropHelper(owner).Handle;
        if (handle == IntPtr.Zero || near == IntPtr.Zero || WorkAreaOf(near) is not { } work) return;

        var scale = VisualTreeHelper.GetDpi(placed ? dialog : owner);
        Rect? around = null;
        if (GetWindowRect(near, out var r) && !IsIconic(near))
            around = placed   // a grown dialog keeps its top-left corner unless it no longer fits
                ? new Rect(r.Left, r.Top, e.NewSize.Width * scale.DpiScaleX, e.NewSize.Height * scale.DpiScaleY)
                : new Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        var (at, max) = Fit(around, e.NewSize, work, scale);

        dialog.SetValue(PlacedProperty, true);
        // A SizeToContent window is centred again by WPF after this first layout; Manual stops that.
        dialog.WindowStartupLocation = WindowStartupLocation.Manual;
        if (dialog.SizeToContent != SizeToContent.Manual)
            (dialog.MaxWidth, dialog.MaxHeight) = (max.Width, max.Height);   // what makes it scroll; no dialog sets its own
        else   // a size the user owns (the picker): shrunk to fit once, free to maximise, snap or grow on a bigger screen
            (dialog.Width, dialog.Height) = (Math.Min(e.NewSize.Width, max.Width), Math.Min(e.NewSize.Height, max.Height));
        SetWindowPos(handle, IntPtr.Zero, (int)at.X, (int)at.Y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
    }

    internal static Rect? WorkAreaNative(IntPtr hwnd)
    {
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return null;
        var w = info.rcWork;
        return w.Right > w.Left && w.Bottom > w.Top ? new Rect(w.Left, w.Top, w.Right - w.Left, w.Bottom - w.Top) : null;
    }

    private const uint SwpNoSize = 0x0001, SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010, MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public int dwFlags; }

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
