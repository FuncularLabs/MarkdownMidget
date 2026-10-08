using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using MarkdownMidget.Picker;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>The file list's rows (review of #12): which double-clicks open one, and a re-sort that
/// keeps the selected one. A ListView laid out like the picker's, in an <see cref="OffscreenWindow"/>.</summary>
[Collection("WpfSta")]
public class PickerRowsTests
{
    private static void OnList(Action<ListView, List<string>> body)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            Window? window = null;
            try
            {
                var (items, view) = (new List<string> { "b", "a", "c" }, new GridView());
                view.Columns.Add(new GridViewColumn { Header = "Name", Width = 120, DisplayMemberBinding = new System.Windows.Data.Binding() });
                var list = new ListView { View = view, ItemsSource = items };
                (window = OffscreenWindow.Create(list, 300, 200)).Show();
                list.UpdateLayout();
                body(list, items);
            }
            catch (Exception ex) { error = ex; } finally { window?.Close(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "the list harness timed out");
        if (error is not null) throw error;
    }

    private static T? Find<T>(DependencyObject root, Func<T, bool>? match = null) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if ((child is T hit && (match?.Invoke(hit) ?? true) ? hit : Find(child, match)) is { } found) return found;
        }
        return null;
    }

    [Fact]
    public void OnlyALeftDoubleClickOnARowOpensIt() => OnList((list, _) =>
    {
        var row = Find<TextBlock>((DependencyObject)list.ItemContainerGenerator.ContainerFromItem("a"))!;
        var header = Find<GridViewColumnHeader>(list, h => h.Column is not null)!;   // not the padding header
        list.SelectedItem = "c";   // the bug opened the selected row wherever the double-click landed
        Assert.Equal("a", PickerRows.DoubleClickedItem(list, MouseButton.Left, row));
        Assert.Null(PickerRows.DoubleClickedItem(list, MouseButton.Right, row));
        Assert.Null(PickerRows.DoubleClickedItem(list, MouseButton.Left, Find<TextBlock>(header)!));   // a header's label
        Assert.Null(PickerRows.DoubleClickedItem(list, MouseButton.Left, Find<Thumb>(header)!));       // its edge
        Assert.Null(PickerRows.DoubleClickedItem(list, MouseButton.Left, null));
    });

    [Fact]
    public void AReSortKeepsTheSelectedRow() => OnList((list, items) =>
    {
        list.SelectedItem = "a";
        PickerRows.Resort(list, items, (x, y) => string.CompareOrdinal(y, x));
        Assert.Equal(["c", "b", "a"], list.Items.Cast<string>());
        Assert.Equal((2, "a"), (list.SelectedIndex, list.SelectedItem));
    });
}
