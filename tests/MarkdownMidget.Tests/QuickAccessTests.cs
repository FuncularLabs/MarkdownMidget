using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
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
    private static byte[] Usual(int version = 4, int sectorShift = 9, int extraEntries = 0, int longer = 0, int? pinned = null) => JumpListWriter.CompoundFile(
        [
            ("DestList", JumpListWriter.DestList(version,
                [(1, 1, @"C:\Projects\Alpha"), (2, -1, @"C:\Recent\Gamma"), (26, 0, @"\\server\share\Beta"),
                 .. Enumerable.Range(100, extraEntries).Select(i => ((uint)i, -1, @"C:\Recent\Filler" + i))], longer: longer, pinned: pinned)),
            ("1", Alpha), ("2", JumpListWriter.Link(local: @"C:\Recent\Gamma")), ("1a", Beta),
        ], sectorShift);

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void PinnedFoldersComeInPinOrderLocalAndOnAShare(int version) =>
        Assert.Equal([@"\\server\share\Beta", @"C:\Projects\Alpha"], QuickAccess.PinnedFolders(Usual(version)));

    /// <summary>Version 1 (Windows 7 and 8) lays its entries out another way, and 2 isn't known from a
    /// released Windows: both are refused. From 3 on (Windows 10's 3, then 4, and Windows 11's 6 today) the
    /// entries are the same, so a version is taken by its layout, not its number: the entries must end exactly
    /// where the stream does. Read by version 4's layout, a list whose entries are each 8 bytes longer, as one
    /// more field would make them, ends short of it, and one 2 bytes longer is refused as surely.</summary>
    [Theory]
    [InlineData(1, 0, false)]
    [InlineData(2, 0, false)]
    [InlineData(3, 0, true)]
    [InlineData(4, 0, true)]
    [InlineData(5, 0, true)]
    [InlineData(6, 0, true)]
    [InlineData(6, 8, false)]
    [InlineData(6, 2, false)]   // exactly the end: not "near enough"
    [InlineData(4, 8, false)]   // the end is checked whatever the version
    public void ADestListIsReadByItsLayoutFromVersion3On(int version, int longer, bool read)
    {
        // One entry: nothing but the stream's end can tell a longer entry from version 4's.
        var one = JumpListWriter.CompoundFile([("DestList", JumpListWriter.DestList(version, [(1, 0, @"C:\Projects\Alpha")], longer: longer)), ("1", Alpha)]);
        Assert.Equal(read ? [@"C:\Projects\Alpha"] : Array.Empty<string>(), QuickAccess.PinnedFolders(one));
        Assert.Equal(read ? [@"\\server\share\Beta", @"C:\Projects\Alpha"] : Array.Empty<string>(), QuickAccess.PinnedFolders(Usual(version, longer: longer)));
    }

    /// <summary>The header counts the pinned entries (offset 8). When the entries don't add up to it, the pin
    /// status isn't where the reader takes it from, and which entries show, and in what order, can't be trusted:
    /// a fault of the whole list, not of one entry, so the whole list is refused.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void APinnedCountThatDisagreesWithTheEntriesShowsNoSection(int pinned)
    {
        Assert.Equal([@"\\server\share\Beta", @"C:\Projects\Alpha"], QuickAccess.PinnedFolders(Usual(6, pinned: 2)));   // two of three pinned
        Assert.Empty(QuickAccess.PinnedFolders(Usual(6, pinned: pinned)));
    }

    /// <summary>
    /// Quick access as Windows 11 writes it, modelled on the structure of a real file but with made-up paths.
    /// DestList version 6 with twelve entries, all pinned, the header saying twelve, and each entry's store
    /// empty. The entries are numbered sparsely, and their streams' hex names have letters in. In the stream,
    /// the last pin comes first and the first pin last. This PC and the Recycle Bin are pinned by CLSID, six
    /// user folders as knownfolder:{…}, and two folders and two drive roots by path. Every link has an ANSI-only
    /// LinkInfo, except the two CLSIDs, which have none. It's a version 3 compound file of 512-byte sectors
    /// with every stream in the mini stream, a 4-byte DestListPropertyStore stream, and many unused
    /// directory slots.
    /// </summary>
    [Fact]
    public void AWindows11QuickAccessListGivesItsTenFoldersInPinOrder()
    {
        const string ThisPc = "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}", RecycleBin = "::{645FF040-5081-101B-9F08-00AA002F954E}";
        static string Known(string id) => "knownfolder:{" + id + "}";
        // In the stream's order: entry number, pin order, the DestList's path, the LinkInfo's LocalBasePath
        // (none for a CLSID) and the link's FileAttributes (0x11 a read-only folder, 0x10 a folder).
        (uint Entry, int Pin, string ListPath, string? Local, uint Attributes)[] pins =
        [
            (0xc5d, 11, @"Q:\", @"Q:\", 0x10),   // a drive that isn't there now: nothing is checked, so still listed
            (0x7f, 4, ThisPc, null, 0),
            (0x89e, 7, @"D:\Work\Projects", @"D:\Work\Projects", 0x11),
            (0x415, 2, Known("FDD39AD0-238F-46AF-ADB4-6C85480369C7"), @"C:\Users\Avery\Documents", 0x11),
            (0x3c2, 9, @"C:\Users\Avery\Source\Repos", @"C:\Users\Avery\Source\Repos", 0x11),
            (0x130, 5, Known("4BD8D571-6D19-48D3-BE97-422220080E43"), @"C:\Users\Avery\Music", 0x11),
            (0x5d1, 8, RecycleBin, null, 0),
            (0xaa4, 1, Known("374DE290-123F-4565-9164-39C4925E467B"), @"C:\Users\Avery\Downloads", 0x11),
            (0x6e0, 10, @"E:\", @"E:\", 0x10),
            (0x9f3, 6, Known("18989B1D-99B5-455B-841C-AB7C74E4DDFC"), @"C:\Users\Avery\Videos", 0x11),
            (0x2b, 3, Known("33E28130-4E1E-4676-835A-98395C3BC3BB"), @"C:\Users\Avery\Pictures", 0x11),
            (0x1ae, 0, Known("754AC886-DF64-4CBA-86B5-F7FBF4FBCEF5"), @"C:\Users\Avery\Desktop", 0x11),   // FOLDERID_ThisPCDesktop
        ];
        var list = JumpListWriter.DestList(6, [.. pins.Select(p => (p.Entry, p.Pin, p.ListPath))]);
        List<(string, byte[])> streams = [.. pins.Select(p => (p.Entry.ToString("x"),
            JumpListWriter.ExplorerLink(p.Local, p.Attributes, p.Local is null ? new Guid(p.ListPath[3..^1]) : new Guid("20D04FE0-3AEA-1069-A2D8-08002B30309D"))))];
        streams.Insert(5, ("DestList", list));
        streams.Insert(6, ("DestListPropertyStore", new byte[4]));
        Assert.All(streams, s => Assert.InRange(s.Item2.Length, 1, 4095));   // all in the mini stream, as in the real file
        Assert.All(new[] { "1ae", "415", "89e", "aa4", "c5d" }, name => Assert.Contains(streams, s => s.Item1 == name));
        Assert.Equal(12, BinaryPrimitives.ReadInt32LittleEndian(list.AsSpan(8)));   // the header's pinned count

        Assert.Equal(
            [@"C:\Users\Avery\Desktop", @"C:\Users\Avery\Downloads", @"C:\Users\Avery\Documents", @"C:\Users\Avery\Pictures", @"C:\Users\Avery\Music",
             @"C:\Users\Avery\Videos", @"D:\Work\Projects", @"C:\Users\Avery\Source\Repos", @"E:\", @"Q:\"],
            QuickAccess.PinnedFolders(JumpListWriter.CompoundFile(streams, spareSlots: 40)));
    }

    /// <summary>Explorer's LinkInfo holds the path in the ANSI code page only, with "?" for each character the
    /// code page lacks; the DestList holds it in UTF-16. A full path there, local or on a share, wins.</summary>
    [Theory]
    [InlineData(@"C:\Projekte\Ärger\Даля", @"C:\Projekte\?rger\????", null, "")]
    [InlineData(@"\\server\Teilen-Ä\Даля", null, @"\\server\Teilen-?", "????")]
    public void AFolderNameOutsideTheAnsiCodePageComesFromTheDestList(string folder, string? local, string? share, string suffix)
    {
        var file = JumpListWriter.CompoundFile([("DestList", JumpListWriter.DestList(6, [(0x1ae, 0, folder)])), ("1ae", JumpListWriter.Link(local: local, share: share, suffix: suffix))]);
        Assert.Equal([folder], QuickAccess.PinnedFolders(file));
    }

    /// <summary>A Windows file name is UTF-16 code units, not necessarily valid Unicode: one with a lone
    /// surrogate keeps it, where decoding as Unicode text would put U+FFFD in its place.</summary>
    [Fact]
    public void ADestListPathIsKeptCodeUnitForCodeUnit()
    {
        var folder = @"C:\Odd\a" + '\uD800' + "b";
        var file = JumpListWriter.CompoundFile([("DestList", JumpListWriter.DestList(6, [(1, 0, folder)])), ("1", JumpListWriter.Link(local: @"C:\Odd\a?b"))]);
        Assert.Equal([folder], QuickAccess.PinnedFolders(file));
    }

    /// <summary>A user folder is pinned by its known folder's ID, This PC and the like by CLSID: names, not
    /// paths, so the LinkInfo's path is used, as it is when the DestList's is anything but a full path.</summary>
    [Theory]
    [InlineData("knownfolder:{FDD39AD0-238F-46AF-ADB4-6C85480369C7}", @"C:\Users\Avery\Documents", null, "", @"C:\Users\Avery\Documents")]
    [InlineData("knownfolder:{FDD39AD0-238F-46AF-ADB4-6C85480369C7}", null, @"\\server\home", @"avery\Documents", @"\\server\home\avery\Documents")]   // redirected to a share
    [InlineData("::{20D04FE0-3AEA-1069-A2D8-08002B30309D}", @"C:\Projects\Alpha", null, "", @"C:\Projects\Alpha")]   // This PC's link has no LinkInfo: here only the name is under test
    [InlineData(@"Projects\Alpha", @"C:\Projects\Alpha", null, "", @"C:\Projects\Alpha")]
    [InlineData(@"C:Projects\Alpha", @"C:\Projects\Alpha", null, "", @"C:\Projects\Alpha")]   // relative to the drive's current folder
    public void ADestListNameThatIsNotAFullPathGivesTheLinkInfosPath(string listPath, string? local, string? share, string suffix, string expected)
    {
        var file = JumpListWriter.CompoundFile([("DestList", JumpListWriter.DestList(6, [(0x415, 0, listPath)])), ("415", JumpListWriter.Link(local: local, share: share, suffix: suffix))]);
        Assert.Equal([expected], QuickAccess.PinnedFolders(file));
    }

    /// <summary>A full path in the DestList names the folder, but only a link with a LinkInfo and the folder
    /// attribute says it is one: a pinned file, or a link whose flags say it has no LinkInfo, is left out.</summary>
    [Fact]
    public void AFullPathInTheDestListDoesNotMakeAFolderOfALinkThatIsNotOne() => Assert.Empty(QuickAccess.PinnedFolders(JumpListWriter.CompoundFile(
        [
            ("DestList", JumpListWriter.DestList(6, [(1, 0, @"C:\Docs\a.txt"), (2, 1, @"C:\NoFlag"), (3, 2, @"C:\Shell")])),
            ("1", JumpListWriter.Link(local: @"C:\Docs\a.txt", folder: false)), ("2", JumpListWriter.Link(local: @"C:\NoFlag", infoFlag: false)),
            ("3", JumpListWriter.Link(local: null)),
        ])));

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

    [Fact]
    public void HomeThisPcALibraryAndAPinnedFileAreLeftOut()
    {
        var file = JumpListWriter.CompoundFile(
        [
            ("DestList", JumpListWriter.DestList(4,
                [(1, 0, "knownfolder:{home}"), (2, 1, @"C:\Docs\a.txt"), (3, 2, @"C:\Projects\Alpha"), (4, 3, "::{library}"), (5, 4, @"c:\projects\alpha"),
                 (6, 5, "Projects"), (7, 6, @"C:\NoFlag")])),
            ("6", JumpListWriter.Link(local: "Projects")),                            // not a full path
            ("7", JumpListWriter.Link(local: @"C:\NoFlag", infoFlag: false)),        // LinkInfo's bytes, but its flag says none
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
        // Documents moved to a folder with such a name: the DestList names it by its known folder's ID, so
        // the path comes from the LinkInfo, from its Unicode fields.
        const string folder = @"C:\Projekte\Ärger\Даля";
        var file = JumpListWriter.CompoundFile(
            [("DestList", JumpListWriter.DestList(4, [(7, 0, "knownfolder:{FDD39AD0-238F-46AF-ADB4-6C85480369C7}")])), ("7", JumpListWriter.Link(local: folder, unicode: true))]);
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
    [InlineData("mini sector shift")]
    [InlineData("DIFAT sectors")]
    [InlineData("truncated DestList")]
    [InlineData("a property store past the end")]
    [InlineData("a negative property store")]
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
            "a negative property store" => JumpListWriter.CompoundFile(   // -8 would step back into its own path
                [("DestList", JumpListWriter.DestList(4, [(1, 0, @"C:\Projects\Alpha")], store: _ => -8)), ("1", Alpha)]),
            "a property store past the end" => JumpListWriter.CompoundFile(   // the second entry's store runs 10 bytes past the stream
                [("DestList", JumpListWriter.DestList(4, [(1, 0, @"C:\Projects\Alpha"), (2, 1, @"C:\B")], store: e => e == 2 ? 20 : 0)[..^10]), ("1", Alpha)]),
            "a negative size" => Usual(sectorShift: 12),
            _ => Usual(),
        };
        // Where the DestList's size is: the directory's second entry, in sectors of this size.
        Span<byte> DestListSize(int sector) => file.AsSpan(sector * (BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(0x30)) + 1) + 128 + 0x78);
        switch (broken)
        {
            case "header signature": file[3] ^= 0xFF; break;
            case "mini sector shift": file[0x20] = 7; break;   // the reader's mini sectors are 64 bytes, shift 6
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
    public void OnlyPinnedEntriesShowInPinOrderAndAPropertyStoreIsSteppedOver()
    {
        // Each entry's modification time sits just before its pin status, its high half above zero:
        // read four bytes early, every entry would look pinned.
        var three = JumpListWriter.CompoundFile(
        [
            ("DestList", JumpListWriter.DestList(4, [(1, 0, @"C:\Alpha"), (2, -1, @"C:\Beta"), (3, 1, @"C:\Gamma")], store: e => e == 2 ? 20 : 0)),
            ("1", JumpListWriter.Link(local: @"C:\Alpha")), ("2", JumpListWriter.Link(local: @"C:\Beta")), ("3", JumpListWriter.Link(local: @"C:\Gamma")),
        ]);
        Assert.Equal([@"C:\Alpha", @"C:\Gamma"], QuickAccess.PinnedFolders(three));
        var recentOnly = JumpListWriter.CompoundFile([("DestList", JumpListWriter.DestList(3, [(1, -1, @"C:\Beta")])), ("1", JumpListWriter.Link(local: @"C:\Beta"))]);
        Assert.Empty(QuickAccess.PinnedFolders(recentOnly));
    }

    [Fact]
    public void OneBrokenLinkLeavesOutOnlyItsEntry() => Assert.Equal([@"C:\Projects\Alpha"], QuickAccess.PinnedFolders(JumpListWriter.CompoundFile(
        [("DestList", JumpListWriter.DestList(4, [(1, 0, @"C:\Projects\Alpha"), (2, 1, @"C:\Cut")])), ("1", Alpha), ("2", JumpListWriter.Link(local: @"C:\Cut")[..90])])));

    [Fact]
    public void AHundredPinnedFoldersAreReadEachEntryOnceAndAnOversizedLinkIsSkipped()
    {
        List<(string, byte[])> streams = [.. Enumerable.Range(1, 150).Select(i => (i.ToString("x"), JumpListWriter.Link(local: $@"C:\P{i}")))];
        streams.Add(("DestList", JumpListWriter.DestList(4, [(1, 0, @"C:\P1"), (1, 1, @"C:\P1"), .. Enumerable.Range(2, 149).Select(i => ((uint)i, i, $@"C:\P{i}"))])));
        streams[2] = ("3", [.. JumpListWriter.Link(local: @"C:\P3"), .. new byte[70_000]]);   // over 64 KB: no link is that long
        var folders = QuickAccess.PinnedFolders(JumpListWriter.CompoundFile(streams));
        Assert.Equal(99, folders.Count);   // 100 pinned entries read, P1 once, P3 too long
        Assert.Equal([@"C:\P1", @"C:\P2", @"C:\P4"], folders.Take(3));
    }

    [Fact]
    public void AChainThatLoopsOrARepeatedFatSectorIsRefusedAtOnce()
    {
        var loops = Usual();
        var dir = BinaryPrimitives.ReadInt32LittleEndian(loops.AsSpan(0x30));
        BinaryPrimitives.WriteInt32LittleEndian(loops.AsSpan(512 + 4 * dir), dir);   // the directory's sector points at itself
        var repeated = Usual();
        BinaryPrimitives.WriteInt32LittleEndian(repeated.AsSpan(0x4C + 4), BinaryPrimitives.ReadInt32LittleEndian(repeated.AsSpan(0x4C)));
        BinaryPrimitives.WriteInt32LittleEndian(repeated.AsSpan(0x2C), 2);
        foreach (var file in new[] { loops, repeated })
        {
            var (before, clock) = (GC.GetAllocatedBytesForCurrentThread(), System.Diagnostics.Stopwatch.StartNew());
            Assert.Empty(QuickAccess.PinnedFolders(file));
            Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 4 << 20);
            Assert.InRange(clock.ElapsedMilliseconds, 0, 1000);
        }
    }

    [Fact]
    public void TheDeadlineIsCheckedInBothLoopsNotOnlyInsideAChain()
    {
        // No link streams: the second loop reads no chain, so only its own check can stop it.
        var file = JumpListWriter.CompoundFile([("DestList", JumpListWriter.DestList(4, [(1, 0, @"C:\A"), (2, 1, @"C:\B"), (3, 2, @"C:\C")]))]);
        foreach (var stopAt in new[] { "entry", "link" })
        {
            using var deadline = new CancellationTokenSource();
            var steps = new List<string>();
            Assert.Empty(QuickAccess.PinnedFolders(file, deadline.Token, step => { steps.Add(step); if (step == stopAt) deadline.Cancel(); }));
            Assert.Single(steps, s => s == stopAt);   // stopped at that loop's next check
        }
    }

    [Fact]
    public void ARepeatedEntryNumberKeepsItsFirstPin() => Assert.Equal([@"C:\B", @"C:\A"], QuickAccess.PinnedFolders(JumpListWriter.CompoundFile(
        [("DestList", JumpListWriter.DestList(4, [(1, 5, @"C:\A"), (1, 0, @"C:\A"), (2, 1, @"C:\B")])), ("1", JumpListWriter.Link(local: @"C:\A")), ("2", JumpListWriter.Link(local: @"C:\B"))])));

    [Fact]
    public void AReadPastItsDeadlineStops()
    {
        var late = new CancellationToken(canceled: true);
        Assert.Throws<OperationCanceledException>(() => new CompoundFile(Usual(), late));
        Assert.Empty(QuickAccess.PinnedFolders(Usual(), late));
    }

    [Fact]
    public void AShareWithAUnicodeNameKeepsIt()
    {
        var link = JumpListWriter.Link(share: @"\\server\Teilen-Ä", suffix: "Beta", unicode: true);
        Assert.Equal([@"\\server\Teilen-Ä\Beta"], QuickAccess.PinnedFolders(JumpListWriter.CompoundFile([("DestList", JumpListWriter.DestList(4, [(1, 0, "x")])), ("1", link)])));
    }

    [Fact]
    public void AStreamOfExactlyTheCutoffIsReadFromTheBigSectors()
    {
        // 32 + (134 + 2 × 17) + 3896: the DestList is exactly 4096 bytes, so not a mini stream.
        var list = JumpListWriter.DestList(4, [(1, 0, @"C:\Projects\Alpha")], store: _ => 3896);
        Assert.Equal(4096, list.Length);
        Assert.Equal([@"C:\Projects\Alpha"], QuickAccess.PinnedFolders(JumpListWriter.CompoundFile([("DestList", list), ("1", Alpha)])));
    }

    [Fact]
    public void AFileOverEightMegabytesIsNotRead()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, QuickAccess.JumpListName);
        File.WriteAllBytes(path, [.. Usual(), .. new byte[(8 << 20) + 1 - Usual().Length]]);
        Assert.Empty(QuickAccess.PinnedFolders(path));
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
/// a DestList stream (format versions 3 on: a 32-byte header, then entries of 134 + 2n +
/// store bytes, n the path's characters); and a shell link ([MS-SHLLINK]) with a LinkInfo.
/// </summary>
internal static class JumpListWriter
{
    private const uint End = 0xFFFFFFFE, Free = 0xFFFFFFFF;

    /// <summary><paramref name="spareSlots"/> unused directory entries follow the streams' ones: type 0,
    /// all zero but for their sibling and child IDs, which are NOSTREAM ([MS-CFB] 2.6).</summary>
    public static byte[] CompoundFile(IReadOnlyList<(string Name, byte[] Data)> streams, int sectorShift = 9, int spareSlots = 0)
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
        int dirSectors = Sectors(128L * (streams.Count + 1 + spareSlots)), miniFatSectors = Sectors(4L * miniFat.Count), miniSectors = Sectors(mini.Length);
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
        for (var i = 0; i < spareSlots; i++) { w.Write(new byte[0x44]); w.Write(Free); w.Write(Free); w.Write(Free); w.Write(new byte[128 - 0x50]); }
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

    /// <summary>
    /// A DestList stream, field by field from the published tables, not from the reader: libyal
    /// dtformats, "Jump lists format", DestList header and "DestList entry - version 2 or later";
    /// and EricZimmerman/JumpList, DestList.cs, for the property store after the path (a 4-byte
    /// size, then that many bytes). <paramref name="store"/> gives an entry's store size;
    /// <paramref name="longer"/> zero bytes end each entry, as one more field in a later layout
    /// would; <paramref name="pinned"/> overrides the header's count of pinned entries.
    /// </summary>
    public static byte[] DestList(int version, IReadOnlyList<(uint Entry, int Pin, string Path)> entries, Func<uint, int>? store = null,
        int longer = 0, int? pinned = null)
    {
        var w = new BinaryWriter(new MemoryStream());
        void At(long start, int offset) => Assert.Equal(offset, w.BaseStream.Position - start);   // the table's offset, checked
        w.Write(version); w.Write(entries.Count); w.Write(pinned ?? entries.Count(e => e.Pin >= 0)); w.Write(0f);   // 0, 4, 8, 12
        w.Write(entries.Count == 0 ? 0 : entries.Max(e => e.Entry)); w.Write(0); w.Write(1); w.Write(0);   // 16, 20, 24, 28
        var fileTime = new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();   // its high half is positive
        foreach (var (entry, pin, path) in entries)
        {
            var start = w.BaseStream.Position;
            w.Write(0x1234L); w.Write(new byte[64]); w.Write(new byte[16]);   // checksum; four object identifiers; NetBIOS name
            At(start, 88); w.Write(entry); w.Write(0); w.Write(1.5f);           // entry number; unknown; a float
            At(start, 100); w.Write(fileTime);                                  // last modification time
            At(start, 108); w.Write(pin);                                       // pin status: -1 not pinned, else the order
            w.Write(0); w.Write(3); w.Write(0L);                                // unknown; access count; unknown
            At(start, 128); w.Write((ushort)path.Length);                       // path size, in characters
            At(start, 130); w.Write(MemoryMarshal.AsBytes(path.AsSpan()));       // the path's UTF-16 code units, as they are
            var size = store?.Invoke(entry) ?? 0;
            w.Write(size); w.Write(Enumerable.Repeat((byte)0xAB, Math.Max(size, 0)).ToArray());   // property store size, then the store
            w.Write(new byte[longer]);
        }
        return ((MemoryStream)w.BaseStream).ToArray();
    }

    /// <summary>
    /// A shell link as Explorer writes one for a Quick access pin on Windows 11, field by field from
    /// [MS-SHLLINK]: the header (2.1) with LinkFlags 0x00A00083 (HasLinkTargetIDList, HasLinkInfo, IsUnicode,
    /// DisableKnownFolderTracking, AllowLinkToLink), or 0x00A00081, no LinkInfo, when there is no
    /// <paramref name="localBasePath"/> (a shell item such as This PC); an ID list (2.2) whose root item is
    /// <paramref name="root"/>'s CLSID; a LinkInfo (2.3) of header size 0x1C, so ANSI fields only, with a
    /// fixed drive's VolumeID and an empty CommonPathSuffix; no StringData; then ExtraData (2.5): a
    /// TrackerDataBlock and a PropertyStoreDataBlock for a folder, and the terminal block. A shell
    /// item's link gets the PropertyStoreDataBlock alone here, a guess: no reader looks.
    /// </summary>
    public static byte[] ExplorerLink(string? localBasePath, uint attributes, Guid root)
    {
        var w = new BinaryWriter(new MemoryStream());
        var ansi = Encoding.Latin1;
        var time = new DateTime(2026, 9, 30, 8, 15, 0, DateTimeKind.Utc).ToFileTimeUtc();
        w.Write(0x4C); w.Write(new Guid("00021401-0000-0000-C000-000000000046").ToByteArray());   // HeaderSize, LinkCLSID
        w.Write(localBasePath is null ? 0x00A00081u : 0x00A00083u); w.Write(attributes);            // LinkFlags, FileAttributes
        w.Write(time); w.Write(time); w.Write(time);                                                // CreationTime, AccessTime, WriteTime
        w.Write(0); w.Write(0); w.Write(1); w.Write((ushort)0); w.Write((ushort)0); w.Write(0); w.Write(0);   // FileSize, IconIndex, ShowCommand, HotKey, reserved
        var items = new MemoryStream();
        items.Write([0x14, 0, 0x1F, 0x50]); items.Write(root.ToByteArray());                        // a root folder item: size, 0x1F, sort index, CLSID
        if (localBasePath is not null)                                                              // then one standing for the path, opaque to a reader
        {
            var name = ansi.GetBytes(localBasePath + "\0");
            items.Write(BitConverter.GetBytes((ushort)(4 + name.Length))); items.Write([0x31, 0]); items.Write(name);
        }
        w.Write((ushort)(items.Length + 2)); w.Write(items.ToArray()); w.Write((ushort)0);          // IDListSize, the items, TerminalID
        if (localBasePath is not null)
        {
            byte[] volume = [0x11, 0, 0, 0, 3, 0, 0, 0, 0x2A, 0x5C, 0x7E, 0x31, 0x10, 0, 0, 0, 0];   // VolumeIDSize, DriveType 3 (fixed), DriveSerialNumber, VolumeLabelOffset, ""
            var basePath = ansi.GetBytes(localBasePath + "\0");
            var size = 0x1C + volume.Length + basePath.Length + 1;
            w.Write(size); w.Write(0x1C); w.Write(1);                                    // LinkInfoSize, LinkInfoHeaderSize, VolumeIDAndLocalBasePath
            w.Write(0x1C); w.Write(0x1C + volume.Length); w.Write(0); w.Write(size - 1);  // VolumeIDOffset, LocalBasePathOffset, no network part, CommonPathSuffixOffset
            w.Write(volume); w.Write(basePath); w.Write((byte)0);                         // ... and CommonPathSuffix ""
            w.Write(0x60); w.Write(0xA0000003u); w.Write(0x58); w.Write(0);               // TrackerDataBlock: BlockSize, BlockSignature, Length, Version
            w.Write(ansi.GetBytes("avery-pc".PadRight(16, '\0')));                       // MachineID
            for (var i = 0; i < 4; i++) w.Write(new Guid(i + 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11).ToByteArray());   // Droid, DroidBirth
        }
        w.Write(0x28); w.Write(0xA0000009u);                                             // PropertyStoreDataBlock: BlockSize, BlockSignature
        w.Write(0x1C); w.Write(0x53505331); w.Write(new Guid("B725F130-47EF-101A-A5F1-02608C9EEBAC").ToByteArray()); w.Write(0);   // one storage ([MS-PROPSTORE]): size, "1SPS", FormatID, no values
        w.Write(0);                                                                      // the store's end
        w.Write(0);                                                                      // TerminalBlock
        return ((MemoryStream)w.BaseStream).ToArray();
    }

    /// <summary>A link to <paramref name="local"/> or to <paramref name="share"/> plus
    /// <paramref name="suffix"/>; with neither, a link with no LinkInfo (a virtual item).</summary>
    public static byte[] Link(string? local = null, string? share = null, string suffix = "", bool folder = true, bool unicode = false, bool idList = true,
        bool infoFlag = true)
    {
        var w = new BinaryWriter(new MemoryStream());
        var hasInfo = local is not null || share is not null;
        w.Write(0x4C); w.Write(new Guid("00021401-0000-0000-C000-000000000046").ToByteArray());
        w.Write((idList ? 1 : 0) | (hasInfo && infoFlag ? 2 : 0) | 0x80); w.Write(folder ? 0x10 : 0x20); w.Write(new byte[0x4C - 0x1C]);
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
                // CommonNetworkRelativeLink: with Unicode, NetNameOffset > 0x14 and NetNameOffsetUnicode at 0x14.
                var (name, wide) = (Z(unicode ? "?" : share!, ansi), unicode ? Z(share!, Encoding.Unicode) : []);
                var head = unicode ? 0x1C : 0x14;
                var link = new byte[head + name.Length + wide.Length];
                BinaryPrimitives.WriteInt32LittleEndian(link, link.Length);
                BinaryPrimitives.WriteInt32LittleEndian(link.AsSpan(4), 2);
                BinaryPrimitives.WriteInt32LittleEndian(link.AsSpan(8), head);
                BinaryPrimitives.WriteInt32LittleEndian(link.AsSpan(16), 0x20000);
                if (unicode) BinaryPrimitives.WriteInt32LittleEndian(link.AsSpan(0x14), head + name.Length);
                name.CopyTo(link, head);
                wide.CopyTo(link, head + name.Length);
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
