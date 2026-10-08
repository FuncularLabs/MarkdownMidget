using System.Buffers.Binary;
using System.IO;
using System.Text;
using MarkdownMidget.Picker;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>
/// Quick access in the built-in picker's Places (#12, item 5): the folders pinned in Explorer,
/// read from its jump list with no shell. Every jump list here is built by
/// <see cref="JumpListWriter"/> from made-up paths; no test reads the real file in %APPDATA%.
/// </summary>
public sealed class QuickAccessTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mdm-quick-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static readonly byte[] Alpha = JumpListWriter.Link(local: @"C:\Projects\Alpha");
    private static readonly byte[] Beta = JumpListWriter.Link(share: @"\\server\share", suffix: "Beta");

    /// <summary>Alpha pinned second, Gamma recent but not pinned, Beta (on a share) pinned first,
    /// as entry 26: its link is the stream named "1a".</summary>
    private static byte[] Usual(int version = 4, int sectorShift = 9, int extraEntries = 0) => JumpListWriter.CompoundFile(
        [
            ("DestList", JumpListWriter.DestList(version,
                [(1, 1, @"C:\Projects\Alpha"), (2, -1, @"C:\Recent\Gamma"), (26, 0, @"\\server\share\Beta"),
                 .. Enumerable.Range(100, extraEntries).Select(i => ((uint)i, -1, @"C:\Recent\Filler" + i))])),
            ("1", Alpha), ("2", JumpListWriter.Link(local: @"C:\Recent\Gamma")), ("1a", Beta),
        ], sectorShift);

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void PinnedFoldersComeInPinOrderLocalAndOnAShare(int version) =>
        Assert.Equal([@"\\server\share\Beta", @"C:\Projects\Alpha"], QuickAccess.PinnedFolders(Usual(version)));

    [Theory]
    [InlineData(9, 0)]       // 512-byte sectors, everything in the mini stream
    [InlineData(9, 60)]      // a DestList too big for the mini stream
    [InlineData(12, 60)]     // a version 4 compound file: 4096-byte sectors
    public void TheSameFolderListWhereverTheStreamsSit(int sectorShift, int extraEntries) =>
        Assert.Equal([@"\\server\share\Beta", @"C:\Projects\Alpha"], QuickAccess.PinnedFolders(Usual(4, sectorShift, extraEntries)));

    [Fact]
    public void AVersion3FileIgnoresTheHighHalfOfAStreamSize()
    {
        var file = Usual();
        var at = 512 * (BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(0x30)) + 1) + 128 + 0x7C;   // DestList's size, high half
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(at), 1);   // read whole, 4 GB more than the file
        Assert.Equal([@"\\server\share\Beta", @"C:\Projects\Alpha"], QuickAccess.PinnedFolders(file));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    public void AnotherDestListVersionShowsNoSection(int version) => Assert.Empty(QuickAccess.PinnedFolders(Usual(version)));

    [Fact]
    public void HomeThisPcALibraryAndAPinnedFileAreLeftOut()
    {
        var file = JumpListWriter.CompoundFile(
        [
            ("DestList", JumpListWriter.DestList(4,
                [(1, 0, "knownfolder:{home}"), (2, 1, @"C:\Docs\a.txt"), (3, 2, @"C:\Projects\Alpha"), (4, 3, "::{library}"), (5, 4, @"c:\projects\alpha"),
                 (6, 5, "Projects")])),
            ("6", JumpListWriter.Link(local: "Projects")),                            // not a full path
            ("1", JumpListWriter.Link(local: null)),                                  // ID list only: a virtual item
            ("2", JumpListWriter.Link(local: @"C:\Docs\a.txt", folder: false)),
            ("3", Alpha),
            ("4", JumpListWriter.Link(local: null, idList: false)),
            ("5", JumpListWriter.Link(local: @"c:\projects\alpha")),                 // the same folder again
        ]);
        Assert.Equal([@"C:\Projects\Alpha"], QuickAccess.PinnedFolders(file));
    }

    [Fact]
    public void ALinkWithUnicodeStringsGivesTheUnicodePath()
    {
        const string folder = @"C:\Projekte\Ärger\Даля";
        var file = JumpListWriter.CompoundFile(
            [("DestList", JumpListWriter.DestList(4, [(7, 0, folder)])), ("7", JumpListWriter.Link(local: folder, unicode: true))]);
        Assert.Equal([folder], QuickAccess.PinnedFolders(file));
    }

    [Fact]
    public void APinnedEntryWhoseLinkIsMissingIsSkipped()
    {
        var file = JumpListWriter.CompoundFile(
            [("DestList", JumpListWriter.DestList(4, [(5, 0, @"C:\Gone"), (1, 1, @"C:\Projects\Alpha")])), ("1", Alpha)]);
        Assert.Equal([@"C:\Projects\Alpha"], QuickAccess.PinnedFolders(file));
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("not a compound file")]
    [InlineData("header signature")]
    [InlineData("1024-byte sectors")]
    [InlineData("DIFAT sectors")]
    [InlineData("truncated DestList")]
    [InlineData("no DestList")]
    [InlineData("a chain that loops")]
    [InlineData("a stream longer than the file")]
    [InlineData("a stream longer than its chain")]
    [InlineData("a negative size")]
    [InlineData("a link cut short")]
    public void AnythingBrokenShowsNoSectionAndThrowsNothing(string broken)
    {
        var file = broken switch
        {
            "empty" => [],
            "not a compound file" => Encoding.ASCII.GetBytes(new string('x', 2048)),
            // Cut inside the entry's path, after its fixed fields: only the length check sees it.
            "truncated DestList" => JumpListWriter.CompoundFile([("DestList", JumpListWriter.DestList(4, [(1, 0, @"C:\Projects\Alpha")])[..170]), ("1", Alpha)]),
            "no DestList" => JumpListWriter.CompoundFile([("1", Alpha)]),
            "a link cut short" => JumpListWriter.CompoundFile([("DestList", JumpListWriter.DestList(4, [(1, 0, @"C:\Projects\Alpha")])), ("1", Alpha[..90])]),
            "1024-byte sectors" => Usual(sectorShift: 10),   // well formed, but not a size [MS-CFB] allows
            "a negative size" => Usual(sectorShift: 12),
            _ => Usual(),
        };
        // Where the DestList's size is: the directory's second entry, in sectors of this size.
        Span<byte> DestListSize(int sector) => file.AsSpan(sector * (BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(0x30)) + 1) + 128 + 0x78);
        switch (broken)
        {
            case "header signature": file[3] ^= 0xFF; break;
            case "DIFAT sectors": file[0x48] = 1; break;
            case "a stream longer than its chain":   // still a mini stream, but past its last mini sector
                BinaryPrimitives.WriteInt32LittleEndian(DestListSize(512), BinaryPrimitives.ReadInt32LittleEndian(DestListSize(512)) + 200);
                break;
            case "a negative size":   // a version 4 file's 64-bit size, top bit set
                BinaryPrimitives.WriteInt64LittleEndian(DestListSize(4096), -100);
                break;
            case "a chain that loops":   // the directory's first sector points back at itself
                var dir = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(0x30));
                BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(512 + 4 * dir), dir);
                break;
            case "a stream longer than the file":
                BinaryPrimitives.WriteInt32LittleEndian(DestListSize(512), int.MaxValue);
                break;
        }
        Assert.Empty(QuickAccess.PinnedFolders(file));
    }

    [Fact]
    public void AMissingFileShowsNoSection() => Assert.Empty(QuickAccess.PinnedFolders(Path.Combine(_dir, "none.automaticDestinations-ms")));

    [Fact]
    public void TheFileIsReadWhileExplorerHasItOpenForWriting()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, QuickAccess.JumpListName);
        File.WriteAllBytes(path, Usual());
        using var explorer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        Assert.Equal([@"\\server\share\Beta", @"C:\Projects\Alpha"], QuickAccess.PinnedFolders(path));
    }
}

