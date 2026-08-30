using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MarkdownMidget.Backup;

/// <summary>
/// Keeps a copy of unsaved work on disk so a crash, a power cut or a killed
/// process doesn't take it with them — the behaviour Notepad++ users expect.
///
/// Each window owns a session made of three files:
///   {id}.md    the markdown, verbatim
///   {id}.json  where it came from
///   {id}.lock  held open, exclusively, for as long as the window lives
///
/// A password-protected document swaps the first for {id}.mdenc — the same
/// encrypted container its file uses, because a crash-backup that leaked the
/// plaintext of an encrypted document every five seconds would defeat the
/// entire feature. Which kind a session holds is recorded in the metadata
/// (BackupSnapshot.Encrypted).
///
/// The lock file is how a later launch tells a crashed session from a running one.
/// Asking the OS whether a process id is alive is unreliable — ids get reused, and
/// two instances of this app look identical — but a lock is released by the kernel
/// when the process dies, however it dies. If the lock opens, nobody owns it.
///
/// Writes go to a temp file and are then moved into place: a snapshot is replaced
/// atomically or not at all, so a crash mid-write can't leave a half-written
/// recovery file, which would be worse than none.
/// </summary>
internal sealed class BackupStore : IDisposable
{
    private readonly string _dir;
    private readonly string _sessionId;
    private FileStream? _lock;

    /// <summary>
    /// Attempts inherited from a snapshot we adopted. Carried forward because every
    /// Save writes fresh metadata: without this the count would reset to zero the
    /// instant a snapshot was recovered — and again on every timer tick after that —
    /// so a document that crashes the app on load would be retried forever, which is
    /// exactly what the counter exists to prevent.
    /// </summary>
    private int _attempts;

    /// <summary>Test seam: runs after the encrypted snapshot's atomic write,
    /// before the read-back verification - lets a test model disk/AV corruption
    /// in the only window where it matters.</summary>
    internal Action<string>? TestHookAfterEncryptedWrite;

    public BackupStore(string directory, string sessionId)
    {
        _dir = directory;
        _sessionId = sessionId;
    }

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MarkdownMidget", "backup");

    private string ContentPath(string id) => Path.Combine(_dir, id + ".md");
    private string EncryptedContentPath(string id) => Path.Combine(_dir, id + ".mdenc");
    private string MetaPath(string id) => Path.Combine(_dir, id + ".json");
    private string LockPath(string id) => Path.Combine(_dir, id + ".lock");

