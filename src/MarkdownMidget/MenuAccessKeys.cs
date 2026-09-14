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
/// never sees an Alt+F coming from the editor. With no document open the splash holds
/// focus (<see cref="NoDocumentFocus"/>) and everything works; open one, the editor
/// takes focus, and Alt goes dead. The fix hands those keys to AccessKeyManager by
/// name and turns an Alt tap into menu mode, the two things WPF would have done itself.
///
/// The decisions are pure and live here; the window supplies the key event, the
/// modifier state and the "is this letter a menu access key" lookup.
/// </summary>
internal static class MenuAccessKeys
{
    /// <summary>What an Alt-related key-down from the editor should do.</summary>
    public enum Down
    {
        /// <summary>Not an Alt combination: not ours at all.</summary>
        Ignore,
        /// <summary>The Alt key itself went down cleanly: start a fresh press.</summary>
        ResetAlt,
        /// <summary>The press is spent — Alt+something that is not a menu key, or an
        /// Alt press that began with Ctrl/Shift/Win held (AltGr) — but the key still
        /// goes to the editor untouched.</summary>
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

    private const ModifierKeys NotAMenuGesture = ModifierKeys.Control | ModifierKeys.Shift | ModifierKeys.Windows;

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
        var altHeld = isSystem || (modifiers & ModifierKeys.Alt) != 0;

        if (IsAltKey(realKey))
        {
            // Alt going down with Ctrl, Shift or Win already held is not the start of
            // a menu gesture — AltGr on international layouts is exactly Ctrl+Alt, and
            // WPF's own KeyboardNavigation refuses to track such a press. Spend it, so
            // the release that follows can never read as an Alt tap.
            return (modifiers & NotAMenuGesture) != 0 ? Down.Used : Down.ResetAlt;
        }
        if (!altHeld) return Down.Ignore;                      // Alt not held: not ours

        // Ctrl+Alt+key is a character being typed (AltGr: €, ß, …) or one of the
        // editor's own Ctrl+Alt bindings, never a menu request. It reaches the editor,
        // and it spends the press.
        if ((modifiers & ModifierKeys.Control) != 0) return Down.Used;

        var key = AccessKeyFor(realKey);
        if (key is null || !isRegistered(key)) return Down.Used;
        accessKey = key;
        return Down.Invoke;
    }

    /// <summary>An Alt key-up with nothing used since the press is an Alt tap — the
    /// gesture that enters the menu. Alt+F then release is not a tap.</summary>
    public static bool IsAltTap(Key realKey, bool altUsedSincePress) =>
        IsAltKey(realKey) && !altUsedSincePress;

    /// <summary>True when a menu's SubmenuOpened is its OWN submenu opening, not a nested
    /// one's bubbling up. Never rebuild a submenu's items from its own opening: Right or
    /// Enter opens it and then focuses the first entry, and items cleared in between
    /// leave nothing to focus — so the next Right moves on to the next top-level menu.</summary>
    public static bool IsOwnSubmenuOpening(object sender, object? originalSource) =>
        ReferenceEquals(sender, originalSource);
}

/// <summary>
/// One Alt press followed from key-down to key-up, so the window can tell an Alt TAP
/// (enter the menu) from Alt+letter, AltGr typing, an Alt that began with another
/// modifier held — or an Alt key-up the editor never saw the key-down for. Pure
/// state over <see cref="MenuAccessKeys"/>; the window feeds it every key event that
/// reaches the editor, and tells it when the editor loses focus.
///
/// A tap needs BOTH halves to arrive here, like WPF's own KeyboardNavigation, which
/// enters menu mode only when the Alt key-up matches the last key-down it saw. A
/// stray key-up is real: dismiss a dialog with Alt+F4 or an Alt mnemonic and the
/// dialog closes on the key-DOWN, focus returns to the editor, and the Alt release
/// lands here a moment later. Without the down half it is not a tap.
/// </summary>
internal sealed class AltPressTracker
{
    /// <summary>The Alt key whose key-down began the press in progress, or
    /// <see cref="Key.None"/> when none is. A tap must end with THAT key's key-up:
    /// left-Alt down, right-Alt up is not a tap, as WPF's KeyboardNavigation compares
    /// the real key too.</summary>
    private Key _altDown = Key.None;

    /// <summary>Something happened under Alt since it went down — the press is spent
    /// and releasing Alt must not also read as a tap.</summary>
    private bool _used;

    private bool Pressed => _altDown != Key.None;

    public MenuAccessKeys.Down KeyDown(bool isSystem, Key realKey, ModifierKeys modifiers,
                                       Func<string, bool> isRegistered, out string? accessKey)
    {
        var decision = MenuAccessKeys.DecideKeyDown(isSystem, realKey, modifiers, isRegistered, out accessKey);
        if (MenuAccessKeys.IsAltKey(realKey))
        {
            // A repeated Alt key-down (typematic, or the other Alt key) while a press
            // is already in progress changes nothing: in particular it must not
            // un-spend it — Alt, X, Alt again, release is not a tap.
            if (Pressed) return decision;
            // The Alt key itself: a press begins. Clean (ResetAlt) or already spent
            // (Used — it came down under Ctrl/Shift/Win).
            _altDown = realKey;
            _used = decision == MenuAccessKeys.Down.Used;
            return decision;
        }
        if (decision is MenuAccessKeys.Down.Used or MenuAccessKeys.Down.Invoke) _used = true;
        return decision;
    }

    /// <summary>True when this key-up completes an Alt tap: the same Alt key that
    /// began a press here, released with nothing used in between. Any Alt key-up ends
    /// the press either way. A non-Alt key-up during a press spends it (a letter that
    /// was down before Alt and released under it), as native WPF does.</summary>
    public bool KeyUp(Key realKey)
    {
        if (!MenuAccessKeys.IsAltKey(realKey))
        {
            if (Pressed) _used = true;
            return false;
        }
        var tap = Pressed && realKey == _altDown && MenuAccessKeys.IsAltTap(realKey, _used);
        _altDown = Key.None;
        _used = false;
        return tap;
    }

    /// <summary>Forget any press in progress — focus left the editor, so the Alt
    /// key-up, if it ever arrives here, belongs to whatever had focus in between (an
    /// Alt+Tab excursion that began here, say). WPF's KeyboardNavigation likewise
    /// drops the key it was tracking when focus goes to null.</summary>
    public void Reset()
    {
        _altDown = Key.None;
        _used = false;
    }
}
