using System;
using System.IO;

namespace MarkdownMidget.Secure;

/// <summary>
/// Transactional file I/O for .mdenc documents, per docs/plans/secure-markdown.md §6.
///
/// The invariant every step serves: <b>at no instant is the only copy of the
/// user's data a partial or unverified file.</b> A crash at any point leaves
/// either the intact original or an intact, verified new file — never a
/// truncated one, and never a plaintext one (the temp is ciphertext).
///
/// Save's sequence:
///   1. encrypt in memory;
///   2. write a temp BESIDE the target (same volume, so the final rename is
///      atomic), fsync'd so a power cut after the rename can't leave it empty;
///   3. read the temp back and PROVE it decrypts to exactly what we meant to
///      save — Markdown Monster compares ciphertext strings here; comparing
///      the decrypted plaintext is strictly stronger, because it also catches
///      a bad encrypt, not just a bad disk;
///   4. atomically replace the target;
///   5. clean up the temp either way.
///
/// A failed verification throws and leaves the original untouched: never
/// delete the good file for a bad write. A crash between fsync and rename can
/// leak a stray .tmp (unique per process id); it is ciphertext, and the next
/// successful Save of the same document cleans its own — stray tmps from dead
/// processes are cosmetic, matching BackupStore's precedent.
/// </summary>
internal static class SecureMarkdownFile
{
    public static void Save(string path, string markdown, string password) =>
        Save(path, markdown, password, SecureMarkdownFormat.KdfProfile.Default);

    internal static void Save(string path, string markdown, string password,
        SecureMarkdownFormat.KdfProfile profile, Action<string>? testHookAfterTempWrite = null)
    {
        var container = SecureMarkdownFormat.Encrypt(markdown, password, profile);
        WriteVerifiedCore(path, container, markdown,
            readBack => SecureMarkdownFormat.Decrypt(readBack, password), testHookAfterTempWrite);
    }

    /// <summary>
    /// The session-key variant the crash-backup path uses: same transaction, the
    /// KDF paid once per session instead of once per snapshot. Verification
    /// decrypts with the same cached key.
    /// </summary>
    internal static void SaveWithKey(string path, string markdown, byte[] key, byte[] salt,
        SecureMarkdownFormat.KdfProfile profile, Action<string>? testHookAfterTempWrite = null)
    {
        var container = SecureMarkdownFormat.EncryptWithKey(markdown, key, salt, profile);
        WriteVerifiedCore(path, container, markdown,
            readBack => SecureMarkdownFormat.DecryptWithKey(readBack, key), testHookAfterTempWrite);
    }

    public static string Load(string path, string password) =>
        SecureMarkdownFormat.Decrypt(File.ReadAllBytes(path), password);

    private static void WriteVerifiedCore(string path, byte[] container, string expectedMarkdown,
        Func<byte[], string> decryptForVerify, Action<string>? testHookAfterTempWrite)
    {
        var tmp = $"{path}.{Environment.ProcessId}.tmp";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(container, 0, container.Length);
                fs.Flush(flushToDisk: true);
            }
            testHookAfterTempWrite?.Invoke(tmp);

            string roundTripped;
            try
            {
                roundTripped = decryptForVerify(File.ReadAllBytes(tmp));
            }
            catch (SecureMarkdownException)
            {
                throw Unverified(path);
            }
            if (!string.Equals(roundTripped, expectedMarkdown, StringComparison.Ordinal))
                throw Unverified(path);

            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* stray tmp is cosmetic */ }
        }
    }

    private static SecureMarkdownException Unverified(string path) => new(
        SecureMarkdownError.WriteVerificationFailed,
        $"The encrypted file could not be verified after writing, so it was not saved. " +
        $"The existing file at {Path.GetFileName(path)} was left untouched. " +
        "This usually means a disk or antivirus problem - try saving again, or Save As to another location.");
}
