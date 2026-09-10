using System;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using MarkdownMidget.Source;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>
/// The adapter that lets the rest of the app keep speaking TextBox to an AvalonEdit
/// control. What is worth testing here is precisely the translation: 0-based lines,
/// character indices, control-relative geometry, and the one measured behavioural
/// difference (a hit test below the text). AvalonEdit itself is not under test.
///
/// Anything that reads glyph geometry needs a laid-out control, so those cases show a
/// real (off-screen) window on an STA thread and force a layout pass — the same shape
/// <see cref="ContextMenuFocusTests"/> uses for WPF menus.
/// </summary>
[Collection("WpfSta")]
public class SourceEditorTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    /// <summary>Run <paramref name="body"/> against a SourceEditor. When
    /// <paramref name="laidOut"/> is set the control is shown in a sized window and a
    /// layout pass is forced first, so visible-line and rect queries have an answer.</summary>
    private static T On<T>(Func<SourceEditor, T> body, string text, bool laidOut, double height = 400)
    {
        var result = default(T)!;
        Exception? error = null;
        var done = new ManualResetEventSlim();
        var t = new Thread(() =>
        {
            Window? win = null;
            try
            {
                var ed = new SourceEditor
                {
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 14,
                    Text = text,
                };
                if (laidOut)
                {
                    win = new Window
                    {
                        Width = 300,
                        Height = height,
                        Left = -10000,
                        Top = -10000,
                        ShowInTaskbar = false,
                        Content = ed,
                    };
                    win.Show();
                    ed.UpdateLayout();
                    ed.TextArea.TextView.EnsureVisualLines();
                    Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
                }
                result = body(ed);
            }
            catch (Exception ex) { error = ex; }
            finally
            {
                try { win?.Close(); } catch { /* best effort */ }
                done.Set();
            }
        }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        Assert.True(done.Wait(Budget), "SourceEditor harness timed out.");
        if (error is not null) throw error;
        return result;
    }

    // ===== 1.1 line / offset mapping =====

    [Fact]
    public void LineAndOffsetRoundTrip()
    {
        var (m0, mMid, mLast, offLine0, offLine2, empty) = On(ed =>
        {
            // "a\nbb\nccc" — lines 0,1,2 start at 0,2,5.
            var line0 = ed.GetLineIndexFromCharacterIndex(0);   // 'a'
            var lineMid = ed.GetLineIndexFromCharacterIndex(3); // inside "bb"
            var lineLast = ed.GetLineIndexFromCharacterIndex(ed.Text.Length); // end
            var startOf0 = ed.GetCharacterIndexFromLineIndex(0);
            var startOf2 = ed.GetCharacterIndexFromLineIndex(2);
            var emptyStart = 0;
            return (line0, lineMid, lineLast, startOf0, startOf2, emptyStart);
        }, "a\nbb\nccc", laidOut: false);

        Assert.Equal(0, m0);
        Assert.Equal(1, mMid);
        Assert.Equal(2, mLast);
        Assert.Equal(0, offLine0);
        Assert.Equal(5, offLine2);
        Assert.Equal(0, empty);
    }

    [Fact]
    public void OutOfRangeMappingReturnsMinusOne()
    {
        var (badChar, badLine, lineCountEmpty) = On(ed =>
            (ed.GetLineIndexFromCharacterIndex(9999),
             ed.GetCharacterIndexFromLineIndex(9999),
             new SourceEditor { Text = "" }.LineCount),
            "hi", laidOut: false);

        Assert.Equal(-1, badChar);
        Assert.Equal(-1, badLine);
        // An empty document is one line in AvalonEdit, never -1 (TextBox's pre-layout
        // answer); the scroll-anchor guard that checked <= 0 simply never trips now.
        Assert.Equal(1, lineCountEmpty);
    }

    // ===== 1.2 visible-line indices are DOCUMENT lines =====

    [Fact]
    public void VisibleLineIndicesAreDocumentLines()
    {
        // 200 short lines in a 400px window: the first visible line is 0 and the last
        // is well before 199, so the window genuinely clipped the document.
        var text = string.Join("\n", System.Linq.Enumerable.Range(0, 200));
        var (first, last, count) = On(ed =>
            (ed.GetFirstVisibleLineIndex(), ed.GetLastVisibleLineIndex(), ed.LineCount),
            text, laidOut: true);

        Assert.Equal(0, first);
        Assert.True(last is > 0 and < 199, $"last visible line {last} should be a clipped document line");
        Assert.Equal(200, count);
    }

    [Fact]
    public void WrapMakesOneLongLineSpanTheViewport()
    {
        // A single very long line, wrapped: it is document line 0 top and bottom, even
        // though it occupies many DISPLAY rows. Proves the methods report document
        // lines, not display rows — the distinction the scroll anchor depends on.
        var longLine = string.Join(" ", System.Linq.Enumerable.Repeat("wordy", 400));
        var (first, last) = On(ed =>
        {
            ed.WordWrap = true;
            ed.UpdateLayout();
            ed.TextArea.TextView.EnsureVisualLines();
            return (ed.GetFirstVisibleLineIndex(), ed.GetLastVisibleLineIndex());
        }, longLine, laidOut: true);

        Assert.Equal(0, first);
        Assert.Equal(0, last);
    }

    // ===== 1.3 the measured behavioural gap =====

    [Fact]
    public void HitTestBelowTextFallsBackToEnd()
    {
        // A point far below the last line: TextBox with snapToText:true returns the
        // end index; AvalonEdit alone returns null. The spell menu needs the index.
        var (snapped, unsnapped, end) = On(ed =>
            (ed.GetCharacterIndexFromPoint(new Point(4, ed.ActualHeight + 500), snapToText: true),
             ed.GetCharacterIndexFromPoint(new Point(4, ed.ActualHeight + 500), snapToText: false),
             ed.Text.Length),
            "one\ntwo\nthree", laidOut: true);

        Assert.Equal(end, snapped);
        Assert.Equal(-1, unsnapped);
    }

    [Fact]
    public void HitTestOverTextReturnsAnOffsetInThatLine()
    {
        var idx = On(ed => ed.GetCharacterIndexFromPoint(new Point(6, 4), snapToText: true),
            "hello world", laidOut: true);
        Assert.True(idx is >= 0 and <= 11, $"offset {idx} should land within the single line");
    }

    // ===== 1.5 the TextBox property shims =====

    [Fact]
    public void CaretIndexClampsAndRoundTrips()
    {
        var (mid, clampedHigh, clampedLow) = On(ed =>
        {
            ed.CaretIndex = 3;
            var m = ed.CaretIndex;
            ed.CaretIndex = 9999;
            var hi = ed.CaretIndex;
            ed.CaretIndex = -5;
            var lo = ed.CaretIndex;
            return (m, hi, lo);
        }, "abcdef", laidOut: false);

        Assert.Equal(3, mid);
        Assert.Equal(6, clampedHigh);   // clamped to text length
        Assert.Equal(0, clampedLow);
    }

    [Fact]
    public void TextWrappingMapsToWordWrap()
    {
        var (wrapOn, wrapOff) = On(ed =>
        {
            ed.TextWrapping = TextWrapping.Wrap;
            var on = (ed.WordWrap, ed.TextWrapping);
            ed.TextWrapping = TextWrapping.NoWrap;
            var off = (ed.WordWrap, ed.TextWrapping);
            return (on, off);
        }, "x", laidOut: false);

        Assert.True(wrapOn.WordWrap);
        Assert.Equal(TextWrapping.Wrap, wrapOn.TextWrapping);
        Assert.False(wrapOff.WordWrap);
        Assert.Equal(TextWrapping.NoWrap, wrapOff.TextWrapping);
    }

    [Fact]
    public void CaretBrushRoundTrips()
    {
        var brush = On(ed =>
        {
            ed.CaretBrush = Brushes.Red;
            return ed.CaretBrush;
        }, "x", laidOut: false);
        Assert.Equal(Brushes.Red, brush);
    }

    // ===== 1.8 (partial) the edit event carries real offsets =====

    [Fact]
    public void TextEditedReportsOffsetInsertionAndRemoval()
    {
        var events = On(ed =>
        {
            var seen = new System.Collections.Generic.List<(int off, int add, int rem)>();
            ed.TextEdited += (o, a, r) => seen.Add((o, a, r));
            ed.Document.Insert(2, "XY");           // insert 2 at offset 2
            ed.Document.Remove(0, 1);              // remove 1 at offset 0
            return seen;
        }, "abcdef", laidOut: false);

        Assert.Equal((2, 2, 0), events[0]);
        Assert.Equal((0, 0, 1), events[1]);
    }

    [Fact]
    public void GetLineTextReturnsTheLineWithoutTerminator()
    {
        var (l0, l1, outOfRange) = On(ed =>
            (ed.GetLineText(0), ed.GetLineText(1), ed.GetLineText(99)),
            "first\nsecond", laidOut: false);

        Assert.Equal("first", l0);
        Assert.Equal("second", l1);
        Assert.Equal("", outOfRange);
    }
}
