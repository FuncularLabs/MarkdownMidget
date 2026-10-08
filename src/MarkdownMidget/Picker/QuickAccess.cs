using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace MarkdownMidget.Picker;

/// <summary>
/// The folders pinned to Explorer's Quick Access (#12, item 5), read straight from the file
/// Explorer keeps them in. No shell call and no COM: those load the add-ons that crash
/// Windows' own dialog. The file is Quick Access's jump list, a compound file
/// (<see cref="CompoundFile"/>). Its DestList stream lists the entries, each pinned one with
/// its place in the pin order, and the stream named by an entry's number in hex is that
/// entry's shell link (<see cref="ShellLink"/>). Only DestList versions 3 and 4 (Windows 10
/// and 11) are read. Nothing is checked on disk: a pinned folder that has gone says so when it
/// is clicked, and a network folder isn't touched until then. The work is bounded: an 8 MB
/// file, 100 pinned entries, links of 64 KB, and a deadline checked as it goes.
/// </summary>
internal static class QuickAccess
{
    public const string JumpListName = "f01b4d95cf55d32a.automaticDestinations-ms";
    private const int MaxFileBytes = 8 << 20, MaxPinned = 100, MaxLinkBytes = 64 << 10;

    public static string JumpListPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Microsoft", "Windows", "Recent", "AutomaticDestinations", JumpListName);

    /// <summary>The pinned folders' paths, in pin order. Empty when the file is missing, can't
    /// be read, is another version, isn't as expected, or <paramref name="stop"/> comes first.
    /// Never throws.</summary>
    public static IReadOnlyList<string> PinnedFolders(string jumpListPath, CancellationToken stop = default)
    {
        try
        {
            // Shared for writing and deleting: Explorer may be updating the file right now.
            using var stream = new FileStream(jumpListPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaxFileBytes) return [];
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            return PinnedFolders(bytes, stop);
        }
        catch (Exception) { return []; }
    }

    internal static IReadOnlyList<string> PinnedFolders(byte[] jumpList, CancellationToken stop = default, Action<string>? step = null)
    {
        try
        {
            var file = new CompoundFile(jumpList, stop);
            var list = file.Read("DestList", int.MaxValue);
            if (list is null || I32(list, 0) is not (3 or 4)) return [];
            // libyal dtformats "Jump lists format" (DestList entry, version 2 or later) and EricZimmerman's
            // JumpList: a 32-byte header; then per entry the entry number at 88, the pin status at 108 (-1 when
            // not pinned, else the pin order), the path's length in characters at 128, the path at 130, then a
            // property store's size (4 bytes) and the store.
            var pinned = new Dictionary<int, int>();   // entry number to pin order, each entry once
            var at = 32;
            for (var count = I32(list, 4); count > 0; count--)
            {
                step?.Invoke("entry");   // tests watch where a deadline stops the work
                stop.ThrowIfCancellationRequested();
                var pathEnd = at + 130 + 2 * BinaryPrimitives.ReadUInt16LittleEndian(list.AsSpan(at + 128, 2));
                if (I32(list, at + 108) is var order and >= 0) pinned.TryAdd(I32(list, at + 88), order);
                at = checked(pathEnd + 4 + (I32(list, pathEnd) is var store and >= 0 ? store : throw new InvalidDataException("a negative store")));
                if (at > list.Length) throw new InvalidDataException("DestList ends inside an entry");
            }
            var folders = new List<string>();
            foreach (var entry in pinned.OrderBy(p => p.Value).Take(MaxPinned).Select(p => p.Key))
            {
                step?.Invoke("link");
                stop.ThrowIfCancellationRequested();
                try   // a bad link leaves out its own entry only
                {
                    if (file.Read(entry.ToString("x"), MaxLinkBytes) is { } link && ShellLink.FolderPath(link) is { } path
                        && !folders.Contains(path, StringComparer.OrdinalIgnoreCase)) folders.Add(path);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { }
            }
            return folders;
        }
        catch (Exception) { return []; }
    }

    private static int I32(byte[] bytes, int at) => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at, 4));
}

