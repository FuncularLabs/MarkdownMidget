using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MarkdownMidget;

/// <summary>A file dropped on the window: its name, and its first bytes — the whole
/// file when the drop came through the editor as content, the first
/// <see cref="DropRouting.SniffLength"/> when it came as a path. Null when the
/// bytes could not be read at all (a dropped folder).</summary>
internal sealed record DroppedFile(string Name, byte[]? Head);

/// <summary>Where one dropped file goes.</summary>
internal enum DropKind
{
    /// <summary>Embedded as a data-URI picture, the way Insert ▸ Picture embeds one.</summary>
    Picture,
    /// <summary>Opened as a document, the way a drop always opened one.</summary>
    Document,
    /// <summary>Neither: named in the status line, and nothing else happens to it.</summary>
    Refused,
}

/// <summary>What the window can do with an insertion at the moment of the drop.
/// Public, like LineEnding, so a public test method can take one.</summary>
public enum DropTarget
{
    Editable,
    /// <summary>View ▸ Read Only (or the Help window): a picture is an edit, and none is taken.</summary>
    ReadOnly,
    /// <summary>File ▸ Close left no document to put a picture in.</summary>
    NoDocument,
}

/// <summary>A picture the drop will embed: which file, and the MIME type its bytes
/// sniffed as (not the one its name claims).</summary>
internal sealed record DroppedPicture(int Index, string Mime);