/// <summary>
/// Writes the three formats Quick access is read from, from made-up paths: an OLE compound
/// file ([MS-CFB]) with streams under 4096 bytes in the mini stream, as Windows writes them;
/// a DestList stream (format versions 3 and 4: a 32-byte header, then entries of 130 bytes
/// plus the path); and a shell link ([MS-SHLLINK]) with a LinkInfo.
/// </summary>
internal static class JumpListWriter
{
    private const uint End = 0xFFFFFFFE, Free = 0xFFFFFFFF;

    public static byte[] CompoundFile(IReadOnlyList<(string Name, byte[] Data)> streams, int sectorShift = 9)
    {
        int size = 1 << sectorShift, perSector = size / 4;
        int Sectors(long bytes) => (int)((bytes + size - 1) / size);
        var mini = new MemoryStream();
        var miniFat = new List<uint>();
        var starts = new uint[streams.Count];
        for (var i = 0; i < streams.Count; i++)
        {
            if (streams[i].Data.Length >= 4096 || streams[i].Data.Length == 0) continue;
            starts[i] = (uint)miniFat.Count;
            var count = (streams[i].Data.Length + 63) / 64;
            for (var k = 1; k <= count; k++) miniFat.Add(k == count ? End : (uint)miniFat.Count + 1);
            mini.Write(streams[i].Data);
            mini.Write(new byte[count * 64 - streams[i].Data.Length]);
        }
        var big = streams.Select(s => s.Data.Length >= 4096 ? Sectors(s.Data.Length) : 0).ToArray();
        int dirSectors = Sectors(128L * (streams.Count + 1)), miniFatSectors = Sectors(4L * miniFat.Count), miniSectors = Sectors(mini.Length);
        var rest = dirSectors + miniFatSectors + miniSectors + big.Sum();
        var fatSectors = 1;
        while (fatSectors * perSector < fatSectors + rest) fatSectors++;
        var fat = Enumerable.Repeat(Free, fatSectors * perSector).ToArray();
        var next = (uint)fatSectors;
        for (var s = 0; s < fatSectors; s++) fat[s] = 0xFFFFFFFD;
        uint Chain(int count)
        {
            if (count == 0) return End;
            var first = next;
            for (var k = 1; k <= count; k++, next++) fat[next] = k == count ? End : next + 1;
            return first;
        }
        uint dirStart = Chain(dirSectors), miniFatStart = Chain(miniFatSectors), miniStart = Chain(miniSectors);
        for (var i = 0; i < streams.Count; i++) if (big[i] > 0) starts[i] = Chain(big[i]);

        var file = new byte[size * (1 + fatSectors + rest)];
        var w = new BinaryWriter(new MemoryStream(file));
        w.Write(0xE11AB1A1E011CFD0); w.Write(new byte[16]); w.Write((ushort)0x3E); w.Write((ushort)(sectorShift == 9 ? 3 : 4));
        w.Write((ushort)0xFFFE); w.Write((ushort)sectorShift); w.Write((ushort)6); w.Write(new byte[6]);
        w.Write(sectorShift == 9 ? 0 : dirSectors); w.Write(fatSectors); w.Write(dirStart); w.Write(0); w.Write(4096);
        w.Write(miniFatStart); w.Write(miniFatSectors); w.Write(End); w.Write(0);
        for (var s = 0; s < 109; s++) w.Write(s < fatSectors ? (uint)s : Free);
        void At(uint sector) => w.Seek((int)((sector + 1) * size), SeekOrigin.Begin);
        At(0);
        foreach (var entry in fat) w.Write(entry);
        At(dirStart);
        DirectoryEntry(w, "Root Entry", 5, streams.Count > 0 ? 1u : Free, Free, mini.Length > 0 ? miniStart : End, mini.Length);
        for (var i = 0; i < streams.Count; i++)
            DirectoryEntry(w, streams[i].Name, 2, Free, i + 1 < streams.Count ? (uint)i + 2 : Free,
                streams[i].Data.Length == 0 ? End : starts[i], streams[i].Data.Length);
        if (miniFatSectors > 0) { At(miniFatStart); foreach (var entry in miniFat) w.Write(entry); }
        if (miniSectors > 0) { At(miniStart); w.Write(mini.ToArray()); }
        for (var i = 0; i < streams.Count; i++) if (big[i] > 0) { At(starts[i]); w.Write(streams[i].Data); }
        return file;
    }

