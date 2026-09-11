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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new DroppedFile(Path.GetFileName(path), []);
        }
    }

    /// <summary>All of a chosen picture's bytes, for embedding.</summary>
    public static async Task<byte[]> ReadAllAsync(string path)
    {
        using var stream = Open(path, useAsync: true);
        using var all = new MemoryStream();
        await stream.CopyToAsync(all);
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
    /// </summary>
    private static FileStream Open(string path, bool useAsync = false) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 4096, useAsync);
}