/// <summary>
/// A minimal read-only reader for an OLE compound file ([MS-CFB]), enough to take a stream
/// out by name: versions 3 and 4 (512- and 4096-byte sectors), the FAT, and the mini stream
/// that holds streams under the cutoff. A file whose FAT needs a DIFAT chain (a version 3
/// file over about 7 MB) is refused. It trusts nothing it reads: a FAT sector listed twice, a
/// chain that comes back to a sector or leaves its table, and a size beyond its chain all
/// throw, so no chain reads more than its source holds; and it stops when told to. The caller
/// treats any exception as no data. The directory is read as a flat list; its red-black tree
/// only orders the names, and nothing here needs that order.
/// </summary>
internal sealed class CompoundFile
{
    private const uint EndOfChain = 0xFFFFFFFE, NoSector = 0xFFFFFFFF;
    private const int MiniSectorSize = 64;

    private readonly byte[] _file;
    private readonly int _sectorSize;
    private readonly uint _cutoff;
    private readonly uint[] _fat, _miniFat = [];
    private readonly byte[] _miniStream = [];
    private readonly Dictionary<string, (uint Start, long Size)> _streams = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationToken _stop;

    public CompoundFile(byte[] file, CancellationToken stop = default)
    {
        (_file, _stop) = (file, stop);
        if (file.Length < 512 || U64(file, 0) != 0xE11AB1A1E011CFD0 || U16(file, 0x1C) != 0xFFFE
            || U16(file, 0x1E) is not (9 or 12) || U16(file, 0x20) != 6 || U32(file, 0x48) != 0)
            throw new InvalidDataException("not a compound file this reader takes");
        _sectorSize = 1 << U16(file, 0x1E);
        _cutoff = U32(file, 0x38);
        var fatSectors = Enumerable.Range(0, 109).Select(i => U32(file, 0x4C + 4 * i)).TakeWhile(s => s != NoSector).ToList();
        if (fatSectors.Distinct().Count() != fatSectors.Count) throw new InvalidDataException("a FAT sector listed twice");
        _fat = fatSectors.SelectMany(s => ToUInts(file.AsSpan(checked((int)((s + 1L) * _sectorSize)), _sectorSize))).ToArray();

        var directory = Follow(_file, _sectorSize, _sectorSize, _fat, U32(file, 0x30), -1);
        var version3 = U16(file, 0x1A) == 3;   // its sizes are 32 bits; the high half may hold anything
        for (var at = 0; at + 128 <= directory.Length; at += 128)
        {
            var (type, start) = (directory[at + 0x42], U32(directory, at + 0x74));
            var size = version3 ? U32(directory, at + 0x78) : (long)U64(directory, at + 0x78);
            if (size < 0) throw new InvalidDataException("a negative size");   // Follow reads -1 as "the whole chain"
            if (at == 0 && type == 5) _miniStream = Follow(_file, _sectorSize, _sectorSize, _fat, start, size);
            else if (type == 2 && U16(directory, at + 0x40) is var nameBytes and >= 2 and <= 64)
                _streams[Encoding.Unicode.GetString(directory, at, nameBytes - 2)] = (start, size);
        }
        if (U32(file, 0x40) is var miniFatSectors and > 0)
            _miniFat = ToUInts(Follow(_file, _sectorSize, _sectorSize, _fat, U32(file, 0x3C), (long)miniFatSectors * _sectorSize));
    }

    /// <summary>The stream called <paramref name="name"/>, or null when there is none or it is
    /// longer than <paramref name="maxBytes"/>.</summary>
    public byte[]? Read(string name, int maxBytes) =>
        !_streams.TryGetValue(name, out var s) || s.Size > maxBytes ? null
        : s.Size < _cutoff ? Follow(_miniStream, 0, MiniSectorSize, _miniFat, s.Start, s.Size)
        : Follow(_file, _sectorSize, _sectorSize, _fat, s.Start, s.Size);

