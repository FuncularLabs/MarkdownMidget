using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MarkdownMidget;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>
/// Insert ▸ Picture under the one picture ceiling (<see cref="PickedPicture"/>).
/// Until now it read whatever file the picker returned, whole, however large, and
/// embedded it — a route past the ceiling the drop applies. What is tested: a
/// picture past the ceiling is refused from its size on disk before a byte of it
/// is read; one that grows past it between that check and the read is refused on
/// what arrived, by a read that stops one byte past the ceiling; and everything
/// under the ceiling goes in exactly as it did — the same markdown, the same
/// sharing, the same errors.
/// </summary>
public class PickedPictureTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mdm-picked-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* a handle outlived the test */ }
        GC.SuppressFinalize(this);
    }

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D, (byte)'I', (byte)'H', (byte)'D', (byte)'R'];

    /// <summary>A PNG signature padded with zeros to <paramref name="length"/> bytes.</summary>
    private static byte[] PngOf(int length)
    {
        var bytes = new byte[length];
        Png.CopyTo(bytes, 0);
        return bytes;
    }

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>Another program holding the file open for writing, and letting others
    /// read — a screenshot tool still flushing, a sync client, a download.</summary>
    private static FileStream HeldByAWriter(string path) =>
        new(path, FileMode.Open, FileAccess.Write, FileShare.Read | FileShare.Write | FileShare.Delete);

    /// <summary>The read PickedPicture is handed, recording what it was asked for.</summary>
    private sealed class ReadSpy(Func<string, long, byte[]> answer)
    {
        public List<(string Path, long Limit)> Calls { get; } = [];

        public Task<byte[]> Read(string path, long limit)
        {
            Calls.Add((path, limit));
            return Task.FromResult(answer(path, limit));
        }
    }

    [Fact]
    public async Task APictureOverTheCeilingIsRefusedWithoutReadingIt()
    {
        // Decided from the file's size on disk, before any of it is read: a 2 GB
        // picture costs a stat, not 2 GB of memory with a base64 copy on top.
        var path = Write("huge.png", PngOf(65));
        var spy = new ReadSpy((_, _) => PngOf(65));

        var result = await PickedPicture.ReadAsync(path, ceiling: 64, read: spy.Read);

        Assert.Null(result.Markdown);
        Assert.Equal(PictureLimit.Notice("huge.png", ceiling: 64), result.Notice);
        Assert.Empty(spy.Calls);
    }

    [Fact]
    public async Task AtTheAppsOwnCeilingTheSizeOnDiskDecidesAndNothingIsRead()
    {
        // The same, end to end with the app's own numbers and no seam: a file one
        // byte past 64 MB (SetLength, so the test writes sixteen bytes, not 64 MB),
        // held open by a handle that lets nobody else read it. A read would throw;
        // the refusal comes back instead, naming the file and the limit.
        var path = Path.Combine(_dir, "huge.png");
        using var held = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        held.Write(Png);
        held.SetLength(PictureLimit.MaxBytes + 1);
        held.Flush();

        var result = await PickedPicture.ReadAsync(path);

        Assert.Null(result.Markdown);
        Assert.Equal(PictureLimit.Notice("huge.png"), result.Notice);

        // And the lock is real: a picture UNDER the ceiling behind the same kind of
        // handle cannot be read, so the answer above is not a read that happened to
        // get through.
        var small = Path.Combine(_dir, "small.png");
        using var heldSmall = new FileStream(small, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        heldSmall.Write(Png);
        heldSmall.Flush();
        await Assert.ThrowsAsync<IOException>(() => PickedPicture.ReadAsync(small));
    }

    [Fact]
    public async Task APictureThatGrewPastTheCeilingAfterTheCheckIsRefused()
    {
        // The size on disk said 32 bytes, under a ceiling of 64; by the time the
        // bytes are read another program has kept writing. What ARRIVED is
        // measured, and the read is asked for no more than one byte past the
        // ceiling — enough to tell "over" from "exactly at" and no more, so a file
        // that grew to a gigabyte is never a gigabyte in memory.
        var path = Write("growing.png", PngOf(32));
        var spy = new ReadSpy((_, limit) => PngOf((int)limit));   // it grew past all the read will take

        var result = await PickedPicture.ReadAsync(path, ceiling: 64, read: spy.Read);

        Assert.Null(result.Markdown);
        Assert.Equal(PictureLimit.Notice("growing.png", ceiling: 64), result.Notice);
        var call = Assert.Single(spy.Calls);
        Assert.Equal(path, call.Path);
        Assert.Equal(65, call.Limit);
    }

    [Fact]
    public async Task APictureUnderTheCeilingGivesTheMarkdownInsertPictureAlwaysGave()
    {
        // Byte for byte what Picture_Click built before there was a ceiling: the
        // whole file, its name for the alt text, its extension for the MIME type.
        var bytes = PngOf(5000);
        var path = Write("photo.png", bytes);

        var result = await PickedPicture.ReadAsync(path);

        Assert.Null(result.Notice);
        Assert.Equal(
            ImageMarkdown.Fragment(ImageMarkdown.AltText(path), ImageMarkdown.MimeForImage(path), await File.ReadAllBytesAsync(path)),
            result.Markdown);
        Assert.Equal($"![photo](data:image/png;base64,{Convert.ToBase64String(bytes)})", result.Markdown);
    }

    [Fact]
    public async Task ExactlyAtTheCeilingThePictureGoesIn()
    {
        // The boundary on both checks: the size on disk and the bytes that arrive
        // are each exactly the ceiling, and neither is "over".
        var bytes = PngOf(64);
        var path = Write("at.png", bytes);
        var spy = new ReadSpy((_, _) => bytes);

        Assert.Equal(ImageMarkdown.Fragment("at", "image/png", bytes),
            (await PickedPicture.ReadAsync(path, ceiling: 64)).Markdown);
        Assert.Equal(ImageMarkdown.Fragment("at", "image/png", bytes),
            (await PickedPicture.ReadAsync(path, ceiling: 64, read: spy.Read)).Markdown);
    }

    [Fact]
    public async Task TheCeilingIsTheOnlyNewCheckSoAnSvgAndAMislabelledFileStillGoIn()
    {
        // The drop checks the bytes that arrive against the format they sniffed as
        // (DropRouting.PictureSurvivedTheRead). Insert ▸ Picture never has: it
        // embeds an SVG, which has no signature to sniff, and a file whose name and
        // content disagree, under the MIME its extension gives. Reusing the drop's
        // whole check would refuse both, so only its ceiling half is reused.
        var svg = Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"/>");
        Assert.Equal(ImageMarkdown.Fragment("logo", "image/svg+xml", svg),
            (await PickedPicture.ReadAsync(Write("logo.svg", svg))).Markdown);

        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F'];
        Assert.Equal(ImageMarkdown.Fragment("mislabelled", "image/png", jpeg),
            (await PickedPicture.ReadAsync(Write("mislabelled.png", jpeg))).Markdown);
    }

    [Fact]
    public async Task APickedAndADroppedPictureAreTheSameMarkdown()
    {
        // The parity DropRoutingTests pins on the fragment builder, here on the two
        // routes' actual reads of one file: pick photo.png or drop it, and the
        // document gets the same string.
        var path = Write("photo.png", PngOf(300));
        var dropped = DropFiles.Read(path);
        var (kind, mime) = DropRouting.Classify(dropped.Name, dropped.Head, dropped.Size);
        Assert.Equal(DropKind.Picture, kind);

        Assert.Equal(
            DropRouting.PictureMarkdown(dropped.Name, mime!, await DropFiles.ReadAllAsync(path)),
            (await PickedPicture.ReadAsync(path)).Markdown);
    }

    [Fact]
    public async Task AFileAnotherProgramIsWritingIsStillRefusedAsItAlwaysWas()
    {
        // Insert ▸ Picture read with File.ReadAllBytesAsync, whose FileShare.Read
        // collides with a writer's handle, so a picture still being written was an
        // error message rather than a picture. It still is. The drop tolerates the
        // writer because it then checks the bytes' signature and length; this route
        // checks only the ceiling, so reading a half-written file here would embed a
        // picture no viewer can decode.
        var path = Write("shot.png", PngOf(100));
        using var writer = HeldByAWriter(path);

        await Assert.ThrowsAsync<IOException>(() => File.ReadAllBytesAsync(path));   // what it was
        await Assert.ThrowsAsync<IOException>(() => PickedPicture.ReadAsync(path));  // what it is
    }

    [Fact]
    public async Task AFileThatHasGoneStillThrowsForTheMessageBox()
    {
        // Picture_Click turns a throw into "Couldn't read the image:" and inserts
        // nothing. A file deleted between the picker and the read still throws what
        // it always did — now from the size check, which comes first.
        var gone = Path.Combine(_dir, "gone.png");
        await Assert.ThrowsAsync<FileNotFoundException>(() => File.ReadAllBytesAsync(gone));
        await Assert.ThrowsAsync<FileNotFoundException>(() => PickedPicture.ReadAsync(gone));
    }

    [Fact]
    public async Task TheDefaultReadIsBoundedAndSharesAsFileReadAllBytesDid()
    {
        // The read ReadAsync uses unless a test hands it another: the drop's bounded
        // read (DropFiles.ReadAllAsync), with File.ReadAllBytesAsync's sharing.
        var bytes = PngOf(100);
        var path = Write("big.png", bytes);
        Assert.Equal(65, (await PickedPicture.ReadBoundedAsync(path, 65)).Length);
        Assert.Equal(bytes, await PickedPicture.ReadBoundedAsync(path, 101));

        using var writer = HeldByAWriter(path);
        await Assert.ThrowsAsync<IOException>(() => PickedPicture.ReadBoundedAsync(path, 101));
    }

    [Fact]
    public void InsertPictureGoesThroughPickedPicture()
    {
        // A wiring pin, read from the source: Picture_Click is a window handler no
        // test can run, so this is the only check that the menu item and the
        // toolbar button (both wired to it in MainWindow.xaml) take the route tested
        // above rather than a read of their own. It proves the call is there, not
        // what it does; the tests above do that.
        var body = MethodBody(
            File.ReadAllText(Path.Combine(RepoRoot(), "src", "MarkdownMidget", "MainWindow.xaml.cs")),
            "private async void Picture_Click(");
        Assert.Contains("await PickedPicture.ReadAsync(picked)", body, StringComparison.Ordinal);
        Assert.Contains("FlashStatus(", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadAllBytes", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ImageMarkdown.Fragment", body, StringComparison.Ordinal);
    }

    /// <summary>A method's text, from its signature to the closing brace at member
    /// indentation.</summary>
    private static string MethodBody(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"no \"{signature}\" in the source");
        var end = source.IndexOf("\n    }", start, StringComparison.Ordinal);
        Assert.True(end > start, $"no end to \"{signature}\"");
        return source[start..end];
    }

    /// <summary>The repository, found from the test assembly (as MenuPathsInDocsTests
    /// finds it).</summary>
    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (dir.EnumerateFiles("MarkdownMidget.sln*").Any()
                || (dir.EnumerateDirectories("src").Any() && dir.EnumerateFiles("HELP.md").Any()))
                return dir.FullName;
        throw new InvalidOperationException(
            $"No MarkdownMidget.sln[x] (or src/ beside HELP.md) above {AppContext.BaseDirectory}");
    }
}
