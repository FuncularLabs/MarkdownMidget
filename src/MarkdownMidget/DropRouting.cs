using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MarkdownMidget;

/// <summary>
/// A file dropped on the window, as much of it as routing needs: its name, its
/// first <see cref="DropRouting.SniffLength"/> bytes, and its length. Neither
/// route reads a whole file to route it — the bytes of the one or two files the
/// plan chooses are fetched afterwards, by path or by asking the editor.
///
/// <paramref name="Head"/> is null when the bytes could not be read at all (a
/// dropped folder through the editor). It may be LONGER than SniffLength: routing
/// only ever looks at the prefix, so the editor's head and the host's constant can
/// differ without breaking.
///
/// <paramref name="Size"/> is the file's full length, for
/// <see cref="DropRouting.MaxPictureBytes"/>; -1 means the drop did not say, and
/// no ceiling is applied. Both routes always say (a path has a length; the editor
/// sends File.size).
/// </summary>
internal sealed record DroppedFile(string Name, byte[]? Head, long Size = -1);

/// <summary>Where one dropped file goes.</summary>
internal enum DropKind
{
    /// <summary>Embedded as a data-URI picture, the way Insert ▸ Picture embeds one.</summary>
    Picture,
    /// <summary>Opened as a document, the way a drop always opened one.</summary>
    Document,
    /// <summary>A picture, but past <see cref="DropRouting.MaxPictureBytes"/>: named
    /// in the status line, and nothing else happens to it.</summary>
    TooLarge,
    /// <summary>Neither: named in the status line, and nothing else happens to it.</summary>
    Refused,
}

