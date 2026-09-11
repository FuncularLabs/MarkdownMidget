using System;
using System.IO;
using System.Threading.Tasks;

namespace MarkdownMidget;

/// <summary>
/// Reading a dropped PATH off disk, for the route that gets paths (the WPF
/// window). Separate from <see cref="DropRouting"/>, which is pure and decides
/// where a file goes, and separate from the window, so the sharing mode these
/// opens use is testable without one.
/// </summary>
internal static class DropFiles
{
    /// <summary>
    /// One dropped path as <see cref="DropRouting.Plan"/> wants it: its name, its
    /// first <see cref="DropRouting.SniffLength"/> bytes, and its length.
    ///
    /// A file that can't be read gives an EMPTY head rather than a null one:
    /// routing then goes by the name, so a markdown name still reaches
    /// OpenPathAsync and its own "Couldn't open the file" — as a drop always did —
    /// and any other name is refused.
    /// </summary>
    public static DroppedFile Read(string path)
    {
        try
        {
            using var stream = Open(path);
            var head = new byte[DropRouting.SniffLength];
            var read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            // Length from the handle already open, not a second FileInfo probe: one
            // stat, and it cannot disagree with the bytes just read.
            return new DroppedFile(Path.GetFileName(path), head[..read], stream.Length);
        }
        // ArgumentException and NotSupportedException are the MALFORMED-path shapes,
        // and they belong here for the same reason the other two do: this is called
        // from Window_Drop, which is `async void`, so anything that escapes takes the
        // process down rather than refusing one file. FileStream raises
        // ArgumentException for an embedded null character or an empty path, and
        // documents NotSupportedException for "path is in an invalid format" — which
        // the current Windows implementation reports as IOException instead, so that
        // one is the documented contract rather than an observed shape.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or ArgumentException or NotSupportedException)
        {
            // No length to report, which is -1 (unknown), never 0: a zero-length
            // picture and one that could not be opened are different answers, and 0
            // would put an unreadable file under the ceiling rather than outside it.
            return new DroppedFile(Path.GetFileName(path), []);
        }
    }

    /// <summary>
    /// All of a chosen picture's bytes, for embedding — but never more than
    /// <paramref name="limit"/> of them.
    ///
    /// Bounded because it is unbounded time since the file was sniffed, and the file
    /// may have grown: the plan let a 4 MB picture through, the writer kept writing,
    /// and an uncapped read puts however much it became into memory before anything
    /// can refuse it. The default stops ONE byte past the picture ceiling, which is
    /// exactly enough for <see cref="DropRouting.PictureSurvivedTheRead"/> to tell a
    /// file at the ceiling from one over it.
    ///
    /// This is a fresh open, so the length it reads is the length the file has NOW,
    /// not the one the sniff saw. What comes back is what was there, short read and
    /// all; whether that is still a picture is PictureSurvivedTheRead's question, and
    /// the caller must ask it.
    /// </summary>
    /// <param name="limit">Take no more than this many bytes; defaults to the picture
    /// ceiling plus one. Takes a value so the bound is testable without a 64 MB file.</param>
    public static async Task<byte[]> ReadAllAsync(string path, long limit = DropRouting.MaxPictureBytes + 1)
    {
        using var stream = Open(path, useAsync: true);
        // Only a capacity hint — a file being written can report one length and yield
        // another, so what is returned is what actually read, never this.
        var length = stream.CanSeek ? stream.Length : 0;
        using var all = new MemoryStream(length > 0 && length <= limit ? (int)length : 0);
        var buffer = new byte[81920];
        long total = 0;
        while (total < limit)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, limit - total)));
            if (read == 0) break;
            all.Write(buffer, 0, read);
            total += read;
        }
        return all.ToArray();
    }

    /// <summary>
    /// Open a dropped file as tolerantly as reading it allows. FileShare says what
    /// OTHER handles may be doing, not what this one will do — FileShare.Read means
    /// "everyone else may only read", which collides with a writer that already has
    /// the file open and throws. A dropped picture very often has exactly such a
    /// writer: a screenshot tool still flushing the PNG you dragged in the second it
    /// appeared, a sync client, a download in progress. Delete is allowed too, so a
    /// file marked for deletion still reads instead of failing the drop.
    ///
    /// The trade-off, stated plainly: tolerating that writer means the bytes read can
    /// be a HALF-WRITTEN file — the header the sniff saw with nothing behind it, or
    /// more than the length the sniff reported. The drop takes that trade (refusing
    /// every file a writer holds would refuse the commonest dropped picture there is)
    /// and pays for it at the other end, where
    /// <see cref="DropRouting.PictureSurvivedTheRead"/> checks what actually arrived
    /// before any of it reaches the document.
    /// </summary>
    private static FileStream Open(string path, bool useAsync = false) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 4096, useAsync);
}
