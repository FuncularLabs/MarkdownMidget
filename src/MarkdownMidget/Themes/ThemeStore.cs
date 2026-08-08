using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using MarkdownMidget.Updates;

namespace MarkdownMidget.Themes;

/// <summary>
/// One entry in the View ▸ Theme menu.
/// </summary>
/// <param name="Name">What the menu shows.</param>
/// <param name="Key">
/// The persistence key — a bare filename, never a path. What goes in settings.json,
/// and what a later launch matches against this list. It is deliberately not a path:
/// a hand-edited settings file must not be able to name a file outside the themes
/// folder, and matching a name against an enumerated list is the only lookup that
/// cannot be talked into traversal.
/// </param>
/// <param name="Path">Where to read it from; null for Default, which is the palette
/// already in the bundle and so has no file and nothing to inject.</param>
/// <param name="IsCustom">From <c>custom\</c> — the user's, never overwritten.</param>
/// <param name="Unusable">
/// Null when the theme can be applied. Otherwise the reason, phrased for a tooltip
/// on a disabled menu item: too big, unreadable, or refused by the validator.
/// </param>
internal sealed record ThemeFile(
    string Name,
    string Key,
    string? Path,
    bool IsCustom,
    string? Unusable)
{
    public bool IsUsable => Unusable is null;
}

/// <summary>
/// Finds, refreshes and reads theme files.
///
/// Two directories, and the difference between them is the whole design:
///
///   themes\          built-ins, rewritten from the assembly when the version moves
///   themes\custom\   the user's, written once (a commented sample) and never again
///
/// A built-in is a copy of something that ships inside the exe, so an update can
/// deliver a fix to it; that is only true if launch overwrites what is there. The
/// corollary — an edit to a built-in is lost — is why <c>custom\</c> exists and why
/// the sample says "copy me and rename" in its first line.
///
/// Nothing here trusts a filename from outside. Enumeration is non-recursive, the
/// persisted setting is matched against the enumerated list rather than combined
/// into a path, and every file is size-capped before it is read and validated
/// before it is offered.
/// </summary>
internal sealed class ThemeStore
{
    /// <summary>
    /// A ceiling on a user-supplied file that is read, held in memory, marshalled
    /// across the JS bridge as a string literal and installed as the text of a
    /// &lt;style&gt; element. The six shipped palettes are a few KB; anything near
    /// this is a mistake or a wedge.
    /// </summary>
    public const int MaxBytes = 256 * 1024;

    /// <summary>The entry that means "the palette already in the bundle".</summary>
    public const string DefaultKey = "";

    private const string ResourcePrefix = "themes/";
    private const string SampleResource = "themes-sample.css";
    private const string SampleFileName = "sample.css";
    private const string StampFile = ".version";

    private readonly string _root;

    public ThemeStore(string root) => _root = root;

    public string Root => _root;
    public string CustomDir => Path.Combine(_root, "custom");

    // ===== where the folder lives =====

    /// <summary>
    /// Installed builds keep themes in the profile; a portable exe keeps them next to
    /// itself, so a copy on a stick carries its themes with it.
    ///
    /// The portable answer is a preference, not a guarantee: <paramref name="exeDir"/>
    /// can be read-only, a network share, a CD, or somewhere an AV product objects to.
    /// So it is *tried*, and a failure falls back to the profile rather than starting
    /// with no themes at all. The caller is told which happened so it can say so once.
    /// </summary>
    public static (string Root, bool FellBack) ResolveRoot(bool installed, string exeDir, string localAppData)
    {
        var profile = Path.Combine(localAppData, "MarkdownMidget", "themes");
        if (installed) return (profile, false);

        var portable = Path.Combine(exeDir, "themes");
        try
        {
            Directory.CreateDirectory(portable);
            // Creating a directory can succeed where writing a file cannot — a
            // read-only share often permits neither, but a quota or a deny-write ACL
            // on files specifically permits exactly the first and not the second. The
            // probe writes, because writing is what we are about to need.
            var probe = Path.Combine(portable, ".write-probe");
            File.WriteAllBytes(probe, Array.Empty<byte>());
            File.Delete(probe);
            return (portable, false);
        }
        catch
        {
            return (profile, true);
        }
    }

