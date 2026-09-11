using System;

namespace MarkdownMidget;

/// <summary>
/// The one place the markdown for an embedded picture is spelled out. Insert ▸
/// Picture and a picture pasted into the source view both build their fragment
/// here, so the two cannot drift apart: the shape is
/// <c>![alt](data:mime;base64,…)</c>, the image carried inside the document rather
/// than linked to a file beside it.
/// </summary>
internal static class ImageMarkdown
{
    /// <summary>
    /// The image fragment for <paramref name="bytes"/> of type <paramref name="mime"/>.
    /// Embedding as a base64 data URI is deliberate: it renders inside the sandboxed
    /// WebView (which can't load local file: paths) and travels with the markdown.
    /// This bloats the document by design.
    /// </summary>
    public static string Fragment(string alt, string mime, byte[] bytes) =>
        $"![{alt}](data:{mime};base64,{Convert.ToBase64String(bytes)})";
}
