using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace MarkdownMidget.Picker;

/// <summary>The file list's row handling, out of the window so it can be tested on a real ListView.</summary>
internal static class PickerRows
{
    /// <summary>The item a double-click opens: only a left double-click on a row. WPF raises the
    /// list's MouseDoubleClick for its headers, a header's edge and its scroll bars as well, and
    /// opening the selected row from one of those acts on a row nobody pointed at.</summary>
    public static object? DoubleClickedItem(ItemsControl list, MouseButton button, object? source) =>
        button == MouseButton.Left && source is DependencyObject element
            && ItemsControl.ContainerFromElement(list, element) is ListViewItem row ? row.Content : null;

    /// <summary>Calls <paramref name="resized"/> when a column header's edge is dropped after a drag.
    /// The header marks that event handled, so it is heard with handledEventsToo; a click on the edge
    /// (no change), Esc during the drag, and the list's scroll-bar thumbs don't count.</summary>
    public static void OnColumnResized(ListView list, Action resized) =>
        list.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, e) =>
        {
            if (!e.Canceled && e.HorizontalChange != 0 && e.OriginalSource is Thumb { TemplatedParent: GridViewColumnHeader }) resized();
        }), handledEventsToo: true);

    /// <summary>Re-sorts the list the view shows, in place. A refresh keeps the selected row selected
    /// (rebinding would not) but makes new rows, so the keyboard, if it was in the list, goes back to
    /// the selected one, and the arrow keys carry on from there.</summary>
    public static void Resort<T>(ListView list, List<T> items, Comparison<T> order)
    {
        var (selected, hadKeyboard) = (list.SelectedItem, list.IsKeyboardFocusWithin);
        items.Sort(order);
        list.Items.Refresh();
        if (selected is null) return;
        list.ScrollIntoView(selected);
        list.UpdateLayout();
        if (hadKeyboard && list.ItemContainerGenerator.ContainerFromItem(selected) is ListViewItem row) row.Focus();
    }
}
