using System.IO;
using System.Text.Json;
using System.Threading;
using MarkdownMidget.Instances;

namespace MarkdownMidget.Picker;

/// <summary>One remembered look of the file list: its sort, and its column widths in
/// <see cref="PickerColumn"/> order.</summary>
internal sealed record PickerView(PickerSort Sort, double[] Widths);

/// <summary>A view as picker-views.json holds it. Folder is null for the default; Used is
/// when it was last saved or opened, in UTC.</summary>
internal sealed class PickerViewEntry
{
    public string? Folder { get; set; }
    public string? Column { get; set; }
    public bool Descending { get; set; }
    public double[]? Widths { get; set; }
    public DateTime Used { get; set; }
}

/// <summary>picker-views.json: the default view, which the first change anywhere sets, and
/// a view for each folder changed after that.</summary>
internal sealed class PickerViewsFile
{
    public int Version { get; set; } = 1;
    public PickerViewEntry? Default { get; set; }
    public List<PickerViewEntry> Folders { get; set; } = [];
}

/// <summary>
/// The rules for remembered views (#12, item 4), with no I/O. A folder opens with its own
/// view, else the default, else the picker's built-in one. A change sets the default while
/// there is none, and after that belongs to the folder it was made in. A folder is known by
/// its full path, any trailing separator trimmed, compared ignoring case (the open-file
/// guard's normaliser), and at most <see cref="MaxFolders"/> are kept, the least recently
/// used going first.
/// </summary>
internal static class PickerViews
{
    public const int MaxFolders = 300;
    private static readonly int ColumnCount = Enum.GetValues<PickerColumn>().Length;

    public static PickerView? Resolve(PickerViewsFile file, string folder) => ToView(Find(file, folder) ?? file.Default);

    public static void Apply(PickerViewsFile file, string folder, PickerView view, DateTime now)
    {
        var entry = new PickerViewEntry
        {
            Column = view.Sort.Column.ToString(), Descending = view.Sort.Descending, Widths = [.. view.Widths], Used = now,
        };
        if (file.Default is null) { file.Default = entry; return; }
        entry.Folder = OpenGuard.Normalize(folder);
        file.Folders.RemoveAll(e => string.Equals(e.Folder, entry.Folder, StringComparison.OrdinalIgnoreCase));
        file.Folders.Add(entry);
        while (file.Folders.Count > MaxFolders) file.Folders.Remove(file.Folders.SkipLast(1).MinBy(e => e.Used)!);
    }

    /// <summary>Marks a folder's own view used, when it was last a day or more ago: true when
    /// that is worth a write. The day keeps opening a folder from writing the file each time.</summary>
    public static bool Touch(PickerViewsFile file, string folder, DateTime now)
    {
        if (Find(file, folder) is not { } entry || now - entry.Used < TimeSpan.FromDays(1)) return false;
        entry.Used = now;
        return true;
    }

    /// <summary>Never throws: text that isn't a views file reads as an empty one, and an entry
    /// that makes no sense (an unknown column, the wrong number of widths) is dropped.</summary>
    public static PickerViewsFile Parse(string? text)
    {
        PickerViewsFile? file = null;
        try { file = JsonSerializer.Deserialize<PickerViewsFile>(text ?? ""); }
        catch (Exception) { /* not a views file: start again */ }
        file ??= new();
        if (ToView(file.Default) is null) file.Default = null;
        file.Folders = (file.Folders ?? [])
            .Where(e => !string.IsNullOrWhiteSpace(e?.Folder) && ToView(e) is not null)
            .GroupBy(e => e.Folder!, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.MaxBy(e => e.Used)!)
            .ToList();
        return file;
    }

    public static string Serialize(PickerViewsFile file) => JsonSerializer.Serialize(file);

    /// <summary>
    /// Whether cleanup may forget a folder's view: only when its drive is a local one that is
    /// there and ready, and the folder is not. A UNC path is never probed, nor a mapped
    /// network drive's folder, because a dead share can block for a long time: the cap
    /// retires those. A drive that is unplugged or not ready, or a probe that fails, keeps it.
    /// </summary>
    public static bool Gone(string folder, Func<string, DriveType> driveType, Func<string, bool> isReady, Func<string, bool> exists)
    {
        try
        {
            if (folder.StartsWith(@"\\", StringComparison.Ordinal) || Path.GetPathRoot(folder) is not { Length: > 0 } root) return false;
            return driveType(root) is DriveType.Fixed or DriveType.Removable or DriveType.CDRom or DriveType.Ram
                && isReady(root) && !exists(folder);
        }
        catch (Exception) { return false; }
    }