    public static (string Root, bool FellBack) ResolveRoot() => ResolveRoot(
        IsInstalled(),
        Path.GetDirectoryName(Environment.ProcessPath ?? "") is { Length: > 0 } d ? d : Environment.CurrentDirectory,
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    private static bool IsInstalled()
    {
        try { return RegistrationService.IsRunningFromAppDataInstall(); }
        catch { return false; }
    }

    // ===== refreshing what ships in the exe =====

    /// <summary>
    /// Write the built-in themes out of the assembly, and seed <c>custom\</c> with the
    /// sample the first time.
    ///
    /// Gated on a version stamp, and the comparison is <em>older than ours</em> rather
    /// than <em>different from ours</em>. Different-from would be enough for an
    /// installed build, where one exe owns the folder — but a portable update
    /// deliberately leaves the old exe in place, so two versions share one folder, and
    /// under different-from they would overwrite each other's built-ins on alternate
    /// launches. Running last must not mean winning: an old exe launched once should
    /// leave a newer version's theme fixes exactly where it found them.
    /// </summary>
    /// <returns>True if anything was written.</returns>
    public bool Refresh(string version) => Refresh(version, Assembly.GetExecutingAssembly());

    public bool Refresh(string version, Assembly assembly)
    {
        try
        {
            Directory.CreateDirectory(_root);
            SeedSample(assembly);

            if (!NeedsExtract(version)) return false;

            foreach (var name in assembly.GetManifestResourceNames())
            {
                if (!name.StartsWith(ResourcePrefix, StringComparison.Ordinal)) continue;
                var leaf = name[ResourcePrefix.Length..];
                // Flat by construction — a resource name with a separator in it would
                // otherwise land outside the folder.
                if (leaf.Length == 0 || leaf.Contains('/') || leaf.Contains('\\')) continue;

                using var stream = assembly.GetManifestResourceStream(name);
                if (stream is null) continue;
                using var file = File.Create(Path.Combine(_root, leaf));
                stream.CopyTo(file);
            }

            // Stamped only now. A stamp written before the copy would tell the next
            // launch the folder was current when extraction had in fact failed
            // half-way, and no later launch would ever repair it.
            File.WriteAllText(Path.Combine(_root, StampFile), version);
            return true;
        }
        catch
        {
            // No themes but Default, which still works. Not worth failing a launch.
            return false;
        }
    }

    private bool NeedsExtract(string version)
    {
        var mine = UpdateVersion.Parse(version);
        if (mine is null) return true;                  // can't reason about it: refresh
        try
        {
            var stampPath = Path.Combine(_root, StampFile);
            if (!File.Exists(stampPath)) return true;
            var theirs = UpdateVersion.Parse(File.ReadAllText(stampPath).Trim());
            if (theirs is null) return true;            // unreadable stamp: refresh
            return mine.CompareTo(theirs) > 0;
        }
        catch { return true; }
    }

    /// <summary>
    /// Put the commented sample in <c>custom\</c> — but only into an empty folder.
    ///
    /// "Written once" has to survive a user who deletes the sample, and a marker file
    /// for that is a second thing that can go wrong. Emptiness is the marker: someone
    /// with themes of their own never sees it return, and someone with none gets a
    /// starting point back, which is the harmless direction to be wrong in.
    /// </summary>
    private void SeedSample(Assembly assembly)
    {
        try
        {
            var dir = CustomDir;
            Directory.CreateDirectory(dir);
            if (Directory.EnumerateFiles(dir, "*.css").Any()) return;

            using var stream = assembly.GetManifestResourceStream(SampleResource);
            if (stream is null) return;
            using var file = File.Create(Path.Combine(dir, SampleFileName));
            stream.CopyTo(file);
        }
        catch { /* the sample is a courtesy, not a dependency */ }
    }

    // ===== what the menu offers =====

    /// <summary>
    /// Every theme on offer: Default first, then built-ins, then the user's, each
    /// group alphabetical.
    ///
    /// A custom file wins a name collision with a built-in and is marked so in the
    /// menu — putting <c>dracula.css</c> in <c>custom\</c> is how you say "mine, not
    /// yours", and the built-in copy would be overwritten by the next update anyway.
    /// </summary>
    public IReadOnlyList<ThemeFile> List()
    {
        var custom = Enumerate(CustomDir, isCustom: true);
        var taken = custom.Select(t => t.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var builtIn = Enumerate(_root, isCustom: false)
            .Where(t => !taken.Contains(t.Key));

        return new[] { new ThemeFile("Default", DefaultKey, null, false, null) }
            .Concat(builtIn.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase))
            .Concat(custom.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase))
            .ToList();
    }