    private static void DirectoryEntry(BinaryWriter w, string name, byte type, uint child, uint right, uint start, long size)
    {
        var nameBytes = Encoding.Unicode.GetBytes(name + "\0");
        w.Write(nameBytes); w.Write(new byte[64 - nameBytes.Length]); w.Write((ushort)nameBytes.Length);
        w.Write(type); w.Write((byte)1); w.Write(Free); w.Write(right); w.Write(child); w.Write(new byte[36]);
        w.Write(start); w.Write(size);
    }

    public static byte[] DestList(int version, IReadOnlyList<(uint Entry, int Pin, string Path)> entries)
    {
        var w = new BinaryWriter(new MemoryStream());
        w.Write(version); w.Write(entries.Count); w.Write(entries.Count(e => e.Pin >= 0)); w.Write(0f);
        w.Write(entries.Count == 0 ? 0 : entries.Max(e => e.Entry)); w.Write(0); w.Write(1L);
        foreach (var (entry, pin, path) in entries)
        {
            w.Write(new byte[88]); w.Write(entry); w.Write(0); w.Write(0L); w.Write(pin); w.Write(-1); w.Write(1); w.Write(0L);
            w.Write((ushort)path.Length); w.Write(Encoding.Unicode.GetBytes(path)); w.Write(0);
        }
        return ((MemoryStream)w.BaseStream).ToArray();
    }

