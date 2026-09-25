using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MarkdownMidget.Chrome;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>
/// Every dialog opens with its whole title bar in its owner's work area and no bigger than it, and the
/// main window's first launch fits its default size. Fit and FitDefault are the arithmetic, in physical
/// pixels. The rest shows Settings and stand-in windows owned by a window far off screen, against a
/// stand-in work area that is off screen too: nothing is drawn where a person could see it, nothing is
/// activated or put in the taskbar, and every window is closed. Shows windows: WpfSta collection.
/// </summary>
[Collection("WpfSta")]
public class DialogPlacementTests
{
    [Theory]
    // owner x, y, w, h (px) · dialog w, h (DIP) · work area x, y, w, h (px) · scale · expected x, y (px) · max w, h (DIP)
    [InlineData("taller: Settings on a maximised owner, 1080p at 150%", -12, -12, 1944, 1032, 480, 796, 0, 0, 1920, 1008, 1.5, 600, 0, 1280, 672)]
    [InlineData("wider than the work area", 0, 0, 1920, 1032, 2400, 300, 0, 0, 1920, 1032, 1, 0, 366, 1920, 1032)]
    [InlineData("off the top", 100, -300, 800, 400, 400, 300, 0, 0, 1920, 1032, 1, 300, 0, 1920, 1032)]
    [InlineData("off the left", -500, 200, 400, 300, 600, 200, 0, 0, 1920, 1032, 1, 0, 250, 1920, 1032)]
    [InlineData("negative-origin monitor", -1800, -350, 1000, 700, 500, 300, -1920, -400, 1920, 1040, 1, -1550, -150, 1920, 1040)]
    [InlineData("negative origin, too big: top-left wins", -1800, -350, 1000, 700, 3000, 2000, -1920, -400, 1920, 1040, 1, -1920, -400, 1920, 1040)]
    [InlineData("exactly fits", 0, 0, 1920, 1032, 1920, 1032, 0, 0, 1920, 1032, 1, 0, 0, 1920, 1032)]
    [InlineData("owner partly off the bottom right", 1700, 900, 800, 600, 500, 400, 0, 0, 1920, 1032, 1, 1420, 632, 1920, 1032)]
    [InlineData("tiny work area", 50, 40, 600, 400, 480, 796, 0, 0, 320, 200, 1, 0, 0, 320, 200)]
    [InlineData("fits: centred on the owner", 100, 100, 1000, 800, 400, 300, 0, 0, 1920, 1032, 1, 400, 350, 1920, 1032)]
    public void FitCentresOnTheOwnerShiftsInsideAndTheTopLeftWins(string name, double ox, double oy, double ow, double oh,
        double dw, double dh, double wx, double wy, double ww, double wh, double scale, double x, double y, double maxW, double maxH)
    {
        var (at, max) = DialogPlacement.Fit(new Rect(ox, oy, ow, oh), new Size(dw, dh), new Rect(wx, wy, ww, wh), new DpiScale(scale, scale));
        Assert.True(new Point(x, y) == at, $"{name}: at {at}");
        Assert.True(new Size(maxW, maxH) == max, $"{name}: max {max}");
    }

    [Fact]
    public void AMinimisedOwnerCentresTheDialogInTheWorkArea() =>
        Assert.Equal((new Point(760, 366), new Size(1920, 1032)),
            DialogPlacement.Fit(null, new Size(400, 300), new Rect(0, 0, 1920, 1032), new DpiScale(1, 1)));

    /// <summary>MainWindow's default 1120 x 720 DIP and minimum 520 x 360, placed in <paramref name="work"/>.</summary>
    private static Rect FirstLaunch(Rect work, double scale) =>
        DialogPlacement.FitDefault(new Size(1120, 720), new Size(520, 360), work, new DpiScale(scale, scale));

    [Fact]   // 1008 px above a 72 px taskbar is 672 DIP: the window becomes 1120 x 672 DIP, flush with the top
    public void FirstLaunchOn1080pAt150PercentShrinksToFitWithTheTitleBarOnScreen() =>
        Assert.Equal(new Rect(120, 0, 1680, 1008), FirstLaunch(new Rect(0, 0, 1920, 1008), 1.5));

