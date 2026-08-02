using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;

namespace MarkdownMidget;

/// <summary>
/// Per-monitor work areas, primary first.
///
/// The virtual-screen rectangle is only the bounding box of all monitors, so it
/// reports a window as on-screen when it actually sits in a gap of an L-shaped
/// arrangement, and gives no per-monitor size to clamp a restored window to.
/// Enumerating the monitors gives both — without pulling in a WinForms reference
/// just to read rectangles.
/// </summary>
internal static class MonitorInfo
{
    public static List<Rect> WorkAreas()
    {
        var areas = new List<Rect>();
        var primary = Rect.Empty;
        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr _, ref RECT _, IntPtr _) =>
            {
                var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfo(monitor, ref info))
                {
                    var work = info.rcWork;
                    var r = new Rect(work.left, work.top,
                        Math.Max(0, work.right - work.left), Math.Max(0, work.bottom - work.top));
                    if (r.Width > 0 && r.Height > 0)
                    {
                        if ((info.dwFlags & MONITORINFOF_PRIMARY) != 0) primary = r;
                        else areas.Add(r);
                    }
                }
                return true;   // keep enumerating
            }, IntPtr.Zero);
        }
        catch { /* fall back below */ }

        // Index 0 must be the primary: callers rescue stranded windows onto it and
        // derive the workspace origin from it. If enumeration found monitors but
        // couldn't identify the primary, synthesize it rather than letting an
        // arbitrary secondary take the slot — on a desk with a monitor to the left
        // that would displace every restored window by the width of that monitor.
        if (primary.IsEmpty)
        {
            var pw = GetSystemMetrics(SM_CXSCREEN);
            var ph = GetSystemMetrics(SM_CYSCREEN);
            if (pw > 0 && ph > 0) primary = new Rect(0, 0, pw, ph);
        }
        if (!primary.IsEmpty)
        {
            areas.RemoveAll(a => a == primary);   // don't list it twice
            areas.Insert(0, primary);
        }
        if (areas.Count == 0)
        {
            // Last resort only (enumeration failed outright). GetSystemMetrics is
            // physical pixels like everything else here — SystemParameters would be
            // WPF's device-independent units and would reintroduce the very
            // coordinate-space mix this class exists to avoid.
            var w = GetSystemMetrics(SM_CXSCREEN);
            var h = GetSystemMetrics(SM_CYSCREEN);
            areas.Add(w > 0 && h > 0 ? new Rect(0, 0, w, h) : SystemParameters.WorkArea);
        }
        return areas;
    }

    private const int MONITORINFOF_PRIMARY = 1;
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref RECT rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }
}