    /// <summary>A link to <paramref name="local"/> or to <paramref name="share"/> plus
    /// <paramref name="suffix"/>; with neither, a link with no LinkInfo (a virtual item).</summary>
    public static byte[] Link(string? local = null, string? share = null, string suffix = "", bool folder = true, bool unicode = false, bool idList = true)
    {
        var w = new BinaryWriter(new MemoryStream());
        var hasInfo = local is not null || share is not null;
        w.Write(0x4C); w.Write(new Guid("00021401-0000-0000-C000-000000000046").ToByteArray());
        w.Write((idList ? 1 : 0) | (hasInfo ? 2 : 0) | 0x80); w.Write(folder ? 0x10 : 0x20); w.Write(new byte[0x4C - 0x1C]);
        if (idList) { w.Write((ushort)2); w.Write((ushort)0); }
        if (hasInfo)
        {
            var header = unicode ? 0x24 : 0x1C;
            var ansi = Encoding.Latin1;
            byte[] Z(string text, Encoding encoding) => encoding.GetBytes(text + "\0");
            var body = new MemoryStream();
            int Put(byte[] bytes) { var at = header + (int)body.Length; body.Write(bytes); return at; }
            int volume = 0, basePath = 0, network = 0, baseUnicode = 0, suffixUnicode = 0;
            if (local is not null)
            {
                volume = Put([0x10, 0, 0, 0, 3, 0, 0, 0, 1, 2, 3, 4, 0x10, 0, 0, 0, 0]);
                basePath = Put(Z(unicode ? "?" : local, ansi));
            }
            else
            {
                var name = Z(share!, ansi);
                var link = new byte[0x14 + name.Length];
                BinaryPrimitives.WriteInt32LittleEndian(link, link.Length);
                BinaryPrimitives.WriteInt32LittleEndian(link.AsSpan(4), 2);
                BinaryPrimitives.WriteInt32LittleEndian(link.AsSpan(8), 0x14);
                BinaryPrimitives.WriteInt32LittleEndian(link.AsSpan(16), 0x20000);
                name.CopyTo(link, 0x14);
                network = Put(link);
            }
            var suffixAt = Put(Z(suffix, ansi));
            if (unicode) { baseUnicode = local is null ? 0 : Put(Z(local, Encoding.Unicode)); suffixUnicode = Put(Z(suffix, Encoding.Unicode)); }
            w.Write(header + (int)body.Length); w.Write(header); w.Write(local is not null ? 1 : 2);
            w.Write(volume); w.Write(basePath); w.Write(network); w.Write(suffixAt);
            if (unicode) { w.Write(baseUnicode); w.Write(suffixUnicode); }
            w.Write(body.ToArray());
        }
        w.Write(0);   // the ExtraData terminal block
        return ((MemoryStream)w.BaseStream).ToArray();
    }
}
