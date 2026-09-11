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
    /// another program put there, or a byte array, which an in-process data object
    /// may hold instead, starting with the eight-byte PNG signature. Those bytes go
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
    /// The bitmap to encode for <paramref name="image"/>. In a format with an alpha
    /// channel, alpha that is zero on EVERY pixel is taken to mean opaque: the colour
    /// is kept exactly and the alpha set to full. That is Chromium's rule, and the
    /// reason the formatted view pastes a screenshot correctly: a screenshot tool
    /// (Windows' Snipping Tool, for one) leaves a 32-bit BI_RGB DIB whose fourth byte
    /// is padding, 0 on every pixel, and WPF reads it back as Bgra32 with that byte
    /// as alpha, so encoding it as it stands gives a picture that is transparent
    /// everywhere. A bitmap with any non-zero alpha, however faint and on however few
    /// pixels, has real transparency and is returned as it is; so is a format without
    /// an alpha channel (Bgr32, Bgr24, grey, indexed…).
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
        if (!AllZero(pixels, slot)) return image;
        for (var p = slot.Offset; p < pixels.Length; p += slot.PixelBytes)
            slot.Full.CopyTo(pixels, p);
        var opaque = BitmapSource.Create(image.PixelWidth, image.PixelHeight, image.DpiX, image.DpiY,
            image.Format, null, pixels, stride);
        opaque.Freeze();
        return opaque;
    }

    /// <summary>
    /// Whether every pixel in <paramref name="pixels"/> (whole pixels in
    /// <paramref name="format"/>, rows packed with no padding) has an alpha of zero.
    /// False for a format without an alpha channel: whatever its spare bits hold,
    /// they are not alpha. One pass, stopping at the first pixel with any alpha at
    /// all. In the float formats −0 counts as zero.
    /// </summary>
    public static bool AlphaIsAllZero(ReadOnlySpan<byte> pixels, PixelFormat format) =>
        AlphaSlotOf(format) is { } slot && AllZero(pixels, slot);

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
    /// its top byte; and full alpha's bytes.</summary>
    private sealed record AlphaSlot(int PixelBytes, int Offset, int Width, int TopMask, byte[] Full);

    // WPF's six formats with an alpha channel, in each of which the three colour
    // channels come first and the alpha, as wide as each of them, last.
    private static readonly AlphaSlot Alpha8 = new(4, 3, 1, 0xFF, [0xFF]);
    private static readonly AlphaSlot Alpha16 = new(8, 6, 2, 0xFF, [0xFF, 0xFF]);
    private static readonly AlphaSlot AlphaFloat = new(16, 12, 4, 0x7F, BitConverter.GetBytes(1f));

    private static AlphaSlot? AlphaSlotOf(PixelFormat format) =>
        format == PixelFormats.Bgra32 || format == PixelFormats.Pbgra32 ? Alpha8
        : format == PixelFormats.Rgba64 || format == PixelFormats.Prgba64 ? Alpha16
        : format == PixelFormats.Rgba128Float || format == PixelFormats.Prgba128Float ? AlphaFloat
        : null;
}
