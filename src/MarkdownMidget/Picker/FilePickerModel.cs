using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MarkdownMidget.Picker;

/// <summary>One entry of a Win32-style filter string.</summary>
internal sealed record FilterGroup(string Label, IReadOnlyList<string> Patterns)
{
    /// <summary>True when this group accepts everything (the "All files (*.*)" entry).</summary>
    public bool IsCatchAll => Patterns.Any(p => p is "*.*" or "*");

    /// <summary>
    /// The extension to append when a save name has none — only meaningful for a
    /// group naming exactly one concrete pattern, since "*.md;*.markdown" gives no
    /// single right answer and "*.*" gives none at all.
    /// </summary>
    public string? SoleExtension =>
        Patterns.Count == 1 && Patterns[0].StartsWith("*.", StringComparison.Ordinal)
            && !IsCatchAll ? Patterns[0][1..] : null;

    public override string ToString() => Label;
}

/// <summary>The file list's sortable columns, in the order the list shows them.</summary>
internal enum PickerColumn { Name, Modified, Type, Size }

/// <summary>How the file list is sorted: one column, one way. <see cref="PickerViews"/>
/// remembers it, with the column widths, by folder.</summary>
internal readonly record struct PickerSort(PickerColumn Column, bool Descending)
{
    /// <summary>The built-in sort, until a view is remembered: folders first, then names A to Z.</summary>
    public static PickerSort Default => new(PickerColumn.Name, Descending: false);

    /// <summary>A header click. The sorted column turns round; another column starts A to Z
    /// or smallest first, except Date modified, which starts newest first: finding the file
    /// just written is the usual reason to click it.</summary>
    public PickerSort Click(PickerColumn column) =>
        column == Column ? this with { Descending = !Descending } : new(column, column == PickerColumn.Modified);
}

/// <summary>What the file list sorts a row by. Modified, in UTC, is null when it couldn't be read;
/// Bytes is -1 for a folder, and for a file whose size couldn't be read.</summary>
internal readonly record struct PickerSortKey(bool IsDirectory, string Name, DateTime? Modified, long Bytes, string Type);

/// <summary>
/// The pure half of the built-in file picker: filter parsing and matching, path
/// resolution, extension enforcement, sorting and size formatting. No file I/O
/// and no UI, so every rule is unit-testable the way CssValidator, CustomDicImport
/// and SecureUi are — the dialog is a thin shell over this.
/// </summary>
internal static class FilePickerModel
{
    /// <summary>
    /// Parse a Win32 filter ("Markdown (*.md)|*.md|All files (*.*)|*.*") into
    /// label/pattern pairs. A trailing unpaired label is dropped rather than
    /// guessed at: a half-written filter is a caller bug, and inventing a pattern
    /// for it would silently show the wrong files.
    /// </summary>
    public static IReadOnlyList<FilterGroup> ParseFilter(string? filter)
    {
        var groups = new List<FilterGroup>();
        if (string.IsNullOrWhiteSpace(filter)) return groups;
        var parts = filter.Split('|');
        for (var i = 0; i + 1 < parts.Length; i += 2)
        {
            var label = parts[i].Trim();
            var patterns = parts[i + 1]
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
            if (patterns.Count == 0) continue;
            groups.Add(new FilterGroup(label, patterns));
        }
        return groups;
    }

