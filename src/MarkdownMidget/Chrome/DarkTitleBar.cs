using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MarkdownMidget.Chrome;

/// <summary>
/// Asks the Desktop Window Manager to draw a window's title bar dark or light. Windows
/// without dark title bars (before Windows 10 1809) refuse the request and keep a light one,
/// which is the only failure there is, so it is not reported.
/// </summary>
public static class DarkTitleBar
{
    /// <summary>DWMWA_USE_IMMERSIVE_DARK_MODE, Windows 10 20H1 and later.</summary>
    internal const int UseImmersiveDarkMode = 20;
    /// <summary>The same attribute's number on Windows 10 1809 to 1909.</summary>
    internal const int UseImmersiveDarkModeBefore20H1 = 19;

    /// <summary>Sends one attribute and returns the HRESULT. Replaced by tests.</summary>
    internal static Func<IntPtr, int, int, int> SetAttribute = SetAttributeNative;

    /// <summary>The mode waiting for a window that had no handle when it was asked.</summary>
    private static readonly DependencyProperty PendingProperty =
        DependencyProperty.RegisterAttached("Pending", typeof(object), typeof(DarkTitleBar), new PropertyMetadata(null));

    /// <summary>
    /// Dark or light title bar for <paramref name="window"/>. True when the request went to
    /// the window's handle; false when the window has none yet, in which case it goes on
    /// SourceInitialized, with the mode of the latest call.
    /// </summary>
    public static bool Apply(Window window, bool dark)
    {
        ArgumentNullException.ThrowIfNull(window);
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            if (window.GetValue(PendingProperty) is null) window.SourceInitialized += OnSourceInitialized;
            window.SetValue(PendingProperty, dark);
            return false;
        }
        Send(handle, dark);
        return true;
    }

    private static void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (sender is not Window window) return;
        window.SourceInitialized -= OnSourceInitialized;
        var pending = window.GetValue(PendingProperty);
        window.ClearValue(PendingProperty);
        if (pending is bool dark) Apply(window, dark);
    }

    private static void Send(IntPtr handle, bool dark)
    {
        var value = dark ? 1 : 0;
        try
        {
            if (SetAttribute(handle, UseImmersiveDarkMode, value) != 0)
                SetAttribute(handle, UseImmersiveDarkModeBefore20H1, value);
            // A window already on screen keeps its old caption until its frame is redrawn.
            if (IsWindowVisible(handle))
                SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0,
                    SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder | SwpFrameChanged);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // No DWM, or one without the call: the title bar stays as Windows draws it.
        }
    }

    private static int SetAttributeNative(IntPtr handle, int attribute, int value) =>
        DwmSetWindowAttribute(handle, attribute, ref value, sizeof(int));

    private const uint SwpNoSize = 0x0001, SwpNoMove = 0x0002, SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010,
                       SwpFrameChanged = 0x0020, SwpNoOwnerZOrder = 0x0200;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
}
