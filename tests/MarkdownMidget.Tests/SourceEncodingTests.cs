using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>
/// The repository's own text files carry no UTF-8 byte-order mark. One slipped into
/// a test file through a helper script: .NET's culture-sensitive
/// <c>string.StartsWith</c> treats U+FEFF as ignorable, so "does this text start
/// with a mark?" answers yes for every string, and the file was written back with
/// one. Nothing else noticed, because a compiler, a diff and a test run all read
/// past it.
/// </summary>
public class SourceEncodingTests
{
    /// <summary>Files that already had a mark and keep it as found. Each must still
    /// have one, so the scan is shown to detect a mark, and a file that loses its
    /// mark comes off this list.</summary>
    private static readonly string[] Allowed = ["src/MarkdownMidget/App.xaml"];

    /// <summary>The text file types the repository tracks.</summary>
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".xaml", ".md", ".css", ".js", ".mjs", ".json", ".html", ".svg",
        ".csproj", ".slnx", ".pubxml", ".yml", ".xshd", ".manifest",
    };

    /// <summary>Build output and dependencies: not the repository's own files, and not
    /// tracked. Every dot-directory is skipped as well - .git, .vs, .claude and the
    /// worktrees under it, any tool's cache - except <see cref="TrackedDotDirectory"/>,
    /// the one the repository tracks files in.</summary>
    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", "dist", "TestResults",
    };

    private const string TrackedDotDirectory = ".github";

    [Fact]
    public void NoTextFileStartsWithAByteOrderMark()
    {
        var root = RepoSources.Root();
        var files = TextFiles(root).ToList();
        string Relative(string file) => Path.GetRelativePath(root, file).Replace('\\', '/');

        Assert.Contains("tests/MarkdownMidget.Tests/SourceEncodingTests.cs", files.Select(Relative));
        Assert.Contains(".github/workflows/ci.yml", files.Select(Relative));
        foreach (var allowed in Allowed)
            Assert.True(StartsWithMark(Path.Combine(root, allowed)), $"{allowed} has no mark now; take it off the list");

        var marked = files.Where(StartsWithMark).Select(Relative)
            .Except(Allowed, StringComparer.OrdinalIgnoreCase).ToList();
        Assert.True(marked.Count == 0, "UTF-8 byte-order mark at the start of: " + string.Join(", ", marked));
    }

    private static IEnumerable<string> TextFiles(string dir)
    {
        foreach (var file in Directory.EnumerateFiles(dir))
            if (Extensions.Contains(Path.GetExtension(file))) yield return file;
        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            var name = Path.GetFileName(sub);
            if (Skipped.Contains(name)) continue;
            if (name.StartsWith('.') && !name.Equals(TrackedDotDirectory, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var file in TextFiles(sub)) yield return file;
        }
    }

    private static bool StartsWithMark(string path)
    {
        Span<byte> head = stackalloc byte[3];
        using var stream = File.OpenRead(path);
        return stream.ReadAtLeast(head, 3, throwOnEndOfStream: false) == 3
            && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF;
    }
}
