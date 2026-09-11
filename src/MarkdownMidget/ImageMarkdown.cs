using System;
using System.IO;

namespace MarkdownMidget;

/// <summary>
/// The one place the markdown for an embedded picture is spelled out. Insert ▸
/// Picture, a picture pasted into the source view and a picture file dropped on
/// either view all build their fragment here, so the three cannot drift apart:
/// the shape is <c>![alt](data:mime;base64,…)</c>, the image carried inside the
/// document rather than linked to a file beside it.
/// </summary>
internal static class ImageMarkdown
{
    /// <summary>The alt text Insert ▸ Picture gives a file: its name without the
    /// extension. A dropped file gets the same, so the two produce the same markdown.</summary>
    public static string AltText(string path) => Path.GetFileNameWithoutExtension(path);

    /// <summary>
    /// The MIME type Insert ▸ Picture writes into the data URI, by extension. The
    /// raster entries are the formats a dropped file is sniffed for
    /// (<see cref="DropRouting.SniffImageMime"/>); SVG is picked here by extension
    /// only, since XML has no signature to sniff.
    /// </summary>
    public static string MimeForImage(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        ".svg" => "image/svg+xml",
        _ => "application/octet-stream",
    };

    /// <summary>
    /// The image fragment for <paramref name="bytes"/> of type <paramref name="mime"/>.
    /// Embedding as a base64 data URI is deliberate: it renders inside the sandboxed
    /// WebView (which can't load local file: paths) and travels with the markdown.
    /// This bloats the document by design.
    /// </summary>
    public static string Fragment(string alt, string mime, byte[] bytes) =>
        $"![{alt}](data:{mime};base64,{Convert.ToBase64String(bytes)})";
}
