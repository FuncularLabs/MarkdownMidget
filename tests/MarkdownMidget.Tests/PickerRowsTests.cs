using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
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

    /// <summary>A ListView measured and arranged with no window, as the review did: the header row
    /// exists. <c>body</c> gets the list, its header's edge, and a count of the changes it reports.</summary>
    private static void OnMeasuredList(Action<ListView, Thumb, Func<int>> body)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var view = new GridView();
                view.Columns.Add(new GridViewColumn { Header = "Name", Width = 120, DisplayMemberBinding = new System.Windows.Data.Binding() });
                var list = new ListView { View = view, ItemsSource = Enumerable.Range(0, 50).ToList() };
                ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Visible);
                list.Measure(new Size(300, 100));
                list.Arrange(new Rect(0, 0, 300, 100));
                list.UpdateLayout();
                var resized = 0;
                PickerRows.OnColumnResized(list, () => resized++);
                body(list, Find<Thumb>(Find<GridViewColumnHeader>(list, h => h.Column is not null)!)!, () => resized);
            }
            catch (Exception ex) { error = ex; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "the list harness timed out");
        if (error is not null) throw error;
    }

    [Fact]
    public void AColumnEdgeDroppedAfterADragIsAChangeAndAClickOrAnEscapeIsNot() => OnMeasuredList((list, edge, resized) =>
    {
        void Drop(DependencyObject thumb, double change, bool canceled) =>
            ((UIElement)thumb).RaiseEvent(new DragCompletedEventArgs(change, 0, canceled) { RoutedEvent = Thumb.DragCompletedEvent });
        Drop(edge, 30, canceled: false);   // the header marks this handled: only handledEventsToo hears it
        Drop(edge, 0, canceled: false);    // a click on the edge, no drag
        Drop(edge, 30, canceled: true);    // Esc during the drag
        Drop(Find<Thumb>(list, t => t.TemplatedParent is ScrollBar)!, 30, canceled: false);   // the list's own scroll bar
        Assert.Equal(1, resized());
    });

    [Fact]
    public void ADoubleClickOnAHeaderEdgeIsAChangeOnceLayoutHasFittedTheColumn() => OnMeasuredList((list, edge, resized) =>
    {
        var column = ((GridView)list.View).Columns[0];
        // WPF's own fit: the header handles its edge's double-click by setting the width to auto.
        edge.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Control.MouseDoubleClickEvent });
        Assert.True(double.IsNaN(column.Width));
        Assert.Equal(0, resized());   // not yet: the width is layout's to give
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        Assert.Equal(1, resized());
        column.Width = 200;           // a width set in code, as a remembered view is shown, is not a change
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        Assert.Equal(1, resized());
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