    [Fact]
    public void FirstLaunchOnALargeWorkAreaKeepsThe1120By720DefaultCentred() =>
        Assert.Equal(new Rect(2640, 336, 1120, 720), FirstLaunch(new Rect(1920, 0, 2560, 1392), 1));

    [Fact]
    public void FirstLaunchBelowTheMinimumSizeKeepsTheMinimumAndTheTopLeftWins() =>
        Assert.Equal(new Rect(-800, 0, 520, 360), FirstLaunch(new Rect(-800, 0, 400, 300), 1));

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);   // on screen, unlike WPF's IsVisible during Show
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    private const uint MoveOnly = 0x0001 | 0x0004 | 0x0010;   // SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE

    private static IntPtr Handle(Window w) => new WindowInteropHelper(w).Handle;

    private static Rect Bounds(Window w) =>
        GetWindowRect(Handle(w), out var r) ? new Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top) : Rect.Empty;

    /// <summary>WindowStartupLocation as the placement left it at the dialog's first layout.</summary>
    private static WindowStartupLocation? s_startupAfterPlacement;

    /// <summary>
    /// Shows the window <paramref name="make"/> builds, owned by a 600 x 400 window at (-20000, -20000), with
    /// <paramref name="work"/> standing in for the monitor's work area, then runs the body and closes both.
    /// </summary>
    private static void WithDialog(Rect work, Func<Window> make, Action<Window, Window> body) => ChromePaletteTests.RunSta(() =>
    {
        ChromeWindows.Register();   // what App.OnStartup calls: attaches the placement to every window
        var (original, moving) = (DialogPlacement.WorkAreaOf, DialogPlacement.UserMovingOrSizing);
        DialogPlacement.WorkAreaOf = _ => work;
        s_startupAfterPlacement = null;
        var owner = new Window { Left = -20000, Top = -20000, Width = 600, Height = 400, ShowActivated = false, ShowInTaskbar = false };
        Window? dialog = null;
        try
        {
            owner.Show();
            var d = dialog = make();
            (d.Owner, d.ShowActivated, d.ShowInTaskbar) = (owner, false, false);
            d.PreviewGotKeyboardFocus += (_, e) => e.Handled = true;   // refused before WPF calls SetFocus: e.g. Settings' Loaded
            // Runs after the placement's class handler and, at the first layout, before the window is visible: a window
            // outside the stand-in work area goes further off screen, and Manual stops WPF centring it on a real monitor
            // afterwards, so a broken placement fails the assertions without showing a window a person could see.
            d.SizeChanged += (_, _) =>
            {
                if (IsWindowVisible(Handle(d))) return;
                s_startupAfterPlacement ??= d.WindowStartupLocation;
                d.WindowStartupLocation = WindowStartupLocation.Manual;
                if (!work.Contains(Bounds(d))) SetWindowPos(Handle(d), IntPtr.Zero, -25000, -25000, 0, 0, MoveOnly);
            };
            d.Show();
            d.UpdateLayout();
            body(owner, d);
        }
        finally { dialog?.Close(); owner.Close(); (DialogPlacement.WorkAreaOf, DialogPlacement.UserMovingOrSizing) = (original, moving); }
    });

    // Of the XAML dialogs only Settings can be built here: the rest name Icon="/Assets/…", looked up in the test host (About,
    // Register, Unregister also read the install folder, registry or network). Stand-ins: DefaultApp's notice, the picker's size.
    private static Window Make(string name) => name switch
    {
        "Settings" => new SettingsDialog(true, 10, true),
        "Code-built" => new Window { SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
                          WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = new TextBlock { Text = "Built in code." } },
        _ => new Window { Width = 820, Height = 520, MinWidth = 560, MinHeight = 360, WindowStartupLocation = WindowStartupLocation.CenterOwner },
    };

    [Theory]
    [InlineData("Settings")]
    [InlineData("Code-built")]
    public void EveryDialogOpensInsideItsOwnersWorkAreaAndNoBiggerThanIt(string name)
    {
        var work = new Rect(-20400, -20300, 1400, 420);   // shorter than Settings
        WithDialog(work, () => Make(name), (_, dialog) =>
        {
            Assert.True(work.Contains(Bounds(dialog)), $"{name} at {Bounds(dialog)}, work area {work}");
            Assert.Equal(WindowStartupLocation.Manual, s_startupAfterPlacement);   // or WPF would centre it again, uncapped
            var dpi = VisualTreeHelper.GetDpi(dialog);
            Assert.Equal(work.Width / dpi.DpiScaleX, dialog.MaxWidth, 3);
            Assert.Equal(work.Height / dpi.DpiScaleY, dialog.MaxHeight, 3);
        });
    }

    [Fact]   // the picker: maximising, snapping or a bigger monitor may still make it larger
    public void AResizableDialogOpensShrunkIntoTheWorkAreaWithNoMaximum()
    {
        var work = new Rect(-20400, -20300, 1400, 420);   // shorter than the picker's 520
        WithDialog(work, () => Make("Picker-sized"), (_, dialog) =>
        {
            Assert.True(work.Contains(Bounds(dialog)), $"at {Bounds(dialog)}, work area {work}");
            Assert.Equal((double.PositiveInfinity, double.PositiveInfinity), (dialog.MaxWidth, dialog.MaxHeight));
            dialog.Height = 700; dialog.UpdateLayout();
            Assert.Equal(700, dialog.ActualHeight, 1);
        });
    }

    [Fact]
    public void ADialogThatFitsIsCentredOnItsOwner() =>
        WithDialog(new Rect(-20400, -20400, 1600, 1300), () => Make("Settings"), (owner, dialog) =>   // work area centre is not the owner's
        {
            var (d, o) = (Bounds(dialog), Bounds(owner));
            Assert.InRange(d.X + d.Width / 2 - (o.X + o.Width / 2), -1, 1);
            Assert.InRange(d.Y + d.Height / 2 - (o.Y + o.Height / 2), -1, 1);
        });

    [Fact]
    public void SettingsScrollsItsSectionsAndKeepsOkAndCancelInView() =>
        WithDialog(new Rect(-20400, -20300, 1400, 420), () => Make("Settings"), (_, window) =>
        {
            var dialog = (SettingsDialog)window;
            Assert.True(dialog.Sections.ScrollableHeight > 0, "the sections should scroll in a short work area");
            Assert.False(dialog.Sections.IsAncestorOf(dialog.OkBtn), "OK must stay outside the scrolling part");
            var client = (FrameworkElement)dialog.Content;
            var ok = dialog.OkBtn.TransformToAncestor(client).TransformBounds(new Rect(dialog.OkBtn.RenderSize));
            Assert.True(new Rect(client.RenderSize).Contains(ok), $"OK at {ok} in {client.RenderSize}");
        });

    [Fact]
    public void ADialogThatGrowsKeepsItsTopLeftWhileItFitsAndIsPulledBackInsideWhenNot()
    {
        var work = new Rect(-20400, -20300, 1400, 900);
        StackPanel? panel = null;
        WithDialog(work, () => new Window { SizeToContent = SizeToContent.WidthAndHeight,
                                            Content = panel = new StackPanel { Width = 300, Children = { new Border { Height = 100 } } } }, (_, dialog) =>
        {
            var before = Bounds(dialog);
            panel!.Children.Add(new Border { Height = 100 }); dialog.UpdateLayout();   // as Find does when it shows Replace
            Assert.Equal((before.X, before.Y), (Bounds(dialog).X, Bounds(dialog).Y));
            Assert.True(Bounds(dialog).Height > before.Height, "it did not grow");
            panel.Children.Add(new Border { Height = 400 }); dialog.UpdateLayout();    // now past the bottom of the work area
            var after = Bounds(dialog);
            Assert.True(after.Height > before.Height + 400, $"grew from {before} to {after}");
            Assert.True(work.Contains(after), $"at {after}, work area {work}");
        });
    }

    [Fact]   // dragged onto a monitor with another scale, Windows resizes it mid-drag: moving it then would pull it from the pointer
    public void ASizeChangeWhileTheUserDragsTheDialogMovesNothingButUpdatesTheCap()
    {
        StackPanel? panel = null;
        WithDialog(new Rect(-20400, -20300, 1400, 900), () => new Window { SizeToContent = SizeToContent.WidthAndHeight,
                                                         Content = panel = new StackPanel { Width = 300, Children = { new Border { Height = 100 } } } }, (_, dialog) =>
        {
            var before = Bounds(dialog);
            var other = new Rect(-20400, -20300, 1400, 300);   // the monitor it is being dragged onto: its bottom is above the dialog
            (DialogPlacement.WorkAreaOf, DialogPlacement.UserMovingOrSizing) = (_ => other, _ => true);
            panel!.Children.Add(new Border { Height = 100 }); dialog.UpdateLayout();
            Assert.Equal((before.X, before.Y), (Bounds(dialog).X, Bounds(dialog).Y));
            Assert.Equal(other.Height / VisualTreeHelper.GetDpi(dialog).DpiScaleY, dialog.MaxHeight, 3);
        });
    }

    [Fact]   // scrolled to the bottom, Import adds its result below the view; a bad number changes a hint above it
    public void SettingsBringsItsFeedbackIntoViewWhenItAppearsOrChanges() =>
        WithDialog(new Rect(-20400, -20300, 1400, 420), () => new SettingsDialog(true, 10, true, importDictionary: _ => "12 words imported."), (_, window) =>
        {
            var dialog = (SettingsDialog)window;
            bool InView(FrameworkElement e)
            {
                Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));   // run what was queued
                var r = e.TransformToAncestor(dialog.Sections).TransformBounds(new Rect(e.RenderSize));
                return new Rect(0, 0, dialog.Sections.ViewportWidth, dialog.Sections.ViewportHeight).Contains(r);
            }
            void ScrollToEnd() { dialog.Sections.ScrollToVerticalOffset(dialog.Sections.ScrollableHeight); dialog.UpdateLayout(); }   // a number, as the wheel leaves it
            ScrollToEnd();
            dialog.ImportDicBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.True(InView(dialog.ImportResult), "the import result is out of view");
            ScrollToEnd();
            dialog.RecentLimitBox.Text = "0";
            dialog.OkBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.True(InView(dialog.RecentHint), "the Recent files hint is out of view");
        });

    [Fact]
    public void AUserResizeAndAWindowWithoutAWpfOwnerAreLeftAlone() =>
        WithDialog(new Rect(-20400, -20300, 1400, 600), () => new Window { Width = 300, Height = 200, ResizeMode = ResizeMode.CanResize }, (owner, dialog) =>
        {
            // The picker child's anchor is owned through its handle only, as MainWindow has no owner at all.
            var anchor = new Window { Left = -21000, Top = -21000, Width = 200, Height = 100, ShowActivated = false, ShowInTaskbar = false };
            new WindowInteropHelper(anchor).Owner = Handle(owner);
            try
            {
                anchor.Show();
                Assert.Equal((-21000.0, -21000.0), (anchor.Left, anchor.Top));
                SetWindowPos(Handle(dialog), IntPtr.Zero, -21000, -21000, 0, 0, MoveOnly);
                dialog.Width = 320; dialog.UpdateLayout();   // a size the user dragged to, after it opened
                Assert.Equal((-21000.0, -21000.0), (Bounds(dialog).X, Bounds(dialog).Y));
            }
            finally { anchor.Close(); }
        });

    [Fact]
    public void TheRealWorkAreaAndMoveSizeLookupsAnswerForAWindow() => ChromePaletteTests.RunSta(() =>
    {
        var window = new Window();   // a handle only: never shown
        try
        {
            var handle = new WindowInteropHelper(window).EnsureHandle();
            Assert.True(DialogPlacement.WorkAreaNative(handle) is { Width: > 0, Height: > 0 });
            Assert.False(DialogPlacement.InMoveSizeNative(handle));   // nobody is dragging it
        }
        finally { window.Close(); }
    });
}
