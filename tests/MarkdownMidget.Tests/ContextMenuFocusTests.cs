using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MarkdownMidget;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>
/// Regression cover for the spell menu's keyboard reachability. The engine flags
/// plenty of words it has no correction for (keyboard mashes, coined terms), and
/// those menus open with a disabled "(no suggestions)" placeholder first — which
/// used to strand keyboard focus on the ContextMenu and put Add to Dictionary out
/// of reach. WPF menus need a real window + STA thread, so each case runs one.
/// </summary>
public class ContextMenuFocusTests
{
    private static T OnStaWindow<T>(Func<ContextMenu, T> build, Action<ContextMenu> fill)
    {
        var result = default(T)!;
        Exception? error = null;
        var done = new ManualResetEventSlim();
        var t = new Thread(() =>
        {
            Window? win = null;
            ContextMenu? menu = null;
            try
            {
                win = new Window { Width = 200, Height = 150, Left = -10000, Top = -10000, ShowInTaskbar = false };
                var host = new TextBox();
                win.Content = host;
                win.Show();

                menu = new ContextMenu { PlacementTarget = host };
                fill(menu);
                menu.IsOpen = true;   // realize the item containers
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);

                result = build(menu);
            }
            catch (Exception ex) { error = ex; }
            finally
            {
                // Tear down even when the body threw, or the dispatcher thread keeps
                // a window alive forever.
                try { if (menu is not null) menu.IsOpen = false; } catch { /* best effort */ }
                try { win?.Close(); } catch { /* best effort */ }
                done.Set();
            }
        })
        {
            // Never let a wedged UI thread outlive the test run: a foreground thread
            // would hang `dotnet test` until CI's job timeout instead of failing.
            IsBackground = true,
        };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        // A timeout must FAIL, not quietly hand back default(T) — otherwise a test
        // asserting null would pass because nothing ever ran.
        Assert.True(done.Wait(TimeSpan.FromSeconds(30)),
            "STA menu harness timed out — the window/menu never finished setting up.");
        if (error is not null) throw error;
        return result;
    }

    [Fact]
    public void NoSuggestions_SkipsDisabledPlaceholder_AndReachesAddToDictionary()
    {
        var header = OnStaWindow(
            menu => ContextMenuFocus.FirstActivatableItem(menu)?.Header?.ToString(),
            menu =>
            {
                menu.Items.Add(new MenuItem { Header = "(no suggestions)", IsEnabled = false });
                menu.Items.Add(new Separator());
                menu.Items.Add(new MenuItem { Header = "Add to Dictionary" });
                menu.Items.Add(new MenuItem { Header = "Ignore All" });
            });
        Assert.Equal("Add to Dictionary", header);
    }

    [Fact]
    public void NoSuggestions_TheChosenItemCanActuallyTakeFocus()
    {
        var focused = OnStaWindow(
            menu => ContextMenuFocus.FirstActivatableItem(menu)?.Focus() ?? false,
            menu =>
            {
                menu.Items.Add(new MenuItem { Header = "(no suggestions)", IsEnabled = false });
                menu.Items.Add(new Separator());
                menu.Items.Add(new MenuItem { Header = "Add to Dictionary" });
            });
        Assert.True(focused, "the picked item must be focusable — that's the whole point");
    }

    [Fact]
    public void WithSuggestions_StillPicksTheFirstSuggestion()
    {
        var header = OnStaWindow(
            menu => ContextMenuFocus.FirstActivatableItem(menu)?.Header?.ToString(),
            menu =>
            {
                menu.Items.Add(new MenuItem { Header = "misspelled" });
                menu.Items.Add(new MenuItem { Header = "mi spelled" });
                menu.Items.Add(new Separator());
                menu.Items.Add(new MenuItem { Header = "Add to Dictionary" });
            });
        Assert.Equal("misspelled", header);
    }

    /// A COLLAPSED item is still "enabled" but still cannot take focus — the table
    /// menu leads with a collapsed Spelling placeholder, so skipping only disabled
    /// items would strand focus exactly the way the original bug did.
    [Fact]
    public void CollapsedFirstItem_IsSkipped()
    {
        var header = OnStaWindow(
            menu => ContextMenuFocus.FirstActivatableItem(menu)?.Header?.ToString(),
            menu =>
            {
                menu.Items.Add(new MenuItem { Header = "Spelling", Visibility = Visibility.Collapsed });
                menu.Items.Add(new Separator { Visibility = Visibility.Collapsed });
                menu.Items.Add(new MenuItem { Header = "Insert" });
            });
        Assert.Equal("Insert", header);
    }

    [Fact]
    public void CollapsedFirstItem_TheChosenItemCanTakeFocus()
    {
        var focused = OnStaWindow(
            menu => ContextMenuFocus.FirstActivatableItem(menu)?.Focus() ?? false,
            menu =>
            {
                menu.Items.Add(new MenuItem { Header = "Spelling", Visibility = Visibility.Collapsed });
                menu.Items.Add(new MenuItem { Header = "Insert" });
            });
        Assert.True(focused, "a collapsed placeholder must not be chosen — it cannot take focus");
    }

    [Fact]
    public void SeparatorsAndAllDisabled_YieldNoItem()
    {
        var item = OnStaWindow(
            menu => ContextMenuFocus.FirstActivatableItem(menu),
            menu =>
            {
                menu.Items.Add(new Separator());
                menu.Items.Add(new MenuItem { Header = "(no suggestions)", IsEnabled = false });
            });
        Assert.Null(item);   // caller falls back to MoveFocus
    }
}
