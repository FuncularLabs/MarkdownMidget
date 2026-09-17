using Microsoft.Win32;

namespace MarkdownMidget;

/// <summary>
/// How far below its button a toolbar tooltip opens. MainWindow.xaml's toolbar button styles and the Style box open
/// each tooltip under its button rather than under the pointer, moved down by <see cref="VerticalOffset"/>. Windows
/// draws the pointer over the tooltip. A standard pointer gets no offset, so the tooltip sits just under the button. A
/// pointer made larger (Settings ▸ Accessibility ▸ Mouse pointer and touch ▸ Size) gets its whole size: the arrow's
/// tip is the top-left of its image and rests somewhere on the button, so the image ends no lower than the button's
/// bottom plus the size.
/// </summary>
public static class ToolbarToolTip
{
    /// <summary>A standard pointer, in pixels at 100% scale: the Size slider at 1. Each step adds 16.</summary>
    internal const int StandardPointerSize = 32;

    /// <summary>The largest pointer the Size slider sets, at 15.</summary>
    internal const int LargestPointerSize = 256;

    /// <summary>
    /// The offset for the pointer size set now, read from the registry each time it is asked for. The toolbar asks as
    /// its window is built, so a size changed while a window is open applies to the next window opened.
    /// </summary>
    public static double VerticalOffset =>
        OffsetFor(() => Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Cursors", "CursorBaseSize", null));

    /// <summary>
    /// The offset for the CursorBaseSize value <paramref name="readCursorBaseSize"/> returns: the pointer's size when it
    /// is larger than standard, in pixels at 100% scale, which are WPF's device-independent units; otherwise none. A
    /// value that is missing, not a DWORD or past the largest size, or a read that throws, counts as standard.
    /// </summary>
    internal static double OffsetFor(Func<object?> readCursorBaseSize)
    {
        try
        {
            return readCursorBaseSize() is int size && size > StandardPointerSize && size <= LargestPointerSize
                ? size
                : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }
}
