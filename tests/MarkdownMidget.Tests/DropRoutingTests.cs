using System.Text;
using System.Text.Json;
using MarkdownMidget;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>
/// Issue #6 (release-1.0 stage 4, row I1): a file dropped on either view is routed
/// by what it IS, not by what its name says. A picture — recognised by its magic
/// bytes — is embedded exactly as Insert ▸ Picture would embed it; a markdown or
/// text file opens as any drop opened it before; anything else is refused by name
/// and never replaces the document. The routing is a pure function of name and
/// content so every rule here runs without a window, a WebView or a file.
/// </summary>
public class DropRoutingTests
{
    // ===== sample heads: the first bytes of real files of each kind =====

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D, (byte)'I', (byte)'H', (byte)'D', (byte)'R'];
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0x00];
    private static readonly byte[] Gif = Encoding.ASCII.GetBytes("GIF89a\x01\x00\x01\x00");
    private static readonly byte[] Webp = Encoding.ASCII.GetBytes("RIFF\x24\x00\x00\x00WEBPVP8 ");
    private static readonly byte[] Bmp = BmpHead();
    private static readonly byte[] Svg = Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?>\n<svg xmlns=\"http://www.w3.org/2000/svg\"/>");
    private static readonly byte[] Text = Encoding.UTF8.GetBytes("# Notes\n\nSome text.\n");

    /// <summary>A BITMAPFILEHEADER (14 bytes) and the start of a BITMAPINFOHEADER:
    /// "BM", the file size, four reserved zero bytes, the pixel offset (54), and the
    /// DIB header's own size (40) — the field the sniff keys on.</summary>
    private static byte[] BmpHead()
    {
        var b = new byte[32];
        b[0] = (byte)'B'; b[1] = (byte)'M';
        b[2] = 0x7A; b[3] = 0; b[4] = 0; b[5] = 0;      // bfSize = 122
        b[10] = 54;                                     // bfOffBits
        b[14] = 40;                                     // biSize
        return b;
    }

    private static DroppedFile File(string name, byte[]? head) => new(name, head);

    // ===== I1a/I1b: what counts as a picture =====

    public static TheoryData<string, byte[]> PicturesByExtension => new()
    {
        { ".png", Png }, { ".jpg", Jpeg }, { ".jpeg", Jpeg }, { ".gif", Gif }, { ".webp", Webp }, { ".bmp", Bmp },
    };

    [Theory]
    [MemberData(nameof(PicturesByExtension))]
    public void SniffedMimeMatchesInsertPicture(string extension, byte[] head)
    {
        // The formats a drop embeds are exactly the raster formats Insert ▸ Picture
        // embeds, and the MIME type written into the data URI is the one it would
        // write for a file of that extension — so a dropped photo.png and a picked
        // photo.png carry the same prefix.
        Assert.Equal(ImageMarkdown.MimeForImage("photo" + extension), DropRouting.SniffImageMime(head));
    }

    [Fact]
    public void SvgAndTextAreNotPictures()
    {
        // SVG is XML text with no magic bytes; a drop cannot tell it from any other
        // XML, so it is not sniffed as a picture (Insert ▸ Picture still embeds it
        // by extension). Plain text is not a picture either.
        Assert.Null(DropRouting.SniffImageMime(Svg));
        Assert.Null(DropRouting.SniffImageMime(Text));
    }

    [Fact]
    public void TextStartingWithBmIsNotABitmap()
    {
        // "BM" is two bytes of magic; a review of a car starts the same way. The DIB
        // header size at bytes 14..17 is a small little-endian number in a real BMP
        // (three zero bytes) and letters in prose, which is what keeps this text
        // out of the picture route.
        var prose = Encoding.UTF8.GetBytes("BMW review\n\nThe car is fine.\n");
        Assert.Null(DropRouting.SniffImageMime(prose));
        Assert.Equal(DropKind.Refused, DropRouting.Classify("BMW review.bmp", prose).Kind);
    }

    [Fact]
    public void BothGifVersionsAreSniffed()
    {
        // GIF89a is the version every fixture above uses, so the GIF87a branch was
        // deletable without a single test noticing. A 1987 GIF is still a GIF, and
        // Insert ▸ Picture would embed it as image/gif.
        var gif87 = Encoding.ASCII.GetBytes("GIF87a\x01\x00\x01\x00");
        Assert.Equal("image/gif", DropRouting.SniffImageMime(gif87));
        Assert.Equal("image/gif", DropRouting.SniffImageMime(Gif));           // GIF89a, for the pair
        Assert.Equal(DropKind.Picture, DropRouting.Classify("old.gif", gif87).Kind);
        // Neither the version digits nor the trailing 'a' is optional: "GIF" alone,
        // and a plausible-looking wrong version, are not pictures.
        Assert.Null(DropRouting.SniffImageMime(Encoding.ASCII.GetBytes("GIF88a\x01\x00")));
        Assert.Null(DropRouting.SniffImageMime(Encoding.ASCII.GetBytes("GIF87b\x01\x00")));
    }

    [Fact]
    public void NearMissSignaturesAreNotPictures()
    {
        // Each of these differs from a real signature in exactly the byte a weakened
        // check would stop looking at, so each one keeps its branch honest.

        // JPEG is FF D8 FF: the third byte is part of the magic, not padding. A file
        // that starts FF D8 00 is not a JPEG, and embedding it as image/jpeg would
        // produce a broken image in the document.
        Assert.Null(DropRouting.SniffImageMime([0xFF, 0xD8, 0x00, 0xE0, 0x00, 0x10]));
        Assert.Equal("image/jpeg", DropRouting.SniffImageMime([0xFF, 0xD8, 0xFF, 0xE0]));

        // The PNG signature is eight bytes, and the four after \x89PNG are the point
        // of it: CR LF SUB LF catches a transfer that translated line endings. A file
        // whose CR was eaten is a CORRUPT png, not a png — so \x89PNG alone must not
        // be enough to match.
        Assert.Null(DropRouting.SniffImageMime([0x89, 0x50, 0x4E, 0x47, 0x0A, 0x0A, 0x1A, 0x0A]));
        Assert.Null(DropRouting.SniffImageMime([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0D]));
        Assert.Equal("image/png", DropRouting.SniffImageMime(Png));
    }

    [Fact]
    public void ShortHeadIsNotAPicture()
    {
        Assert.Null(DropRouting.SniffImageMime([]));
        Assert.Null(DropRouting.SniffImageMime([0x89, 0x50]));            // a PNG signature cut short
        Assert.Null(DropRouting.SniffImageMime(Encoding.ASCII.GetBytes("RIFF"))); // a RIFF with no form tag
        Assert.Null(DropRouting.SniffImageMime([(byte)'B', (byte)'M']));   // "BM" with no header behind it
    }

    [Theory]
    [MemberData(nameof(PicturesByExtension))]
    public void SniffLengthCoversEverySignature(string extension, byte[] head)
    {
        // The chrome route reads only SniffLength bytes of each dropped file before
        // deciding; every signature must fit, or a picture would be refused by name.
        var truncated = head[..Math.Min(head.Length, DropRouting.SniffLength)];
        Assert.Equal(ImageMarkdown.MimeForImage("x" + extension), DropRouting.SniffImageMime(truncated));
    }

    // ===== I1c/I1d/I1e/I1f: routing one file =====

    [Fact]
    public void PictureContentBeatsMarkdownName()
    {
        var (kind, mime) = DropRouting.Classify("photo.md", Png);
        Assert.Equal(DropKind.Picture, kind);
        Assert.Equal("image/png", mime);
    }

    [Fact]
    public void TextInAPictureNameIsRefused()
    {
        // notes.png that is really text: not a picture (no magic), not a document
        // (not a markdown name) — refused, rather than embedded as garbage or opened.
        var (kind, mime) = DropRouting.Classify("notes.png", Text);
        Assert.Equal(DropKind.Refused, kind);
        Assert.Null(mime);
    }

    [Fact]
    public void OpenDialogExtensionsRouteAsDocuments()
    {
        // The names a drop opens are the names File ▸ Open lists (with the encrypted
        // container included: the open path detects and prompts for it), read from
        // the filter itself so the two cannot drift.
        var listed = Secure.SecureUi.OpenFilter(includeEncrypted: true)
            .Split('|')
            .Where((_, i) => i % 2 == 1)                     // the pattern halves
            .SelectMany(p => p.Split(';'))
            .Where(p => p != "*.*")
            .Select(p => p.TrimStart('*'))
            .ToList();
        Assert.NotEmpty(listed);
        foreach (var ext in listed)
        {
            Assert.Contains(ext, DropRouting.DocumentExtensions, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(DropKind.Document, DropRouting.Classify("notes" + ext, Text).Kind);
            Assert.Equal(DropKind.Document, DropRouting.Classify("NOTES" + ext.ToUpperInvariant(), Text).Kind);
        }
        Assert.Equal(listed.Count, DropRouting.DocumentExtensions.Length);
        // An empty file with a markdown name is still a document: an empty document.
        Assert.Equal(DropKind.Document, DropRouting.Classify("empty.md", []).Kind);
    }

    [Fact]
    public void OtherAndUnreadableFilesAreRefused()
    {
        Assert.Equal(DropKind.Refused, DropRouting.Classify("archive.zip", Text).Kind);
        Assert.Equal(DropKind.Refused, DropRouting.Classify("README", Text).Kind);   // no extension
        Assert.Equal(DropKind.Refused, DropRouting.Classify("page.html", Text).Kind);
        // A null head is a file whose bytes could not be read (a dropped folder,
        // say): refused whatever its name, since there is nothing to embed or open.
        Assert.Equal(DropKind.Refused, DropRouting.Classify("notes.md", null).Kind);
        Assert.Equal(DropKind.Refused, DropRouting.Classify("photo.png", null).Kind);
    }

    // ===== I1g/I1h: the markdown that goes in =====

    [Fact]
    public void DroppedPictureMarkdownMatchesInsertPicture()
    {
        // Insert ▸ Picture: alt text is the file name without its extension, the MIME
        // type comes from the extension, the bytes are the file. A dropped
        // photo.png must produce the identical string.
        var picked = ImageMarkdown.Fragment(ImageMarkdown.AltText(@"C:\pics\photo.png"), ImageMarkdown.MimeForImage("photo.png"), Png);
        var (_, mime) = DropRouting.Classify("photo.png", Png);
        Assert.Equal(picked, DropRouting.PictureMarkdown("photo.png", mime!, Png));
        Assert.Equal($"![photo](data:image/png;base64,{Convert.ToBase64String(Png)})", picked);
    }

    [Fact]
    public void DroppedPictureMarkdownCarriesTheSniffedMimeNotTheNamesMime()
    {
        // The case where the two disagree, which is the whole point of sniffing: a PNG
        // named photo.md. The data URI must say image/png — what the bytes ARE — and
        // not what the extension claims (MimeForImage(".md") is application/octet-stream,
        // which no browser renders). Without this case, PictureMarkdown could ignore the
        // sniffed mime entirely and every other test would still pass.
        var (kind, mime) = DropRouting.Classify("photo.md", Png);
        Assert.Equal(DropKind.Picture, kind);
        Assert.Equal("application/octet-stream", ImageMarkdown.MimeForImage("photo.md"));  // the wrong answer
        var md = DropRouting.PictureMarkdown("photo.md", mime!, Png);
        Assert.StartsWith("![photo](data:image/png;base64,", md);
        Assert.Equal($"![photo](data:image/png;base64,{Convert.ToBase64String(Png)})", md);
    }

    [Fact]
    public void SinglePictureHasNoSeparator()
    {
        var one = DropRouting.PictureMarkdown("a.png", "image/png", Png);
        Assert.Equal(one, DropRouting.Markdown([one]));
    }

    [Fact]
    public void PicturesInsertInDropOrder()
    {
        var plan = DropRouting.Plan([File("b.jpg", Jpeg), File("a.png", Png), File("c.gif", Gif)], DropTarget.Editable, oneDocument: false);

        Assert.Equal([0, 1, 2], plan.Insert.Select(p => p.Index));
        Assert.Equal(["image/jpeg", "image/png", "image/gif"], plan.Insert.Select(p => p.Mime));
        Assert.Empty(plan.Open);
        Assert.Empty(plan.Refused);
        Assert.Null(plan.Notice());

        // Several pictures go in as one fragment, each on its own line, so the drop
        // is one insertion (and one undo step) in either view.
        var md = DropRouting.Markdown(["![b](x)", "![a](y)"]);
        Assert.Equal("![b](x)\n\n![a](y)", md);
    }

    // ===== I1i/I1j/I1k: several files, and the state of the window =====

    [Fact]
    public void MarkdownOpensOnlyWhenNoPictureWasDropped()
    {
        var alone = DropRouting.Plan([File("notes.md", Text)], DropTarget.Editable, oneDocument: false);
        Assert.Equal([0], alone.Open);
        Assert.Empty(alone.NotOpened);
        Assert.Null(alone.Notice());

        var mixed = DropRouting.Plan([File("notes.md", Text), File("photo.png", Png)], DropTarget.Editable, oneDocument: false);
        Assert.Equal([1], mixed.Insert.Select(p => p.Index));
        Assert.Empty(mixed.Open);                                 // the picture took the drop
        Assert.Equal([0], mixed.NotOpened);
        Assert.Equal("Not opened (pictures were dropped with it): notes.md", mixed.Notice());
    }

    [Fact]
    public void OneDocumentSurfaceOpensTheFirstAndNamesTheRest()
    {
        // The formatted view receives content, not paths, and can open one document
        // at a time; the chrome and source view receive paths and open every one
        // (in place, then in new windows), as they always did.
        var files = new[] { File("a.md", Text), File("b.txt", Text) };

        var content = DropRouting.Plan(files, DropTarget.Editable, oneDocument: true);
        Assert.Equal([0], content.Open);
        Assert.Equal([1], content.NotOpened);
        Assert.Equal("Not opened (one document per drop here): b.txt", content.Notice());

        var paths = DropRouting.Plan(files, DropTarget.Editable, oneDocument: false);
        Assert.Equal([0, 1], paths.Open);
        Assert.Empty(paths.NotOpened);
        Assert.Null(paths.Notice());
    }

    [Theory]
    [InlineData(DropTarget.ReadOnly, "Read-only, so not inserted: photo.png")]
    [InlineData(DropTarget.NoDocument, "No document open, so not inserted: photo.png")]
    public void ReadOnlyRefusesPicturesButOpensDocuments(DropTarget target, string notice)
    {
        // A picture is an edit, and a read-only (or closed) window takes none — the
        // same gate that ignores a pasted picture. A markdown file dropped with it
        // still opens: no picture was inserted, so the document rule stands.
        var plan = DropRouting.Plan([File("photo.png", Png), File("notes.md", Text)], target, oneDocument: false);
        Assert.Empty(plan.Insert);
        Assert.Equal([0], plan.NotInserted);
        Assert.Equal([1], plan.Open);
        Assert.Empty(plan.NotOpened);
        Assert.Equal(notice, plan.Notice());
    }

    // ===== I1l: what the status line says =====

    [Fact]
    public void NoticeNamesEveryFileSetAside()
    {
        var plan = DropRouting.Plan(
            [File("a.zip", Text), File("photo.png", Png), File("notes.md", Text), File("folder", null), File("b.md", Text)],
            DropTarget.Editable, oneDocument: false);

        Assert.Equal([1], plan.Insert.Select(p => p.Index));
        Assert.Equal([2, 4], plan.NotOpened);
        Assert.Equal([0, 3], plan.Refused);
        Assert.Equal(
            "Not a picture or a markdown file: a.zip, folder; Not opened (pictures were dropped with it): notes.md, b.md",
            plan.Notice());
    }

    [Fact]
    public void NoticeIsNullWhenEverythingWasTaken()
    {
        Assert.Null(DropRouting.Plan([File("a.png", Png)], DropTarget.Editable, oneDocument: false).Notice());
        Assert.Null(DropRouting.Plan([File("a.md", Text)], DropTarget.Editable, oneDocument: true).Notice());
        Assert.Null(DropRouting.Plan([], DropTarget.Editable, oneDocument: true).Notice());
    }

    [Fact]
    public void RefusedOnlyDropTouchesNothing()
    {
        // The issue's case, generalised: a drop that is neither picture nor markdown
        // inserts nothing and opens nothing, whatever the window's state.
        foreach (var target in Enum.GetValues<DropTarget>())
        {
            var plan = DropRouting.Plan([File("setup.exe", [0x4D, 0x5A])], target, oneDocument: true);
            Assert.Empty(plan.Insert);
            Assert.Empty(plan.Open);
            Assert.Equal([0], plan.Refused);
            Assert.Equal("Not a picture or a markdown file: setup.exe", plan.Notice());
        }
    }

    // ===== the size ceiling on an embedded picture =====

    [Fact]
    public void APictureLargerThanTheCeilingIsNamedRatherThanEmbedded()
    {
        // A picture goes into the document as base64 — about 4/3 its size, in the
        // markdown, in the editor's copy, and in every save. Past the ceiling the
        // file is named in the status line instead. Decided from the SIZE, because
        // only the head has been read at this point: the bytes are fetched after the
        // plan chooses, which is the whole reason the ceiling can be applied at all.
        Assert.Equal(DropKind.Picture, DropRouting.Classify("ok.png", Png, DropRouting.MaxPictureBytes).Kind);
        Assert.Equal(DropKind.TooLarge, DropRouting.Classify("huge.png", Png, DropRouting.MaxPictureBytes + 1).Kind);
        // A size the drop did not state (-1) is not a size over the ceiling.
        Assert.Equal(DropKind.Picture, DropRouting.Classify("unknown.png", Png).Kind);
        Assert.Equal(DropKind.Picture, DropRouting.Classify("unknown.png", Png, -1).Kind);

        var plan = DropRouting.Plan(
            [new DroppedFile("huge.png", Png, DropRouting.MaxPictureBytes + 1), new DroppedFile("ok.png", Png, 4096)],
            DropTarget.Editable, oneDocument: false);
        Assert.Equal([1], plan.Insert.Select(p => p.Index));
        Assert.Equal([0], plan.TooLarge);
        Assert.Empty(plan.Refused);
        Assert.Equal("Too large to insert (over 64 MB): huge.png", plan.Notice());
    }

    [Fact]
    public void TheCeilingAppliesToPicturesOnlyAndDoesNotStopADocumentOpening()
    {
        // A dropped markdown or text file is OPENED, and File ▸ Open has never
        // capped what it opens — capping it here would refuse a file the same user
        // can open from the menu a second later.
        Assert.Equal(DropKind.Document, DropRouting.Classify("huge.md", Text, DropRouting.MaxPictureBytes * 10).Kind);

        // And a too-large picture is not an insertion, so it does not take the drop
        // away from a markdown file dropped with it — exactly as a refused file does not.
        var plan = DropRouting.Plan(
            [new DroppedFile("huge.png", Png, DropRouting.MaxPictureBytes + 1), new DroppedFile("notes.md", Text, 20)],
            DropTarget.Editable, oneDocument: false);
        Assert.Empty(plan.Insert);
        Assert.Equal([0], plan.TooLarge);
        Assert.Equal([1], plan.Open);
        Assert.Equal("Too large to insert (over 64 MB): huge.png", plan.Notice());
    }

    // ===== the bytes that actually arrived (review finding NF-11) =====

    [Fact]
    public void BytesThatNoLongerSniffAsThePictureTheyRoutedAsAreRefused()
    {
        // The ceiling and the format are decided from a HEAD and a SIZE that are a
        // sniff (or a round trip) old. F-8's own motivating case is a file another
        // process is still writing: a screenshot tool that had flushed its PNG header
        // and nothing else when the drop routed it. What is embedded is what actually
        // arrived, so what actually arrived is checked — the cheapest honest check
        // being that it still starts the way its format does.
        Assert.True(DropRouting.PictureSurvivedTheRead(Png, "image/png"));
        // Truncated below its own signature: eight bytes are the whole PNG magic, so
        // four of them are not a PNG any viewer will decode from the data URI.
        Assert.False(DropRouting.PictureSurvivedTheRead(Png[..4], "image/png"));
        // Read as nothing at all — the file was reset between the sniff and the read.
        Assert.False(DropRouting.PictureSurvivedTheRead([], "image/png"));
        // Still a picture, but not the one that was routed: the alt text, the MIME in
        // the data URI and the bytes would disagree.
        Assert.False(DropRouting.PictureSurvivedTheRead(Jpeg, "image/png"));
        // And text where a picture was: refused rather than embedded as garbage.
        Assert.False(DropRouting.PictureSurvivedTheRead(Text, "image/png"));
    }

    [Fact]
    public void BytesOverTheCeilingAreRefusedEvenThoughTheSizeSaidOtherwise()
    {
        // The other direction: the file GREW past the ceiling while it was being
        // written, so the size the plan allowed is not the size that arrived. The
        // ceiling is a parameter here only so the boundary can be tested without
        // allocating 64 MB; the default is the real one, pinned below.
        var bytes = new byte[64];
        Png.CopyTo(bytes, 0);
        Assert.True(DropRouting.PictureSurvivedTheRead(bytes, "image/png", ceiling: 64));
        Assert.False(DropRouting.PictureSurvivedTheRead(bytes, "image/png", ceiling: 63));
    }

    [Fact]
    public void TheCeilingOnBytesReadIsTheSameOneThePlanApplied()
    {
        // Two ceilings that could drift apart is one ceiling too many: a picture the
        // plan allowed must not be refused by the read, or refused by the plan and
        // allowed by the read.
        var bytes = new byte[16];
        Png.CopyTo(bytes, 0);
        Assert.True(DropRouting.PictureSurvivedTheRead(bytes, "image/png"));
        Assert.True(DropRouting.PictureSurvivedTheRead(bytes, "image/png", DropRouting.MaxPictureBytes));
        // The classify side of the same boundary, so the pair is visible in one test.
        Assert.Equal(DropKind.Picture, DropRouting.Classify("ok.png", Png, DropRouting.MaxPictureBytes).Kind);
        Assert.Equal(DropKind.TooLarge, DropRouting.Classify("over.png", Png, DropRouting.MaxPictureBytes + 1).Kind);
    }

    [Fact]
    public void NoticeNamesEveryReasonAtOnce()
    {
        var plan = DropRouting.Plan(
            [
                new DroppedFile("a.zip", Text, 10),
                new DroppedFile("huge.png", Png, DropRouting.MaxPictureBytes + 1),
                new DroppedFile("photo.png", Png, 16),
                new DroppedFile("notes.md", Text, 20),
            ],
            DropTarget.Editable, oneDocument: false);

        Assert.Equal(
            "Not a picture or a markdown file: a.zip; Too large to insert (over 64 MB): huge.png; "
            + "Not opened (pictures were dropped with it): notes.md",
            plan.Notice());
    }

    // ===== I1m: the formatted view's two messages =====

    [Fact]
    public void FileDropMessageCarriesNameSizeAndHeadOnly()
    {
        // Phase one. The editor sends each file's name, size and FIRST bytes — never
        // the whole file, which is what stopped a dropped 200 MB video from being
        // base64'd into a web message before anything had decided it was not a
        // picture. A head that failed to read is null; anything malformed is treated
        // as unreadable rather than thrown at the message pump.
        var json = JsonSerializer.Serialize(new
        {
            type = "fileDrop",
            drop = 7,
            files = new object[]
            {
                new { name = "photo.png", size = 120_000, headBase64 = Convert.ToBase64String(Png) },
                new { name = "folder", size = 0, headBase64 = (string?)null },
                new { name = "bad.md", size = 12, headBase64 = "not base64!" },
                new { name = "empty.md", size = 0, headBase64 = "" },
            },
        });
        using var doc = JsonDocument.Parse(json);
        var message = DropRouting.ParseMessage(doc.RootElement);

        Assert.Equal(7, message.Drop);
        var files = message.Files;
        Assert.Equal(4, files.Count);
        Assert.Equal("photo.png", files[0].Name);
        Assert.Equal(Png, files[0].Head);
        Assert.Equal(120_000, files[0].Size);
        Assert.Null(files[1].Head);
        Assert.Null(files[2].Head);
        var empty = files[3].Head;
        Assert.NotNull(empty);
        Assert.Empty(empty);
    }

    [Fact]
    public void AHeadLongerThanSniffLengthIsTolerated()
    {
        // The editor's head length is its own constant, not one read from here. A
        // head longer than SniffLength must route on its prefix rather than be
        // rejected, so the two can differ without breaking the drop.
        var longHead = new byte[DropRouting.SniffLength * 4];
        Png.CopyTo(longHead, 0);
        var json = JsonSerializer.Serialize(new
        {
            files = new[] { new { name = "photo.png", size = 900, headBase64 = Convert.ToBase64String(longHead) } },
        });
        using var doc = JsonDocument.Parse(json);
        var file = Assert.Single(DropRouting.ParseMessage(doc.RootElement).Files);

        Assert.Equal(longHead.Length, file.Head!.Length);
        Assert.Equal(DropKind.Picture, DropRouting.Classify(file.Name, file.Head, file.Size).Kind);
    }

    [Fact]
    public void MissingOrNonsenseSizeMeansNoCeiling()
    {
        // The editor always sends File.size; a message without a usable one says -1,
        // and a picture is not refused because a field went missing.
        var json = JsonSerializer.Serialize(new
        {
            files = new object[]
            {
                new { name = "no-size.png", headBase64 = Convert.ToBase64String(Png) },
                new { name = "text-size.png", size = "lots", headBase64 = Convert.ToBase64String(Png) },
                new { name = "negative.png", size = -5, headBase64 = Convert.ToBase64String(Png) },
            },
        });
        using var doc = JsonDocument.Parse(json);
        var files = DropRouting.ParseMessage(doc.RootElement).Files;

        Assert.All(files, f => Assert.Equal(-1, f.Size));
        Assert.All(files, f => Assert.Equal(DropKind.Picture, DropRouting.Classify(f.Name, f.Head, f.Size).Kind));
    }

    [Fact]
    public void MessageWithoutFilesIsEmpty()
    {
        using var none = JsonDocument.Parse("{\"type\":\"fileDrop\",\"drop\":3}");
        var parsed = DropRouting.ParseMessage(none.RootElement);
        Assert.Equal(3, parsed.Drop);              // the drop id survives, so a stale answer is still detectable
        Assert.Empty(parsed.Files);
        using var notAnArray = JsonDocument.Parse("{\"type\":\"fileDrop\",\"files\":\"photo.png\"}");
        Assert.Empty(DropRouting.ParseMessage(notAnArray.RootElement).Files);
        using var noDrop = JsonDocument.Parse("{\"type\":\"fileDrop\",\"files\":[]}");
        Assert.Equal(0, DropRouting.ParseMessage(noDrop.RootElement).Drop);
    }

    [Fact]
    public void MessageFileWithoutANameStillArrives()
    {
        // A File always has a name in the browser; if one ever arrived without, the
        // bytes are still routed (a picture is still a picture) under a placeholder.
        using var doc = JsonDocument.Parse("{\"files\":[{\"headBase64\":\"\"}]}");
        var file = Assert.Single(DropRouting.ParseMessage(doc.RootElement).Files);
        Assert.Equal("Dropped", file.Name);
        Assert.NotNull(file.Head);
        Assert.Empty(file.Head);
        Assert.Equal(-1, file.Size);
    }

    [Fact]
    public void BytesMessageIsIndexedByPositionInTheDrop()
    {
        // Phase two: the bytes of the files the plan chose, keyed by their index in
        // the fileDrop list — indices rather than names because two files from
        // different folders can share one. A null is a file the editor could not
        // read, and malformed base64 is the same thing.
        var json = JsonSerializer.Serialize(new
        {
            type = "droppedFileBytes",
            drop = 4,
            files = new object[]
            {
                new { index = 2, base64 = Convert.ToBase64String(Png) },
                new { index = 0, base64 = (string?)null },
                new { index = 5, base64 = "not base64!" },
                new { index = 6, base64 = "" },
            },
        });
        using var doc = JsonDocument.Parse(json);
        var answer = DropRouting.ParseBytesMessage(doc.RootElement);

        Assert.Equal(4, answer.Drop);
        Assert.Equal(Png, answer.Bytes[2]);
        Assert.Null(answer.Bytes[0]);
        Assert.Null(answer.Bytes[5]);
        Assert.Empty(answer.Bytes[6]!);            // an empty file is read, not unreadable
        Assert.False(answer.Bytes.ContainsKey(1)); // an index never asked about is absent, not null
    }

    [Fact]
    public void BytesMessageSurvivesAMalformedEntry()
    {
        // An entry with no usable index is skipped rather than throwing at the
        // message pump — the host then finds no bytes for that index and refuses to
        // insert, which is the safe direction.
        using var doc = JsonDocument.Parse(
            "{\"drop\":1,\"files\":[{\"base64\":\"QUJD\"},{\"index\":\"two\",\"base64\":\"QUJD\"},{\"index\":3,\"base64\":\"QUJD\"}]}");
        var answer = DropRouting.ParseBytesMessage(doc.RootElement);

        Assert.Equal(1, answer.Drop);
        Assert.Equal(3, Assert.Single(answer.Bytes).Key);

        using var empty = JsonDocument.Parse("{\"drop\":9}");
        var none = DropRouting.ParseBytesMessage(empty.RootElement);
        Assert.Equal(9, none.Drop);
        Assert.Empty(none.Bytes);
    }

    // ===== ImageMarkdown's share =====

    [Fact]
    public void AltTextIsTheFileNameWithoutExtension()
    {
        Assert.Equal("photo", ImageMarkdown.AltText(@"C:\pics\photo.png"));
        Assert.Equal("photo", ImageMarkdown.AltText("photo.png"));
        Assert.Equal("my.photo", ImageMarkdown.AltText("my.photo.jpeg"));
    }

    [Theory]
    [InlineData("a.png", "image/png")]
    [InlineData("a.JPG", "image/jpeg")]
    [InlineData("a.jpeg", "image/jpeg")]
    [InlineData("a.gif", "image/gif")]
    [InlineData("a.webp", "image/webp")]
    [InlineData("a.bmp", "image/bmp")]
    [InlineData("a.svg", "image/svg+xml")]
    [InlineData("a.tiff", "application/octet-stream")]
    public void MimeForImageIsInsertPicturesTable(string path, string mime) =>
        Assert.Equal(mime, ImageMarkdown.MimeForImage(path));
}