    /// <summary>The chain from <paramref name="start"/> through <paramref name="table"/>, in
    /// sectors of <paramref name="size"/> bytes counted from <paramref name="origin"/>: its first
    /// <paramref name="length"/> bytes, or every sector of it when that is -1. Each sector once:
    /// so never more than <paramref name="source"/> holds.</summary>
    private byte[] Follow(byte[] source, long origin, int size, uint[] table, uint start, long length)
    {
        using var buffer = new MemoryStream();
        var seen = new HashSet<uint>();
        for (var sector = start; sector != EndOfChain && (length < 0 || buffer.Length < length); sector = table[sector])
        {
            _stop.ThrowIfCancellationRequested();
            if (sector >= table.Length || !seen.Add(sector)) throw new InvalidDataException("a broken chain");
            buffer.Write(source, checked((int)(origin + (long)sector * size)), length < 0 ? size : (int)Math.Min(size, length - buffer.Length));
        }
        if (length >= 0 && buffer.Length != length) throw new InvalidDataException("a chain shorter than its stream");
        return buffer.ToArray();
    }

    private static uint[] ToUInts(ReadOnlySpan<byte> bytes) => MemoryMarshal.Cast<byte, uint>(bytes).ToArray();
    private static ushort U16(byte[] b, int at) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at, 2));
    private static uint U32(byte[] b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at, 4));
    private static ulong U64(byte[] b, int at) => BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(at, 8));
}

/// <summary>
/// The one thing the picker needs from a shell link ([MS-SHLLINK]): the folder it points at,
/// from its LinkInfo. A local target is LocalBasePath plus CommonPathSuffix, a network one the
/// share's NetName plus the suffix; the Unicode copies are used when the link has them. A
/// link to something that isn't a folder, by the attributes the link records rather than a
/// look at the disk, or one with no LinkInfo at all (Home, Gallery, This PC, a library), gives
/// null. The ID list is skipped, not read.
/// </summary>
internal static class ShellLink
{
    /// <summary>The system's ANSI code page, which a link's non-Unicode strings are in.</summary>
    private static readonly Encoding Ansi = CodePagesEncodingProvider.Instance.GetEncoding(0) ?? Encoding.UTF8;   // null when it is UTF-8 (65001)

    public static string? FolderPath(byte[] link)
    {
        if (U32(link, 0) != 0x4C || (U32(link, 0x18) & 0x10) == 0) return null;   // header size; FILE_ATTRIBUTE_DIRECTORY
        var flags = U32(link, 0x14);
        if ((flags & 2) == 0) return null;                                          // no LinkInfo
        var info = 0x4C + ((flags & 1) != 0 ? 2 + U16(link, 0x4C) : 0);             // after the ID list, if any
        var unicode = U32(link, info + 4) >= 0x24;
        string Text(int ansiField, int unicodeField, int from) =>
            unicode && unicodeField > 0 && U32(link, info + unicodeField) is var u and > 0 ? Utf16Z(link, from + (int)u) : AnsiZ(link, from + (int)U32(link, info + ansiField));
        var suffix = Text(24, 32, info);
        string? path = null;
        if ((U32(link, info + 8) & 1) != 0) path = Text(16, 28, info) + suffix;
        else if ((U32(link, info + 8) & 2) != 0)
        {
            var net = info + (int)U32(link, info + 20);
            var share = U32(link, net + 8) > 0x14 && U32(link, net + 20) is var u and > 0 ? Utf16Z(link, net + (int)u) : AnsiZ(link, net + (int)U32(link, net + 8));
            path = suffix.Length == 0 ? share : Path.Join(share, suffix);
        }
        return path is not null && Path.IsPathFullyQualified(path) ? path : null;
    }

    private static string AnsiZ(byte[] b, int at) =>
        Ansi.GetString(b, at, b.AsSpan(at).IndexOf((byte)0) is var n and >= 0 ? n : throw new InvalidDataException("an unterminated string"));

    private static string Utf16Z(byte[] b, int at)
    {
        var chars = MemoryMarshal.Cast<byte, char>(b.AsSpan(at, (b.Length - at) & ~1));
        return chars.IndexOf('\0') is var n and >= 0 ? new string(chars[..n]) : throw new InvalidDataException("an unterminated string");
    }

    private static ushort U16(byte[] b, int at) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at, 2));
    private static uint U32(byte[] b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at, 4));
}
