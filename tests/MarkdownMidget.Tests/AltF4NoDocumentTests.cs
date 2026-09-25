using System;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using MarkdownMidget;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>
/// Alt+F4 with no document open (dogfooding, 1.0 beta). Alt+F4 goes to the window
/// that holds Win32 keyboard focus. In the formatted view that is the editor's
/// WebView2, and SetClosed collapsed the WebView2 without taking focus off it, so
/// the key went to a window nobody could see. The close box and File ▸ Exit do not
/// travel by keyboard focus, and Closing lets a window with no document go.
///
/// Neither a WebView2 nor a real Alt+F4 can run here: no app window, no synthetic
/// input. What can run is the mechanism: a native child window holding Win32 focus,
/// and the calls that move focus onto the window's own surface and back off it. The
/// wiring, and the with-a-document paths the fix must leave alone, are pinned by
/// reading the source (<see cref="RepoSources"/>). A pin proves a call is there;
/// the runtime tests prove what it does, on CI or on request (<see cref="OnARealWindow"/>).
/// </summary>
public class AltF4NoDocumentTests
{
    private static string MainWindowSource() => RepoSources.Read("src", "MarkdownMidget", "MainWindow.xaml.cs");

    // ===== the wiring =====

    [Fact]
    public void SetClosedHandsFocusToTheSplashBetweenShowingItAndHidingTheEditor()
    {
        // After the splash is shown, because a collapsed element takes no focus
        // (ACollapsedSplashTakesNothingFromTheViewThatHasFocus). Before the editor is
        // hidden, so focus leaves the WebView2 while its window is still up, the way
        // a click on the menu takes it. The alternative is asking a hidden window in
        // another process to give focus up. Only on entering the state; leaving it is
        // SetClosedHandsFocusBackToTheViewOnlyOnceItIsShowing.
        // And every time (G1): a braceless `if (_sourceMode)` on the line above left this
        // line here, in this order, and never run in the formatted view — the reported
        // bug, with all thirteen of these tests green.
        var body = RepoSources.WithoutComments(RepoSources.MethodBody(MainWindowSource(), "private void SetClosed("));
        const string takeCall = "if (on) NoDocumentFocus.Take(ClosedSplash);";
        RepoSources.AssertRunsUnconditionally(body, takeCall);
        var shown = body.IndexOf("ClosedSplash.Visibility =", StringComparison.Ordinal);
        var take = body.IndexOf(takeCall, StringComparison.Ordinal);
        var hidden = body.IndexOf("Web.Visibility =", StringComparison.Ordinal);
        Assert.True(shown >= 0, "SetClosed no longer shows ClosedSplash");
        Assert.True(hidden >= 0, "SetClosed no longer sets Web.Visibility");
        Assert.True(shown < take, "focus is handed to the splash before it is shown, and a collapsed element refuses it");
        Assert.True(take < hidden, "focus is handed to the splash only after the editor is hidden");
    }

    [Fact]
    public void SetClosedHandsFocusBackToTheViewOnlyOnceItIsShowing()
    {
        // F2. Leaving the state collapses the splash, which may hold the focus Take
        // gave it; WPF then moves focus off it, but not into the document. The ways out
        // that do not go on to FocusDocumentAsync — a read-only window, where it returns
        // early — would leave focus on no element at all, so SetClosed hands it to the
        // view itself. After BOTH views have their visibility, because a collapsed view
        // takes no focus; with the view each mode shows; and, in the formatted view, the
        // editor's own DOM focus inside that same branch, as FocusDocumentAsync does, or
        // the first keystroke goes nowhere. And every time (G1).
        var body = RepoSources.WithoutComments(RepoSources.MethodBody(MainWindowSource(), "private void SetClosed("));
        const string handBackCall = "if (!on && NoDocumentFocus.HandBack(ClosedSplash, _sourceMode ? (UIElement)SourceBox : Web))";
        RepoSources.AssertRunsUnconditionally(body, handBackCall);
        const string domFocus = "if (!_sourceMode && _editorReady) _ = RunEditorAsync(\"window.MDM.focus()\");";
        var web = body.IndexOf("Web.Visibility =", StringComparison.Ordinal);
        var source = body.IndexOf("SourceBox.Visibility =", StringComparison.Ordinal);
        var handBack = body.IndexOf(handBackCall, StringComparison.Ordinal);
        var dom = body.IndexOf(domFocus, StringComparison.Ordinal);
        Assert.True(web >= 0 && source >= 0, "SetClosed no longer sets both views' visibility");
        Assert.True(handBack >= 0, "SetClosed does not hand focus back to the showing view when a document arrives");
        Assert.True(handBack > web, "focus is handed back before the formatted view is made visible");
        Assert.True(handBack > source, "focus is handed back before the source view is made visible");
        Assert.True(dom > handBack, "the editor's DOM focus is not requested after the hand-back");
        // Inside the hand-back's own block: nothing between the two but its brace and comments.
        var between = body[(handBack + handBackCall.Length)..dom];
        Assert.Matches(new Regex(@"^\s*\{\s*(//[^\n]*\n\s*)*$"), between);
    }

