using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MarkdownMidget.Source;

/// <summary>
/// The decisions behind pasting a clipboard image into the source view, kept apart
/// from the clipboard and the editor so they can be tested without either.
///
/// AvalonEdit's paste is text-only: it asks the clipboard for text, and when there
/// is none — a screenshot, a copied picture — it does nothing, silently. The
/// formatted view accepts the same paste and embeds the picture, and Insert ▸
/// Picture embeds too, so the consistent answer is the same markdown at the caret:
/// <c>![](data:image/png;base64,…)</c>. Not a file beside the document (Markdown
/// Monster's choice), because this editor embeds everywhere else.
/// </summary>
internal static class ImagePaste
{
    /// <summary>
    /// The registered clipboard format under which Chrome, Office and other programs
    /// offer a picture as a PNG file's bytes, beside the bitmap. Not every program
    /// offers one: Windows' Snipping Tool, for one, does not.
    /// </summary>
    public const string PngFormat = "PNG";

    /// <summary>
    /// Whether a paste is ours to handle: an image and no text. Text always wins — a
    /// copy from a browser or a document often carries a picture AND its text, and
    /// someone pasting into a text editor wants the text, which is what AvalonEdit
    /// pastes unaided.
    /// </summary>
    public static bool ShouldHandle(bool hasText, bool hasImage) => hasImage && !hasText;

    /// <summary>The fragment for a pasted image, already encoded as PNG. The alt
    /// text is empty because a clipboard image has no name to put there.</summary>
    public static string MarkdownFor(byte[] png) => ImageMarkdown.Fragment(string.Empty, "image/png", png);

    /// <summary>
    /// The clipboard's own PNG, when <paramref name="data"/> (what reading
    /// <see cref="PngFormat"/> returned) is one to insert as it is: a
    /// <see cref="MemoryStream"/>, which is how WPF hands over a registered format
    /// another program put there as raw bytes, or a byte array, which is how it
    /// arrives when the data object holding it kept it as one, in this process or in
    /// another .NET program (WPF reads that back as an array), starting with the
    /// eight-byte PNG signature. Those bytes go
    /// into the document untouched, with no decode and no re-encode: the picture
    /// exactly as its owner encoded it. Anything else (null, another kind of stream,
    /// bytes without the signature) is null, and the caller falls back to the bitmap.
    /// </summary>
    public static byte[]? UsablePng(object? data)
    {
        var bytes = data switch
        {
            // ToArray copies the stream's whole content whatever its position, so a
            // second paste of the same data reads the same picture.
            MemoryStream stream => stream.ToArray(),
            byte[] array => array,
            _ => null,
        };
        return bytes is not null && bytes.AsSpan().StartsWith(PngSignature) ? bytes : null;
    }

    /// <summary>The eight bytes every PNG file starts with.</summary>
    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// Encode a clipboard bitmap as PNG. This is the path for a clipboard with no
    /// usable PNG of its own (<see cref="UsablePng"/>), typically a screenshot tool's,
    /// whose DIB WPF hands over as a <see cref="BitmapSource"/>. The bitmap goes
    /// through <see cref="ForEncoding"/> first, so a 32-bit DIB whose fourth byte is
    /// padding, zero on every pixel as a screenshot tool leaves it, comes out opaque
    /// rather than invisible. PNG is lossless, which suits the screenshots that are
    /// the usual case, and every renderer of the document reads it.
    /// </summary>
    public static byte[] EncodePng(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(ForEncoding(image)));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>
    /// The bitmap to encode for <paramref name="image"/>: <paramref name="image"/>
    /// itself, or, when its alpha channel does not hold real alpha
    /// (<see cref="MustBeMadeOpaque"/> decides), an opaque copy: the colour kept
    /// exactly, the alpha set to full, the pixel format unchanged. A format without an
    /// alpha channel (Bgr32, Bgr24, grey, indexed…) is returned as it is.
    ///
    /// This is what makes a pasted screenshot visible. A screenshot tool (Windows'
    /// Snipping Tool, for one) leaves a 32-bit BI_RGB DIB whose fourth byte is
    /// padding, 0 on every pixel, and WPF reads it back as Bgra32 with that byte as
    /// alpha, so encoding it as it stands gives a picture that is transparent
    /// everywhere. The formatted view pastes the same screenshot opaque because
    /// Chromium tests the bitmap it reads from the clipboard, and
    /// <see cref="MustBeMadeOpaque"/> applies that test to Bgra32, the format WPF
    /// reads a 32-bit clipboard DIB back as, so the two views paste the same picture.
    ///
    /// Opaque is made by writing full alpha into a copy of the pixels, in the same
    /// format, and never by converting to a format without alpha: a conversion from a
    /// premultiplied format divides the colour by the alpha, and with an alpha of 0
    /// the picture would come out black.
    /// </summary>
    public static BitmapSource ForEncoding(BitmapSource image)
    {
        if (AlphaSlotOf(image.Format) is not { } slot) return image;
        var stride = checked(image.PixelWidth * slot.PixelBytes);
        var pixels = new byte[checked(stride * image.PixelHeight)];
        image.CopyPixels(pixels, stride, 0);
        if (!MustBeMadeOpaque(pixels, image.Format)) return image;
        for (var p = slot.Offset; p < pixels.Length; p += slot.PixelBytes)
            slot.Full.CopyTo(pixels, p);
        var opaque = BitmapSource.Create(image.PixelWidth, image.PixelHeight, image.DpiX, image.DpiY,
            image.Format, null, pixels, stride);
        opaque.Freeze();
        return opaque;
    }