/// <summary>What the window can do with an insertion at the moment of the drop.
/// Public, like LineEnding, so a public test method can take one.</summary>
public enum DropTarget
{
    Editable,
    /// <summary>Edit ▸ Read Only (or the Help window): a picture is an edit, and none is taken.</summary>
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
/// <param name="TooLarge">Pictures past <see cref="DropRouting.MaxPictureBytes"/>.</param>
internal sealed record DropPlan(
    IReadOnlyList<DroppedFile> Files,
    DropTarget Target,
    IReadOnlyList<DroppedPicture> Insert,
    IReadOnlyList<int> Open,
    IReadOnlyList<int> NotInserted,
    IReadOnlyList<int> NotOpened,
    IReadOnlyList<int> Refused,
    IReadOnlyList<int> TooLarge)
{
    /// <summary>
    /// The status line for what the drop did NOT do, naming every file involved —
    /// or null when every dropped file was inserted or opened, which needs no
    /// saying. One segment per reason, joined so a mixed drop still reads as one
    /// line.
    /// </summary>
    public string? Notice()
    {
        var parts = new List<string>(4);
        if (Refused.Count > 0) parts.Add("Not a picture or a markdown file: " + Names(Refused));
        // The shared wording (PictureLimit), so every route that refuses a picture
        // for its size says so in the same words.
        if (TooLarge.Count > 0) parts.Add(PictureLimit.Notice(Names(TooLarge)));
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
    /// The largest picture a drop will embed: <see cref="PictureLimit.MaxBytes"/>,
    /// the one ceiling every route applies (PictureLimit says why there is one).
    /// This is the drop's name for that constant, not a second number.
    ///
    /// Only PICTURES are capped. A dropped markdown or text file is opened, and
    /// File ▸ Open has never capped what it opens; capping it here would refuse a
    /// file the same user can open from the menu a second later.
    /// </summary>
    public const long MaxPictureBytes = PictureLimit.MaxBytes;

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
    /// name — unless <paramref name="size"/> puts it past
    /// <see cref="MaxPictureBytes"/>, which is still a picture, just not one that
    /// goes in. Then the name: a markdown extension opens. A null
    /// <paramref name="head"/> (unreadable) is refused regardless — there is nothing
    /// to embed or open — while an empty one is an empty file, which is still a
    /// document if its name is.
    /// </summary>
    /// <param name="size">The file's full length, or -1 when the drop did not say
    /// (then no ceiling applies). Only the head is in hand here; the bytes are
    /// fetched after the plan chooses, which is exactly why the ceiling has to be
    /// decided from the size rather than from what was read.</param>
    public static (DropKind Kind, string? Mime) Classify(string name, byte[]? head, long size = -1)
    {
        if (head is null) return (DropKind.Refused, null);
        if (SniffImageMime(head) is { } mime)
            return PictureLimit.IsTooLarge(size) ? (DropKind.TooLarge, null) : (DropKind.Picture, mime);
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
        var tooLarge = new List<int>();
        for (var i = 0; i < files.Count; i++)
        {
            var (kind, mime) = Classify(files[i].Name, files[i].Head, files[i].Size);
            switch (kind)
            {
                case DropKind.Picture when target == DropTarget.Editable: insert.Add(new DroppedPicture(i, mime!)); break;
                case DropKind.Picture: notInserted.Add(i); break;
                case DropKind.Document: documents.Add(i); break;
                case DropKind.TooLarge: tooLarge.Add(i); break;
                default: refused.Add(i); break;
            }
        }

        List<int> open, notOpened;
        if (insert.Count > 0) { open = []; notOpened = documents; }
        else if (oneDocument && documents.Count > 1) { open = documents.Take(1).ToList(); notOpened = documents.Skip(1).ToList(); }
        else { open = documents; notOpened = []; }

        return new DropPlan(files, target, insert, open, notInserted, notOpened, refused, tooLarge);
    }

    /// <summary>
    /// Whether the bytes that actually ARRIVED for a chosen picture may be embedded.
    ///
    /// <see cref="Classify"/> decides from a head and a size that are already stale
    /// by the time the bytes turn up: on the path route by one more open, on the
    /// editor route by a whole round trip. And the case that gap was widened for
    /// (F-8: a file another process is still writing — a screenshot tool flushing the
    /// PNG you dragged in the second it appeared) is exactly the case where the file
    /// changes in between. A picture goes into the document as a data URI, and a
    /// truncated one is a data URI no viewer can decode, embedded silently.
    ///
    /// So what arrived is measured again, on three counts:
    ///
    /// <list type="bullet">
    /// <item>It still starts the way <paramref name="mime"/> starts — which catches a
    /// cut THROUGH the signature, and an entirely different file swapped in under the
    /// same name. An empty read fails here too: no bytes sniff as anything.</item>
    /// <item>It is no SHORTER than <paramref name="statedSize"/>, the length the drop
    /// said the file had. Sniffing alone would pass a PNG signature with one byte
    /// behind it, which is exactly the shape a writer mid-flush leaves; only this
    /// catches it. A read LONGER than stated is fine — the drop deliberately
    /// tolerates a writer that is still going, and growth is what that looks
    /// like.</item>
    /// <item>It is not past <paramref name="ceiling"/>: it may have grown past the
    /// size the plan allowed.</item>
    /// </list>
    ///
    /// What it still cannot prove is that the bytes BETWEEN the signature and the end
    /// are the picture's own — a file of the right length whose middle was rewritten
    /// passes. Nothing short of decoding would catch that, and decoding a 64 MB
    /// picture to refuse it costs more than the refusal is worth.
    /// </summary>
    /// <param name="statedSize">The length the drop stated for this file
    /// (<see cref="DroppedFile.Size"/>, the same value Classify weighed against the
    /// ceiling), or -1 when it did not say — and then there is no length to compare
    /// against and none is compared, exactly as no ceiling is applied.</param>
    /// <param name="ceiling">Takes a value only so the boundary is testable without
    /// allocating 64 MB; it is <see cref="MaxPictureBytes"/>, the same ceiling
    /// Classify applied.</param>
    public static bool PictureSurvivedTheRead(byte[] bytes, string mime, long statedSize, long ceiling = MaxPictureBytes) =>
        SniffImageMime(bytes) == mime
        && !PictureLimit.IsTooLarge(bytes.LongLength, ceiling)
        && (statedSize < 0 || bytes.LongLength >= statedSize);

    /// <summary>The markdown for one dropped picture: Insert ▸ Picture's fragment,
    /// with the same alt text it would give the file (its name without the
    /// extension) and the MIME type the bytes sniffed as.</summary>
    public static string PictureMarkdown(string name, string mime, byte[] bytes) =>
        ImageMarkdown.Fragment(ImageMarkdown.AltText(name), mime, bytes);

    /// <summary>Several pictures as one insertion, each on its own line; a single
    /// picture is exactly its fragment, with nothing added.</summary>
    public static string Markdown(IReadOnlyList<string> fragments) => string.Join("\n\n", fragments);

    /// <summary>
    /// The editor's <c>fileDrop</c> message — phase one of the drop:
    /// <c>{drop: n, files: [{name, size, headBase64}]}</c>.
    ///
    /// Only the HEAD of each file travels, so a 200 MB video dropped by accident
    /// costs 32 bytes in the message rather than a base64 copy of itself in both
    /// processes. The host routes on this and then asks for the bytes of the one or
    /// two files it chose (<c>MDM.readDroppedFiles</c>, answered into
    /// <see cref="ParseBytesMessage"/>).
    ///
    /// <c>drop</c> is the editor's counter for which drop this is; it comes back on
    /// the phase-two request and answer so a reply about a superseded drop can be
    /// told apart from the current one. A missing head is a file the browser could
    /// not read; malformed base64 is treated the same way rather than thrown at the
    /// message pump.
    /// </summary>
    public static DropMessage ParseMessage(JsonElement root)
    {
        var files = new List<DroppedFile>();
        var drop = DropId(root);
        if (!root.TryGetProperty("files", out var array) || array.ValueKind != JsonValueKind.Array)
            return new DropMessage(drop, files);
        foreach (var f in array.EnumerateArray())
        {
            var name = f.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : "Dropped";
            files.Add(new DroppedFile(name, Base64Property(f, "headBase64"), SizeProperty(f)));
        }
        return new DropMessage(drop, files);
    }

    /// <summary>
    /// The editor's <c>droppedFileBytes</c> message — phase two:
    /// <c>{drop: n, files: [{index, base64}]}</c>, the full bytes of the files the
    /// host asked for, keyed by their index in the phase-one list. A null
    /// <c>base64</c> (or one that will not decode) is a file the browser could not
    /// read; the host inserts none of the pictures when any of them is null, as a
    /// failed path read has always done.
    /// </summary>
    public static DropBytesMessage ParseBytesMessage(JsonElement root)
    {
        var bytes = new Dictionary<int, byte[]?>();
        var drop = DropId(root);
        if (!root.TryGetProperty("files", out var array) || array.ValueKind != JsonValueKind.Array)
            return new DropBytesMessage(drop, bytes);
        foreach (var f in array.EnumerateArray())
        {
            // An entry with no usable index is skipped rather than throwing at the
            // message pump: the host then finds no bytes for it and declines to
            // insert, which is the safe direction.
            if (!f.TryGetProperty("index", out var i) || i.ValueKind != JsonValueKind.Number || !i.TryGetInt32(out var index)) continue;
            bytes[index] = Base64Property(f, "base64");
        }
        return new DropBytesMessage(drop, bytes);
    }

    /// <summary>Which drop a message is about; 0 when it did not say, which no drop
    /// the editor numbers ever is (the counter starts at 1).</summary>
    private static long DropId(JsonElement root) =>
        root.TryGetProperty("drop", out var d) && d.ValueKind == JsonValueKind.Number && d.TryGetInt64(out var n) ? n : 0;

    /// <summary>A base64 string property as bytes; null when absent, not a string,
    /// or not decodable.</summary>
    private static byte[]? Base64Property(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var b) || b.ValueKind != JsonValueKind.String) return null;
        try { return Convert.FromBase64String(b.GetString()!); }
        catch (FormatException) { return null; }
    }

    /// <summary>A dropped file's length, or -1 when the message did not state a
    /// usable one — no ceiling then, rather than refusing a picture because a field
    /// went missing. The editor always sends File.size.</summary>
    private static long SizeProperty(JsonElement element) =>
        element.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number
        && s.TryGetInt64(out var size) && size >= 0
            ? size
            : -1;
}

/// <summary>One <c>fileDrop</c> message: which drop it is, its files in drop order, and each
/// file's path as the message's additional objects carried it (null when they did not).</summary>
internal sealed record DropMessage(long Drop, IReadOnlyList<DroppedFile> Files, IReadOnlyList<string?>? Paths = null);

/// <summary>One <c>droppedFileBytes</c> answer: which drop it is about, and the bytes
/// of each index asked for (null where the editor could not read the file).</summary>
internal sealed record DropBytesMessage(long Drop, IReadOnlyDictionary<int, byte[]?> Bytes);
