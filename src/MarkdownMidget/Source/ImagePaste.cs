using System.IO;
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
    /// Encode a clipboard bitmap as PNG. Whatever the clipboard held (a DIB, a GDI
    /// bitmap), WPF hands it over as a <see cref="BitmapSource"/>; PNG is lossless,
    /// which suits the screenshots that are the usual case, and every renderer of
    /// the document reads it.
    /// </summary>
    public static byte[] EncodePng(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