    /// <summary>
    /// Whether <paramref name="pixels"/> (whole pixels in <paramref name="format"/>,
    /// rows packed with no padding) are to be encoded opaque because their alpha
    /// channel does not hold real alpha. One pass, stopping at the first pixel that
    /// settles the answer. False for a format without an alpha channel: whatever its
    /// spare bits hold, they are not alpha.
    ///
    /// <para><b>Bgra32 and Pbgra32</b> are judged by Chromium's test: opaque when any
    /// pixel has a blue, green or red byte greater than its alpha byte, otherwise left
    /// alone. Bgra32 is the format WPF reads a 32-bit clipboard DIB back as; Pbgra32
    /// has the same four bytes a pixel and is judged the same way. The test is cited
    /// from Chromium's <c>ui/base/clipboard/clipboard_win.cc</c> (the
    /// premultiplied-validity check, <c>BitmapHasInvalidPremultipliedColors</c>, which
    /// Chromium applies to a 32-bit bitmap read from the clipboard); it has not been
    /// observed in a running browser. Chromium's reasoning: Windows bitmaps with alpha
    /// are premultiplied, and a premultiplied colour never exceeds its alpha, so a
    /// colour that does shows the fourth byte is not alpha. The scan stops at the
    /// first such pixel. The bytes are judged that way whichever of the two formats
    /// WPF labels them, because a clipboard DIB carries no flag saying straight or
    /// premultiplied: a straight-alpha picture whose colour exceeds its alpha is
    /// flattened, as Chromium flattens it. A bitmap that is black and alpha 0 on every
    /// pixel has no colour above its alpha and stays transparent; Chromium's own
    /// comment names that as the case its test gets wrong, and it is matched here
    /// rather than improved on, so the two views agree.</para>
    ///
    /// <para><b>Rgba64, Prgba64, Rgba128Float and Prgba128Float</b>, which a clipboard
    /// DIB never arrives in (a DIB has at most 32 bits a pixel), keep a conservative
    /// rule that is not Chromium's: opaque only when the alpha is zero on every pixel,
    /// the colour not looked at, which makes opaque only a bitmap that would otherwise
    /// be invisible. The scan stops at the first pixel with any alpha at all. In the
    /// float formats −0 counts as zero.</para>
    /// </summary>
    public static bool MustBeMadeOpaque(ReadOnlySpan<byte> pixels, PixelFormat format) =>
        AlphaSlotOf(format) is { } slot
        && (slot.ByChromiumsTest ? AnyColourAboveAlpha(pixels) : AllZero(pixels, slot));

    /// <summary>Chromium's test on Bgra32 or Pbgra32 bytes (blue, green, red, alpha):
    /// whether any pixel has a colour byte greater than its alpha byte. Stops at the
    /// first pixel that has.</summary>
    private static bool AnyColourAboveAlpha(ReadOnlySpan<byte> pixels)
    {
        for (var p = 0; p + 3 < pixels.Length; p += 4)
        {
            var alpha = pixels[p + 3];
            if (pixels[p] > alpha || pixels[p + 1] > alpha || pixels[p + 2] > alpha) return true;
        }
        return false;
    }

    /// <summary>The all-zero rule, for the wide formats: whether every pixel's alpha
    /// is zero. Stops at the first pixel with any alpha at all.</summary>
    private static bool AllZero(ReadOnlySpan<byte> pixels, AlphaSlot slot)
    {
        var top = slot.Width - 1;
        for (var p = slot.Offset; p + top < pixels.Length; p += slot.PixelBytes)
        {
            // Little-endian, so the top byte is the one holding a float's sign bit,
            // which the mask leaves out.
            var any = pixels[p + top] & slot.TopMask;
            for (var b = 0; b < top; b++) any |= pixels[p + b];
            if (any != 0) return false;
        }
        return true;
    }

    /// <summary>Where a format keeps its alpha: bytes a pixel; the alpha's byte
    /// offset within the pixel and its width in bytes, little-endian; the mask for
    /// its top byte; full alpha's bytes; and whether the format is judged by
    /// Chromium's test (<see cref="AnyColourAboveAlpha"/>) rather than by the
    /// all-zero rule (<see cref="AllZero"/>).</summary>
    private sealed record AlphaSlot(int PixelBytes, int Offset, int Width, int TopMask, byte[] Full, bool ByChromiumsTest);

    // WPF's six formats with an alpha channel, in each of which the three colour
    // channels come first and the alpha, as wide as each of them, last. A 32-bit
    // clipboard DIB arrives as Bgra32; the wide formats never come from a DIB.
    private static readonly AlphaSlot Alpha8 = new(4, 3, 1, 0xFF, [0xFF], ByChromiumsTest: true);
    private static readonly AlphaSlot Alpha16 = new(8, 6, 2, 0xFF, [0xFF, 0xFF], ByChromiumsTest: false);
    private static readonly AlphaSlot AlphaFloat = new(16, 12, 4, 0x7F, BitConverter.GetBytes(1f), ByChromiumsTest: false);

    private static AlphaSlot? AlphaSlotOf(PixelFormat format) =>
        format == PixelFormats.Bgra32 || format == PixelFormats.Pbgra32 ? Alpha8
        : format == PixelFormats.Rgba64 || format == PixelFormats.Prgba64 ? Alpha16
        : format == PixelFormats.Rgba128Float || format == PixelFormats.Prgba128Float ? AlphaFloat
        : null;
}
