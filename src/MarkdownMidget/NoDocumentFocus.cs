using System.Windows;

namespace MarkdownMidget;

/// <summary>
/// Keyboard focus when the window has no document open.
///
/// Alt+F4 goes to whichever window holds Win32 keyboard focus, and the close is that
/// window's default handling of the key. In the formatted view the focused window is
/// the editor's WebView2, a child window of another process. File ▸ Close collapsed
/// the WebView2 without taking focus off it, so Alt+F4 went to a window nobody could
/// see, and nothing closed. The close box and File ▸ Exit were unaffected: neither
/// travels by keyboard focus.
///
/// So the no-document placeholder takes focus as it appears. An element that takes
/// WPF keyboard focus takes Win32 focus for its window too (WPF's keyboard input
/// provider sets it), and that is the half Alt+F4 follows. From there the window's
/// own handling applies as it does anywhere else in it: Alt+F4, menu access keys, an
/// Alt tap, the Ctrl shortcuts.
/// </summary>
internal static class NoDocumentFocus
{
    /// <summary>
    /// Give <paramref name="placeholder"/> keyboard focus. True when it holds it
    /// afterwards; false when it cannot take it (collapsed, or not focusable), in which
    /// case focus stays where it was.
    /// </summary>
    public static bool Take(UIElement placeholder)
    {
        // Keyboard focus, which WPF acquires by setting Win32 focus on the element's
        // window. Asked of keyboard focus rather than IsFocused, because a collapsed
        // or unfocusable element can still be granted logical focus.
        placeholder.Focus();
        return placeholder.IsKeyboardFocused;
    }
}
