using System.IO;

namespace MarkdownMidget.Picker;

/// <summary>
/// The built-in picker's Type column: "File folder" for a folder, and for a file the name
/// Windows registers for its extension, read as plain registry values and nothing else:
/// loading DLLs and shell extensions, as shell calls do, is what crashes Windows' dialog.
/// HKEY_CLASSES_ROOT\.ext names a ProgID, and its FriendlyTypeName, or else its default
/// value, is the name. An indirect name ("@shell32.dll,-10152", which only loading that DLL
/// resolves), no name, or a key that can't be read gives "MD File", the extension
/// upper-cased; no extension gives "File". One instance lives as long as one picker and
/// reads each extension once, so the next picker sees a name registered since.
/// </summary>
internal sealed class FileTypeNames
{
    public const string Folder = "File folder";

    /// <summary>A value under HKEY_CLASSES_ROOT, by subkey and value name ("" for the default
    /// value), or null. The picker passes the real registry; tests pass a fake, so no test
    /// reads the real one.</summary>
    private readonly Func<string, string, object?> _read;
    private readonly Dictionary<string, string> _byExtension = new(StringComparer.OrdinalIgnoreCase);

    public FileTypeNames(Func<string, string, object?> read) => _read = read;

    public string Of(string name, bool isDirectory)
    {
        if (isDirectory) return Folder;
        var ext = Path.GetExtension(name);
        if (ext.Length < 2) return "File";
        if (!_byExtension.TryGetValue(ext, out var type))
            _byExtension[ext] = type = Registered(ext) ?? ext[1..].ToUpperInvariant() + " File";
        return type;
    }

    private string? Registered(string ext)
    {
        try { return Plain(_read(ext, "")) is { } progId ? Plain(_read(progId, "FriendlyTypeName")) ?? Plain(_read(progId, "")) : null; }
        catch (Exception) { return null; }   // denied, gone, or too long for a key name: the extension still names it
    }

    /// <summary>Text to show as it is: not blank, and not an indirect "@file,-id".</summary>
    private static string? Plain(object? value) =>
        value is string text && text.Trim() is { Length: > 0 } trimmed && trimmed[0] != '@' ? trimmed : null;
}