    /// <summary>
    /// Serialize recovery across instances. Two windows opening at once would
    /// otherwise both find the same orphans and both restore them, so the user gets
    /// each lost document twice. Whoever loses the race simply doesn't recover —
    /// the other one is already doing it.
    /// </summary>
    public IDisposable? BeginRecovery()
    {
        try
        {
            Directory.CreateDirectory(_dir);
            return new FileStream(Path.Combine(_dir, "recovery.lock"), FileMode.Create,
                                  FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch { return null; }
    }

    /// <summary>
    /// Abandoned ENCRYPTED snapshots, for the recovery flow's password prompts.
    /// The sealed container is returned as-is - only the caller can open it, and
    /// only with the user's password. Enumerated separately from FindOrphans so
    /// the plaintext flow's contract (markdown in, markdown out) stays intact.
    /// </summary>
    public IReadOnlyList<(BackupSnapshot Meta, byte[] Container)> FindEncryptedOrphans()
    {
        var found = new List<(BackupSnapshot, byte[])>();
        if (!Directory.Exists(_dir)) return found;
        foreach (var metaFile in SafeEnumerate("*.json"))
        {
            var id = Path.GetFileNameWithoutExtension(metaFile);
            if (id == _sessionId) continue;
            if (!IsAbandoned(id)) continue;
            try
            {
                var meta = JsonSerializer.Deserialize<BackupSnapshot>(File.ReadAllText(metaFile));
                if (meta is null || !meta.Encrypted) continue;
                var content = EncryptedContentPath(id);
                if (!File.Exists(content)) continue;   // FindOrphans purges these
                meta.SessionId = id;
                found.Add((meta, File.ReadAllBytes(content)));
            }
            catch { /* unreadable snapshot: leave it alone */ }
        }
        return found.OrderBy(f => f.Item1.SavedUtc).ToList();
    }

    /// <summary>One specific encrypted orphan, for a --recover child window.</summary>
    public (BackupSnapshot Meta, byte[] Container)? FindEncryptedOrphan(string sessionId) =>
        FindEncryptedOrphans().FirstOrDefault(o => o.Meta.SessionId == sessionId) is { Meta: not null } hit
            ? hit : null;

    /// <summary>
    /// Take over an encrypted orphan as our own session. The caller has already
    /// opened the container with the user's password; we re-home the SEALED bytes
    /// (never the plaintext) under our id. Copy before delete, like Adopt.
    /// </summary>
    public bool AdoptEncrypted(BackupSnapshot orphan, byte[] container)
    {
        var previous = _attempts;
        _attempts = orphan.RecoveryAttempts;
        if (!SaveEncrypted(container, orphan.Path, orphan.DisplayName)) { _attempts = previous; return false; }
        Purge(orphan.SessionId);
        return true;
    }

    /// <summary>One specific orphan, for a window launched to recover it by name.</summary>
    public (BackupSnapshot Meta, string Markdown)? FindOrphan(string sessionId) =>
        FindOrphans().FirstOrDefault(o => o.Meta.SessionId == sessionId) is { Meta: not null } hit
            ? hit : null;

    /// <summary>Claim this session. Returns false if the lock can't be taken, in
    /// which case we simply don't back up rather than fighting for it.</summary>
    public bool Start()
    {
        try
        {
            Directory.CreateDirectory(_dir);
            _lock = new FileStream(LockPath(_sessionId), FileMode.Create, FileAccess.Write,
                                   FileShare.None, 1, FileOptions.DeleteOnClose);
            return true;
        }
        catch { _lock = null; return false; }
    }

    /// <summary>
    /// Record the current unsaved content. Content first, metadata second: metadata
    /// pointing at content that isn't there yet is the one ordering that loses data,
    /// because the recovery scan trusts the metadata.
    /// </summary>
    public bool Save(string markdown, string? path, string? displayName)
    {
        if (_lock is null) return false;
        try
        {
            Directory.CreateDirectory(_dir);
            WriteAtomic(ContentPath(_sessionId), markdown);
            WriteAtomic(MetaPath(_sessionId), JsonSerializer.Serialize(new BackupSnapshot
            {
                SessionId = _sessionId,
                Path = path,
                DisplayName = displayName,
                SavedUtc = DateTime.UtcNow,
                RecoveryAttempts = _attempts,
            }));
            // The document may have been converted back from encrypted: with the
            // plaintext snapshot fully in place (content, then metadata saying
            // plaintext), the old .mdenc is stale. Deleted LAST - a crash before
            // this line leaves an extra encrypted file, never a missing snapshot.
            TryDelete(EncryptedContentPath(_sessionId));
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Record the current unsaved content of an ENCRYPTED document. The caller
    /// hands us the sealed .mdenc container (it owns the session key); this
    /// store never sees the plaintext. Same content-first ordering as Save,
    /// plus the leakage-critical step: the previous PLAINTEXT snapshot - from
    /// before the document was encrypted - is deleted only after the encrypted
    /// snapshot and its metadata are both fully in place. A crash anywhere in
    /// between leaves either the old plaintext snapshot or both; run N+1's
    /// next tick completes the swap. What it never leaves is no snapshot.
    /// </summary>
    public bool SaveEncrypted(byte[] container, string? path, string? displayName)
    {
        if (_lock is null) return false;
        try
        {
            Directory.CreateDirectory(_dir);
            var target = EncryptedContentPath(_sessionId);
            WriteAtomicDurable(target, container);
            TestHookAfterEncryptedWrite?.Invoke(target);
            // Read back and compare before ANYTHING downstream trusts this write.
            // The plaintext delete below is the step that makes this snapshot the
            // only copy of an unsaved document - it must never run on the strength
            // of bytes the disk hasn't proven it kept. (The caller already proved
            // the container DECRYPTS - it built it; what's unproven is the disk.)
            if (!container.AsSpan().SequenceEqual(File.ReadAllBytes(target)))
                return false;
            WriteAtomic(MetaPath(_sessionId), JsonSerializer.Serialize(new BackupSnapshot
            {
                SessionId = _sessionId,
                Path = path,
                DisplayName = displayName,
                SavedUtc = DateTime.UtcNow,
                RecoveryAttempts = _attempts,
                Encrypted = true,
            }));
            TryDelete(ContentPath(_sessionId));
            return true;
        }
        catch { return false; }
    }

    /// <summary>Drop this session's snapshot — the work is saved, or the user chose
    /// to discard it. Metadata first: without it the content is already invisible to
    /// recovery, so a crash between the two deletions leaks a file, not a document.</summary>
    public void Discard()
    {
        TryDelete(MetaPath(_sessionId));
        TryDelete(ContentPath(_sessionId));
        TryDelete(EncryptedContentPath(_sessionId));
        // The count belongs to the snapshot, not to this window. Every caller of
        // Discard means "that snapshot no longer exists", so anything written next is
        // new work and must start from zero — otherwise a window that recovered a
        // crash-looping document three times would stamp that score onto whatever the
        // user typed afterwards, and the app would refuse to hand it back at all.
        _attempts = 0;
    }

    /// <summary>
    /// Snapshots left behind by sessions that are no longer running. Ordered oldest
    /// first so recovery is deterministic.
    /// </summary>
    public IReadOnlyList<(BackupSnapshot Meta, string Markdown)> FindOrphans()
    {
        var found = new List<(BackupSnapshot, string)>();
        if (!Directory.Exists(_dir)) return found;

        foreach (var metaFile in SafeEnumerate("*.json"))
        {
            var id = Path.GetFileNameWithoutExtension(metaFile);
            if (id == _sessionId) continue;                 // our own
            if (!IsAbandoned(id)) continue;                 // still running
            try
            {
                var meta = JsonSerializer.Deserialize<BackupSnapshot>(File.ReadAllText(metaFile));
                if (meta is null) continue;
                if (meta.Encrypted)
                {
                    // An encrypted snapshot needs its password to be worth
                    // anything, and the prompt belongs to the recovery UI stage
                    // that doesn't exist yet. Held back, NOT purged: purging
                    // here would silently destroy a crashed encrypted
                    // document's only remaining copy. The existence check must
                    // look at the .mdenc, or the "metadata without content"
                    // purge below would eat every encrypted snapshot on sight.
                    if (!File.Exists(EncryptedContentPath(id))) { Purge(id); continue; }
                    // A plaintext file beside an Encrypted-flagged session is a
                    // crash mid-swap - but in WHICH direction decides everything.
                    // Encrypt-side (SaveEncrypted died before its .md delete):
                    // the .mdenc is newer, the .md is the stale pre-encryption
                    // leak - complete the swap. Convert-back-side (Save wrote a
                    // fresh .md and died before flipping the metadata): the .md
                    // is NEWER and holds edits the .mdenc doesn't - deleting it
                    // there would destroy the freshest copy on the word of
                    // metadata the crash outran. The file times discriminate;
                    // when they don't clearly say the .mdenc is newer, keep
                    // both and let the recovery UI stage adjudicate - a held
                    // extra file beats a guessed deletion.
                    try
                    {
                        var md = ContentPath(id);
                        if (File.Exists(md) &&
                            File.GetLastWriteTimeUtc(EncryptedContentPath(id)) > File.GetLastWriteTimeUtc(md))
                            TryDelete(md);
                    }
                    catch { /* timestamps unreadable: keep both */ }
                    continue;
                }
                var content = ContentPath(id);
                if (!File.Exists(content)) { Purge(id); continue; }   // metadata without content
                meta.SessionId = id;                        // trust the filename, not the field
                found.Add((meta, File.ReadAllText(content)));
            }
            catch { /* unreadable snapshot: leave it alone rather than delete it */ }
        }
        SweepStaleLocks();
        return found.OrderBy(f => f.Item1.SavedUtc).ToList();
    }

    /// <summary>
    /// Delete lock files with nothing to protect. A window that never went dirty has
    /// no snapshot, so a power cut (which denies DeleteOnClose its chance to run)
    /// leaves a lock nothing will ever clean up. Harmless individually, unbounded
    /// over time, and every one of them gets probed on each launch.
    /// </summary>
    private void SweepStaleLocks()
    {
        foreach (var lockFile in SafeEnumerate("*.lock"))
        {
            var id = Path.GetFileNameWithoutExtension(lockFile);
            if (id == _sessionId || id == "recovery") continue;
            if (File.Exists(MetaPath(id)) || File.Exists(ContentPath(id))
                || File.Exists(EncryptedContentPath(id))) continue;  // has content
            if (IsAbandoned(id)) TryDelete(lockFile);
        }
    }

    /// <summary>
    /// Take over an orphan's content as our own session, then remove the original.
    /// Copy before delete, always: the reverse loses the document if anything fails
    /// in between.
    /// </summary>
    public bool Adopt(BackupSnapshot orphan, string markdown)
    {
        // Inherit the count BEFORE saving, so the record of how many times this
        // document has been handed to a window survives the change of ownership.
        var previous = _attempts;
        _attempts = orphan.RecoveryAttempts;
        if (!Save(markdown, orphan.Path, orphan.DisplayName)) { _attempts = previous; return false; }
        Purge(orphan.SessionId);
        return true;
    }

    /// <summary>Note that we tried to recover this one, without adopting it — so a
    /// document that kills the app on load doesn't do it forever.</summary>
    public void RecordAttempt(BackupSnapshot orphan)
    {
        try
        {
            orphan.RecoveryAttempts++;
            WriteAtomic(MetaPath(orphan.SessionId), JsonSerializer.Serialize(orphan));
        }
        catch { /* best effort */ }
    }

    /// <summary>Remember that we've told the user we're giving up on this one.</summary>
    public void MarkGiveUpReported(BackupSnapshot orphan)
    {
        try
        {
            orphan.GiveUpReported = true;
            WriteAtomic(MetaPath(orphan.SessionId), JsonSerializer.Serialize(orphan));
        }
        catch { /* it'll be reported once more next launch; not worth failing over */ }
    }

    /// <summary>Remove another session's files entirely.</summary>
    public void Purge(string sessionId)
    {
        TryDelete(MetaPath(sessionId));
        TryDelete(ContentPath(sessionId));
        TryDelete(EncryptedContentPath(sessionId));
        TryDelete(LockPath(sessionId));
    }

    /// <summary>A 32-character hex GUID, which is the only shape we ever write.</summary>
    public static bool IsSessionId(string id) =>
        id.Length == 32 && id.All(Uri.IsHexDigit);

    /// <summary>True when nobody holds the session's lock.</summary>
    private bool IsAbandoned(string id)
    {
        var lockFile = LockPath(id);
        // No lock file at all: the owner exited and DeleteOnClose removed it, or it
        // never existed. Either way there's nothing alive to protect.
        if (!File.Exists(lockFile)) return true;
        try
        {
            // Read, not ReadWrite: opening for read is enough to prove nobody holds
            // it exclusively, and asking for write turns a read-only attribute — which
            // a backup or AV tool can set on a lock file left behind by a power cut —
            // into "still running", hiding the user's work from recovery permanently.
            using var _ = new FileStream(lockFile, FileMode.Open, FileAccess.Read, FileShare.None);
            return true;    // opened exclusively, so no live owner
        }
        catch (IOException) { return false; }              // still held
        catch (UnauthorizedAccessException) { return false; }
    }

    private IEnumerable<string> SafeEnumerate(string pattern)
    {
        try { return Directory.EnumerateFiles(_dir, pattern).ToList(); }
        catch { return Enumerable.Empty<string>(); }
    }

    private static void WriteAtomic(string target, string text)
    {
        var tmp = $"{target}.{Environment.ProcessId}.tmp";
        try
        {
            File.WriteAllText(tmp, text);
            File.Move(tmp, target, overwrite: true);
        }
        finally { TryDelete(tmp); }
    }

    /// <summary>Like WriteAtomic, plus a flush to physical disk before the
    /// rename - for the encrypted snapshot, whose plaintext sibling is deleted
    /// on the strength of this write having really happened.</summary>
    private static void WriteAtomicDurable(string target, byte[] bytes)
    {
        var tmp = $"{target}.{Environment.ProcessId}.tmp";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, target, overwrite: true);
        }
        finally { TryDelete(tmp); }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* leave it */ }
    }

    public void Dispose()
    {
        // FileOptions.DeleteOnClose removes the lock file as the handle closes.
        try { _lock?.Dispose(); } catch { }
        _lock = null;
    }
}
