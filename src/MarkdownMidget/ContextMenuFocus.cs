using System.Windows.Controls;

namespace MarkdownMidget;

/// <summary>
/// Keyboard-focus selection for context menus opened over the WebView2 surface.
/// </summary>
internal static class ContextMenuFocus
{
    /// <summary>
    /// The first item the user can actually activate, or null if the menu has none.
    ///
    /// Blindly focusing item 0 breaks whenever a menu opens with a DISABLED entry
    /// first — the spell menu's "(no suggestions)" placeholder, shown for a
    /// misspelling the engine has no correction for. A disabled item can't take
    /// focus, so focus fell through to the ContextMenu itself and everything below
    /// (Add to Dictionary, Ignore All) became unreachable by keyboard.
    /// </summary>
    public static MenuItem? FirstActivatableItem(ContextMenu menu)
    {
        for (var i = 0; i < menu.Items.Count; i++)
        {
            // Enabled is not sufficient: a COLLAPSED item is still "enabled" and still
            // can't take focus. The table menu leads with a collapsed Spelling
            // placeholder, which would otherwise strand focus exactly the way a
            // disabled first item does.
            if (menu.ItemContainerGenerator.ContainerFromIndex(i) is MenuItem { IsEnabled: true, IsVisible: true } item)
                return item;
        }
        return null;
    }
}
