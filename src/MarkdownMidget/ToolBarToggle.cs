using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace MarkdownMidget;

/// <summary>
/// A toggle button with a toolbar item's look when it is not one of the toolbar's items, such as a button inside a
/// ToggleGroupBorder. ToolBar gives its own ToggleButton items that look by setting DefaultStyleKey, under any explicit
/// Style; a button one level down would look like a plain ToggleButton. This sets the same key.
/// </summary>
public class ToolBarToggle : ToggleButton
{
    public ToolBarToggle() => DefaultStyleKey = ToolBar.ToggleButtonStyleKey;
}
