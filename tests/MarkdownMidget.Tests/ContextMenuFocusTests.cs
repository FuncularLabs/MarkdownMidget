using System;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using MarkdownMidget;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>
/// Regression cover for the spell menu's keyboard reachability. The engine flags
/// plenty of words it has no correction for (keyboard mashes, coined terms), and
/// those menus open with a disabled "(no suggestions)" placeholder first — which
/// used to strand keyboard focus on the ContextMenu and put Add to Dictionary out
/// of reach. WPF menus need a real window + STA thread, so each case runs one, and
/// opens the menu with its popup kept off every monitor
/// (<see cref="OffscreenWindow.KeepPopupOffscreen"/>): a ContextMenu opens at the
/// mouse pointer, on the screen of whoever is using the desktop.
/// </summary>
[Collection("WpfSta")]
public class ContextMenuFocusTests
{
    /// <summary>
    /// How long to wait for the menu to finish setting itself up. Generous, because
    /// it is only ever reached on a machine under load, and it costs nothing on one
    /// that isn't: the wait returns the moment the menu is ready.
    /// </summary>
    private static readonly TimeSpan ReadyBudget = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Pump the dispatcher until <paramref name="ready"/> or the budget runs out.
    ///
    /// This waits for a PRECONDITION of the test, never for the assertion — nothing
    /// here re-tries <c>Focus()</c> and nothing here can turn a real failure into a
    /// pass. See <see cref="OnStaWindow"/> for why the wait is needed at all.
    /// </summary>
    private static bool PumpUntil(Func<bool> ready)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < ReadyBudget)
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
            if (ready()) return true;
            Thread.Sleep(5);
        }
        return ready();
    }

    /// <param name="needsKeyboardFocus">
    /// True for the cases that assert on focus. Opening a menu realizes its item
    /// containers and hands the popup keyboard focus, and BOTH happen asynchronously.
    /// One Background-priority pump is enough on an idle machine and measurably not
    /// enough on a busy one: 6 of 40 cold starts under CPU contention reached the
    /// assertion with the chosen item loaded, visible, enabled and attached to a
    /// PresentationSource, but with <c>Keyboard.FocusedElement</c> still null — no
    /// element anywhere had keyboard focus yet, so <c>Focus()</c> returned false and
    /// the test read that as "this item cannot be focused".
    ///
    /// So the wait is for SOMETHING to hold keyboard focus, which is deliberately
    /// weaker than "the item holds it" — in practice the holder is the ContextMenu,
    /// and requiring the item itself would be waiting for the assertion's own answer.
    /// It removes the race without touching what is asserted.
    ///
    /// The popup gets keyboard focus only while its thread holds Win32 focus, which a
    /// plain window gets by being shown, activated. So only these cases show a plain
    /// window, and they are <see cref="TakesFocusFactAttribute"/> cases; the others
    /// use a window that is never activated (<see cref="OffscreenWindow"/>).
    /// </param>
    private static T OnStaWindow<T>(Func<ContextMenu, T> build, Action<ContextMenu> fill,
                                    bool needsKeyboardFocus = false)
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
                var host = new TextBox();
                win = needsKeyboardFocus
                    ? new Window { Width = 200, Height = 150, Left = -10000, Top = -10000, ShowInTaskbar = false, Content = host }
                    : OffscreenWindow.Create(host, 200, 150);
                win.Show();

                menu = new ContextMenu { PlacementTarget = host };
                fill(menu);
                OffscreenWindow.KeepPopupOffscreen(menu);
                menu.IsOpen = true;   // realize the item containers

                ContextMenu opened = menu;   // the closures below must not see a nullable local
                if (!PumpUntil(() => opened.ItemContainerGenerator.Status == GeneratorStatus.ContainersGenerated
                                     && (!needsKeyboardFocus || Keyboard.FocusedElement is not null)))
                {
                    // Say which half was missing. A container-generation timeout is a
                    // broken menu; no keyboard focus at all is the machine refusing to
                    // give the popup focus, which is not a claim about the code under
                    // test and must not be reported as one.
                    throw new InvalidOperationException(
                        opened.ItemContainerGenerator.Status != GeneratorStatus.ContainersGenerated
                            ? $"the menu never generated its item containers within {ReadyBudget.TotalSeconds:0}s."
                            : $"the menu never received keyboard focus within {ReadyBudget.TotalSeconds:0}s, so the "
                              + "focus assertion could not run. This is the environment, not the code under test.");
                }

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

    /// <summary>
    /// What happened when the chosen item was asked to take focus, captured as data
    /// so a failure can say WHICH way it went wrong. Focus landing back on the
    /// ContextMenu is the original bug; landing nowhere is a different problem and
    /// deserves a different message.
    /// </summary>
    /// <param name="Landed">Where focus ended up, or null when no item was chosen at all.</param>
    private sealed record FocusOutcome(bool Accepted, bool ItemHasKeyboardFocus, string? Landed);

    private static FocusOutcome TryFocusFirstActivatable(ContextMenu menu)
    {
        var item = ContextMenuFocus.FirstActivatableItem(menu);
        if (item is null) return new FocusOutcome(false, false, null);
        var accepted = item.Focus();
        return new FocusOutcome(accepted, item.IsKeyboardFocused,
            Keyboard.FocusedElement?.GetType().Name ?? "nothing");
    }

    /// <summary>
    /// Keyboard focus, deliberately, and not the logical kind. A COLLAPSED item is
    /// granted LOGICAL focus by WPF — measured — so asserting on
    /// <c>FocusManager.GetFocusedElement</c> or <c>IsFocused</c> would quietly stop
    /// catching the collapsed-placeholder regression that
    /// <see cref="CollapsedFirstItem_TheChosenItemCanTakeFocus"/> exists for.
    /// </summary>
    private static void AssertTookFocus(FocusOutcome outcome) =>
        Assert.True(outcome.Accepted && outcome.ItemHasKeyboardFocus,
            outcome.Landed is null
                ? "no item was chosen at all, so nothing could take focus"
                : $"the picked item must take keyboard focus — it went to {outcome.Landed} instead");

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

    [TakesFocusFact]
    public void NoSuggestions_TheChosenItemCanActuallyTakeFocus()
    {
        var outcome = OnStaWindow(
            TryFocusFirstActivatable,
            menu =>
            {
                menu.Items.Add(new MenuItem { Header = "(no suggestions)", IsEnabled = false });
                menu.Items.Add(new Separator());
                menu.Items.Add(new MenuItem { Header = "Add to Dictionary" });
            },
            needsKeyboardFocus: true);
        AssertTookFocus(outcome);
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

    [TakesFocusFact]
    public void CollapsedFirstItem_TheChosenItemCanTakeFocus()
    {
        var outcome = OnStaWindow(
            TryFocusFirstActivatable,
            menu =>
            {
                menu.Items.Add(new MenuItem { Header = "Spelling", Visibility = Visibility.Collapsed });
                menu.Items.Add(new MenuItem { Header = "Insert" });
            },
            needsKeyboardFocus: true);
        AssertTookFocus(outcome);
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
