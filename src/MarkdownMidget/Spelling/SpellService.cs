using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace MarkdownMidget.Spelling;

/// <summary>
/// The app's own spell-check engine: the Windows Spell Checking API
/// (<c>ISpellChecker</c>) for the actual checking, with a dictionary that is
/// PRIVATE to Markdown Midget. We deliberately never call
/// <c>ISpellChecker::Add</c> — that writes into the user's OS-wide custom
/// dictionary — and instead filter results against our own word list, stored at
/// %LocalAppData%\MarkdownMidget\dictionary.txt. "Ignore All" words live only for
/// the session.
///
/// All COM access is serialized onto worker threads via a semaphore; the checker
/// is created lazily on first use. Results come back as [start, length) ranges
/// into the text that was checked.
/// </summary>
internal sealed class SpellService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ISpellChecker? _checker;
    private bool _unavailable; // COM creation failed once — don't retry every call

    // _dictionary/_ignored are mutated from the UI thread (context-menu actions)
    // while CheckCore reads them on a worker mid-check — every touch takes _sync.
    private readonly object _sync = new();
    private readonly HashSet<string> _dictionary = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _ignored = new(StringComparer.OrdinalIgnoreCase);

    private static string DictionaryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MarkdownMidget", "dictionary.txt");

    public SpellService()
    {
        try
        {
            if (File.Exists(DictionaryPath))
                foreach (var w in File.ReadAllLines(DictionaryPath))
                    if (!string.IsNullOrWhiteSpace(w)) _dictionary.Add(w.Trim());
        }
        catch { /* no dictionary is fine */ }
    }

    /// <summary>Misspelled ranges in <paramref name="text"/>, private-dictionary and
    /// ignore-list already filtered out. Empty when the engine is unavailable.</summary>
    public async Task<List<(int Start, int Length)>> CheckAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new();
        await _gate.WaitAsync();
        try
        {
            return await Task.Run(() => CheckCore(text));
        }
        finally { _gate.Release(); }
    }

    /// <summary>Up to <paramref name="max"/> suggestions for a word (may be empty).</summary>
    public async Task<List<string>> SuggestAsync(string word, int max = 5)
    {
        if (string.IsNullOrWhiteSpace(word)) return new();
        await _gate.WaitAsync();
        try
        {
            return await Task.Run(() => SuggestCore(word, max));
        }
        finally { _gate.Release(); }
    }

    /// <summary>Add to the app-private dictionary (persisted). Never touches the OS dictionary.</summary>
    public void AddToDictionary(string word)
    {
        if (string.IsNullOrWhiteSpace(word)) return;
        string[] snapshot;
        lock (_sync)
        {
            if (!_dictionary.Add(word.Trim())) return;
            snapshot = _dictionary.OrderBy(w => w, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DictionaryPath)!);
            File.WriteAllLines(DictionaryPath, snapshot);
        }
        catch { /* in-memory add still works this session */ }
    }

    /// <summary>Ignore a word for the rest of this session.</summary>
    public void IgnoreAll(string word)
    {
        if (string.IsNullOrWhiteSpace(word)) return;
        lock (_sync) _ignored.Add(word.Trim());
    }

    public bool IsKnown(string word)
    {
        lock (_sync) return _dictionary.Contains(word) || _ignored.Contains(word);
    }

    // ---- engine core (worker thread; serialized by _gate) ----

    private List<(int, int)> CheckCore(string text)
    {
        var checker = GetChecker();
        var results = new List<(int, int)>();
        if (checker is null) return results;
        try
        {
            var errors = checker.Check(text);
            while (true)
            {
                ISpellingError? err = null;
                try { errors.Next(out err); } catch { break; }
                if (err is null) break;
                var start = (int)err.StartIndex;
                var len = (int)err.Length;
                Marshal.ReleaseComObject(err);
                if (start < 0 || len <= 0 || start + len > text.Length) continue;
                var word = text.Substring(start, len);
                if (IsKnown(word)) continue;               // the private dictionary IS the filter
                results.Add((start, len));
            }
            Marshal.ReleaseComObject(errors);
        }
        catch { /* a single bad call returns what we have */ }
        return results;
    }

    private List<string> SuggestCore(string word, int max)
    {
        var checker = GetChecker();
        var list = new List<string>();
        if (checker is null) return list;
        try
        {
            var e = checker.Suggest(word);
            var buf = new string[1];
            while (list.Count < max && e.Next(1, buf, IntPtr.Zero) == 0 && buf[0] is not null)
                list.Add(buf[0]);
            Marshal.ReleaseComObject(e);
        }
        catch { /* suggestions are best-effort */ }
        return list;
    }

    private ISpellChecker? GetChecker()
    {
        if (_checker is not null) return _checker;
        if (_unavailable) return null;
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("7AB36653-1796-484B-BDFA-E74F1DB7C1DC"))!;
            var factory = (ISpellCheckerFactory)Activator.CreateInstance(type)!;
            factory.IsSupported("en-US", out var supported);
            if (supported == 0) { _unavailable = true; return null; }
            _checker = factory.CreateSpellChecker("en-US");
            return _checker;
        }
        catch
        {
            _unavailable = true;   // e.g. Windows spell checking disabled by policy
            return null;
        }
    }
}

