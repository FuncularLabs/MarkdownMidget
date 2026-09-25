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
/// exhaustively, including whole press sequences (down … up) through the tracker,
/// since the bug that review found lived between the two halves: a key-down that was
/// rightly ignored left the press unspent, and the Alt key-up then read as a tap.
/// The one WPF call that can be proved on a real menu — focusing the first item for
/// an Alt tap — is.
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
    public void AltGrTypingIsSpentAndNeverSwallowed()
    {
        // AltGr+E on a European layout arrives as Ctrl+Alt+E. "E" IS a registered
        // access key (_Edit), and it must still reach the editor as a character: the
        // Control check is what stops this being an Invoke that swallows the key. (The
        // press is normally already spent by the Alt-under-Ctrl branch; `Used` here
        // keeps that true even when Alt came down before Ctrl.)
        var d = MenuAccessKeys.DecideKeyDown(true, Key.E, ModifierKeys.Control | ModifierKeys.Alt, Registered, out var key);
        Assert.Equal(MenuAccessKeys.Down.Used, d);
        Assert.Null(key);
    }

    [Theory]
    [InlineData(ModifierKeys.Control | ModifierKeys.Alt)]   // AltGr: Windows holds Ctrl before Alt goes down
    [InlineData(ModifierKeys.Shift | ModifierKeys.Alt)]
    [InlineData(ModifierKeys.Windows | ModifierKeys.Alt)]
    public void AltGoingDownUnderAnotherModifierIsNotAMenuGesture(ModifierKeys mods)
        // Mirrors WPF's KeyboardNavigation, which refuses to track such a press: it is
        // spent from the start, so its release can never enter the menu.
        => Assert.Equal(MenuAccessKeys.Down.Used,
            MenuAccessKeys.DecideKeyDown(false, Key.RightAlt, mods, Registered, out _));

    [Fact]
    public void AltShiftPlusARegisteredLetterStillInvokes()
        // Alt+Shift+F opens File in native WPF (the key is normalised to upper case).
        => Assert.Equal(MenuAccessKeys.Down.Invoke,
            MenuAccessKeys.DecideKeyDown(true, Key.F, ModifierKeys.Shift | ModifierKeys.Alt, Registered, out _));

    [Theory]
    [InlineData(Key.LeftAlt, false, true)]    // Alt down, Alt up, nothing between: a tap
    [InlineData(Key.RightAlt, false, true)]
    [InlineData(Key.LeftAlt, true, false)]    // Alt+F then release: not a tap
    [InlineData(Key.F, false, false)]         // releasing a letter is never a tap
    public void AnAltTapIsAltUpWithNothingUsed(Key realKey, bool used, bool expected)
        => Assert.Equal(expected, MenuAccessKeys.IsAltTap(realKey, used));

    // ===== whole presses, down to up, through the tracker =====

    private static bool Tap(params (bool down, bool isSystem, Key key, ModifierKeys mods)[] events)
    {
        var t = new AltPressTracker();
        var tapped = false;
        foreach (var (down, isSystem, key, mods) in events)
        {
            if (down) t.KeyDown(isSystem, key, mods, Registered, out _);
            else tapped |= t.KeyUp(key);
        }
        return tapped;
    }

    private const ModifierKeys A = ModifierKeys.Alt;
    private const ModifierKeys CA = ModifierKeys.Control | ModifierKeys.Alt;

    [Fact]
    public void AltDownAltUpIsATap()
        => Assert.True(Tap((true, false, Key.LeftAlt, A), (false, false, Key.LeftAlt, ModifierKeys.None)));

    [Fact]
    public void AltFThenAltUpIsNotATap()
        => Assert.False(Tap((true, false, Key.LeftAlt, A), (true, false, Key.F, A), (false, false, Key.LeftAlt, ModifierKeys.None)));

    [Fact]
    public void AltXThenAltUpIsNotATap()
        => Assert.False(Tap((true, false, Key.LeftAlt, A), (true, false, Key.X, A), (false, false, Key.LeftAlt, ModifierKeys.None)));

    [Fact]
    public void AltGrLetterThenAltGrUpIsNotATap()
    {
        // The review finding: LCtrl down (synthesised by Windows), RAlt down, E down,
        // RAlt up. The old logic ignored the E and then read the RAlt release as a
        // tap — menu mode after every accented character.
        var seq = new (bool, bool, Key, ModifierKeys)[]
        {
            (true, false, Key.LeftCtrl, ModifierKeys.Control),
            (true, false, Key.RightAlt, CA),
            (true, false, Key.E, CA),
            (false, false, Key.RightAlt, ModifierKeys.Control),
        };
        Assert.False(Tap(seq));
    }

    [Fact]
    public void ABareAltGrTapIsNotATap()
    {
        var seq = new (bool, bool, Key, ModifierKeys)[]
        {
            (true, false, Key.LeftCtrl, ModifierKeys.Control),
            (true, false, Key.RightAlt, CA),
            (false, false, Key.RightAlt, ModifierKeys.Control),
        };
        Assert.False(Tap(seq));
    }

    [Fact]
    public void AShiftAltTapIsNotATap()
        => Assert.False(Tap((true, false, Key.LeftAlt, ModifierKeys.Shift | A), (false, false, Key.LeftAlt, ModifierKeys.Shift)));

    [Fact]
    public void ANewAltPressAfterASpentOneStartsClean()
    {
        // Alt+F (spent), Alt released (not a tap), then a clean Alt tap: the second
        // press must not inherit the first's spent state.
        var t = new AltPressTracker();
        t.KeyDown(false, Key.LeftAlt, A, Registered, out _);
        t.KeyDown(false, Key.F, A, Registered, out _);
        Assert.False(t.KeyUp(Key.LeftAlt));
        t.KeyDown(false, Key.LeftAlt, A, Registered, out _);
        Assert.True(t.KeyUp(Key.LeftAlt));
    }

    [Fact]
    public void ALetterKeyUpNeverEndsThePressAsATap()
    {
        var t = new AltPressTracker();
        t.KeyDown(false, Key.LeftAlt, A, Registered, out _);
        Assert.False(t.KeyUp(Key.F));
    }

    [Fact]
    public void AnAltUpTheEditorNeverSawTheDownForIsNotATap()
    {
        // Dismiss a dialog with Alt+F4: it closes on the key-DOWN, focus returns to
        // the editor, and the Alt release lands here alone. Not a tap.
        var t = new AltPressTracker();
        Assert.False(t.KeyUp(Key.LeftAlt));
    }

    [Fact]
    public void ASecondAltUpAfterATapIsNotATap()
    {
        // The press ended with the first key-up; a stray second key-up (focus bounced
        // through another window and back) has no down half.
        var t = new AltPressTracker();
        t.KeyDown(false, Key.LeftAlt, A, Registered, out _);
        Assert.True(t.KeyUp(Key.LeftAlt));
        Assert.False(t.KeyUp(Key.LeftAlt));
    }

    [Fact]
    public void LosingFocusForgetsThePressInProgress()
    {
        // Alt down here, then Alt+Tab away (the OS eats the Tab) and back: the Alt
        // key-up that finally arrives belongs to that excursion, not to a tap. This
        // proves Reset() does its job; the window calls it from the WebView2's
        // LostFocus and the window's Deactivated, which no unit test can raise — that
        // hookup is verified by dogfooding.
        var t = new AltPressTracker();
        t.KeyDown(false, Key.LeftAlt, A, Registered, out _);
        t.Reset();
        Assert.False(t.KeyUp(Key.LeftAlt));
    }

    [Fact]
    public void TheOtherAltKeyComingUpIsNotATap()
    {
        // Left Alt down, right Alt up: two keys, not one press. Native compares the
        // real key; so does this.
        var t = new AltPressTracker();
        t.KeyDown(false, Key.LeftAlt, A, Registered, out _);
        Assert.False(t.KeyUp(Key.RightAlt));
    }

    [Fact]
    public void ARepeatedAltDownDoesNotUnspendThePress()
    {
        // Alt, X (spent), Alt again (typematic repeat or the other Alt key), release:
        // still spent, still not a tap.
        var t = new AltPressTracker();
        t.KeyDown(false, Key.LeftAlt, A, Registered, out _);
        t.KeyDown(false, Key.X, A, Registered, out _);
        t.KeyDown(false, Key.LeftAlt, A, Registered, out _);
        Assert.False(t.KeyUp(Key.LeftAlt));
    }

    [Fact]
    public void ALetterReleasedUnderAltSpendsThePress()
    {
        // Rollover typing: X was down before Alt and comes up under it. Native WPF
        // cancels the tap on any key-up; so does this.
        var t = new AltPressTracker();
        t.KeyDown(false, Key.X, ModifierKeys.None, Registered, out _);   // Ignore: no Alt yet
        t.KeyDown(false, Key.LeftAlt, A, Registered, out _);
        Assert.False(t.KeyUp(Key.X));
        Assert.False(t.KeyUp(Key.LeftAlt));
    }

    [Fact]
    public void ALetterReleasedWithNoPressInProgressChangesNothing()
    {
        // No Alt down: a letter key-up must not poison the next press.
        var t = new AltPressTracker();
        Assert.False(t.KeyUp(Key.X));
        t.KeyDown(false, Key.LeftAlt, A, Registered, out _);
        Assert.True(t.KeyUp(Key.LeftAlt));
    }

    [Fact]
    public void OnlyAMenusOwnOpeningIsItsOwn()
    {
        object file = new(), recent = new();
        Assert.True(MenuAccessKeys.IsOwnSubmenuOpening(file, file));
        Assert.False(MenuAccessKeys.IsOwnSubmenuOpening(file, recent));
    }

    // ===== the WPF side, against a real menu =====

    /// <param name="takesFocus">True only for a <see cref="TakesFocusFactAttribute"/> case:
    /// a plain window, which showing activates, so keyboard focus can move in it. Otherwise
    /// a window that is never activated (<see cref="OffscreenWindow"/>).</param>
    private static T OnMenuWindow<T>(Func<Window, Menu, T> body, bool takesFocus = false)
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

                win = takesFocus
                    ? new Window { Width = 300, Height = 200, Left = -10000, Top = -10000, ShowInTaskbar = false, Content = panel }
                    : OffscreenWindow.Create(panel, 300, 200);
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
    /// both on the Alt key being PHYSICALLY held, in <c>Menu.OnAccessKeyPressed</c>:
    /// unless <c>Keyboard.IsKeyDown(LeftAlt/RightAlt)</c> (read live from the OS), the
    /// Menu claims the key's scope as itself, so a top-level item never matches the
    /// window scope those calls are asked about. That is what stops a bare "F" in a
    /// text box opening File, and why the handler can never open a menu unless Alt
    /// is truly down. It also means no unit test can exercise the positive path: a
    /// test cannot hold Alt, and injecting a real Alt press into the desktop from a
    /// test run is not acceptable. Measured: with the access keys registered and the
    /// menu even in menu mode, IsKeyRegistered("F") is false without Alt. The
    /// positive path is verified by dogfooding with a document open.
    /// </summary>
    [Collection("WpfSta")]
    public class OnARealMenu
    {
        [TakesFocusFact]
        public void FocusingTheFirstItemEntersTheMenu()
        {
            // This is what an Alt tap becomes.
            var (focusWithin, firstFocused) = OnMenuWindow(takesFocus: true, body: (_, menu) =>
            {
                var first = (MenuItem)menu.Items[0];
                first.Focus();
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
                return (menu.IsKeyboardFocusWithin, first.IsKeyboardFocused);
            });
            Assert.True(focusWithin, "the menu must hold keyboard focus");
            Assert.True(firstFocused, "the first item must be the focused one");
        }

        [Fact]
        public void ANestedSubmenuOpeningReachesTheParentButIsNotItsOwn()
        {
            // File ▸ Open Recent opening bubbles to File's SubmenuOpened handler, which
            // rebuilt Open Recent mid-open: Right found nothing to focus.
            var own = OnMenuWindow((_, menu) =>
            {
                var file = (MenuItem)menu.Items[0];
                bool? seen = null;
                file.SubmenuOpened += (s, e) => seen = MenuAccessKeys.IsOwnSubmenuOpening(s, e.OriginalSource);
                var nested = (MenuItem)file.Items[0];
                nested.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent, nested));
                return seen;
            });
            Assert.NotNull(own);                     // it bubbled to the parent
            Assert.False(own.Value);
        }
    }
}