    private static PickerViewEntry? Find(PickerViewsFile file, string folder)
    {
        var key = OpenGuard.Normalize(folder);
        return file.Folders.FirstOrDefault(e => string.Equals(e.Folder, key, StringComparison.OrdinalIgnoreCase));
    }

    private static PickerView? ToView(PickerViewEntry? e) =>
        e is not null && Enum.TryParse<PickerColumn>(e.Column, out var column) && Enum.IsDefined(column)
            && e.Widths is { } widths && widths.Length == ColumnCount && widths.All(w => double.IsFinite(w) && w is >= 0 and <= 10000)
            ? new PickerView(new PickerSort(column, e.Descending), [.. widths])
            : null;
}

/// <summary>
/// picker-views.json in Markdown Midget's app-data folder: a file of its own, because a
/// version older than this one rewrites settings.json without the fields it doesn't know.
/// Every window is its own process, so a write takes a lock file first (FileShare.None,
/// deleted on close, so a crashed window can't leave it held), re-reads the file, applies
/// only its own change and replaces the file through a temp copy: the last writer wins an
/// entry and no other entry is lost. A file that doesn't parse is rebuilt; one that can't be
/// read at all is left as it is and the change is dropped. Nothing here throws.
/// </summary>
internal sealed class PickerViewStore
{
    public const string FileName = "picker-views.json";
    private const int GateAttempts = 80;   // 25 ms apart: two seconds behind another window's write

    private readonly string _path;
    private readonly Action<int> _wait;
    private readonly object _queueLock = new();
    private Task _queue = Task.CompletedTask;
    private int _cleaned;

    /// <param name="wait">The pause before a retry, in milliseconds; tests pass their own.</param>
    public PickerViewStore(string directory, Action<int>? wait = null)
    {
        _path = Path.Combine(directory, FileName);
        _wait = wait ?? Thread.Sleep;
    }

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MarkdownMidget");

    public PickerViewsFile Load() => TryRead(out var file) ? file : new();

    /// <summary>Applies <paramref name="change"/> to the file as it is now. False when nothing
    /// was written: the lock or the file couldn't be had, or the write failed.</summary>
    public bool Update(Action<PickerViewsFile> change)
    {
        var tmp = $"{_path}.{Environment.ProcessId}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            using var gate = Gate();
            if (gate is null || !TryRead(out var file)) return false;
            change(file);
            File.WriteAllText(tmp, PickerViews.Serialize(file));
            MainWindow.ReplaceFile(tmp, _path, () => _wait(25));
            return true;
        }
        catch (Exception) { return false; }
        finally { try { File.Delete(tmp); } catch { /* left for the next write to replace */ } }
    }

    /// <summary><see cref="Update"/> off the UI thread, after every change queued before it.</summary>
    public Task UpdateLater(Action<PickerViewsFile> change)
    {
        lock (_queueLock) return _queue = _queue.ContinueWith(_ => Update(change), TaskScheduler.Default);
    }

    /// <summary>
    /// Forgets the views of folders that are <paramref name="gone"/>, on a background thread
    /// and only on the first call. The probing runs without the lock; the removal then takes
    /// it and removes only entries unchanged since they were probed, so a view another window
    /// saved in the meantime stays.
    /// </summary>
    public Task CleanOnce(Func<string, bool> gone)
    {
        if (Interlocked.Exchange(ref _cleaned, 1) == 1) return Task.CompletedTask;
        return Task.Run(() =>
        {
            try
            {
                var stale = Load().Folders.Where(e => gone(e.Folder!))
                    .ToDictionary(e => e.Folder!, e => e.Used, StringComparer.OrdinalIgnoreCase);
                if (stale.Count > 0)
                    Update(f => f.Folders.RemoveAll(e => stale.TryGetValue(e.Folder!, out var used) && used == e.Used));
            }
            catch (Exception) { /* housekeeping: the cap still bounds the file */ }
        });
    }

    private FileStream? Gate()
    {
        for (var attempt = 1; attempt <= GateAttempts; attempt++)
        {
            try { return new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _wait(25); }
        }
        return null;
    }

    private bool TryRead(out PickerViewsFile file)
    {
        file = new();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (!File.Exists(_path)) return true;
                using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                file = PickerViews.Parse(reader.ReadToEnd());
                return true;
            }
            catch (FileNotFoundException) { return true; }
            catch (Exception ex) when (attempt < 4 && ex is IOException or UnauthorizedAccessException) { _wait(25); }
            catch (Exception) { return false; }
        }
    }
}