// ===== Minimal COM interop for the Windows Spell Checking API (spellcheck.h) =====
// Vtable order must match the header exactly. Verified against .NET 10 in the
// de-risk spike (interop, ranges, suggestions, and perf: 50k chars ≈ 100ms,
// one paragraph ≈ 1ms).

[ComImport, Guid("8E018A9D-2415-4677-BF08-794EA61F94BB")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISpellCheckerFactory
{
    [return: MarshalAs(UnmanagedType.Interface)] object get_SupportedLanguages();
    void IsSupported([MarshalAs(UnmanagedType.LPWStr)] string languageTag, out int value);
    [return: MarshalAs(UnmanagedType.Interface)] ISpellChecker CreateSpellChecker(
        [MarshalAs(UnmanagedType.LPWStr)] string languageTag);
}

[ComImport, Guid("B6FD0B71-E2BC-4653-8D05-F197E412770B")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISpellChecker
{
    string LanguageTag { [return: MarshalAs(UnmanagedType.LPWStr)] get; }
    [return: MarshalAs(UnmanagedType.Interface)] IEnumSpellingError Check(
        [MarshalAs(UnmanagedType.LPWStr)] string text);
    [return: MarshalAs(UnmanagedType.Interface)] IEnumString Suggest(
        [MarshalAs(UnmanagedType.LPWStr)] string word);
    void Add([MarshalAs(UnmanagedType.LPWStr)] string word);   // NEVER CALLED — writes the OS dictionary
    void Ignore([MarshalAs(UnmanagedType.LPWStr)] string word);
    void AutoCorrect([MarshalAs(UnmanagedType.LPWStr)] string from,
                     [MarshalAs(UnmanagedType.LPWStr)] string to);
    void GetOptionValue([MarshalAs(UnmanagedType.LPWStr)] string optionId, out byte value);
    [return: MarshalAs(UnmanagedType.Interface)] object get_OptionIds();
    string Id { [return: MarshalAs(UnmanagedType.LPWStr)] get; }
    string LocalizedName { [return: MarshalAs(UnmanagedType.LPWStr)] get; }
}

[ComImport, Guid("803E3BD4-2828-4410-8290-418D1D73C762")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IEnumSpellingError
{
    void Next([MarshalAs(UnmanagedType.Interface)] out ISpellingError? value);
}

[ComImport, Guid("B7C82D61-FBE8-4B47-9B27-6C0D2E0DE0A3")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISpellingError
{
    uint StartIndex { get; }
    uint Length { get; }
    uint CorrectiveAction { get; }
    string Replacement { [return: MarshalAs(UnmanagedType.LPWStr)] get; }
}

[ComImport, Guid("00000101-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IEnumString
{
    [PreserveSig] int Next(int celt,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr), Out] string[] rgelt,
        IntPtr pceltFetched);
    [PreserveSig] int Skip(int celt);
    void Reset();
    void Clone(out IEnumString ppenum);
}
