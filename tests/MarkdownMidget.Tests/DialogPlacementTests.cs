using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using MarkdownMidget.Chrome;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>
/// Every dialog opens with its whole title bar in its owner's work area and no bigger than it.
/// Fit is the arithmetic, in physical pixels. The rest shows Settings and stand-in windows, not
/// activated, owned by a window far off screen, with the work area replaced by one that is off
/// screen too, so nothing is drawn where a person could see it. Shows windows: WpfSta collection.
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

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    private static Rect Bounds(Window w)
    {
        GetWindowRect(new WindowInteropHelper(w).Handle, out var r);
        return new Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    /// <summary>The owner is 600 x 400 at (-20000, -20000); the work area is <paramref name="work"/>.</summary>
    private static void WithOwner(Rect work, Action<Window> body) => ChromePaletteTests.RunSta(() =>
    {
        ChromeWindows.Register();   // what App.OnStartup calls: attaches the placement to every window
        var original = DialogPlacement.WorkAreaOf;
        DialogPlacement.WorkAreaOf = _ => work;
        var owner = new Window { Left = -20000, Top = -20000, Width = 600, Height = 400, ShowActivated = false, ShowInTaskbar = false };
        try { owner.Show(); body(owner); }
        finally { DialogPlacement.WorkAreaOf = original; owner.Close(); }
    });

    private static Window Open(Window dialog, Window owner)
    {
        dialog.Owner = owner;
        dialog.ShowActivated = false;
        dialog.Show();
        dialog.UpdateLayout();
        return dialog;
    }

    // Only Settings of the XAML dialogs can be built here: the others name Icon="/Assets/…", which WPF looks up in
    // the entry assembly (the test host) and won't let a test change; About, Register and Unregister also read the
    // install folder, the registry or the network. The stand-ins are DefaultApp's notice and the picker's size.
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
    [InlineData("Picker-sized")]
    public void EveryDialogOpensInsideItsOwnersWorkAreaAndNoBiggerThanIt(string name)
    {
        var work = new Rect(-20400, -20300, 1400, 420);   // shorter than Settings and the picker
        WithOwner(work, owner =>
        {
            var dialog = Open(Make(name), owner);
            try
            {
                Assert.True(work.Contains(Bounds(dialog)), $"{name} at {Bounds(dialog)}, work area {work}");
                var dpi = VisualTreeHelper.GetDpi(dialog);
                Assert.Equal(work.Width / dpi.DpiScaleX, dialog.MaxWidth, 3);
                Assert.Equal(work.Height / dpi.DpiScaleY, dialog.MaxHeight, 3);
            }
            finally { dialog.Close(); }
        });
    }

    [Fact]
    public void ADialogThatFitsIsCentredOnItsOwner()
    {
        WithOwner(new Rect(-20400, -20400, 1600, 1300), owner =>   // its centre is not the owner's
        {
            var dialog = Open(Make("Settings"), owner);
            try
            {
                var (d, o) = (Bounds(dialog), Bounds(owner));
                Assert.InRange(d.X + d.Width / 2 - (o.X + o.Width / 2), -1, 1);
                Assert.InRange(d.Y + d.Height / 2 - (o.Y + o.Height / 2), -1, 1);
            }
            finally { dialog.Close(); }
        });
    }

    [Fact]
    public void SettingsScrollsItsSectionsAndKeepsOkAndCancelInView()
    {
        WithOwner(new Rect(-20400, -20300, 1400, 420), owner =>
        {
            var dialog = (SettingsDialog)Open(Make("Settings"), owner);
            try
            {
                Assert.True(dialog.Sections.ScrollableHeight > 0, "the sections should scroll in a short work area");
                Assert.False(dialog.Sections.IsAncestorOf(dialog.OkBtn), "OK must stay outside the scrolling part");
                var client = (FrameworkElement)dialog.Content;
                var ok = dialog.OkBtn.TransformToAncestor(client).TransformBounds(new Rect(dialog.OkBtn.RenderSize));
                Assert.True(new Rect(client.RenderSize).Contains(ok), $"OK at {ok} in {client.RenderSize}");
            }
            finally { dialog.Close(); }
        });
    }

    [Fact]
    public void ADialogThatGrowsAfterItOpensIsPulledBackInside()
    {
        var work = new Rect(-20400, -20300, 1400, 600);
        WithOwner(work, owner =>
        {
            var panel = new StackPanel { Width = 300, Children = { new Border { Height = 100 } } };
            var dialog = Open(new Window { SizeToContent = SizeToContent.WidthAndHeight, Content = panel }, owner);
            try
            {
                var before = Bounds(dialog);
                panel.Children.Add(new Border { Height = 400 });   // as a longer message or an expander would
                dialog.UpdateLayout();
                var after = Bounds(dialog);
                Assert.True(after.Height >= before.Height + 400, $"grew from {before} to {after}");
                Assert.True(work.Contains(after), $"at {after}, work area {work}");
            }
            finally { dialog.Close(); }
        });
    }

    [Fact]
    public void AUserResizeAndAWindowWithoutAWpfOwnerAreLeftAlone()
    {
        WithOwner(new Rect(-20400, -20300, 1400, 600), owner =>
        {
            // The picker child's anchor is owned through its handle only, as MainWindow has no owner at all.
            var anchor = new Window { Left = -21000, Top = -21000, Width = 200, Height = 100, ShowActivated = false, ShowInTaskbar = false };
            new WindowInteropHelper(anchor).Owner = new WindowInteropHelper(owner).Handle;
            var dialog = Open(new Window { Width = 300, Height = 200, ResizeMode = ResizeMode.CanResize }, owner);
            try
            {
                anchor.Show();
                Assert.Equal((-21000.0, -21000.0), (anchor.Left, anchor.Top));
                SetWindowPos(new WindowInteropHelper(dialog).Handle, IntPtr.Zero, -21000, -21000, 0, 0, 0x0001 | 0x0004 | 0x0010);
                dialog.Width = 320;   // a size the user dragged to, after it opened
                dialog.UpdateLayout();
                Assert.Equal((-21000.0, -21000.0), (Bounds(dialog).X, Bounds(dialog).Y));
            }
            finally { anchor.Close(); dialog.Close(); }
        });
    }

    [Fact]
    public void TheRealWorkAreaLookupAnswersForAWindow() => ChromePaletteTests.RunSta(() =>
    {
        var window = new Window();
        try { Assert.True(DialogPlacement.WorkAreaNative(new WindowInteropHelper(window).EnsureHandle()) is { Width: > 0, Height: > 0 }); }
        finally { window.Close(); }
    });
}
