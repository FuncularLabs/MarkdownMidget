using System.IO;
using System.Linq;
using System.Text;
using MarkdownMidget;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>
/// Reading a dropped PATH off disk (issue #6, review finding F-8). The routing
/// rules live in <see cref="DropRouting"/> and are tested there; what is tested
/// here is the only thing these two methods decide — how tolerant the open is of
/// whoever else has the file.
/// </summary>
public class DropFilesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mdm-dropfiles-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* a handle outlived the test */ }
        GC.SuppressFinalize(this);
    }

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D, (byte)'I', (byte)'H', (byte)'D', (byte)'R'];

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>
    /// Another process holding the file open FOR WRITING, and allowing readers —
    /// exactly what a screenshot tool still flushing its PNG, OneDrive, or a
    /// download in progress looks like from here.
    /// </summary>
    private static FileStream HeldByAWriter(string path) =>
        new(path, FileMode.Open, FileAccess.Write, FileShare.Read | FileShare.Write | FileShare.Delete);

    [Fact]
    public void HeadIsReadWhileAnotherProcessHasTheFileOpenForWriting()
    {
        // The finding: opening with FileShare.Read says "others may only read", which
        // collides with the writer's existing handle and throws. The drop then saw an
        // empty head and refused a perfectly good picture by name — the screenshot you
        // dragged in the moment it appeared.
        var path = Write("shot.png", Png);
        using var writer = HeldByAWriter(path);

        var dropped = DropFiles.Read(path);

        Assert.Equal("shot.png", dropped.Name);
        Assert.Equal(Png, dropped.Head);
        Assert.Equal(DropKind.Picture, DropRouting.Classify(dropped.Name, dropped.Head).Kind);
    }

    [Fact]
    public async Task AllBytesAreReadWhileAnotherProcessHasTheFileOpenForWriting()
    {
        // The same sharing question for the second read: the head routes the file,
        // then the whole of a chosen picture is read to embed it. A tolerant sniff
        // followed by an intolerant full read would route the picture and then fail
        // the insert with "Couldn't read the image".
        var bytes = new byte[Png.Length + 1000];
        Png.CopyTo(bytes, 0);
        var path = Write("big.png", bytes);
        using var writer = HeldByAWriter(path);

        Assert.Equal(bytes, await DropFiles.ReadAllAsync(path));
    }

    [Fact]
    public void HeadIsAtMostSniffLengthBytes()
    {
        var path = Write("long.png", Encoding.ASCII.GetBytes(new string('x', DropRouting.SniffLength * 4)));
        Assert.Equal(DropRouting.SniffLength, DropFiles.Read(path).Head!.Length);
    }

    [Fact]
    public void ShortFileGivesAShortHeadAndAnEmptyFileAnEmptyOne()
    {
        // Not padded to SniffLength: trailing zeros would be indistinguishable from a
        // file that really ends in them, and "BM" + 16 zeros sniffs as a bitmap.
        Assert.Equal<byte>([(byte)'h', (byte)'i'], DropFiles.Read(Write("tiny.md", "hi"u8.ToArray())).Head!);
        Assert.Empty(DropFiles.Read(Write("empty.md", [])).Head!);
    }

    [Fact]
    public void ReadReportsTheFilesOwnLengthAsItsSize()
    {
        // The size is the only thing the picture ceiling is decided from, and on this
        // route nothing checked it: DropFiles.Read could have reported -1 for every
        // file — no ceiling, ever — and the whole suite stayed green.
        var bytes = new byte[Png.Length + 5000];
        Png.CopyTo(bytes, 0);
        Assert.Equal(bytes.Length, DropFiles.Read(Write("photo.png", bytes)).Size);
        Assert.Equal(2, DropFiles.Read(Write("tiny2.md", "hi"u8.ToArray())).Size);
        Assert.Equal(0, DropFiles.Read(Write("nothing.md", [])).Size);
    }

    [Fact]
    public void AnUnreadablePathSaysItDoesNotKnowTheSize()
    {
        // -1 is "the drop did not say", and no ceiling is applied to it. A file that
        // could not be opened has no length to report, and must not report 0 — a
        // zero-length picture and an unknown one are different answers.
        Assert.Equal(-1, DropFiles.Read(Path.Combine(_dir, "not-here.md")).Size);
        Assert.Equal(-1, DropFiles.Read(Directory.CreateDirectory(Path.Combine(_dir, "sized folder")).FullName).Size);
    }

    [Fact]
    public void APictureOverTheCeilingIsTooLargeOnThePathRouteToo()
    {
        // End to end on the route that has paths: a real file just over the ceiling,
        // read the way a drop reads one, routed the way a drop routes it. The ceiling
        // had no test at all on this side — only on the message route, where the size
        // is whatever the test passes in.
        var path = Path.Combine(_dir, "huge.png");
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
        {
            stream.Write(Png);
            stream.SetLength(DropRouting.MaxPictureBytes + 1);
        }

        var over = DropFiles.Read(path);
        Assert.Equal(DropRouting.MaxPictureBytes + 1, over.Size);
        var plan = DropRouting.Plan([over], DropTarget.Editable, oneDocument: false);
        Assert.Empty(plan.Insert);
        Assert.Equal([0], plan.TooLarge);
        Assert.Equal($"Too large to insert (over {DropRouting.MaxPictureBytes / (1024 * 1024)} MB): huge.png", plan.Notice());

        // And exactly AT the ceiling it still goes in — the boundary is the half of
        // this that a wrong comparison gets wrong.
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write))
            stream.SetLength(DropRouting.MaxPictureBytes);

        var atCeiling = DropFiles.Read(path);
        Assert.Equal(DropRouting.MaxPictureBytes, atCeiling.Size);
        Assert.Equal([0], DropRouting.Plan([atCeiling], DropTarget.Editable, oneDocument: false).Insert.Select(p => p.Index));
    }

    [Fact]
    public async Task AFileTruncatedBetweenTheSniffAndTheReadIsRefused()
    {
        // NF-11. The head routes the file and the full read embeds it, and the two
        // are separate opens of a file another process may still be writing — which
        // is exactly the case F-8 widened the sharing mode FOR. Here the writer resets
        // the file after the sniff: the head said PNG, the read gets four bytes, and
        // without this check those four bytes go into the document as a data URI no
        // viewer can decode, silently.
        var path = Write("shot.png", Png);
        var sniffed = DropFiles.Read(path);
        Assert.Equal(DropKind.Picture, DropRouting.Classify(sniffed.Name, sniffed.Head, sniffed.Size).Kind);

        using (var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            writer.SetLength(4);

        var bytes = await DropFiles.ReadAllAsync(path);
        // Read what is there — no throw, no padding to the length the sniff saw.
        Assert.Equal(4, bytes.Length);
        Assert.NotEqual(sniffed.Size, bytes.Length);
        // And refused, because four bytes are not the PNG the plan routed.
        Assert.False(DropRouting.PictureSurvivedTheRead(bytes, "image/png"));
    }

    [Fact]
    public async Task ReadAllReadsAtMostOneByteOverTheLimitSoAGrownFileCanBeRefused()
    {
        // The read is bounded, or a file that grew to a gigabyte between the sniff and
        // the read is a gigabyte in memory before anything can refuse it. One byte
        // over the limit, so "too big" is still distinguishable from "exactly at it".
        var bytes = new byte[64];
        Png.CopyTo(bytes, 0);
        var path = Write("grown.png", bytes);

        Assert.Equal(17, (await DropFiles.ReadAllAsync(path, limit: 17)).Length);
        Assert.Equal(64, (await DropFiles.ReadAllAsync(path, limit: 64)).Length);
        Assert.Equal(64, (await DropFiles.ReadAllAsync(path, limit: 65)).Length);
        // The default limit is the picture ceiling plus that one byte.
        Assert.Equal(bytes, await DropFiles.ReadAllAsync(path));
    }

    [Fact]
    public async Task ReadAllTakesTheLengthTheFileHasWhenItIsOpenedNotTheOneTheSniffSaw()
    {
        // The file grew after it was routed — the screenshot tool finished flushing.
        // All of it is read, not the prefix the sniff's length would have allowed.
        var path = Write("growing.png", Png);
        var sniffed = DropFiles.Read(path);

        var grown = new byte[Png.Length + 500];
        Png.CopyTo(grown, 0);
        File.WriteAllBytes(path, grown);

        var bytes = await DropFiles.ReadAllAsync(path);
        Assert.Equal(grown.Length, bytes.Length);
        Assert.True(bytes.Length > sniffed.Size);
        Assert.True(DropRouting.PictureSurvivedTheRead(bytes, "image/png"));
    }

    [Fact]
    public void AMalformedPathIsUnreadableRatherThanAThrow()
    {
        // Carried over from the previous round. The catch filter listed IOException
        // and UnauthorizedAccessException only, and a path the OS cannot parse at all
        // throws neither: an embedded null character is FileStream's own
        // ArgumentException, and an empty path likewise. Read() is called from
        // Window_Drop, which is `async void` — nothing there catches, and the throw
        // takes the process down over a path the shell should never have handed us.
        var nullChar = DropFiles.Read($"{_dir}\\a\0b.md");
        Assert.NotNull(nullChar.Head);
        Assert.Empty(nullChar.Head);
        Assert.Equal(-1, nullChar.Size);

        var empty = DropFiles.Read("");
        Assert.NotNull(empty.Head);
        Assert.Empty(empty.Head);
    }

    [Fact]
    public void AnUnreadablePathGivesAnEmptyHeadNotNull()
    {
        // A missing file, and a folder dropped on the window. Empty rather than null
        // so routing falls back to the name: a markdown name still reaches
        // OpenPathAsync and its own error, as a drop always did.
        var gone = DropFiles.Read(Path.Combine(_dir, "not-here.md"));
        Assert.NotNull(gone.Head);
        Assert.Empty(gone.Head);
        Assert.Equal(DropKind.Document, DropRouting.Classify(gone.Name, gone.Head).Kind);

        var folder = Directory.CreateDirectory(Path.Combine(_dir, "a folder")).FullName;
        var dropped = DropFiles.Read(folder);
        Assert.NotNull(dropped.Head);
        Assert.Empty(dropped.Head);
        Assert.Equal(DropKind.Refused, DropRouting.Classify(dropped.Name, dropped.Head).Kind);
    }
}