    [Fact]
    public void SavingFromTheNoDocumentStateFocusesTheDocumentItJustSaved()
    {
        // A pick disables this window while it is up — FilePickerService.WaitWhilePumping
        // sets IsEnabled false and calls EnableWindow(false), and the native dialog's
        // ShowDialog(owner) does the same — which takes keyboard focus off the splash and
        // gives it back to nothing. So by the time Save As with nothing open leaves the
        // state, SetClosed's hand-back has nothing to hand on, and the document it just
        // saved would be showing with the caret in no element at all. Alt+F4 still closes
        // the window, because focus is then the window's; the first keystroke is what
        // would go nowhere. Whatever else SaveAsync returns early for, once it HAS left
        // the state it focuses the document, as an open does.
        var body = RepoSources.WithoutComments(RepoSources.MethodBody(MainWindowSource(),
            "private async Task<bool> SaveAsync("));
        Assert.Matches(new Regex(@"var leftNoDocument = _closed;\s*SetClosed\(false\);"), body);
        // Through TryFocusDocumentAsync: the file is written by now, and the focus call
        // reaches into the editor, which throws outright when the WebView2 has died
        // (RunEditorAsync awaits ExecuteScriptAsync). Failing there would turn a good
        // save into the app's crash dialog.
        RepoSources.AssertRunsUnconditionally(body,
            "if (leftNoDocument) await TryFocusDocumentAsync();", after: "SetClosed(false);");
    }

