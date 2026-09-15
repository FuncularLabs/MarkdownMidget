using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Shapes;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>ToolBar styles only its own items; a ToolBarToggle in a 1px group box keeps an item's template and size (no window).</summary>
public class ToolBarToggleTests
{
    [Fact]
    public void AGroupedButtonKeepsTheToolbarTemplateAndSize()
    {
        Exception? error = null;
        var t = new Thread(() => { try {
            ToggleButton Sized(ToggleButton b) { b.MinWidth = 30; b.Height = 26; b.Padding = b.BorderThickness = new Thickness(0); b.Content = new Rectangle { Width = 21, Height = 21 }; return b; }
            var (item, grouped) = (Sized(new ToggleButton()), Sized(new ToolBarToggle()));
            var box = new Border { BorderThickness = new Thickness(1), Child = new StackPanel { Children = { grouped } } };
            var bar = new ToolBar { Items = { item, box } };
            bar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity)); bar.Arrange(new Rect(bar.DesiredSize));
            Assert.Same(item.Template ?? throw new InvalidOperationException("no toolbar template"), grouped.Template);
            Assert.Equal((30d, 26d, 30d, 26d, 32d, 28d), (item.ActualWidth, item.ActualHeight, grouped.ActualWidth, grouped.ActualHeight, box.ActualWidth, box.ActualHeight));
        } catch (Exception ex) { error = ex; } });
        t.SetApartmentState(ApartmentState.STA); t.Start();
        Assert.True(t.Join(TimeSpan.FromSeconds(30)), "timed out"); if (error is not null) throw error;
    }
}
