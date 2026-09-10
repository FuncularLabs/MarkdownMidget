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
    private readonly AltPressTracker _altPress = new();

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

        var decision = _altPress.KeyDown(
            isSystem: e.Key == Key.System,
            realKey: MenuAccessKeys.RealKey(e),
            modifiers: e.KeyboardDevice.Modifiers,
            isRegistered: k => AccessKeyManager.IsKeyRegistered(scope, k),
            out var accessKey);

        if (decision != MenuAccessKeys.Down.Invoke) return;

        // Swallow it so the browser does not also see Alt+F, then do what
        // AccessKeyManager would have done had the press reached it: open the menu
        // labelled with that letter, in keyboard mode. When the menu closes WPF puts
        // focus back on the window's focused element, which is the editor.
        //
        // WPF gates this on Alt being physically down, in Menu.OnAccessKeyPressed:
        // unless Keyboard.IsKeyDown(LeftAlt/RightAlt) — read live from the OS — the
        // Menu claims the access key's scope as itself, so a top-level item never
        // matches the window scope that IsKeyRegistered/ProcessKey are asked about.
        // That is why a bare F in a text box never opens File, and why this can never
        // open a menu from a synthetic or stale key event. Here the user is holding
        // Alt, so the letter resolves.
        e.Handled = true;
        AccessKeyManager.ProcessKey(scope, accessKey!, false);
    }

    private void Web_KeyUp(object sender, KeyEventArgs e)
    {
        if (!_altPress.KeyUp(MenuAccessKeys.RealKey(e))) return;

        // An Alt tap enters the menu, the way WPF does from any other control: focus
        // the first top-level item, which puts the menu in keyboard mode with that
        // item highlighted. Escape leaves it and focus returns to the editor. Native
        // WPF refuses while something holds mouse capture (a drag in progress); so
        // does this.
        if (Mouse.Captured is not null) return;
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
