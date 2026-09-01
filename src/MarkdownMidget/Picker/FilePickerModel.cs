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
    /// Folders first, then names, both case-insensitive — Explorer's order, which
    /// is what a user's eye expects to scan.
    /// </summary>
    public static int CompareEntries(bool aIsDirectory, string aName, bool bIsDirectory, string bName)
    {
        if (aIsDirectory != bIsDirectory) return aIsDirectory ? -1 : 1;
        return string.Compare(aName, bName, StringComparison.OrdinalIgnoreCase);
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

    /// <summary>
    /// The breadcrumb segments of a path, each paired with the path that segment
    /// navigates to: C:\a\b → [("C:\", "C:\"), ("a", "C:\a"), ("b", "C:\a\b")].
    /// </summary>
    public static IReadOnlyList<(string Label, string Path)> Breadcrumbs(string path)
    {
        var crumbs = new List<(string, string)>();
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root)) return crumbs;
            crumbs.Add((root, root));
            var rest = full[root.Length..].Trim(Path.DirectorySeparatorChar);
            if (rest.Length == 0) return crumbs;
            var current = root;
            foreach (var segment in rest.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                crumbs.Add((segment, current));
            }
        }
        catch { /* an unparseable path simply has no breadcrumbs */ }
        return crumbs;
    }
}
