using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MarkdownMidget;

/// <summary>
/// Alt menu mnemonics while the WebView2 editor has focus. The why is on
/// <see cref="MenuAccessKeys"/>; this is the glue.
/// </summary>
public partial class MainWindow
{
    /// <summary>Set when a key was pressed under Alt since Alt went down, so releasing
    /// Alt after Alt+F does not ALSO read as an Alt tap and re-enter the menu.</summary>
    private bool _altUsedSincePress;

    private void InitAccessKeys()
    {
        // The bubbling events, not the Preview ones: bubbling KeyDown from the WebView2
        // is the channel the window's Ctrl+ command bindings already prove works.
        // These handlers only ever see events that ORIGINATE at the editor — a key
        // pressed with the source view or a dialog focused bubbles up a different
        // branch and is handled by WPF natively, so nothing is processed twice.
        Web.KeyDown += Web_KeyDown;
        Web.KeyUp += Web_KeyUp;
    }

    private void Web_KeyDown(object sender, KeyEventArgs e)
    {
        var scope = PresentationSource.FromVisual(this);
        if (scope is null) return;

        var decision = MenuAccessKeys.DecideKeyDown(
            isSystem: e.Key == Key.System,
            realKey: MenuAccessKeys.RealKey(e),
            modifiers: e.KeyboardDevice.Modifiers,
            isRegistered: k => AccessKeyManager.IsKeyRegistered(scope, k),
            out var accessKey);

        switch (decision)
        {
            case MenuAccessKeys.Down.ResetAlt:
                _altUsedSincePress = false;
                break;
            case MenuAccessKeys.Down.Used:
                _altUsedSincePress = true;
                break;
            case MenuAccessKeys.Down.Invoke:
                _altUsedSincePress = true;
                // Swallow it so the browser does not also see Alt+F, then do what
                // AccessKeyManager would have done had the press reached it: open the
                // menu labelled with that letter. The menu takes focus; when it closes
                // WPF hands focus back to the editor, as it does for any control.
                //
                // Both AccessKeyManager calls are gated by WPF itself: GetTargetsForScope
                // yields no top-level targets unless the Alt key is physically held
                // (Keyboard.Modifiers, read live from the OS). Here it is — the user is
                // holding it — so the letter resolves; and it is why this can never
                // open a menu on a synthetic or stale key event.
                e.Handled = true;
                AccessKeyManager.ProcessKey(scope, accessKey!, false);
                break;
        }
    }

    private void Web_KeyUp(object sender, KeyEventArgs e)
    {
        var real = MenuAccessKeys.RealKey(e);
        if (!MenuAccessKeys.IsAltKey(real)) return;
        var tap = MenuAccessKeys.IsAltTap(real, _altUsedSincePress);
        _altUsedSincePress = false;
        if (!tap) return;

        // An Alt tap enters the menu, the way WPF does from any other control: focus
        // the first top-level item, which puts the menu in keyboard mode with that
        // item highlighted. Escape leaves it and focus returns to the editor.
        e.Handled = true;
        foreach (var item in MainMenu.Items)
        {
            if (item is MenuItem { IsEnabled: true, Focusable: true } first)
            {
                first.Focus();
                break;
            }
        }
    }
}
