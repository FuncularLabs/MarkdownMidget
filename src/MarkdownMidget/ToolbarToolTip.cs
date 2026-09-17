using System.Windows.Controls.Primitives;
using Microsoft.Win32;

namespace MarkdownMidget;

/// <summary>
/// Where a toolbar tooltip opens. MainWindow.xaml's toolbar button styles and the Style box take
/// <see cref="Placement"/> and <see cref="VerticalOffset"/> from here. WPF opens a tooltip just below the pointer, and
/// a standard pointer keeps exactly that: WPF's own values, with no offset. An enlarged pointer can still sit over that
/// tooltip's text, and Windows draws the pointer on top. So a pointer made larger (Settings ▸ Accessibility ▸ Mouse pointer
/// and touch ▸ Size) gets the tooltip under the button, down by the pointer's whole size: the arrow's tip is the
/// top-left of its image and rests somewhere on the button, so the image ends no lower than that.
/// </summary>
public static class ToolbarToolTip
{
    public static readonly PlacementMode Placement;

    public static readonly double VerticalOffset;

    // Read once, both together, when the toolbar first asks as the window builds it. Each window is its own process, so
    // a size changed while a window is open applies to the next window opened. Tests use Mapping, which never runs this.
    static ToolbarToolTip() => (Placement, VerticalOffset) =
        Mapping.For(() => Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Cursors", "CursorBaseSize", null));

    internal static class Mapping
    {
        /// <summary>A standard pointer, in pixels at 100% scale: the Size slider at 1. Each step adds 16.</summary>
        internal const int StandardPointerSize = 32;

        /// <summary>The largest pointer the Size slider sets, at 15.</summary>
        internal const int LargestPointerSize = 256;

        /// <summary>
        /// The placement for the CursorBaseSize value <paramref name="readCursorBaseSize"/> returns. Larger than
        /// standard, up to the largest size: under the button, down by the size, in pixels at 100% scale, which are
        /// WPF's device-independent units. Otherwise, and for a value that is missing, not a DWORD or past the largest
        /// size, or a read that throws: <see cref="PlacementMode.Mouse"/> and 0, the values WPF uses when nothing sets
        /// them.
        /// </summary>
        internal static (PlacementMode Placement, double VerticalOffset) For(Func<object?> readCursorBaseSize)
        {
            object? value;
            try
            {
                value = readCursorBaseSize();
            }
            catch (Exception)
            {
                value = null;
            }
            return value is int size && size > StandardPointerSize && size <= LargestPointerSize
                ? (PlacementMode.Bottom, size)
                : (PlacementMode.Mouse, 0);
        }
    }
}
