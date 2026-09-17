using System;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using MarkdownMidget.Chrome;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>
/// The public hook, <see cref="ChromePalette.Apply(bool)"/>, through its injectable overload:
/// high contrast takes the light path, a call from another thread is carried out on the UI
/// thread, and when calls cross threads the last one made is the one in force. A real
/// dispatcher runs on its own STA thread; no window is created. In the WpfSta collection
/// because the hook also records the title bar mode for the whole process.
/// </summary>
[Collection("WpfSta")]
public class ChromePaletteHookTests
{
    private static void WithUiThread(Action<Dispatcher, int> body)
    {
        Dispatcher? ui = null;
        var uiThreadId = 0;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            ui = Dispatcher.CurrentDispatcher;
            uiThreadId = Environment.CurrentManagedThreadId;
            ready.Set();
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(ready.Wait(TimeSpan.FromSeconds(10)), "UI thread did not start");
        try { body(ui!, uiThreadId); }
        finally
        {
            ChromePalette.Apply(false);   // no Application here: back to light for the process
            ui!.InvokeShutdown();
            thread.Join(TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>Wait until everything already queued on the UI thread has run.</summary>
    private static void Drain(Dispatcher ui) => ui.Invoke(() => { }, DispatcherPriority.Background);

    [Fact]
    public void HighContrastTakesTheLightPathThroughTheHook()
    {
        WithUiThread((ui, _) =>
        {
            var resources = new ResourceDictionary();
            ui.Invoke(() => ChromePalette.Apply(true, ui, () => resources, () => true));
            Assert.IsType<ChromeLightPalette>(Assert.Single(resources.MergedDictionaries));
            Assert.False(resources.Contains(SystemColors.WindowTextBrushKey));
        });
    }

    [Fact]
    public void ACallFromAnotherThreadIsCarriedOutOnTheUiThread()
    {
        WithUiThread((ui, uiThreadId) =>
        {
            var resources = new ResourceDictionary();
            int? readOn = null;
            ChromePalette.Apply(true, ui, () => { readOn = Environment.CurrentManagedThreadId; return resources; }, () => false);
            Drain(ui);
            Assert.Equal(uiThreadId, readOn);
            Assert.IsType<ChromeDarkMode>(Assert.Single(resources.MergedDictionaries));
        });
    }

    [Fact]
    public void TheLastCallWinsWhenAnEarlierOneWasQueuedFromAnotherThread()
    {
        WithUiThread((ui, _) =>
        {
            var resources = new ResourceDictionary();
            ui.Invoke(() =>
            {
                // Dark is asked from a background thread while the UI thread is busy, so it
                // queues; then light is asked on the UI thread and applied at once.
                var background = new Thread(() => ChromePalette.Apply(true, ui, () => resources, () => false));
                background.Start();
                background.Join();
                ChromePalette.Apply(false, ui, () => resources, () => false);
            });
            Drain(ui);   // the queued dark call runs now
            Assert.IsType<ChromeLightPalette>(Assert.Single(resources.MergedDictionaries));
        });
    }
}
