using System;
using System.Buffers.Binary;
using System.IO;
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
        // The real route: TextEditor.Paste() executes the Paste command against the
        // TextArea (no CanExecute first - the menu route runs straight to Executed,
        // which tunnels down through the editor on its way there). This test proves
        // the PreviewExecuted hook; the PreviewCanExecute hook, which only the key
        // gestures consult, is PasteCommandIsEnabledForAnImageOnlyClipboard's job.
        // Laid out, so the TextArea sits inside the editor's template as it does in
        // the app. Only the clipboard read is substituted.
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

    // ----- the clipboard's real shapes (#7, found dogfooding) -----
    //
    // Every paste above uses a Bgra32 bitmap with full alpha, which is not what a
    // screenshot is. A Windows screenshot tool leaves a 32-bit DIB whose fourth
    // byte is padding and always 0; WPF reads it back as an InteropBitmap in Bgra32
    // and takes that byte as alpha, so the PNG encoded from it was transparent on
    // every pixel and the document showed an empty frame. Other programs (Chrome,
    // Office) offer a registered "PNG" format as well. ClipboardFixtures builds
    // those shapes in memory. Nothing here reads or writes the real clipboard: the
    // one case driven through ed.Paste() is one the editor's own hook takes both
    // before and after this fix, so AvalonEdit's text paste, which does read the
    // real clipboard, is never reached; every other case calls TryPasteImage or
    // asks CanExecute of the editor, neither of which reaches it.

    /// <summary>BGRA bytes for a small picture: every pixel a different colour, none
    /// of them black, and its fourth byte whatever <paramref name="alpha"/> says for
    /// its index.</summary>
    private static byte[] Bgra(int pixels, Func<int, byte> alpha)
    {
        var bytes = new byte[pixels * 4];
        for (var i = 0; i < pixels; i++)
        {
            bytes[i * 4] = (byte)(0x10 + 0x20 * i);
            bytes[i * 4 + 1] = (byte)(0xF0 - 0x10 * i);
            bytes[i * 4 + 2] = (byte)(0x40 + 0x08 * i);
            bytes[i * 4 + 3] = alpha(i);
        }
        return bytes;
    }

    /// <summary>The picture a paste put in the text, as bytes. The text must be
    /// <paramref name="before"/> and then one image fragment, nothing after it.</summary>
    private static byte[] PastedBytes(string text, string before)
    {
        Assert.StartsWith(before + PngPrefix, text);
        Assert.EndsWith(")", text);
        return Convert.FromBase64String(text[(before + PngPrefix).Length..^1]);
    }

    /// <summary>A PNG's pixels as Bgra32, read back by WPF's own PNG decoder, which
    /// also proves the bytes are a PNG at all.</summary>
    private static byte[] DecodeToBgra(byte[] png)
    {
        var frame = new PngBitmapDecoder(new MemoryStream(png), BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad).Frames[0];
        var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[bgra.PixelWidth * bgra.PixelHeight * 4];
        bgra.CopyPixels(pixels, bgra.PixelWidth * 4, 0);
        return pixels;
    }

    /// <summary>A real PNG that neither re-encoding path could reproduce byte for
    /// byte: interlaced, which WPF's encoder never is unless asked, and of a picture
    /// no bitmap in these tests shows.</summary>
    private static byte[] InterlacedPng()
    {
        var encoder = new PngBitmapEncoder { Interlace = PngInterlaceOption.On };
        encoder.Frames.Add(BitmapFrame.Create(
            BitmapSource.Create(3, 1, 96, 96, PixelFormats.Bgra32, null, Bgra(3, _ => 0xFF), 12)));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    [Fact]
    public void PastedScreenshotKeepsItsColoursAndIsOpaque()
    {
        // The dogfood defect, through the real Paste command: a screenshot-shaped
        // bitmap (alpha 0 on every pixel, colour on every pixel) must paste as the
        // same colours, opaque.
        var shot = Bgra(6, _ => 0x00);
        var (type, format, pasted) = On(ed =>
        {
            var image = ClipboardFixtures.Dib(3, 2, shot);
            var data = new DataObject();
            data.SetImage(image);
            ed.ClipboardSource = () => data;
            ed.CaretOffset = 3;
            ed.Paste();
            return (image.GetType().Name, image.Format, DecodeToBgra(PastedBytes(ed.Text, "abc")));
        }, "abc", laidOut: true);

        Assert.Equal("InteropBitmap", type);           // the fixture has the clipboard's shape,
        Assert.Equal(PixelFormats.Bgra32, format);     // padding byte read as alpha and all
        var opaque = (byte[])shot.Clone();
        for (var i = 3; i < opaque.Length; i += 4) opaque[i] = 0xFF;
        Assert.Equal(opaque, pasted);
    }

    [Fact]
    public void PastedPictureWithRealTransparencyKeepsItsAlpha()
    {
        // Alpha that is not zero everywhere is real alpha, and it survives exactly,
        // the zero pixels included (a rule of "any zero means padding" would have
        // made those opaque).
        byte[] alphas = [0x00, 0x40, 0x80, 0xC0, 0xFF, 0x00];
        var picture = Bgra(6, i => alphas[i]);
        var pasted = On(ed =>
        {
            var data = new DataObject();
            data.SetImage(ClipboardFixtures.Dib(3, 2, picture));
            Assert.True(ed.TryPasteImage(data));
            return DecodeToBgra(PastedBytes(ed.Text, ""));
        }, "", laidOut: false);

        Assert.Equal(picture, pasted);
    }

    [Theory]
    [InlineData(true)]    // a MemoryStream: what WPF hands over for a registered format read through OLE
    [InlineData(false)]   // a byte[]: what an in-process data object may carry instead
    public void ClipboardPngGoesInByteForByte(bool asStream)
    {
        // Chrome, Office and others offer a registered "PNG" format beside the
        // bitmap: the picture as its owner encoded it. Those bytes go in untouched.
        // The bitmap alongside is a different picture, so a fallback to it shows.
        var (png, text) = On(ed =>
        {
            var bytes = InterlacedPng();
            var data = new DataObject();
            data.SetImage(TinyBitmap());
            data.SetData("PNG", asStream ? new MemoryStream(bytes) : (object)bytes);
            Assert.True(ed.TryPasteImage(data));
            return (bytes, ed.Text);
        }, "", laidOut: false);

        Assert.Equal(png, PastedBytes(text, ""));
    }

    [Fact]
    public void ClipboardPngReadThroughOleArrivesAsAStreamAndGoesInAsIs()
    {
        // The route another program's PNG takes: WPF's OLE converter fetches the
        // format into an HGLOBAL and hands it back as a MemoryStream. ComOnly sends
        // an in-memory data object that way. PNG only, no bitmap: the registered
        // format is a picture on its own.
        var (arrived, png, text) = On(ed =>
        {
            var bytes = InterlacedPng();
            var owner = new DataObject();
            owner.SetData("PNG", new MemoryStream(bytes));
            var data = new DataObject(new ClipboardFixtures.ComOnly(owner));
            var type = data.GetData("PNG")?.GetType();
            Assert.True(ed.TryPasteImage(data));
            return (type, bytes, ed.Text);
        }, "", laidOut: false);

        Assert.Equal(typeof(MemoryStream), arrived);
        Assert.Equal(png, PastedBytes(text, ""));
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46 })]  // a JPEG under the PNG name
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00 })]  // CR LF turned to LF: what the signature exists to catch
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A })]                    // seven of the eight signature bytes
    [InlineData(new byte[0])]                                                                   // nothing at all
    public void ClipboardPngWithoutTheSignatureFallsBackToTheBitmap(byte[] notPng)
    {
        var picture = Bgra(6, _ => 0xFF);
        var pasted = On(ed =>
        {
            var data = new DataObject();
            data.SetImage(ClipboardFixtures.Dib(3, 2, picture));
            data.SetData("PNG", new MemoryStream(notPng));
            Assert.True(ed.TryPasteImage(data));
            return DecodeToBgra(PastedBytes(ed.Text, ""));
        }, "", laidOut: false);

        Assert.Equal(picture, pasted);
    }

    [Fact]
    public void PngOnlyClipboardIsAPicture()
    {
        // No Bitmap, only the registered PNG. The Paste command must be enabled for
        // it (Ctrl+V and Shift+Insert ask first), and the paste must insert it.
        var (enabled, png, text) = On(ed =>
        {
            var bytes = InterlacedPng();
            ed.ClipboardSource = () => new DataObject("PNG", new MemoryStream(bytes));
            var canPaste = ApplicationCommands.Paste.CanExecute(null, ed);
            ed.CaretOffset = 3;
            var handled = ed.TryPasteImage(ed.ClipboardSource()!);
            return (canPaste, bytes, handled ? ed.Text : "(not handled) " + ed.Text);
        }, "abc", laidOut: false);

        Assert.True(enabled);
        Assert.Equal(png, PastedBytes(text, "abc"));
    }

    [Fact]
    public void TextStillWinsOverAClipboardPng()
    {
        var (handled, enabled, text) = On(ed =>
        {
            var data = new DataObject("PNG", new MemoryStream(InterlacedPng()));
            data.SetText("caption");
            ed.ClipboardSource = () => data;
            return (ed.TryPasteImage(data), ApplicationCommands.Paste.CanExecute(null, ed), ed.Text);
        }, "abc", laidOut: false);

        Assert.False(handled);
        Assert.False(enabled);         // our hook declines, leaving the paste to AvalonEdit's text path
        Assert.Equal("abc", text);
    }

    [Fact]
    public void PngOnlyClipboardWithoutTheSignatureIsNotPasted()
    {
        // Nothing usable and no bitmap to fall back to: the paste is declined and the
        // text left alone, rather than a broken picture going in.
        var (handled, text) = On(ed =>
            (ed.TryPasteImage(new DataObject("PNG", new MemoryStream([0xFF, 0xD8, 0xFF, 0xE0]))), ed.Text),
            "abc", laidOut: false);

        Assert.False(handled);
        Assert.Equal("abc", text);
    }

    [Fact]
    public void AFailingClipboardPngReadFallsBackToTheBitmap()
    {
        // Reading the PNG is new, so a read that throws must not cost the paste the
        // bitmap path it always had.
        var picture = Bgra(6, _ => 0xFF);
        var pasted = On(ed =>
        {
            var inner = new DataObject();
            inner.SetImage(ClipboardFixtures.Dib(3, 2, picture));
            inner.SetData("PNG", new MemoryStream(InterlacedPng()));
            Assert.True(ed.TryPasteImage(new ClipboardFixtures.PngReadFails(inner)));
            return DecodeToBgra(PastedBytes(ed.Text, ""));
        }, "", laidOut: false);

        Assert.Equal(picture, pasted);
    }

    [Fact]
    public void TheSameClipboardPngPastesTwice()
    {
        // A second paste of the same data must insert the same picture: reading the
        // stream the first time does not use it up.
        var (png, text) = On(ed =>
        {
            var bytes = InterlacedPng();
            var data = new DataObject("PNG", new MemoryStream(bytes));
            Assert.True(ed.TryPasteImage(data));
            Assert.True(ed.TryPasteImage(data));
            return (bytes, ed.Text);
        }, "", laidOut: false);

        var once = ImagePaste.MarkdownFor(png);
        Assert.Equal(once + once, text);
    }

    // ----- ImagePaste's alpha rule and PNG pass-through, on their own -----

    /// <summary>Where each WPF format with an alpha channel keeps it: bytes a pixel,
    /// the alpha's byte offset within the pixel, and its width in bytes. In all six
    /// the three colour channels come first, each as wide as the alpha. The table is
    /// checked against WPF itself by <see cref="AlphaSitsWhereTheseTestsSayItDoes"/>.</summary>
    private static (PixelFormat Format, int PixelBytes, int Offset, int Width) AlphaFormat(string name) => name switch
    {
        "Bgra32" => (PixelFormats.Bgra32, 4, 3, 1),
        "Pbgra32" => (PixelFormats.Pbgra32, 4, 3, 1),
        "Rgba64" => (PixelFormats.Rgba64, 8, 6, 2),
        "Prgba64" => (PixelFormats.Prgba64, 8, 6, 2),
        "Rgba128Float" => (PixelFormats.Rgba128Float, 16, 12, 4),
        "Prgba128Float" => (PixelFormats.Prgba128Float, 16, 12, 4),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    /// <summary>Four pixels in one of the alpha formats: colour channels non-zero and
    /// different on every pixel (real floats in the float formats), the alpha slot
    /// holding the low bytes of <paramref name="alphaBits"/>(pixel), little-endian.</summary>
    private static byte[] AlphaPixels(string name, Func<int, uint> alphaBits)
    {
        var (_, size, offset, width) = AlphaFormat(name);
        var bytes = new byte[4 * size];
        for (var p = 0; p < 4; p++)
        {
            var pixel = bytes.AsSpan(p * size, size);
            for (var c = 0; c < 3; c++)
            {
                var channel = pixel.Slice(c * width, width);
                switch (width)
                {
                    case 1: channel[0] = (byte)(0x10 + 0x20 * p + 0x08 * c); break;
                    case 2: BinaryPrimitives.WriteUInt16LittleEndian(channel, (ushort)(0x1000 + 0x2000 * p + 0x0300 * c)); break;
                    default: BinaryPrimitives.WriteSingleLittleEndian(channel, 0.1f + 0.2f * p + 0.03f * c); break;
                }
            }
            var a = alphaBits(p);
            for (var b = 0; b < width; b++) pixel[offset + b] = (byte)(a >> (8 * b));
        }
        return bytes;
    }

    /// <summary>Full alpha's bits in a format's alpha slot.</summary>
    private static uint FullAlpha(int width) => width switch
    {
        1 => 0xFFu,
        2 => 0xFFFFu,
        _ => BitConverter.SingleToUInt32Bits(1f),
    };

    [Theory]
    [InlineData("Bgra32")]
    [InlineData("Pbgra32")]
    [InlineData("Rgba64")]
    [InlineData("Prgba64")]
    [InlineData("Rgba128Float")]
    [InlineData("Prgba128Float")]
    public void AlphaSitsWhereTheseTestsSayItDoes(string name)
    {
        // WPF converts a pixel whose alpha is 0x80; the slot the table names must hold
        // that alpha. The colours (0x10, 0xF0, 0x40) cannot land on 0x80/255 in any of
        // these formats, premultiplied or linear, so the slot is not found by chance.
        var (format, size, offset, width) = AlphaFormat(name);
        var pixel = On(_ =>
        {
            var one = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0x10, 0xF0, 0x40, 0x80 }, 4);
            var bytes = new byte[size];
            new FormatConvertedBitmap(one, format, null, 0).CopyPixels(bytes, size, 0);
            return bytes;
        }, "", laidOut: false);

        var slot = pixel.AsSpan(offset, width);
        var alpha = width switch
        {
            1 => slot[0] / 255.0,
            2 => BinaryPrimitives.ReadUInt16LittleEndian(slot) / 65535.0,
            _ => BinaryPrimitives.ReadSingleLittleEndian(slot),
        };
        Assert.Equal(0x80 / 255.0, alpha, 3);
    }

    [Fact]
    public void TheSixAlphaFormatsAreEveryWpfFormatThatKeepsAlpha()
    {
        // "Any format with an alpha channel WPF can hand over": every WPF format is
        // tried with a pixel whose alpha is 0x80, there and back, and the formats that
        // keep that alpha must be exactly the six the tests above name and
        // ForEncoding handles. Indexed formats are left out, as the rule leaves them
        // out: whatever alpha they have lives in a palette, not in the pixels.
        var keepsAlpha = On(_ =>
        {
            var one = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0x10, 0xF0, 0x40, 0x80 }, 4);
            var found = new List<string>();
            foreach (var property in typeof(PixelFormats).GetProperties(
                         System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
            {
                if (property.Name == "Default" || property.Name.StartsWith("Indexed", StringComparison.Ordinal)) continue;
                var format = (PixelFormat)property.GetValue(null)!;
                var back = new FormatConvertedBitmap(new FormatConvertedBitmap(one, format, null, 0), PixelFormats.Bgra32, null, 0);
                var pixel = new byte[4];
                back.CopyPixels(pixel, 4, 0);
                if (pixel[3] != 0xFF) found.Add(property.Name);
            }
            found.Sort(StringComparer.Ordinal);
            return found;
        }, "", laidOut: false);

        Assert.Equal(new[] { "Bgra32", "Pbgra32", "Prgba128Float", "Prgba64", "Rgba128Float", "Rgba64" }, keepsAlpha);
    }

    [Theory]
    [InlineData("Bgra32")]
    [InlineData("Pbgra32")]
    [InlineData("Rgba64")]
    [InlineData("Prgba64")]
    [InlineData("Rgba128Float")]
    [InlineData("Prgba128Float")]
    public void ForEncodingMakesAllZeroAlphaOpaqueAndKeepsTheColour(string name)
    {
        // Alpha zero on every pixel is a padding byte, not transparency (Chromium's
        // rule), in every format with an alpha channel: the colour channels come out
        // exactly as they went in, the alpha is full, and the format is unchanged.
        var (format, size, _, width) = AlphaFormat(name);
        var source = AlphaPixels(name, _ => 0);
        var (same, outFormat, result) = On(_ =>
        {
            var image = BitmapSource.Create(2, 2, 96, 96, format, null, source, 2 * size);
            var prepared = ImagePaste.ForEncoding(image);
            var bytes = new byte[4 * size];
            prepared.CopyPixels(bytes, 2 * size, 0);
            return (ReferenceEquals(prepared, image), prepared.Format, bytes);
        }, "", laidOut: false);

        Assert.False(same);
        Assert.Equal(format, outFormat);
        Assert.Equal(AlphaPixels(name, _ => FullAlpha(width)), result);
    }

    [Theory]
    [InlineData("Bgra32", 0x01u)]
    [InlineData("Bgra32", 0x80u)]
    [InlineData("Pbgra32", 0x01u)]
    [InlineData("Rgba64", 0x0001u)]           // only the low byte of the 16-bit alpha
    [InlineData("Rgba64", 0x0100u)]           // only the high byte
    [InlineData("Prgba64", 0x0001u)]
    [InlineData("Rgba128Float", 0x00000001u)] // the smallest float above zero
    [InlineData("Rgba128Float", 0x3F800000u)] // 1.0: only the top two bytes set
    [InlineData("Prgba128Float", 0x00000001u)]
    public void ForEncodingLeavesAnyNonZeroAlphaAlone(string name, uint alphaBits)
    {
        // Real alpha on one pixel, however faint, and on the last pixel so the whole
        // bitmap is scanned first: it is left exactly as it is, the same instance.
        var (format, size, _, _) = AlphaFormat(name);
        var same = On(_ =>
        {
            var image = BitmapSource.Create(2, 2, 96, 96, format, null,
                AlphaPixels(name, p => p == 3 ? alphaBits : 0), 2 * size);
            return ReferenceEquals(ImagePaste.ForEncoding(image), image);
        }, "", laidOut: false);

        Assert.True(same);
    }

    [Theory]
    [InlineData("Bgr32")]
    [InlineData("Bgr24")]
    [InlineData("Gray8")]
    [InlineData("Rgb48")]
    [InlineData("Indexed8")]
    public void ForEncodingLeavesFormatsWithoutAlphaAlone(string name)
    {
        // Every byte zero, so Bgr32's fourth byte is zero too, and it is not alpha:
        // there is nothing to decide, and the bitmap comes back as it went in.
        var same = On(_ =>
        {
            var format = (PixelFormat)typeof(PixelFormats).GetProperty(name)!.GetValue(null)!;
            var stride = (2 * format.BitsPerPixel + 7) / 8;
            var palette = format == PixelFormats.Indexed8 ? BitmapPalettes.Gray256 : null;
            var image = BitmapSource.Create(2, 2, 96, 96, format, palette, new byte[stride * 2], stride);
            return ReferenceEquals(ImagePaste.ForEncoding(image), image);
        }, "", laidOut: false);

        Assert.True(same);
    }

    [Theory]
    [InlineData("Bgra32")]
    [InlineData("Pbgra32")]
    [InlineData("Rgba64")]
    [InlineData("Prgba64")]
    [InlineData("Rgba128Float")]
    [InlineData("Prgba128Float")]
    public void EncodePngOfAnAllZeroAlphaBitmapIsOpaque(string name)
    {
        // EncodePng applies the rule itself, whatever the format: every pixel of the
        // PNG it writes reads back fully opaque.
        var (format, size, _, _) = AlphaFormat(name);
        var decoded = On(_ => DecodeToBgra(ImagePaste.EncodePng(
            BitmapSource.Create(2, 2, 96, 96, format, null, AlphaPixels(name, _ => 0), 2 * size))),
            "", laidOut: false);

        for (var i = 3; i < decoded.Length; i += 4) Assert.Equal(0xFF, decoded[i]);
    }

    [Fact]
    public void EncodePngKeepsThePremultipliedColourOfAnAllZeroAlphaBitmap()
    {
        // A Pbgra32 bitmap with alpha 0 must not come out black: opaque is made by
        // writing full alpha, never by converting to a format without alpha, which
        // would un-premultiply the colour by dividing it by that zero.
        var source = Bgra(4, _ => 0x00);
        var decoded = On(_ => DecodeToBgra(ImagePaste.EncodePng(
            BitmapSource.Create(2, 2, 96, 96, PixelFormats.Pbgra32, null, source, 8))),
            "", laidOut: false);

        var opaque = (byte[])source.Clone();
        for (var i = 3; i < opaque.Length; i += 4) opaque[i] = 0xFF;
        Assert.Equal(opaque, decoded);
    }

    [Fact]
    public void AlphaIsAllZeroReadsOnlyTheAlphaChannel()
    {
        // Colour does not count; one alpha byte anywhere does, first pixel or last.
        byte[] colourOnly = [0x10, 0x20, 0x30, 0x00, 0xFF, 0xFF, 0xFF, 0x00];
        byte[] firstHasAlpha = [0x10, 0x20, 0x30, 0x01, 0xFF, 0xFF, 0xFF, 0x00];
        byte[] lastHasAlpha = [0x10, 0x20, 0x30, 0x00, 0xFF, 0xFF, 0xFF, 0x01];

        Assert.True(ImagePaste.AlphaIsAllZero(colourOnly, PixelFormats.Bgra32));
        Assert.True(ImagePaste.AlphaIsAllZero(colourOnly, PixelFormats.Pbgra32));
        Assert.False(ImagePaste.AlphaIsAllZero(firstHasAlpha, PixelFormats.Bgra32));
        Assert.False(ImagePaste.AlphaIsAllZero(lastHasAlpha, PixelFormats.Bgra32));
        // A format without alpha has none to be zero: Bgr32's fourth byte is padding.
        Assert.False(ImagePaste.AlphaIsAllZero(colourOnly, PixelFormats.Bgr32));
    }

    [Fact]
    public void AlphaIsAllZeroCountsNegativeZeroAsZero()
    {
        // -0 is a zero alpha in the float formats although its sign bit is set; the
        // smallest negative float is not.
        var negativeZero = AlphaPixels("Rgba128Float", _ => 0x80000000u);
        var negativeTiny = AlphaPixels("Rgba128Float", p => p == 3 ? 0x80000001u : 0x80000000u);

        Assert.True(ImagePaste.AlphaIsAllZero(negativeZero, PixelFormats.Rgba128Float));
        Assert.True(ImagePaste.AlphaIsAllZero(negativeZero, PixelFormats.Prgba128Float));
        Assert.False(ImagePaste.AlphaIsAllZero(negativeTiny, PixelFormats.Rgba128Float));
    }

    [Fact]
    public void UsablePngTakesAPngFromAStreamOrAnArray()
    {
        var png = On(_ => InterlacedPng(), "", laidOut: false);

        Assert.Equal(png, ImagePaste.UsablePng(new MemoryStream(png)));
        Assert.Equal(png, ImagePaste.UsablePng(png));
        // The whole stream, wherever its position was left: a second paste of the
        // same data reads the same picture.
        var read = new MemoryStream(png) { Position = png.Length };
        Assert.Equal(png, ImagePaste.UsablePng(read));
        // A MemoryStream can be a window on a larger buffer; only the window is the
        // PNG (GetBuffer would hand back the whole buffer).
        var buffer = new byte[png.Length + 7];
        png.CopyTo(buffer, 7);
        Assert.Equal(png, ImagePaste.UsablePng(new MemoryStream(buffer, 7, png.Length)));
        // The check is the signature and nothing more: eight bytes of it pass.
        byte[] signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        Assert.Equal(signature, ImagePaste.UsablePng(signature));
    }

    [Fact]
    public void UsablePngTurnsDownAnythingElse()
    {
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10];
        byte[] signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

        Assert.Null(ImagePaste.UsablePng(null));
        Assert.Null(ImagePaste.UsablePng(jpeg));
        Assert.Null(ImagePaste.UsablePng(new MemoryStream(jpeg)));
        Assert.Null(ImagePaste.UsablePng(signature[..7]));                        // one byte short
        Assert.Null(ImagePaste.UsablePng(Convert.ToBase64String(signature)));     // text, not the bytes
        Assert.Null(ImagePaste.UsablePng(new BufferedStream(new MemoryStream(signature)))); // only a MemoryStream is read
    }

    // ===== 3. Replace All (#5 F4): the planned edits land as one undo unit =====

    [Fact]
    public void ReplaceAllIsOneUndoUnit()
    {
        const string original = "cat one cat two cat";
        var (after, count, afterUndo, canUndo, afterRedo) = On(ed =>
        {
            var spec = FindEngine.Prepare("cat", FindEngine.Mode.Normal, false, false, "tiger");
            var n = ed.ApplyEdits(spec!.ReplaceAllEdits(ed.Text));
            var replaced = ed.Text;
            ed.Undo();                       // ONE undo
            var undone = ed.Text;
            var more = ed.CanUndo;           // nothing left of the replace to undo
            ed.Redo();
            return (replaced, n, undone, more, ed.Text);
        }, original, laidOut: false);

        Assert.Equal("tiger one tiger two tiger", after);
        Assert.Equal(3, count);
        Assert.Equal(original, afterUndo);
        Assert.False(canUndo);
        Assert.Equal("tiger one tiger two tiger", afterRedo);
    }

    [Fact]
    public void ApplyEditsHandlesGrowingShrinkingAndEmptyPlans()
    {
        // Edits are planned against the text as it was: a longer first replacement
        // must not shift the later ones (they are applied last to first), and an
        // empty plan touches neither the text nor the undo stack.
        var (mixed, emptyCount, emptyCanUndo) = On(ed =>
        {
            var spec = FindEngine.Prepare(@"(\w+)@", FindEngine.Mode.Regex, false, false, "[$1]");
            ed.ApplyEdits(spec!.ReplaceAllEdits(ed.Text));
            var text = ed.Text;
            ed.Undo();
            var n = ed.ApplyEdits(System.Array.Empty<FindEngine.Edit>());
            return (text, n, ed.CanUndo);
        }, "a@ bb@ c", laidOut: false);

        Assert.Equal("[a] [bb] c", mixed);
        Assert.Equal(0, emptyCount);
        Assert.False(emptyCanUndo);
    }
}
