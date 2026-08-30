using System;

namespace MarkdownMidget.Backup;

/// <summary>
/// What was in a window that never got saved, and where it came from.
///
/// Stored beside the content rather than inside it so the content file stays
/// exactly the markdown the user typed — recoverable by hand with any editor if
/// this code ever fails them.
/// </summary>
internal sealed class BackupSnapshot
{
    /// <summary>Identifies the session's three files. Not the process id: those get
    /// reused, and a reused id makes a dead session look alive.</summary>
    public string SessionId { get; set; } = "";

    /// <summary>The file this content belongs to, or null for a document that was
    /// never saved anywhere.</summary>
    public string? Path { get; set; }

    /// <summary>Title for content with no path — dropped text, mostly.</summary>
    public string? DisplayName { get; set; }

    public DateTime SavedUtc { get; set; }

    /// <summary>
    /// How many times we've handed this back to a window. A document that crashes
    /// the app on load would otherwise be restored, crash, and be restored again on
    /// every launch forever.
    /// </summary>
    public int RecoveryAttempts { get; set; }

    /// <summary>
    /// True when the content file is an encrypted .mdenc container rather than
    /// plain markdown — the snapshot of a password-protected document, readable
    /// only with that document's password. Old metadata files lack the field
    /// and deserialize to false, which is correct: they are all plaintext.
    /// </summary>
    public bool Encrypted { get; set; }

    /// <summary>
    /// True once the user has been told we're giving up on this one. The snapshot
    /// stays on disk — it's their work — but telling them again on every launch for
    /// the rest of the install's life is nagging, not helping.
    /// </summary>
    public bool GiveUpReported { get; set; }

    /// <summary>Name shown to the user when talking about this snapshot.</summary>
    public string Describe() =>
        Path is not null ? System.IO.Path.GetFileName(Path)
        : DisplayName is not null ? DisplayName
        : "Untitled";
}
