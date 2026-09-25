using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MarkdownMidget.Tests;

/// <summary>
/// Real WPF windows for tests that need a PresentationSource (layout, IsVisible, item
/// containers), made so they never reach the screens, the keyboard or the taskbar of
/// whoever is using the desktop while <c>dotnet test</c> runs.
///
/// <see cref="Create"/> gives a window that is far off every monitor, shown without
/// activation (ShowActivated false) and owned (ShowInTaskbar false), so it is in neither
/// the taskbar nor Alt+Tab. It also has WS_EX_NOACTIVATE, so WPF never asks Windows to
/// focus it and a Focus() call in it cannot activate it. An activation matters even far
/// off screen: Windows can turn it into the foreground, and the user's typing then goes
/// to the test's window. The price is that nothing in it takes keyboard focus: WPF gives
/// keyboard focus in such a window only while its thread already holds Win32 focus
/// somewhere. A test that needs keyboard focus is a <see cref="TakesFocusFactAttribute"/>.
/// Close the window in a finally block.
///
/// A popup needs <see cref="KeepPopupOffscreen"/> as well. WPF puts a popup where its
/// placement says, and a ContextMenu opens at the mouse pointer by default, whatever its
/// target's position, so an opened menu would be on the screen of whoever is using the
/// desktop.
/// </summary>
internal static class OffscreenWindow
{
    /// <summary>Left of and above every monitor. DarkTitleBarTests uses the same position.</summary>
    private const int Position = -20000;

    private const int GwlExStyle = -20;
    private const long WsExNoActivate = 0x08000000;
    private const int WmWindowPosChanging = 0x0046;
    private const uint SwpNoMove = 0x0002;

    public static Window Create(object content, double width, double height)
    {
        var window = new Window
        {
            Left = Position,
            Top = Position,
            Width = width,
            Height = height,
            ShowActivated = false,
            ShowInTaskbar = false,
            Content = content,
        };
        // After the handle exists and before Show makes the window visible.
        window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            SetWindowLongPtrW(handle, GwlExStyle, (IntPtr)((long)GetWindowLongPtrW(handle, GwlExStyle) | WsExNoActivate));
        };
        return window;
    }

    /// <summary>
    /// Keeps the popup that <paramref name="content"/> opens in (a ContextMenu) far off
    /// every monitor. WPF places a popup itself, at the mouse pointer for a ContextMenu,
    /// and keeps it on a monitor, so this moves every placement of the popup's window
    /// before Windows applies it, the one that shows it included. Call it before opening.
    /// </summary>
    public static void KeepPopupOffscreen(UIElement content) =>
        PresentationSource.AddSourceChangedHandler(content, (_, e) =>
        {
            if (e.NewSource is HwndSource popup) popup.AddHook(PinOffscreen);
        });

    private static IntPtr PinOffscreen(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmWindowPosChanging)
        {
            var pos = Marshal.PtrToStructure<WindowPos>(lParam);
            pos.X = Position;
            pos.Y = Position;
            pos.Flags &= ~SwpNoMove;
            Marshal.StructureToPtr(pos, lParam, fDeleteOld: false);
        }
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPos
    {
        public IntPtr Hwnd, InsertAfter;
        public int X, Y, Width, Height;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowLongPtrW(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowLongPtrW(IntPtr hwnd, int index, IntPtr value);
}
