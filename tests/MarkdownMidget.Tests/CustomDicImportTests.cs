using System;
using System.Linq;
using System.Text;
using MarkdownMidget.Spelling;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>
/// Parsing Word's CUSTOM.DIC. The file is the user's, written by several
/// generations of Word in several encodings, and every word that comes out of here
/// lands permanently in the app dictionary — so the failure that matters is not
/// "missed a word", it is importing garbage that then suppresses real squiggles
/// and is near-impossible to find and remove by eye.
/// </summary>
public class CustomDicImportTests
{
    private static byte[] Utf16Le(string text, bool bom = true)
    {
        var body = Encoding.Unicode.GetBytes(text);
        return bom ? Encoding.Unicode.GetPreamble().Concat(body).ToArray() : body;
    }

    // ===== encodings: what real Words actually write =====

    [Fact]
    public void ModernWordsUtf16BomFileParses()
        // The common case: UTF-16 LE with BOM, CRLF line ends.
        => Assert.Equal(new[] { "Funcular", "MarkdownMidget", "Solarized" },
            CustomDicImport.Parse(Utf16Le("Funcular\r\nMarkdownMidget\r\nSolarized\r\n")));

    [Fact]
    public void BomlessUtf16StillDecodes()
    {
        // Older files can lack the BOM. Text never legitimately contains NUL bytes,
        // so their presence is decisive for UTF-16 — reading these bytes as UTF-8
        // would yield interleaved NULs and zero usable words.
        var words = CustomDicImport.Parse(Utf16Le("Pelorus\r\nSentinel\r\n", bom: false));
        Assert.Equal(new[] { "Pelorus", "Sentinel" }, words);
    }

    [Fact]
    public void BigEndianUtf16WithBomParses()
        => Assert.Equal(new[] { "Björk" },
            CustomDicImport.Parse(
                Encoding.BigEndianUnicode.GetPreamble()
                    .Concat(Encoding.BigEndianUnicode.GetBytes("Björk\r\n")).ToArray()));

    [Fact]
    public void PlainAsciiParsesAsUtf8()
        => Assert.Equal(new[] { "alpha", "beta" },
            CustomDicImport.Parse(Encoding.ASCII.GetBytes("alpha\nbeta\n")));

    [Fact]
    public void Utf8WithBomAndAccentsParses()
        => Assert.Equal(new[] { "café", "naïve" },
            CustomDicImport.Parse(
                Encoding.UTF8.GetPreamble()
                    .Concat(Encoding.UTF8.GetBytes("café\r\nnaïve\r\n")).ToArray()));

    [Fact]
    public void MisencodedLegacyBytesAreSkippedNotImported()
    {
        // An ANSI-era word with a 0xE9 é read as UTF-8 produces U+FFFD. That word is
        // dropped; the valid words around it still import. Mojibake in a dictionary
        // silently blesses misspellings forever, which is worse than missing a word
        // the user can re-add in two clicks.
        var bytes = Encoding.ASCII.GetBytes("good\r\ncaf")
            .Concat(new byte[] { 0xE9 })
            .Concat(Encoding.ASCII.GetBytes("\r\nalso\r\n")).ToArray();
        Assert.Equal(new[] { "good", "also" }, CustomDicImport.Parse(bytes));
    }

    // ===== filtering: what must never get in =====

    [Fact]
    public void BlankAndWhitespaceLinesVanish()
        => Assert.Equal(new[] { "word" },
            CustomDicImport.Parse(Utf16Le("\r\n   \r\nword\r\n\r\n")));

    [Fact]
    public void ALineWithInteriorWhitespaceIsNotAWord()
        // Headers, phrases, or the wrong kind of file entirely. Importing "half" of
        // a phrase would add a word the user never chose.
        => Assert.Equal(new[] { "single" },
            CustomDicImport.Parse(Utf16Le("two words\r\nsingle\r\n")));

    [Fact]
    public void AWordLongerThanTheCapIsACorruptLineNotAWord()
        => Assert.Equal(new[] { "fits" },
            CustomDicImport.Parse(Utf16Le(new string('x', CustomDicImport.MaxWordLength + 1) + "\r\nfits\r\n")));

    [Fact]
    public void AWordExactlyAtTheCapSurvives()
    {
        var max = new string('x', CustomDicImport.MaxWordLength);
        Assert.Equal(new[] { max }, CustomDicImport.Parse(Utf16Le(max + "\r\n")));
    }

    [Fact]
    public void ControlCharactersDisqualifyTheWord()
        => Assert.Equal(new[] { "clean" },
            CustomDicImport.Parse(Utf16Le("bad\u0001word\r\nclean\r\n")));

    [Fact]
    public void DuplicatesCollapseCaseInsensitively()
        // Matches the app dictionary's own comparer (OrdinalIgnoreCase): importing
        // both "Sentinel" and "sentinel" must count one word, first spelling wins.
        => Assert.Equal(new[] { "Sentinel", "other" },
            CustomDicImport.Parse(Utf16Le("Sentinel\r\nsentinel\r\nSENTINEL\r\nother\r\n")));

    [Fact]
    public void AHunspellDictionaryImportsNothingItShouldnt()
    {
        // Hunspell .dic files share the extension and the picker's filter: first
        // line is a bare entry count, entries carry affix flags after a slash.
        // Both shapes passed every original filter — no whitespace, no control
        // chars, under the length cap — and would have imported thousands of
        // "abandon/DGS"-style non-words permanently, reported as success.
        var words = CustomDicImport.Parse(Utf16Le("49271\r\nabandon/DGS\r\nplainword\r\nzero/0\r\n"));
        Assert.Equal(new[] { "plainword" }, words);
    }

    [Fact]
    public void AllDigitLinesAreNeverWords()
        // Covers the Hunspell count header wherever it appears, and stray numbers
        // generally — a number is not a spelling.
        => Assert.Equal(new[] { "real" },
            CustomDicImport.Parse(Utf16Le("12345\r\nreal\r\n007\r\n")));

    [Fact]
    public void TheFileSizeCapIsSaneForRealDictionaries()
    {
        // A genuine CUSTOM.DIC is kilobytes. The cap guards the UI thread against a
        // mispicked huge file, and must stay far above any plausible real one.
        Assert.True(CustomDicImport.MaxFileBytes >= 1024 * 1024,
            "cap must comfortably exceed any real custom dictionary");
    }

    [Fact]
    public void AnEmptyFileYieldsNoWordsAndNoThrow()
        => Assert.Empty(CustomDicImport.Parse(Array.Empty<byte>()));

    [Fact]
    public void OrderIsPreservedFromTheFile()
        // Not sorted here — the dictionary sorts on write. Preserving file order
        // keeps this parser a pure read with no opinions to drift.
        => Assert.Equal(new[] { "zebra", "apple", "mango" },
            CustomDicImport.Parse(Utf16Le("zebra\r\napple\r\nmango\r\n")));

    [Fact]
    public void TheDefaultPathPointsAtWordsUProofFolder()
    {
        Assert.EndsWith(@"Microsoft\UProof\CUSTOM.DIC", CustomDicImport.DefaultPath);
        // And it is under the roaming profile, which is where Office keeps it.
        Assert.Contains("Roaming", CustomDicImport.DefaultPath, StringComparison.OrdinalIgnoreCase);
    }
}
