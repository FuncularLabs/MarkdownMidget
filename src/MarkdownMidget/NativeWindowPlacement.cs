using System;
using System.Runtime.InteropServices;
using System.Windows;

namespace MarkdownMidget;

/// <summary>
/// Reads and writes the window rectangle through Win32 rather than WPF properties.
///
/// WPF's Left/Top/Width/Height are device-independent units scaled by whichever
/// monitor the window is on, while monitor rectangles (see <see cref="MonitorInfo"/>)
/// are physical pixels. Remembering one and validating it against the other gives
/// wrong answers the moment a display isn't at 100% scaling — on a mixed-DPI desk
/// the window comes back on the wrong monitor, or sized so its close button is off
/// the edge. Get/SetWindowPlacement work in physical pixels, and this class converts
/// their workspace origin to screen coordinates, so callers see one space throughout.
///
/// It also answers the maximized question correctly: WPF only reports the state
/// right now, but WPF_RESTORETOMAXIMIZED records that a currently-minimized window
/// would return to maximized — which is exactly what should be remembered when the
/// user closes from the taskbar.
/// </summary>
internal static class NativeWindowPlacement
{
    /// <summary>The window's normal (non-maximized) rectangle, in physical SCREEN
    /// pixels — the same space <see cref="MonitorInfo"/> reports.</summary>
    public static bool TryGet(IntPtr hwnd, out Rect normal, out bool maximized)
    {
        normal = Rect.Empty;
        maximized = false;
        if (hwnd == IntPtr.Zero) return false;
        var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (!GetWindowPlacement(hwnd, ref wp)) return false;

        var r = wp.rcNormalPosition;
        var w = r.right - r.left;
        var h = r.bottom - r.top;
        if (w <= 0 || h <= 0) return false;

        var origin = WorkspaceOrigin();
        normal = new Rect(r.left + origin.X, r.top + origin.Y, w, h);
        maximized = wp.showCmd == SW_SHOWMAXIMIZED
                 || (wp.flags & WPF_RESTORETOMAXIMIZED) != 0;   // minimized-but-was-maximized
        return true;
    }

    /// <summary>
    /// Move/resize the window to a screen-pixel rectangle.
    ///
    /// Deliberately SetWindowPlacement and not SetWindowPos. Positioning a window
    /// onto a monitor with a different scale factor raises WM_DPICHANGED, and WPF
    /// answers it by rescaling the window by the DPI ratio — so a rectangle applied
    /// with SetWindowPos arrives 1.5x too big on a 150% display, and grows again on
    /// every launch. SetWindowPlacement is interpreted as the window's own placement
    /// record and doesn't trigger that. Measured both ways: SetWindowPos restored
    /// 1200x800 as 2700x1453; SetWindowPlacement round-trips exactly.
    /// </summary>
    public static void Apply(IntPtr hwnd, Rect rect, bool maximized)
    {
        if (hwnd == IntPtr.Zero) return;
        var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (!GetWindowPlacement(hwnd, ref wp)) return;   // keep the default placement

        var origin = WorkspaceOrigin();                  // screen -> workspace
        wp.rcNormalPosition = new RECT
        {
            left = (int)Math.Round(rect.X - origin.X),
            top = (int)Math.Round(rect.Y - origin.Y),
            right = (int)Math.Round(rect.X - origin.X + rect.Width),
            bottom = (int)Math.Round(rect.Y - origin.Y + rect.Height),
        };
        // Never restore minimized — an app that starts in the taskbar looks like it
        // failed to launch.
        wp.showCmd = maximized ? SW_SHOWMAXIMIZED : SW_SHOWNORMAL;
        wp.flags = 0;
        SetWindowPlacement(hwnd, ref wp);
    }

    /// <summary>
    /// Offset between workspace coordinates (what WINDOWPLACEMENT uses) and screen
    /// coordinates (what monitor rectangles use). They differ only when the taskbar
    /// is docked at the top or left of the primary monitor, in which case it is the
    /// primary work area's origin.
    /// </summary>
    private static Point WorkspaceOrigin()
    {
        var work = MonitorInfo.WorkAreas()[0];      // primary, by construction
        return new Point(work.X, work.Y);
    }

    private const int SW_SHOWNORMAL = 1;
    private const int SW_SHOWMAXIMIZED = 3;
    private const int WPF_RESTORETOMAXIMIZED = 0x0002;

    [DllImport("user32.dll")]
    private static extern bool GetWindowPlacement(IntPtr hwnd, ref WINDOWPLACEMENT wp);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPlacement(IntPtr hwnd, ref WINDOWPLACEMENT wp);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT
    {
        public int length;
        public int flags;
        public int showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT rcNormalPosition;
    }
}
