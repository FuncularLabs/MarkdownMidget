using System.Globalization;

namespace MarkdownMidget;

/// <summary>
/// The one size ceiling on a picture going into a document, and the one way the
/// status bar says a picture was refused for it.
///
/// A picture is embedded as a base64 data URI (<see cref="ImageMarkdown"/>), so it
/// costs about 4/3 its size in the markdown, again in the editor's copy of it, and
/// again in every save — 64 MB is already an 85 MB data URI in a text file. Past
/// the ceiling the picture is refused with a note in the status bar instead, which
/// is a far better outcome than a wedged window.
///
/// Every route that puts a picture in asks <see cref="IsTooLarge"/> and says no
/// with <see cref="Notice"/>, so no two routes can disagree about where the line is
/// or how the refusal is worded: a picture file dropped on either view
/// (<see cref="DropRouting"/>, whose MaxPictureBytes is this constant under the
/// drop's own name).
///
/// Only PICTURES are capped. A dropped markdown or text file is opened, and
/// File ▸ Open has never capped what it opens.
/// </summary>
internal static class PictureLimit
{
    /// <summary>The largest picture any route will embed, counted in the picture's
    /// own bytes rather than its base64.</summary>
    public const long MaxBytes = 64L * 1024 * 1024;

    private const long BytesPerMegabyte = 1024 * 1024;

    /// <summary>
    /// Whether a picture of <paramref name="bytes"/> bytes is past the ceiling:
    /// exactly the ceiling goes in, one byte more does not. A negative size is one
    /// nobody stated (a drop message that gave none), and is past nothing.
    /// </summary>
    /// <param name="ceiling">Takes a value only so the boundary is testable without
    /// 64 MB of picture; it is <see cref="MaxBytes"/> everywhere in the app.</param>
    public static bool IsTooLarge(long bytes, long ceiling = MaxBytes) => bytes > ceiling;

    /// <summary>
    /// The status line for a picture refused for its size: the limit, computed from
    /// the ceiling and never written down, and what was refused —
    /// "Too large to insert (over 64 MB): huge.png".
    /// </summary>
    /// <param name="names">The refused file's name, or several joined by ", " (a
    /// drop names every file it refused); null for a picture that has no name,
    /// which is one pasted from the clipboard.</param>
    /// <param name="ceiling">As for <see cref="IsTooLarge"/>; a whole number of
    /// megabytes, as MaxBytes is.</param>
    public static string Notice(string? names, long ceiling = MaxBytes) =>
        string.Create(CultureInfo.InvariantCulture,
            $"Too large to insert (over {ceiling / BytesPerMegabyte} MB): {names ?? "pasted picture"}");
}
