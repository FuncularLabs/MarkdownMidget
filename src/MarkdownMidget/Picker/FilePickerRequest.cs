using System.Collections.Generic;

namespace MarkdownMidget.Picker;

/// <summary>
/// Everything a file dialog needs, in one value — so the same request can be
/// served by the native Win32 dialog (in a child process), by the built-in
/// managed picker, or by a test.
/// </summary>
internal sealed record FilePickerRequest
{
    /// <summary>Save semantics (overwrite prompt, New Folder) rather than open.</summary>
    public bool Save { get; init; }

    public string? Title { get; init; }

    /// <summary>Win32-style: "Markdown (*.md)|*.md|All files (*.*)|*.*".</summary>
    public string Filter { get; init; } = "";

    /// <summary>1-based, matching the Win32 convention the call sites already use.</summary>
    public int FilterIndex { get; init; } = 1;

    public string? InitialDirectory { get; init; }

    /// <summary>Suggested file name (not a path).</summary>
    public string? FileName { get; init; }

    /// <summary>Appended when the user types a name with no extension.</summary>
    public string? DefaultExt { get; init; }

    /// <summary>Open mode: refuse a name that isn't there.</summary>
    public bool CheckFileExists { get; init; }

    /// <summary>Folders offered in the built-in picker's shortcut rail, newest first.</summary>
    public IReadOnlyList<string> RecentFolders { get; init; } = [];
}
