using System.Windows;
using System.Windows.Controls;
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
