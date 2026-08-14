using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MarkdownMidget.Spelling;

/// <summary>
/// Reads words out of a Word custom dictionary (CUSTOM.DIC) so they can be imported
/// into the app-private dictionary.
///
/// IMPORT ONLY — NEVER WRITE BACK. This class deliberately has no write method, and
/// must never grow one: the app's whole spelling design is that its dictionary is
/// private (<see cref="SpellService"/> never touches the OS or Office dictionaries),
/// and an import that also "synced" would quietly break that promise. The constraint
/// is stated here, in the code, because it is the sort of rule a later change erodes
/// by accident when it only lives in the roadmap.
/// </summary>
internal static class CustomDicImport
{
    /// <summary>
    /// Where Word keeps the default custom dictionary for the current user.
    /// (Word can be configured to use others; the file picker covers those.)
    /// </summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Microsoft", "UProof", "CUSTOM.DIC");

    /// <summary>Longest word accepted. CUSTOM.DIC entries are single words; anything
    /// longer than this is a corrupt line or the wrong kind of file.</summary>
    public const int MaxWordLength = 64;

    /// <summary>
    /// Largest file accepted. A real CUSTOM.DIC is kilobytes; 2 MB of UTF-16 is
    /// roughly a hundred thousand words, far past any human vocabulary file. The
    /// cap exists because parsing runs synchronously on the UI thread from the
    /// Settings dialog — a mistakenly-picked huge file should be refused with a
    /// sentence, not freeze the window while it chews.</summary>
    public const long MaxFileBytes = 2 * 1024 * 1024;

    /// <summary>
    /// Parse the words from a .dic file's bytes. Order preserved, duplicates removed
    /// (case-insensitively, matching the app dictionary's own comparer).
    /// </summary>
    public static List<string> Parse(byte[] bytes)
    {
        var text = Decode(bytes);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var words = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var w = raw.Trim().TrimEnd('\r').Trim();
            if (w.Length == 0 || w.Length > MaxWordLength) continue;
            // A custom dictionary holds one word per line. A line with interior
            // whitespace is not a word — likely a header or a different file format —
            // and importing "half" of it would add a word the user never chose.
            if (w.Any(char.IsWhiteSpace)) continue;
            // Not-words from the wrong kind of .dic. Hunspell dictionaries share the
            // extension: their first line is a bare entry count and their entries
            // carry affix flags ("abandon/DGS") — both would sail through the checks
            // above and import thousands of flagged non-words permanently, reported
            // as success. An all-digit line is never a spelling, and '/' never
            // appears in one.
            if (w.All(char.IsDigit)) continue;
            if (w.Contains('/')) continue;
            // Control characters and the U+FFFD replacement char both mean the bytes
            // were not text in the encoding we decoded with; skip rather than import
            // mojibake into the dictionary, where it would be near-impossible to
            // find and remove by eye.
            if (w.Any(c => char.IsControl(c) || c == '�')) continue;
            if (seen.Add(w)) words.Add(w);
        }
        return words;
    }

    public static List<string> ParseFile(string path) => Parse(File.ReadAllBytes(path));

    /// <summary>
    /// CUSTOM.DIC encoding, by evidence rather than assumption. Modern Word writes
    /// UTF-16 LE with a BOM. Older files can be BOM-less UTF-16 or ANSI. A text file
    /// never legitimately contains NUL bytes, so their presence in a BOM-less file is
    /// decisive for UTF-16; everything else is read as UTF-8, which covers ASCII and
    /// correctly-encoded UTF-8, while mis-encoded legacy bytes surface as U+FFFD and
    /// are filtered per word above rather than failing the whole import.
    /// </summary>
    private static string Decode(byte[] b)
    {
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return Encoding.Unicode.GetString(b, 2, b.Length - 2);
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2);
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return Encoding.UTF8.GetString(b, 3, b.Length - 3);
        if (b.Contains((byte)0))
        {
            // BOM-less UTF-16: decide endianness by which side of the pairs the NULs
            // sit on. For the Latin-heavy text a dictionary holds, LE puts NULs at
            // odd indexes, BE at even.
            int oddNuls = 0, evenNuls = 0;
            for (var i = 0; i < b.Length; i++)
                if (b[i] == 0) { if ((i & 1) == 1) oddNuls++; else evenNuls++; }
            return oddNuls >= evenNuls ? Encoding.Unicode.GetString(b) : Encoding.BigEndianUnicode.GetString(b);
        }
        return Encoding.UTF8.GetString(b);
    }
}
