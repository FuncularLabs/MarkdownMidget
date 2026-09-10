using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MarkdownMidget;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>
/// Alt mnemonics while the editor has focus. The decision logic is pure and covered
/// exhaustively; the two WPF calls the window then makes — hand a letter to
/// AccessKeyManager, and focus the first menu item for an Alt tap — are proved
/// against a real window with a real menu, since that is where "it opened the File
/// menu" is actually decided.
/// </summary>
public class MenuAccessKeysTests
{
    // ===== the pure decisions =====

    [Theory]
    [InlineData(Key.A, "A")]
    [InlineData(Key.F, "F")]
    [InlineData(Key.Z, "Z")]
    [InlineData(Key.D0, "0")]
    [InlineData(Key.D9, "9")]
    public void LettersAndDigitsNameAnAccessKey(Key key, string expected)
        => Assert.Equal(expected, MenuAccessKeys.AccessKeyFor(key));

    [Theory]
    [InlineData(Key.F1)]
    [InlineData(Key.LeftAlt)]
    [InlineData(Key.OemPlus)]
    [InlineData(Key.Space)]
    [InlineData(Key.Tab)]
    public void OtherKeysNameNone(Key key)
        => Assert.Null(MenuAccessKeys.AccessKeyFor(key));

    private static bool Registered(string k) => k is "F" or "E" or "V";

    [Fact]
    public void WithoutAltNothingIsOurs()
    {
        var d = MenuAccessKeys.DecideKeyDown(isSystem: false, Key.F, ModifierKeys.None, Registered, out var key);
        Assert.Equal(MenuAccessKeys.Down.Ignore, d);
        Assert.Null(key);
    }

    [Fact]
    public void TheAltKeyItselfStartsAFreshPress()
        => Assert.Equal(MenuAccessKeys.Down.ResetAlt,
            MenuAccessKeys.DecideKeyDown(true, Key.LeftAlt, ModifierKeys.Alt, Registered, out _));

    [Fact]
    public void AltPlusARegisteredLetterInvokesIt()
    {
        var d = MenuAccessKeys.DecideKeyDown(true, Key.F, ModifierKeys.Alt, Registered, out var key);
        Assert.Equal(MenuAccessKeys.Down.Invoke, d);
        Assert.Equal("F", key);
    }

    [Fact]
    public void TheWebViewsShapeIsAPlainKeyWithTheAltModifier()
    {
        // The WebView2 control cannot mark a key as Key.System (WPF-internal), so its
        // Alt+F arrives as Key.F with Alt in the modifiers and isSystem FALSE. That is
        // the shape this whole fix exists for; it must invoke.
        var d = MenuAccessKeys.DecideKeyDown(isSystem: false, Key.F, ModifierKeys.Alt, Registered, out var key);
        Assert.Equal(MenuAccessKeys.Down.Invoke, d);
        Assert.Equal("F", key);
    }

    [Fact]
    public void TheAltKeyArrivingAsAPlainKeyStillStartsAFreshPress()
        => Assert.Equal(MenuAccessKeys.Down.ResetAlt,
            MenuAccessKeys.DecideKeyDown(isSystem: false, Key.LeftAlt, ModifierKeys.Alt, Registered, out _));

    [Fact]
    public void AltPlusAnUnregisteredKeyIsSpentButNotSwallowed()
    {
        // Alt+X with no _X menu: the press counts as used (so releasing Alt is not a
        // tap), but the key is not ours to swallow.
        var d = MenuAccessKeys.DecideKeyDown(true, Key.X, ModifierKeys.Alt, Registered, out var key);
        Assert.Equal(MenuAccessKeys.Down.Used, d);
        Assert.Null(key);
    }

    [Fact]
    public void AltPlusAFunctionKeyIsSpentToo()
        => Assert.Equal(MenuAccessKeys.Down.Used,
            MenuAccessKeys.DecideKeyDown(true, Key.F4, ModifierKeys.Alt, Registered, out _));