    /// <summary>
    /// Does this file name pass the group? Patterns are the simple Win32 shapes
    /// ("*.md", "*.*", "name.txt") — deliberately not full globbing, because the
    /// filter strings we hand ourselves never use more than that, and a homemade
    /// glob engine is a bug farm.
    /// </summary>
    public static bool MatchesFilter(string fileName, FilterGroup group)
    {
        if (group.IsCatchAll) return true;
        foreach (var pattern in group.Patterns)
        {
            if (pattern.StartsWith("*.", StringComparison.Ordinal))
            {
                var ext = pattern[1..];   // ".md"
                if (fileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return true;
            }
            else if (string.Equals(pattern, fileName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// What the user typed in the address or file-name box, resolved against the
    /// folder they are looking at. Handles quotes, environment variables, "~",
    /// relative paths and bare names. Returns null when the text cannot be a path
    /// at all — the caller then leaves the box alone rather than navigating
    /// somewhere arbitrary.
    /// </summary>
    public static string? ResolveTypedPath(string? typed, string currentDirectory)
    {
        if (string.IsNullOrWhiteSpace(typed)) return null;
        var text = typed.Trim().Trim('"');
        if (text.Length == 0) return null;
        if (text.Contains('%')) text = Environment.ExpandEnvironmentVariables(text);
        if (text == "~" || text.StartsWith("~\\", StringComparison.Ordinal) || text.StartsWith("~/", StringComparison.Ordinal))
            text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), text.Length > 1 ? text[2..] : "");
        // Reject the characters Windows will refuse anyway, so a stray "?" reads as
        // "not a path" here instead of throwing out of Path.GetFullPath below.
        if (text.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0) return null;
        try
        {
            return Path.IsPathRooted(text)
                ? Path.GetFullPath(text)
                : Path.GetFullPath(Path.Combine(currentDirectory, text));
        }
        catch { return null; }
    }

    /// <summary>
    /// Apply the save dialog's extension rules to a typed name: a name with no
    /// extension takes the selected filter's extension, or the caller's default.
    /// A name that already has ANY extension is left alone — silently rewriting
    /// "notes.v2" to "notes.v2.md" is the kind of helpfulness users curse at.
    /// </summary>
    public static string EnsureExtension(string fileName, FilterGroup? group, string? defaultExt)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return fileName;
        if (Path.HasExtension(fileName)) return fileName;
        var ext = group?.SoleExtension ?? defaultExt;
        if (string.IsNullOrWhiteSpace(ext)) return fileName;
        if (!ext.StartsWith('.')) ext = "." + ext;
        return fileName + ext;
    }

    /// <summary>
    /// The name a save box should show after the file-type dropdown changes.
    /// Windows' own dialog retypes the extension here, and matching it is not
    /// cosmetic: the caller decides what to WRITE from the returned path's
    /// extension, so leaving "notes.md" in the box after the user picked
    /// "Secure Markdown" would save plaintext into a file they believe is
    /// encrypted. Returns the unchanged name when there is nothing to do -
    /// no name, no single extension to apply, or the name already fits.
    /// </summary>
    public static string RetypeForFilter(string fileName, FilterGroup? group)
    {
        if (string.IsNullOrWhiteSpace(fileName) || group is null) return fileName;
        var ext = group.SoleExtension;
        if (ext is null) return fileName;                       // "*.*" or several patterns
        if (MatchesFilter(fileName, group)) return fileName;    // already the right type
        try { return Path.ChangeExtension(fileName, ext); } catch { return fileName; }
    }

    /// <summary>Explorer's size column: whole units, KB from 1 KB up.</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes < 0) return "";
        if (bytes < 1024) return $"{bytes} B";
        double value = bytes / 1024.0;
        foreach (var unit in new[] { "KB", "MB", "GB", "TB" })
        {
            if (value < 1024 || unit == "TB")
                return value >= 100 ? $"{value:N0} {unit}" : $"{value:N1} {unit}";
            value /= 1024;
        }
        return $"{bytes} B";
    }

    /// <summary>
    /// The list's order: folders before files whichever way it runs; then the sorted column,
    /// sizes as numbers and dates as timestamps, never their displayed text; ties by name A to
    /// Z ignoring case, then by exact spelling, so the order never depends on how the folder
    /// listed them. <see cref="PickerSort.Default"/> is the order the picker always opened with.
    /// </summary>
    public static int CompareEntries(PickerSortKey a, PickerSortKey b, PickerSort sort)
    {
        if (a.IsDirectory != b.IsDirectory) return a.IsDirectory ? -1 : 1;
        var byColumn = sort.Column switch
        {
            PickerColumn.Modified => Nullable.Compare(a.Modified, b.Modified),
            PickerColumn.Size => a.Bytes.CompareTo(b.Bytes),
            PickerColumn.Type => string.Compare(a.Type, b.Type, StringComparison.OrdinalIgnoreCase),
            _ => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
        };
        if (byColumn != 0) return sort.Descending ? -byColumn : byColumn;
        var byName = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        return byName != 0 ? byName : string.CompareOrdinal(a.Name, b.Name);
    }

    /// <summary>The line under the picker's buttons: whose picker it is and, while "Always use
    /// the built-in file picker" is on, the way back. After a one-off fallback it is off already.</summary>
    public static string NoteText(bool settingOn) => "Markdown Midget's own file picker."
        + (settingOn ? " To use Windows' dialog again: Edit ▸ Settings ▸ File dialogs, then restart Markdown Midget." : "");

    /// <summary>A column's header: its name, and on the sorted column ▲ (A to Z, oldest or
    /// smallest first) or ▼. Text, so it takes the header's own colour in light and dark mode.</summary>
    public static string HeaderText(PickerColumn column, PickerSort sort)
    {
        var name = column switch { PickerColumn.Modified => "Date modified", PickerColumn.Type => "Type", PickerColumn.Size => "Size", _ => "Name" };
        return column != sort.Column ? name : name + (sort.Descending ? "  ▼" : "  ▲");
    }

    /// <summary>
    /// The next match for a type-ahead prefix, starting the search after
    /// <paramref name="startIndex"/> and wrapping — so typing "re" repeatedly
    /// walks every entry beginning with "re". Returns -1 when nothing matches.
    /// </summary>
    public static int FindByPrefix(IReadOnlyList<string> names, string prefix, int startIndex)
    {
        if (names.Count == 0 || string.IsNullOrEmpty(prefix)) return -1;
        for (var offset = 1; offset <= names.Count; offset++)
        {
            var i = ((startIndex + offset) % names.Count + names.Count) % names.Count;
            if (names[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return -1;
    }

}