    private List<ThemeFile> Enumerate(string dir, bool isCustom)
    {
        var found = new List<ThemeFile>();
        try
        {
            if (!Directory.Exists(dir)) return found;
            // Top level only. A theme is a file you dropped in a folder, not a tree to
            // walk, and not walking is also how a junction someone planted stops being
            // interesting.
            foreach (var path in Directory.EnumerateFiles(dir, "*.css", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(path);
                found.Add(new ThemeFile(DisplayName(name), name, path, isCustom, Inspect(path)));
            }
        }
        catch { /* unreadable folder: no themes from it */ }
        return found;
    }

    /// <summary>
    /// Why this file can't be offered, or null.
    ///
    /// The work is done here, at enumeration, so the menu can grey an entry and put
    /// the reason in its tooltip. A theme that turns out to be broken at click time is
    /// a worse experience than one that was never clickable.
    /// </summary>
    private static string? Inspect(string path)
    {
        var css = ReadBounded(path, out var failure);
        if (css is null) return failure;
        return CssValidator.Validate(css)?.ToString();
    }

    /// <summary>
    /// The text of a theme, or null with a reason.
    ///
    /// Read and validated again rather than trusting what enumeration decided: the
    /// menu is built when the submenu opens and clicked some time after, and the file
    /// is the user's to change in between. Cheap, and it closes the window.
    /// </summary>
    public string? Read(ThemeFile theme, out string? failure)
    {
        failure = null;
        if (theme.Path is null) return string.Empty;    // Default: nothing to inject

        var css = ReadBounded(theme.Path, out failure);
        if (css is null) return null;

        var problem = CssValidator.Validate(css);
        if (problem is null) return css;
        failure = problem.Value.ToString();
        return null;
    }

    /// <summary>Match a persisted key against what is actually there.</summary>
    public ThemeFile? Find(string? key) =>
        List().FirstOrDefault(t => string.Equals(t.Key, key ?? DefaultKey, StringComparison.OrdinalIgnoreCase));

    private static string? ReadBounded(string path, out string? failure)
    {
        failure = null;
        try
        {
            // ReadWrite sharing: a theme open in someone's editor is still readable,
            // and refusing to show it in the menu because Notepad has it would be an
            // odd thing for the app to do.
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            // One byte past the cap, so a file that grew between the length check and
            // the read is refused rather than read whole. The length check alone is a
            // statement about the past.
            var buffer = new byte[MaxBytes + 1];
            var read = fs.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            if (read > MaxBytes)
            {
                failure = $"the file is larger than {MaxBytes / 1024} KB";
                return null;
            }

            using var ms = new MemoryStream(buffer, 0, read);
            // BOM-sniffing, defaulting to UTF-8. A UTF-16 file saved by Notepad would
            // otherwise arrive as alternating nulls and be rejected for a reason that
            // says nothing about what is wrong with it.
            using var reader = new StreamReader(ms, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch (Exception ex)
        {
            failure = ex.Message;
            return null;
        }
    }

    /// <summary>
    /// <c>solarized-light.css</c> → "Solarized Light". Filename-derived on purpose:
    /// a name declared inside the file would be a second parser over untrusted text,
    /// and one that could put anything it liked in the menu bar.
    /// </summary>
    internal static string DisplayName(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (stem.StartsWith("theme-", StringComparison.OrdinalIgnoreCase)) stem = stem[6..];
        stem = stem.Replace('-', ' ').Replace('_', ' ').Trim();
        if (stem.Length == 0) return fileName;

        var words = stem.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => char.ToUpperInvariant(w[0]) + w[1..]);
        return string.Join(' ', words);
    }
}
