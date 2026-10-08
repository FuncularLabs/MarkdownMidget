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
                win = OffscreenWindow.Create(ed, 400, height);
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
    public void ARangePastTheDocumentEndUnderlinesOnlyTheTextThatIsLeft()
    {
        // A stale range, checked against longer text, must not throw or draw beyond the
        // document: one running past the end draws what the same range cut at the end
        // draws, and one starting at or past the end draws nothing, not a stub after the
        // last character. AvalonEdit's GetRectsForSegment clamps both ends into the
        // document itself, so this holds without WaveSpans' own end clamp too; when that
        // clamp is gone, a range starting at the end reaches AvalonEdit as an empty
        // segment, drawn as a 1-DIP caret (TextView.EmptyLineSelectionWidth) that the
        // hairline filter drops. No input makes the end clamp the only guard, so this
        // test pins the outcome, not that clamp.
        var (past, cut, after) = On(ed =>
        {
            var r = new SquiggleRenderer(ed);
            List<(double, double, double)> Spans((int, int)[] ranges) { r.SetRanges(ranges); return r.WaveSpans(ed.TextArea.TextView).ToList(); }
            return (Spans([(2, 999)]), Spans([(2, 3)]), Spans([(5, 3), (7, 4)]));
        }, "short");
        Assert.Single(past);          // "ort", from offset 2
        Assert.Equal(cut, past);
        Assert.Empty(after);
    }

    [Fact]
    public void ARangeHoldingOnlyALineBreakDrawsNoHairlineStub()
    {
        // AvalonEdit gives "\n" back as two empty segments, one after "ab" and one before
        // "cd", each drawn as a 1-DIP caret (TextView.EmptyLineSelectionWidth). The
        // hairline filter is what keeps those stubs off the page.
        var spans = On(ed =>
        {
            var r = new SquiggleRenderer(ed);
            r.SetRanges([(2, 1)]);
            return r.WaveSpans(ed.TextArea.TextView).ToList();
        }, "ab\ncd");
        Assert.Empty(spans);
    }
}
