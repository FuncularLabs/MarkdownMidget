using System;
using System.IO;
using System.Threading.Tasks;

namespace MarkdownMidget;

/// <summary>What Insert ▸ Picture makes of the file the picker returned: the
/// markdown to insert, or — for a picture past <see cref="PictureLimit"/> — the
/// status-bar notice instead. Exactly one of the two is set.</summary>
internal sealed record PickedPictureResult(string? Markdown, string? Notice);

/// <summary>
/// Insert ▸ Picture's read of the file the picker returned, under the one picture
/// ceiling (<see cref="PictureLimit"/>). Apart from the window, which only shows
/// what this answers, so the ceiling on this route is testable without one.
///
/// Until the ceiling reached this route it read the whole file, however large, and
/// embedded it. Now the ceiling is applied twice, the way the drop applies it: to
/// the size on disk BEFORE a byte is read, so a picture past it costs a stat rather
/// than its own size in memory and a third again in base64; and to the bytes that
/// actually arrived, because the file can grow between the two (a screenshot tool
/// still writing) — through a read bounded one byte past the ceiling, so a file
/// that grew to a gigabyte is refused without being read to the end.
///
/// Only the ceiling is new. The drop also checks that the bytes that arrived still
/// sniff as the format it routed and are no shorter than it was told
/// (<see cref="DropRouting.PictureSurvivedTheRead"/>). Insert ▸ Picture has never
/// sniffed — it embeds an SVG, which has no signature, and takes the MIME type from
/// the extension — so reusing that whole check would refuse pictures this route has
/// always taken. What is reused is the drop's ceiling and its bounded read.
/// </summary>
internal static class PickedPicture
{
    /// <summary>
    /// The markdown for the picture at <paramref name="path"/>, or the notice that
    /// refuses it. Under the ceiling the markdown is exactly what Insert ▸ Picture
    /// always built: the whole file, its name for the alt text, its extension for
    /// the MIME type.
    /// </summary>
    /// <param name="ceiling">Takes a value only so the boundary is testable without
    /// 64 MB of picture; it is <see cref="PictureLimit.MaxBytes"/> in the app.</param>
    /// <param name="read">The bounded read, as a seam so a test can stand in for a
    /// file that grows between the size check and the read; null is
    /// <see cref="ReadBoundedAsync"/>.</param>
    /// <exception cref="IOException">And whatever else the size check or the read
    /// throws — a file that has gone, one another program is writing. It
    /// propagates, as File.ReadAllBytesAsync's did, to Picture_Click's "Couldn't
    /// read the image" message.</exception>
    public static async Task<PickedPictureResult> ReadAsync(
        string path, long ceiling = PictureLimit.MaxBytes, Func<string, long, Task<byte[]>>? read = null)
    {
        read ??= ReadBoundedAsync;
        var name = Path.GetFileName(path);

        if (PictureLimit.IsTooLarge(new FileInfo(path).Length, ceiling))
            return new PickedPictureResult(null, PictureLimit.Notice(name, ceiling));

        var bytes = await read(path, ceiling + 1);
        if (PictureLimit.IsTooLarge(bytes.LongLength, ceiling))
            return new PickedPictureResult(null, PictureLimit.Notice(name, ceiling));

        return new PickedPictureResult(
            ImageMarkdown.Fragment(ImageMarkdown.AltText(path), ImageMarkdown.MimeForImage(path), bytes), null);
    }

    /// <summary>
    /// The drop's bounded read (<see cref="DropFiles.ReadAllAsync"/>) with the
    /// sharing File.ReadAllBytesAsync gave this route: FileShare.Read, so a file
    /// another program still has open for writing is refused with the message it
    /// always got, rather than read half-written. The drop can tolerate that writer
    /// because it checks what arrived against the signature and the stated length;
    /// this route checks only the ceiling, and a half-written picture would go in as
    /// a data URI no viewer can decode.
    /// </summary>
    public static Task<byte[]> ReadBoundedAsync(string path, long limit) =>
        DropFiles.ReadAllAsync(path, limit, FileShare.Read);
}
