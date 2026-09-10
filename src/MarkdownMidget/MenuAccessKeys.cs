using System;
using System.Windows.Input;

namespace MarkdownMidget;

/// <summary>
/// Menu mnemonics — Alt+F, and an Alt tap — while the WebView2 editor has keyboard
/// focus.
///
/// The WPF WebView2 control re-raises accelerator keys as WPF key events on itself,
/// which is why the window's Ctrl+O / Ctrl+S command bindings work with the editor
/// focused. But it raises them straight on the element, bypassing InputManager, and
/// WPF's menu mnemonics live in AccessKeyManager's InputManager hook — which therefore
/// never sees an Alt+F coming from the editor. Before a document is open the splash
/// has focus and everything works; open one, the editor takes focus, and Alt goes
/// dead. The fix hands those keys to AccessKeyManager by name and turns an Alt tap
/// into menu mode, the two things WPF would have done itself.
///
/// The decisions are pure and live here; the window supplies the key event, the
/// modifier state and the "is this letter a menu access key" lookup.
/// </summary>
internal static class MenuAccessKeys
{
    /// <summary>What an Alt-related key-down from the editor should do.</summary>
    public enum Down
    {
        /// <summary>Not an Alt combination, or one that must reach the editor (AltGr).</summary>
        Ignore,
        /// <summary>The Alt key itself went down: start a fresh press.</summary>
        ResetAlt,
        /// <summary>Alt+something that is not a menu key: the press is spent, but the
        /// key still goes to the editor.</summary>
        Used,
        /// <summary>Alt+letter that names a menu: invoke it and swallow the key.</summary>
        Invoke,
    }

    /// <summary>The access-key character an Alt+key names, or null when no menu can
    /// be labelled with that key — only letters and digits are.</summary>
    public static string? AccessKeyFor(Key key) => key switch
    {
        >= Key.A and <= Key.Z => ((char)('A' + (key - Key.A))).ToString(),
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        _ => null,
    };

    /// <summary>The key actually pressed: WPF reports an Alt combination as
    /// <see cref="Key.System"/> with the real key in <c>SystemKey</c>.</summary>
    public static Key RealKey(KeyEventArgs e) => e.Key == Key.System ? e.SystemKey : e.Key;

    public static bool IsAltKey(Key key) => key is Key.LeftAlt or Key.RightAlt;

    /// <param name="isSystem">True when WPF reported the event as <see cref="Key.System"/>.
    /// Only WPF's own input path marks system keys; the WebView2 control re-raises
    /// Alt+F as a plain <see cref="Key.F"/> with the Alt modifier set, so Alt being
    /// held is read from EITHER signal.</param>
    /// <param name="realKey">The key under Alt (see <see cref="RealKey"/>).</param>
    /// <param name="modifiers">The keyboard's modifier state at the time.</param>
    /// <param name="isRegistered">Whether a menu in the window is labelled with the key.</param>
    /// <param name="accessKey">The key to invoke, when the answer is <see cref="Down.Invoke"/>.</param>
    public static Down DecideKeyDown(bool isSystem, Key realKey, ModifierKeys modifiers,
                                     Func<string, bool> isRegistered, out string? accessKey)
    {
        accessKey = null;
        if (IsAltKey(realKey)) return Down.ResetAlt;           // the Alt key itself
        var altHeld = isSystem || (modifiers & ModifierKeys.Alt) != 0;
        if (!altHeld) return Down.Ignore;                     // Alt not held: not ours
        // AltGr on international layouts arrives as Ctrl+Alt: that is a character
        // being typed (€, ß, …), never a menu request. It must reach the editor.
        if ((modifiers & ModifierKeys.Control) != 0) return Down.Ignore;

        var key = AccessKeyFor(realKey);
        if (key is null || !isRegistered(key)) return Down.Used;
        accessKey = key;
        return Down.Invoke;
    }

    /// <summary>An Alt key-up with nothing used since the press is an Alt tap — the
    /// gesture that enters the menu. Alt+F then release is not a tap.</summary>
    public static bool IsAltTap(Key realKey, bool altUsedSincePress) =>
        IsAltKey(realKey) && !altUsedSincePress;
}
