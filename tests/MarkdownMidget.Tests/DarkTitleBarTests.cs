using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using MarkdownMidget.Chrome;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>
/// The dark title bar request, with DWM replaced by a recorder so nothing about the machine
/// matters: a window without a handle gets it when the handle arrives, attribute 19 is the
/// fallback for older Windows 10, a DWM that refuses or is missing is not an error, and every
/// window (through the class handler) is set before it is shown and follows a switch.
/// One shown window, off screen and not activated, hence the WpfSta collection.
/// </summary>
[Collection("WpfSta")]
public class DarkTitleBarTests
{
    private const int EInvalidArg = unchecked((int)0x80070057);

    private static void WithDwm(Func<IntPtr, int, int, int> dwm, Action body)
    {
        ChromePaletteTests.RunSta(() =>
        {
            var original = DarkTitleBar.SetAttribute;
            DarkTitleBar.SetAttribute = dwm;
            try { body(); }
            finally { DarkTitleBar.SetAttribute = original; }
        });
    }

    [Fact]
    public void AWindowWithoutAHandleIsSetWhenItsHandleIsCreatedWithTheLatestMode()
    {
        var calls = new List<(IntPtr, int, int)>();
        WithDwm((h, attribute, value) => { calls.Add((h, attribute, value)); return 0; }, () =>
        {
            var window = new Window();
            try
            {
                Assert.False(DarkTitleBar.Apply(window, dark: false));
                Assert.False(DarkTitleBar.Apply(window, dark: true));
                Assert.Empty(calls);

                var handle = new WindowInteropHelper(window).EnsureHandle();   // raises SourceInitialized; nothing is shown
                Assert.Equal([(handle, DarkTitleBar.UseImmersiveDarkMode, 1)], calls);

                Assert.True(DarkTitleBar.Apply(window, dark: false));
                Assert.Equal((handle, DarkTitleBar.UseImmersiveDarkMode, 0), calls[^1]);
                Assert.Equal(2, calls.Count);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void AttributeNineteenIsTriedWhenTwentyIsRefused()
    {
        var calls = new List<(IntPtr, int, int)>();
        WithDwm((h, attribute, value) => { calls.Add((h, attribute, value)); return attribute == 20 ? EInvalidArg : 0; }, () =>
        {
            var window = new Window();
            try
            {
                var handle = new WindowInteropHelper(window).EnsureHandle();
                Assert.True(DarkTitleBar.Apply(window, dark: true));
                Assert.Equal([(handle, 20, 1), (handle, 19, 1)], calls);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData("refused")]
    [InlineData("no dwmapi")]
    [InlineData("no entry point")]
    public void AWindowsThatCannotDarkenTheTitleBarIsNotAnError(string failure)
    {
        WithDwm((_, _, _) => failure switch
        {
            "refused" => EInvalidArg,
            "no dwmapi" => throw new DllNotFoundException("dwmapi.dll"),
            _ => throw new EntryPointNotFoundException("DwmSetWindowAttribute"),
        }, () =>
        {
            var window = new Window();
            try
            {
                new WindowInteropHelper(window).EnsureHandle();
                Assert.True(DarkTitleBar.Apply(window, dark: true));
            }
            finally { window.Close(); }
        });
    }

    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);

    [Fact]
    public void EveryWindowIsSetBeforeItIsShownAndFollowsASwitchWhileOpen()
    {
        if (SystemParameters.HighContrast) return;   // Apply(true) is light there by design
        var calls = new List<(IntPtr Handle, int Value, bool Visible)>();
        WithDwm((h, attribute, value) => { calls.Add((h, value, IsWindowVisible(h))); return 0; }, () =>
        {
            ChromePalette.Apply(false);   // no Application here: registers the class handler, records light
            var light = new Window { Left = -20000, Top = -20000, Width = 120, Height = 60, ShowActivated = false, ShowInTaskbar = false };
            Window? dark = null;
            try
            {
                light.Show();
                Assert.Empty(calls);   // light is Windows' own caption: nothing is sent

                ChromePalette.Apply(true);
                var lightHandle = new WindowInteropHelper(light).Handle;
                Assert.Contains((lightHandle, 1, true), calls);   // the open window followed

                dark = new Window { Left = -20000, Top = -20000, Width = 120, Height = 60, ShowActivated = false, ShowInTaskbar = false };
                dark.Show();
                var darkHandle = new WindowInteropHelper(dark).Handle;
                var first = calls.Find(c => c.Handle == darkHandle);
                Assert.Equal((darkHandle, 1, false), first);   // set before it became visible

                calls.Clear();
                dark.Width = 140;   // later layouts do not resend
                dark.UpdateLayout();
                Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
                Assert.Empty(calls);

                ChromePalette.Apply(false);
                Assert.Contains((lightHandle, 0, true), calls);
                Assert.Contains((darkHandle, 0, true), calls);
            }
            finally
            {
                ChromePalette.Apply(false);
                light.Close();
                dark?.Close();
            }
        });
    }
}
