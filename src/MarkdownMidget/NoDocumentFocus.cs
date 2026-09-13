using System.Windows;

namespace MarkdownMidget;

/// <summary>
/// Keyboard focus into and out of the window's no-document state.
///
/// Alt+F4 goes to whichever window holds Win32 keyboard focus, and the close is that
/// window's default handling of the key. In the formatted view the focused window is
/// the editor's WebView2, a child window of another process. File ▸ Close collapsed
/// the WebView2 without taking focus off it, so Alt+F4 went to a window nobody could
/// see, and nothing closed. The close box and File ▸ Exit were unaffected: neither
/// travels by keyboard focus.
///
/// So the no-document placeholder takes focus as it appears (<see cref="Take"/>). An
/// element that takes WPF keyboard focus takes Win32 focus for its window too (WPF's
/// keyboard input provider sets it), and that is the half Alt+F4 follows. From there
/// the window's own handling applies as it does anywhere else in it: Alt+F4, menu
/// access keys, an Alt tap, the Ctrl shortcuts.
///
/// And it gives focus back as a document arrives (<see cref="HandBack"/>): collapsing
/// the placeholder moves WPF focus off it, but not into the document, and not every
/// way out of the state goes on to focus the document itself.
/// </summary>
internal static class NoDocumentFocus
{
    /// <summary>
    /// Give <paramref name="placeholder"/> keyboard focus. True when it holds keyboard
    /// focus afterwards. False when it cannot take it (collapsed, or not focusable):
    /// keyboard focus then stays where it was, though a collapsed placeholder that is
    /// focusable and enabled can still become the logical focus of a focus scope that
    /// has none.
    /// </summary>
    public static bool Take(UIElement placeholder)
    {
        // Focus() asks for keyboard focus, and so sets Win32 focus on the element's
        // window. Logical focus is not enough: when the placeholder already has it,
        // asking for it again changes nothing and leaves Win32 focus in the child
        // window. The answer is read from keyboard focus, not IsFocused, because
        // Focus() falls back to logical focus for an element that is focusable and
        // enabled but cannot take keyboard focus.
        placeholder.Focus();
        return placeholder.IsKeyboardFocused;
    }

    /// <summary>
    /// Leaving the state: when <paramref name="placeholder"/> holds keyboard focus, hand
    /// it to <paramref name="view"/>, the surface now showing the document. True when it
    /// was handed on, whatever the view then does with it (the WebView2 passes focus
    /// into its own window, which WPF does not count as keyboard focus). False, with
    /// nothing moved, when the placeholder does not hold it: a dialog, the menu or the
    /// document has focus and keeps it.
    ///
    /// Ask after the placeholder is collapsed and the view is visible, before the
    /// dispatcher runs: WPF moves focus off a collapsed element later, not at once, and
    /// a collapsed view takes no focus.
    /// </summary>
    public static bool HandBack(UIElement placeholder, UIElement view)
    {
        if (!placeholder.IsKeyboardFocusWithin) return false;
        view.Focus();
        return true;
    }
}