/// <summary>
/// The outcome of one drop, as indices into <see cref="Files"/> — indices rather
/// than names because two files from different folders can share a name.
/// </summary>
/// <param name="Files">The drop, in drop order.</param>
/// <param name="Target">What the window could take when the drop landed.</param>
/// <param name="Insert">Pictures to embed, in drop order.</param>
/// <param name="Open">Documents to open, in drop order; empty whenever a picture was inserted.</param>
/// <param name="NotInserted">Pictures the window could not take (read-only, or no document).</param>
/// <param name="NotOpened">Documents set aside: pictures took the drop, or the surface opens one at a time.</param>
/// <param name="Refused">Neither a picture nor a markdown file, or unreadable.</param>
internal sealed record DropPlan(
    IReadOnlyList<DroppedFile> Files,
    DropTarget Target,
    IReadOnlyList<DroppedPicture> Insert,
    IReadOnlyList<int> Open,
    IReadOnlyList<int> NotInserted,
    IReadOnlyList<int> NotOpened,
    IReadOnlyList<int> Refused)
{
    /// <summary>
    /// The status line for what the drop did NOT do, naming every file involved —
    /// or null when every dropped file was inserted or opened, which needs no
    /// saying. One segment per reason, joined so a mixed drop still reads as one
    /// line.
    /// </summary>
    public string? Notice()
    {
        var parts = new List<string>(3);
        if (Refused.Count > 0) parts.Add("Not a picture or a markdown file: " + Names(Refused));
        if (NotInserted.Count > 0)
            parts.Add((Target == DropTarget.NoDocument ? "No document open, so not inserted: " : "Read-only, so not inserted: ") + Names(NotInserted));
        if (NotOpened.Count > 0)
            parts.Add((Insert.Count > 0 ? "Not opened (pictures were dropped with it): " : "Not opened (one document per drop here): ") + Names(NotOpened));
        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    private string Names(IReadOnlyList<int> indices) => string.Join(", ", indices.Select(i => Files[i].Name));
}

/// <summary>
/// Where a dropped file goes, decided from its name and its first bytes (issue #6).
///
/// Until this existed every dropped file opened as a document — a PNG replaced the
/// document with what its bytes decode to as text, after the discard prompt. Now a
/// picture is recognised by its magic bytes, whatever its name, and embedded the way
/// Insert ▸ Picture embeds one; a file with a markdown name (the names File ▸ Open
/// lists) opens as before; anything else is refused by name and touches nothing.
/// Content wins over name in both directions: <c>photo.md</c> that is really a PNG
/// is a picture, and <c>notes.png</c> that is really text is refused rather than
/// embedded as garbage.
///
/// Pure: the two surfaces that receive drops (the WPF window, which sees paths, and
/// the formatted view, which sees content) both hand their files to
/// <see cref="Plan"/> and act on the answer.
/// </summary>
internal static class DropRouting
{
    /// <summary>How many bytes of a dropped path are read before it is routed: enough
    /// for the longest signature below (a BMP's file header plus the DIB header size
    /// at offset 14).</summary>
    public const int SniffLength = 32;

    /// <summary>
    /// The names that open as a document: the extensions File ▸ Open lists
    /// (<see cref="Secure.SecureUi.OpenFilter"/>, encrypted included — the open
    /// path sniffs the container and prompts for the password). A test pins the
    /// two lists to each other.
    /// </summary>
    public static readonly string[] DocumentExtensions = [".md", ".markdown", Secure.SecureMarkdownFormat.Extension, ".txt"];

    // Bytes, not a u8 literal: 0x89 is not ASCII, and as a string escape it would
    // encode to two UTF-8 bytes and never match.
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// The MIME type of a picture whose file starts with <paramref name="head"/>, or
    /// null when the bytes are not one of the raster formats Insert ▸ Picture lists.
    /// SVG is deliberately absent: it is XML text with no signature, so a drop cannot
    /// tell it from any other XML.
    /// </summary>
    public static string? SniffImageMime(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith(PngSignature)) return "image/png";
        if (head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF) return "image/jpeg";
        if (head.StartsWith("GIF87a"u8) || head.StartsWith("GIF89a"u8)) return "image/gif";
        if (head.Length >= 12 && head.StartsWith("RIFF"u8) && head[8..12].SequenceEqual("WEBP"u8)) return "image/webp";
        // "BM" alone is two letters that prose can start with, so the DIB header that
        // follows the 14-byte file header is checked too: it begins with its own
        // size, one of a handful of small values — three zero bytes that no text has.
        if (head.Length >= 18 && head.StartsWith("BM"u8)
            && BinaryPrimitives.ReadUInt32LittleEndian(head[14..18]) is 12 or 40 or 52 or 56 or 64 or 108 or 124)
            return "image/bmp";
        return null;
    }

    /// <summary>
    /// Route one file. Content first: a sniffed picture is a picture whatever its
    /// name. Then the name: a markdown extension opens. A null <paramref name="head"/>
    /// (unreadable) is refused regardless — there is nothing to embed or open — while
    /// an empty one is an empty file, which is still a document if its name is.
    /// </summary>
    public static (DropKind Kind, string? Mime) Classify(string name, byte[]? head)
    {
        if (head is null) return (DropKind.Refused, null);
        if (SniffImageMime(head) is { } mime) return (DropKind.Picture, mime);
        var ext = Path.GetExtension(name);
        return DocumentExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)
            ? (DropKind.Document, null)
            : (DropKind.Refused, null);
    }

    /// <summary>
    /// Route a whole drop. Pictures go in, in drop order, when <paramref name="target"/>
    /// can take an edit; a markdown file opens only if no picture was inserted (a
    /// drop is either an insertion or an open, never both — an open would replace
    /// the document the pictures just went into); with <paramref name="oneDocument"/>
    /// (the formatted view, which has content rather than paths) only the first
    /// markdown file opens and the rest are named. Refused files are only named.
    /// </summary>
    public static DropPlan Plan(IReadOnlyList<DroppedFile> files, DropTarget target, bool oneDocument)
    {
        var insert = new List<DroppedPicture>();
        var notInserted = new List<int>();
        var documents = new List<int>();
        var refused = new List<int>();
        for (var i = 0; i < files.Count; i++)
        {
            var (kind, mime) = Classify(files[i].Name, files[i].Head);
            switch (kind)
            {
                case DropKind.Picture when target == DropTarget.Editable: insert.Add(new DroppedPicture(i, mime!)); break;
                case DropKind.Picture: notInserted.Add(i); break;
                case DropKind.Document: documents.Add(i); break;
                default: refused.Add(i); break;
            }
        }

        List<int> open, notOpened;
        if (insert.Count > 0) { open = []; notOpened = documents; }
        else if (oneDocument && documents.Count > 1) { open = documents.Take(1).ToList(); notOpened = documents.Skip(1).ToList(); }
        else { open = documents; notOpened = []; }

        return new DropPlan(files, target, insert, open, notInserted, notOpened, refused);
    }

    /// <summary>The markdown for one dropped picture: Insert ▸ Picture's fragment,
    /// with the same alt text it would give the file (its name without the
    /// extension) and the MIME type the bytes sniffed as.</summary>
    public static string PictureMarkdown(string name, string mime, byte[] bytes) =>
        ImageMarkdown.Fragment(ImageMarkdown.AltText(name), mime, bytes);

    /// <summary>Several pictures as one insertion, each on its own line; a single
    /// picture is exactly its fragment, with nothing added.</summary>
    public static string Markdown(IReadOnlyList<string> fragments) => string.Join("\n\n", fragments);

    /// <summary>
    /// The files in the editor's <c>fileDrop</c> message: <c>{files: [{name, base64}]}</c>.
    /// A null <c>base64</c> is a file the browser could not read; malformed base64 is
    /// treated the same way rather than thrown at the message pump.
    /// </summary>
    public static List<DroppedFile> ParseMessage(JsonElement root)
    {
        var files = new List<DroppedFile>();
        if (!root.TryGetProperty("files", out var array) || array.ValueKind != JsonValueKind.Array) return files;
        foreach (var f in array.EnumerateArray())
        {
            var name = f.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : "Dropped";
            byte[]? head = null;
            if (f.TryGetProperty("base64", out var b) && b.ValueKind == JsonValueKind.String)
            {
                try { head = Convert.FromBase64String(b.GetString()!); }
                catch (FormatException) { head = null; }
            }
            files.Add(new DroppedFile(name, head));
        }
        return files;
    }
}