    [Fact]
    public void AltGrIsTypingNotAMenuRequest()
    {
        // AltGr+E on a European layout arrives as Ctrl+Alt+E. "E" IS a registered
        // access key (_Edit), and it must still reach the editor as a character.
        var d = MenuAccessKeys.DecideKeyDown(true, Key.E, ModifierKeys.Control | ModifierKeys.Alt, Registered, out var key);
        Assert.Equal(MenuAccessKeys.Down.Ignore, d);
        Assert.Null(key);
    }

    [Theory]
    [InlineData(Key.LeftAlt, false, true)]    // Alt down, Alt up, nothing between: a tap
    [InlineData(Key.RightAlt, false, true)]
    [InlineData(Key.LeftAlt, true, false)]    // Alt+F then release: not a tap
    [InlineData(Key.F, false, false)]         // releasing a letter is never a tap
    public void AnAltTapIsAltUpWithNothingUsed(Key realKey, bool used, bool expected)
        => Assert.Equal(expected, MenuAccessKeys.IsAltTap(realKey, used));

    // ===== the WPF side, against a real menu =====

    private static T OnMenuWindow<T>(Func<Window, Menu, T> body)
    {
        var result = default(T)!;
        Exception? error = null;
        var done = new ManualResetEventSlim();
        var t = new Thread(() =>
        {
            Window? win = null;
            try
            {
                var menu = new Menu();
                var file = new MenuItem { Header = "_File" };
                file.Items.Add(new MenuItem { Header = "_Open" });
                var edit = new MenuItem { Header = "_Edit" };
                edit.Items.Add(new MenuItem { Header = "_Undo" });
                menu.Items.Add(file);
                menu.Items.Add(edit);

                var panel = new DockPanel();
                DockPanel.SetDock(menu, Dock.Top);
                panel.Children.Add(menu);
                var host = new TextBox();
                panel.Children.Add(host);

                win = new Window { Width = 300, Height = 200, Left = -10000, Top = -10000, ShowInTaskbar = false, Content = panel };
                win.Show();
                host.Focus();
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
                result = body(win, menu);
            }
            catch (Exception ex) { error = ex; }
            finally { try { win?.Close(); } catch { } done.Set(); }
        }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        Assert.True(done.Wait(TimeSpan.FromSeconds(30)), "menu window harness timed out");
        if (error is not null) throw error;
        return result;
    }

    /// <summary>
    /// What is NOT here, and why. The Alt+letter half of the fix is two WPF calls —
    /// <c>AccessKeyManager.IsKeyRegistered</c> then <c>ProcessKey</c> — and WPF gates
    /// both: <c>GetTargetsForScope</c> returns no top-level targets unless the Alt key
    /// is PHYSICALLY held (<c>Keyboard.Modifiers</c>, read live from the OS) whenever
    /// the scope is a PresentationSource. That is what stops a bare "F" in a text box
    /// opening File, and it is why the handler can never open a menu unless Alt is
    /// truly down. It also means no unit test can exercise the positive path: a test
    /// cannot hold Alt, and injecting a real Alt press into the desktop from a test
    /// run is not acceptable. Measured: with the access keys registered and the menu
    /// even in menu mode, IsKeyRegistered("F") is false without Alt. The positive
    /// path is verified by dogfooding with a document open.
    /// </summary>
    [Collection("WpfSta")]
    public class OnARealMenu
    {
        [Fact]
        public void FocusingTheFirstItemEntersTheMenu()
        {
            // This is what an Alt tap becomes.
            var (focusWithin, firstFocused) = OnMenuWindow((_, menu) =>
            {
                var first = (MenuItem)menu.Items[0];
                first.Focus();
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
                return (menu.IsKeyboardFocusWithin, first.IsKeyboardFocused);
            });
            Assert.True(focusWithin, "the menu must hold keyboard focus");
            Assert.True(firstFocused, "the first item must be the focused one");
        }
    }
}
