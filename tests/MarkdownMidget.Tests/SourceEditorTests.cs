using System;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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

    // ===== 2. pasting a picture (#7) =====
    //
    // AvalonEdit's paste is text-only, so an image on the clipboard used to insert
    // nothing. The editor's TryPasteImage is the seam: it takes an IDataObject, so
    // these build one in memory and never touch the real clipboard. The last case
    // drives the actual Paste command through the injected clipboard read, which is
    // what proves the command is enabled at all for an image-only clipboard —
    // AvalonEdit's own CanPaste says no to anything without text.

    private const string PngPrefix = "![](data:image/png;base64,";

    /// <summary>A 2×2 opaque bitmap: the smallest thing the PNG encoder will take.
    /// Built on the calling (STA) thread, as a DispatcherObject must be.</summary>
    private static BitmapSource TinyBitmap()
    {
        var wb = new WriteableBitmap(2, 2, 96, 96, PixelFormats.Bgra32, null);
        var pixels = new byte[16];
        for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = 0x10; pixels[i + 1] = 0x80; pixels[i + 2] = 0xF0; pixels[i + 3] = 0xFF; }
        wb.WritePixels(new Int32Rect(0, 0, 2, 2), pixels, 8, 0);
        return wb;
    }

    /// <summary>A data object carrying only an image — what a screenshot leaves on
    /// the clipboard.</summary>
    private static DataObject ImageOnly()
    {
        var d = new DataObject();
        d.SetImage(TinyBitmap());
        return d;
    }

    [Fact]
    public void ImagePasteInsertsDataUri()
    {
        var (handled, after, caret, restored) = On(ed =>
        {
            ed.Select(4, 3);                                  // "def"
            var h = ed.TryPasteImage(ImageOnly());
            var text = ed.Text;
            var c = ed.CaretOffset;
            ed.Undo();
            return (h, text, c, ed.Text);
        }, "abc def", laidOut: false);

        Assert.True(handled);
        Assert.StartsWith("abc " + PngPrefix, after);
        Assert.EndsWith(")", after);
        Assert.DoesNotContain("def", after);                 // the selection was replaced
        // The payload is a real PNG, not the bitmap's bytes: check the signature.
        var payload = Convert.FromBase64String(after[("abc " + PngPrefix).Length..^1]);
        Assert.Equal(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }, payload[..4]);
        Assert.Equal(after.Length, caret);                   // caret after the picture, like a text paste
        Assert.Equal("abc def", restored);                   // ONE undo takes the whole paste back
    }

    [Fact]
    public void TextPasteIsUntouched()
    {
        var (textOnly, textAndImage, after) = On(ed =>
        {
            var text = new DataObject(DataFormats.UnicodeText, "plain");
            var both = ImageOnly();
            both.SetText("caption");                          // a browser copy: picture AND text
            return (ed.TryPasteImage(text), ed.TryPasteImage(both), ed.Text);
        }, "abc", laidOut: false);

        Assert.False(textOnly);
        Assert.False(textAndImage);
        Assert.Equal("abc", after);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, true, false)]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    public void ShouldHandleOnlyAnImageWithoutText(bool hasText, bool hasImage, bool expected) =>
        Assert.Equal(expected, ImagePaste.ShouldHandle(hasText, hasImage));

    [Fact]
    public void MarkdownForIsTheSameShapeAsInsertPicture()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        var md = ImagePaste.MarkdownFor(png);

        // Insert ▸ Picture builds its fragment through the same helper; a pasted
        // image is that fragment with no name to put in the alt text...
        Assert.Equal(ImageMarkdown.Fragment("", "image/png", png), md);
        // ...and the helper's shape is the one documents have always carried.
        Assert.Equal($"![](data:image/png;base64,{Convert.ToBase64String(png)})", md);
    }

    [Fact]
    public void EncodePngProducesAPng()
    {
        var bytes = On(_ => ImagePaste.EncodePng(TinyBitmap()), "", laidOut: false);
        Assert.True(bytes.Length > 8, "an encoded PNG has a signature and chunks");
        Assert.Equal(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }, bytes[..4]);
    }

    [Fact]
    public void ReadOnlyIgnoresImagePaste()
    {
        var (handled, after) = On(ed =>
        {
            ed.IsReadOnly = true;
            return (ed.TryPasteImage(ImageOnly()), ed.Text);
        }, "abc", laidOut: false);

        Assert.False(handled);
        Assert.Equal("abc", after);
    }

    [Fact]
    public void PasteCommandRoutesAnImageIntoTheEditor()
    {
        // The real route: TextEditor.Paste() asks the Paste command CanExecute, then
        // executes it against the TextArea, and both tunnel down through the editor
        // on their way there. Laid out, so the TextArea sits inside the editor's
        // template as it does in the app. Only the clipboard read is substituted.
        var after = On(ed =>
        {
            ed.ClipboardSource = () => ImageOnly();
            ed.CaretOffset = 3;
            ed.Paste();
            return ed.Text;
        }, "abc", laidOut: true);

        Assert.StartsWith("abc" + PngPrefix, after);
        Assert.EndsWith(")", after);
    }

    [Fact]
    public void PasteCommandIsEnabledForAnImageOnlyClipboard()
    {
        // AvalonEdit enables Paste only when the clipboard has text, so this is the
        // assertion that the editor's own CanExecute hook is wired and load-bearing.
        // Asked of the editor (not the TextArea), the route never reaches AvalonEdit's
        // binding, which keeps the answer independent of the real clipboard — so
        // the text-only case must come back false from OUR hook declining, too.
        var (image, text) = On(ed =>
        {
            ed.ClipboardSource = () => ImageOnly();
            var forImage = ApplicationCommands.Paste.CanExecute(null, ed);
            ed.ClipboardSource = () => new DataObject(DataFormats.UnicodeText, "plain");
            var forText = ApplicationCommands.Paste.CanExecute(null, ed);
            return (forImage, forText);
        }, "abc", laidOut: false);

        Assert.True(image);
        Assert.False(text);
    }
}