    [Fact]
    public void TheSplashIsDeclaredFocusableOutOfTheTabOrderAndNamedForNarrator()
    {
        // A Grid is not focusable by default, and a placeholder that is not focusable
        // cannot take focus (ASplashThatCannotHoldFocusDoesNotClaimIt), so the XAML has
        // to say so. Out of the tab order, so Tab still walks the real controls and the
        // splash's own Open / New links, never an invisible stop. And named: focus
        // lands on a panel with no text of its own, so Narrator reads its name, which
        // is the heading the splash shows.
        var xaml = RepoSources.Read("src", "MarkdownMidget", "MainWindow.xaml");
        var start = xaml.IndexOf("<Grid x:Name=\"ClosedSplash\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "no ClosedSplash grid in MainWindow.xaml");
        var tag = xaml[start..xaml.IndexOf('>', start)];
        Assert.Contains("Focusable=\"True\"", tag, StringComparison.Ordinal);
        Assert.Contains("KeyboardNavigation.IsTabStop=\"False\"", tag, StringComparison.Ordinal);
        var block = xaml[start..xaml.IndexOf("</Grid>", start, StringComparison.Ordinal)];
        Assert.Contains("Text=\"No document open\"", block, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"No document open\"", tag, StringComparison.Ordinal);
    }

    // ===== with a document open: what the fix must leave alone =====

    [Fact]
    public void ClosingAModifiedDocumentStillAsksFirst()
    {
        // Alt+F4, the close box and File ▸ Exit all arrive here. A modified document
        // defers the close to the save prompt, and the no-document state plays no part
        // in that decision: the fix is in where focus goes, not in whether closing may
        // proceed.
        var body = RepoSources.MethodBody(MainWindowSource(), "private async void MainWindow_Closing(");
        var guard = body.IndexOf("if (_dirty)", StringComparison.Ordinal);
        var cancel = body.IndexOf("e.Cancel = true;", StringComparison.Ordinal);
        var ask = body.IndexOf("await ConfirmDiscardAsync()", StringComparison.Ordinal);
        Assert.True(guard >= 0 && cancel > guard && ask > cancel,
            "Closing must cancel and ask about a modified document, in that order");
        Assert.DoesNotContain("_closed", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]    // WPF's own input path: Key.System, SystemKey F4
    [InlineData(false)]   // the WebView2's re-raise: a plain F4 with Alt in the modifiers
    public void AltF4FromTheEditorIsNeverSwallowed(bool isSystem)
    {
        // With a document open in the formatted view, Alt+F4 passes through the
        // editor's Alt handling (Web_KeyDown). Every name counts as a menu here, so
        // nothing but the decision itself can keep F4 from being taken for a mnemonic.
        var d = MenuAccessKeys.DecideKeyDown(isSystem, Key.F4, ModifierKeys.Alt, _ => true, out var key);
        Assert.NotEqual(MenuAccessKeys.Down.Invoke, d);
        Assert.Null(key);

        // And through the tracker the window actually feeds, mid-press.
        var tracker = new AltPressTracker();
        tracker.KeyDown(true, Key.LeftAlt, ModifierKeys.Alt, _ => true, out _);
        Assert.NotEqual(MenuAccessKeys.Down.Invoke,
            tracker.KeyDown(isSystem, Key.F4, ModifierKeys.Alt, _ => true, out _));
    }

    [Fact]
    public void TheEditorsKeyHandlerSwallowsOnlyAMenuInvoke()
    {
        // The other half of the above: a decision that is not Invoke returns before
        // anything marks the key handled, so Alt+F4 goes on to the browser's default
        // handling, which closes the window.
        var body = RepoSources.MethodBody(
            RepoSources.Read("src", "MarkdownMidget", "MainWindow.AccessKeys.cs"),
            "private void Web_KeyDown(");
        var gate = body.IndexOf("if (decision != MenuAccessKeys.Down.Invoke) return;", StringComparison.Ordinal);
        var handled = body.IndexOf("e.Handled = true;", StringComparison.Ordinal);
        Assert.True(gate >= 0, "Web_KeyDown no longer returns on every decision but Invoke");
        Assert.True(handled > gate, "Web_KeyDown marks a key handled before the Invoke gate");
        Assert.Equal(handled, body.LastIndexOf("e.Handled = true;", StringComparison.Ordinal));
    }

    // ===== the mechanism, on a real window =====

    /// <summary>
    /// Every case here needs a window that can take keyboard focus, and only a window
    /// that is activated can, so each is a <see cref="TakesFocusFactAttribute"/> case.
    /// The ones asserting that focus does NOT move need that too: in a window that
    /// cannot take focus, ASplashThatCannotHoldFocusDoesNotClaimIt would pass whatever
    /// the code did, and the other two would stop at their RequireKeyboardFocus check.
    /// </summary>
    [Collection("WpfSta")]
    public class OnARealWindow
    {
        [TakesFocusFact]
        public void TheSplashTakesFocusBackFromAChildWindowThatHoldsIt()
        {
            // The bug's shape: Win32 focus in a child window, which is the editor's
            // WebView2 in the app. The splash is shown and handed focus in that order,
            // with no pump between (as SetClosed does it), then the child is hidden.
            var outcome = OnStaWindow((win, root) =>
            {
                var editor = new NativeChild();
                var splash = new Grid { Focusable = true, Visibility = Visibility.Collapsed };
                root.Children.Add(editor);
                root.Children.Add(splash);
                Pump();
                RequireChild(editor);
                SetFocus(editor.Child);
                if (GetFocus() != editor.Child)
                    throw new InvalidOperationException("the child window never received Win32 focus, so the test "
                        + "could not run. This is the environment, not the code under test.");

                splash.Visibility = Visibility.Visible;
                var took = NoDocumentFocus.Take(splash);
                var window = new WindowInteropHelper(win).Handle;
                var onWindow = GetFocus() == window;
                var splashFocused = splash.IsKeyboardFocused;

                editor.Visibility = Visibility.Collapsed;
                Pump();
                var stillOnWindow = GetFocus() == window && splash.IsKeyboardFocused;
                return (took, onWindow, splashFocused, stillOnWindow);
            });
            Assert.True(outcome.onWindow, "Win32 focus must leave the child window for the window itself: Alt+F4 goes wherever it is");
            Assert.True(outcome.splashFocused, "the splash must hold WPF keyboard focus, not only logical focus");
            Assert.True(outcome.took, "Take must report the focus it took");
            Assert.True(outcome.stillOnWindow, "hiding the editor afterwards must not move focus off the window");
        }

        [TakesFocusFact]
        public void TheSplashTakesFocusBackFromAChildWindowWhileItStillHoldsLogicalFocus()
        {
            // F1. The splash had focus before (a document closed earlier), a child window
            // took Win32 focus since, and the splash was never shown a document that
            // moved its logical focus on. WPF keyboard focus is then null, but the
            // window's focus scope still names the splash. Asking for LOGICAL focus sees
            // no change there and moves nothing, so Win32 focus stays in the child; only
            // keyboard focus takes it back.
            var outcome = OnStaWindow((win, root) =>
            {
                var editor = new NativeChild();
                var splash = new Grid { Focusable = true };
                root.Children.Add(editor);
                root.Children.Add(splash);
                Pump();
                RequireChild(editor);
                splash.Focus();
                if (!splash.IsKeyboardFocused)
                    throw new InvalidOperationException("the splash never received keyboard focus, so the test "
                        + "could not run. This is the environment, not the code under test.");
                SetFocus(editor.Child);
                Pump();
                if (GetFocus() != editor.Child)
                    throw new InvalidOperationException("the child window never received Win32 focus, so the test "
                        + "could not run. This is the environment, not the code under test.");

                var keyboardFocusNull = Keyboard.FocusedElement is null;
                var logicalOnSplash = ReferenceEquals(FocusManager.GetFocusedElement(FocusManager.GetFocusScope(splash)), splash);

                var took = NoDocumentFocus.Take(splash);
                var onWindow = GetFocus() == new WindowInteropHelper(win).Handle;
                return (keyboardFocusNull, logicalOnSplash, took, onWindow, splashFocused: splash.IsKeyboardFocused);
            });
            Assert.True(outcome.keyboardFocusNull, "precondition: a child window holding Win32 focus leaves WPF keyboard focus null");
            Assert.True(outcome.logicalOnSplash, "precondition: the splash keeps its logical focus meanwhile");
            Assert.True(outcome.onWindow, "Win32 focus must leave the child window for the window itself, even when the splash already has logical focus");
            Assert.True(outcome.splashFocused, "the splash must hold WPF keyboard focus");
            Assert.True(outcome.took, "Take must report the focus it took");
        }

        [TakesFocusFact]
        public void ASplashThatCannotHoldFocusDoesNotClaimIt()
        {
            var outcome = OnStaWindow((_, root) =>
            {
                var splash = new Grid();   // not focusable: the Grid default
                root.Children.Add(splash);
                Pump();
                return (took: NoDocumentFocus.Take(splash), focused: splash.IsKeyboardFocused);
            });
            Assert.False(outcome.took);
            Assert.False(outcome.focused);
        }

        [TakesFocusFact]
        public void ACollapsedSplashTakesNothingFromTheViewThatHasFocus()
        {
            // With a document showing the splash is collapsed; the source view (a
            // TextBox stands in for it) keeps focus whatever is asked of the splash.
            var outcome = OnStaWindow((_, root) =>
            {
                var source = new TextBox();
                var splash = new Grid { Focusable = true, Visibility = Visibility.Collapsed };
                root.Children.Add(source);
                root.Children.Add(splash);
                Pump();
                source.Focus();
                RequireKeyboardFocus(source);
                var took = NoDocumentFocus.Take(splash);
                return (took, sourceKeeps: source.IsKeyboardFocused);
            });
            Assert.False(outcome.took);
            Assert.True(outcome.sourceKeeps, "a collapsed splash must not take focus from the view that has it");
        }

        [TakesFocusFact]
        public void LeavingTheStateHandsTheSplashsFocusToTheViewNowShowing()
        {
            // F2. As SetClosed(false) does it, with no pump between: the focused splash
            // is collapsed, the view is made visible, and the hand-back is asked. WPF
            // moves focus off a collapsed element later, on the dispatcher, so the
            // splash still holds it at that moment; the pump afterwards is that later.
            var outcome = OnStaWindow((_, root) =>
            {
                var view = new TextBox { Visibility = Visibility.Collapsed };
                var splash = new Grid { Focusable = true };
                root.Children.Add(view);
                root.Children.Add(splash);
                Pump();
                splash.Focus();
                RequireKeyboardFocus(splash);

                splash.Visibility = Visibility.Collapsed;
                view.Visibility = Visibility.Visible;
                var handed = NoDocumentFocus.HandBack(splash, view);
                var viewFocused = view.IsKeyboardFocused;
                Pump();
                return (handed, viewFocused, viewStillFocused: view.IsKeyboardFocused);
            });
            Assert.True(outcome.handed, "HandBack must report that the splash's focus was handed on");
            Assert.True(outcome.viewFocused, "the view now showing must take the focus the splash held");
            Assert.True(outcome.viewStillFocused, "WPF's later re-evaluation of the collapsed splash must not undo it");
        }

        [TakesFocusFact]
        public void LeavingTheStateLeavesFocusAloneWhenTheSplashDidNotHoldIt()
        {
            // A dialog, the menu, or the document itself already has focus: the
            // hand-back is not the view's to take. A second TextBox stands in for it.
            var outcome = OnStaWindow((_, root) =>
            {
                var elsewhere = new TextBox();
                var view = new TextBox { Visibility = Visibility.Collapsed };
                var splash = new Grid { Focusable = true };
                var panel = new StackPanel();
                panel.Children.Add(elsewhere);
                panel.Children.Add(view);
                panel.Children.Add(splash);
                root.Children.Add(panel);
                Pump();
                elsewhere.Focus();
                RequireKeyboardFocus(elsewhere);

                splash.Visibility = Visibility.Collapsed;
                view.Visibility = Visibility.Visible;
                var handed = NoDocumentFocus.HandBack(splash, view);
                return (handed, elsewhereKeeps: elsewhere.IsKeyboardFocused, viewFocused: view.IsKeyboardFocusWithin);
            });
            Assert.False(outcome.handed);
            Assert.True(outcome.elsewhereKeeps, "focus held outside the splash must stay where it is");
            Assert.False(outcome.viewFocused);
        }

        private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);

        private static void RequireChild(NativeChild editor)
        {
            if (editor.Child == IntPtr.Zero)
                throw new InvalidOperationException("the child window was never created, so the test could not run.");
        }

        private static void RequireKeyboardFocus(UIElement element)
        {
            if (!element.IsKeyboardFocused)
                throw new InvalidOperationException($"the {element.GetType().Name} never received keyboard focus, so the "
                    + "test could not run. This is the environment, not the code under test.");
        }

        private static T OnStaWindow<T>(Func<Window, Grid, T> body)
        {
            var result = default(T)!;
            Exception? error = null;
            var done = new ManualResetEventSlim();
            var t = new Thread(() =>
            {
                Window? win = null;
                try
                {
                    var root = new Grid();
                    win = new Window { Width = 300, Height = 200, Left = -10000, Top = -10000, ShowInTaskbar = false, Content = root };
                    win.Show();
                    Pump();
                    result = body(win, root);
                }
                catch (Exception ex) { error = ex; }
                finally { try { win?.Close(); } catch { /* best effort */ } done.Set(); }
            }) { IsBackground = true };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            // A timeout must fail, not hand back default(T) and let a false-asserting test pass.
            Assert.True(done.Wait(TimeSpan.FromSeconds(30)), "STA window harness timed out");
            if (error is not null) throw error;
            return result;
        }

        /// <summary>A plain native child window: an HWND of its own inside the WPF
        /// window, which is what the WebView2 is to the app's window.</summary>
        private sealed class NativeChild : HwndHost
        {
            public IntPtr Child { get; private set; }

            protected override HandleRef BuildWindowCore(HandleRef hwndParent)
            {
                Child = CreateWindowEx(0, "STATIC", string.Empty, WsChild | WsVisible,
                    0, 0, 100, 40, hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                if (Child == IntPtr.Zero)
                    throw new InvalidOperationException($"CreateWindowEx failed ({Marshal.GetLastWin32Error()})");
                return new HandleRef(this, Child);
            }

            protected override void DestroyWindowCore(HandleRef hwnd) => DestroyWindow(hwnd.Handle);
        }

        private const int WsChild = 0x40000000;
        private const int WsVisible = 0x10000000;

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style,
            int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern IntPtr SetFocus(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetFocus();
    }
}
