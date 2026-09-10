using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using MarkdownMidget.Source;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>
/// The squiggle renderer's geometry decisions, checked against a real laid-out text
/// view: one underline per visible word-run, positioned under the word, nothing for a
/// range scrolled off-screen, and no hairline stubs. The wave drawing itself (a
/// StreamGeometry) is not asserted — the decision of WHERE to draw is what regresses.
/// </summary>
[Collection("WpfSta")]
public class SquiggleRendererTests
{
    private static T On<T>(Func<SourceEditor, T> body, string text, double height = 400)
    {
        var result = default(T)!;
        Exception? error = null;
        var done = new ManualResetEventSlim();
        var t = new Thread(() =>
        {
            Window? win = null;
            try
            {
                var ed = new SourceEditor { FontFamily = new FontFamily("Consolas"), FontSize = 14, Text = text };
                win = new Window { Width = 400, Height = height, Left = -10000, Top = -10000, ShowInTaskbar = false, Content = ed };
                win.Show();
                ed.UpdateLayout();
                ed.TextArea.TextView.EnsureVisualLines();
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
                result = body(ed);
            }
            catch (Exception ex) { error = ex; }
            finally { try { win?.Close(); } catch { } done.Set(); }
        }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        Assert.True(done.Wait(TimeSpan.FromSeconds(30)), "renderer harness timed out");
        if (error is not null) throw error;
        return result;
    }

    [Fact]
    public void OneWordYieldsOneUnderlineUnderIt()
    {
        // "hello mistake there" — flag "mistake" at [6,7).
        var spans = On(ed =>
        {
            var r = new SquiggleRenderer(ed);
            r.SetRanges(new[] { (6, 7) });
            return r.WaveSpans(ed.TextArea.TextView).ToList();
        }, "hello mistake there");

        Assert.Single(spans);
        var (x1, x2, y) = spans[0];
        Assert.True(x2 > x1, "the underline must have width");
        Assert.True(x1 > 0, "the word is not at the left edge, so its underline starts inboard");
        Assert.True(y > 0, "the underline sits on a laid-out line");
    }

    [Fact]
    public void TwoWordsOnOneLineYieldTwoSeparateUnderlines()
    {
        // flag "aaa" [0,3) and "ccc" [8,3): two runs, not one merged span.
        var spans = On(ed =>
        {
            var r = new SquiggleRenderer(ed);
            r.SetRanges(new[] { (0, 3), (8, 3) });
            return r.WaveSpans(ed.TextArea.TextView).ToList();
        }, "aaa bbb ccc");

        Assert.Equal(2, spans.Count);
        Assert.True(spans[1].X1 > spans[0].X2, "the second underline starts after the first ends");
    }

    [Fact]
    public void ARangeScrolledOffScreenDrawsNothing()
    {
        var text = string.Join("\n", Enumerable.Range(0, 300).Select(i => "word" + i));
        var spans = On(ed =>
        {
            var r = new SquiggleRenderer(ed);
            r.SetRanges(new[] { (0, 4) });     // on line 0, far above the viewport
            ed.ScrollToLine(250);
            ed.UpdateLayout();
            ed.TextArea.TextView.EnsureVisualLines();
            return r.WaveSpans(ed.TextArea.TextView).ToList();
        }, text);

        Assert.Empty(spans);
    }

    [Fact]
    public void NoRangesDrawNothing()
    {
        var spans = On(ed => new SquiggleRenderer(ed).WaveSpans(ed.TextArea.TextView).ToList(),
            "nothing flagged here");
        Assert.Empty(spans);
    }

    [Fact]
    public void RangesPastTheDocumentEndAreClampedNotThrown()
    {
        // A stale range extending past the current text must not throw and must not
        // draw beyond the document.
        var spans = On(ed =>
        {
            var r = new SquiggleRenderer(ed);
            r.SetRanges(new[] { (2, 999) });
            return r.WaveSpans(ed.TextArea.TextView).ToList();
        }, "short");
        // "short" from offset 2 is "ort" — one visible run, no exception.
        Assert.Single(spans);
    }
}
