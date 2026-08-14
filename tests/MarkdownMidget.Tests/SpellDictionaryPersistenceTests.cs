using System;
using System.IO;
using System.Linq;
using MarkdownMidget.Spelling;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>
/// The dictionary file is shared by every window (the app is one process per
/// window), and each window loads it once at startup. These tests pin the
/// merge-on-write behaviour that keeps one window's bulk import from being
/// erased by another window's single later "Add to Dictionary" — the highest
/// finding of the 0.8 import review, reproduced here exactly as it would happen.
/// </summary>
public class SpellDictionaryPersistenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "mm-dict-tests-" + Guid.NewGuid().ToString("N"));
    private string DictPath => Path.Combine(_dir, "dictionary.txt");

    public SpellDictionaryPersistenceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void AStaleWindowsSingleAddDoesNotEraseASiblingsImport()
    {
        // The clobber, step by step. Window B opens first and loads an empty
        // dictionary. Window A then imports a vocabulary. B — still holding its
        // stale in-memory view — adds ONE word an hour later. Under snapshot-write,
        // B's write published its stale set and deleted A's entire import from
        // disk; under merge-on-write, the file ends with all of it.
        var windowB = new SpellService(DictPath);              // opened first, empty view
        var windowA = new SpellService(DictPath);
        var (added, _, persisted) = windowA.ImportWords(new[] { "funcular", "pelorus", "sentinel" });
        Assert.Equal(3, added);
        Assert.True(persisted);

        windowB.AddToDictionary("midget");                     // the stale single add

        var onDisk = File.ReadAllLines(DictPath);
        Assert.Contains("funcular", onDisk);
        Assert.Contains("pelorus", onDisk);
        Assert.Contains("sentinel", onDisk);
        Assert.Contains("midget", onDisk);
    }

    [Fact]
    public void TheWriterAlsoLearnsWhatItsSiblingsWrote()
    {
        // Merge-on-write flows both directions: the words B found on disk during
        // its write join B's own memory, so B stops re-squiggling A's vocabulary
        // for the rest of its session.
        var windowB = new SpellService(DictPath);
        var windowA = new SpellService(DictPath);
        windowA.ImportWords(new[] { "solarized" });

        windowB.AddToDictionary("unrelated");
        Assert.True(windowB.IsKnown("solarized"));
    }

    [Fact]
    public void ImportReportsCountsAndPersistence()
    {
        var svc = new SpellService(DictPath);
        var first = svc.ImportWords(new[] { "alpha", "beta", "alpha" });
        Assert.Equal((2, 1, true), first);

        var again = new SpellService(DictPath).ImportWords(new[] { "alpha", "gamma" });
        Assert.Equal((1, 1, true), again);
        Assert.Equal(3, File.ReadAllLines(DictPath).Count(l => !string.IsNullOrWhiteSpace(l)));
    }

    [Fact]
    public void AnImportOfNothingNewDoesNotTouchTheFile()
    {
        var svc = new SpellService(DictPath);
        svc.ImportWords(new[] { "word" });
        var before = File.GetLastWriteTimeUtc(DictPath);

        var (added, known, persisted) = new SpellService(DictPath).ImportWords(new[] { "word" });
        Assert.Equal((0, 1, true), (added, known, persisted));
        Assert.Equal(before, File.GetLastWriteTimeUtc(DictPath));
    }

    [Fact]
    public void NoTempFileSurvivesAWrite()
    {
        // The atomic-rename pattern must clean its scaffolding; a litter of .tmp
        // files beside the dictionary is how the settings pattern's mistakes look.
        new SpellService(DictPath).ImportWords(new[] { "one", "two" });
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }
}
