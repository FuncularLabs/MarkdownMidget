using System;
using System.IO;
using System.Linq;
using System.Text;
using MarkdownMidget.Secure;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>
/// The transactional .mdenc writer (docs/plans/secure-markdown.md §6). The
/// invariant under test: at no instant is the only copy of the user's data a
/// partial or unverified file — and no plaintext ever touches the disk.
/// </summary>
public class SecureMarkdownFileTests : IDisposable
{
    private static readonly SecureMarkdownFormat.KdfProfile Fast =
        SecureMarkdownFormat.KdfProfile.FastForTests;

    private readonly string _dir = Directory.CreateTempSubdirectory("mdm-securefile-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private string PathFor(string name) => Path.Combine(_dir, name);

    [Fact]
    public void SaveThenLoadRoundTrips()
    {
        var p = PathFor("doc.mdenc");
        SecureMarkdownFile.Save(p, "# Secret\n\ncontent\n", "pw", Fast);
        Assert.Equal("# Secret\n\ncontent\n", SecureMarkdownFile.Load(p, "pw"));
        Assert.True(SecureMarkdownFormat.LooksLikeContainer(File.ReadAllBytes(p)));
    }

    [Fact]
    public void SaveReplacesAnExistingFileAtomically()
    {
        var p = PathFor("doc.mdenc");
        SecureMarkdownFile.Save(p, "version one", "pw", Fast);
        SecureMarkdownFile.Save(p, "version two", "pw", Fast);
        Assert.Equal("version two", SecureMarkdownFile.Load(p, "pw"));
        Assert.Single(Directory.GetFiles(_dir));   // no temp left behind
    }

    [Fact]
    public void ACorruptedTempFailsVerificationAndKeepsTheOriginal()
    {
        var p = PathFor("doc.mdenc");
        SecureMarkdownFile.Save(p, "the good version", "pw", Fast);
        var originalBytes = File.ReadAllBytes(p);

        var ex = Assert.Throws<SecureMarkdownException>(() =>
            SecureMarkdownFile.Save(p, "the doomed version", "pw", Fast, testHookAfterTempWrite: tmp =>
            {
                // Simulate a disk/AV mangling the bytes between write and verify.
                var bytes = File.ReadAllBytes(tmp);
                bytes[^1] ^= 0xFF;
                File.WriteAllBytes(tmp, bytes);
            }));

        Assert.Equal(SecureMarkdownError.WriteVerificationFailed, ex.Error);
        Assert.Contains("left untouched", ex.Message);
        Assert.Equal(originalBytes, File.ReadAllBytes(p));            // byte-identical original
        Assert.Equal("the good version", SecureMarkdownFile.Load(p, "pw"));
        Assert.Single(Directory.GetFiles(_dir));                      // temp cleaned up
    }

    [Fact]
    public void ACrashBeforeTheRenameLeavesTheOriginalIntact()
    {
        var p = PathFor("doc.mdenc");
        SecureMarkdownFile.Save(p, "the good version", "pw", Fast);

        // A hook that throws models dying after the temp write: the rename never
        // ran, so whatever happens afterwards the target is the old file.
        Assert.Throws<InvalidOperationException>(() =>
            SecureMarkdownFile.Save(p, "never lands", "pw", Fast,
                testHookAfterTempWrite: _ => throw new InvalidOperationException("simulated death")));

        Assert.Equal("the good version", SecureMarkdownFile.Load(p, "pw"));
    }

    [Fact]
    public void NoPlaintextEverTouchesTheDisk()
    {
        const string sentinel = "EXTREMELY-SENSITIVE-9912";
        var p = PathFor("doc.mdenc");
        var seen = new System.Collections.Generic.List<string>();
        SecureMarkdownFile.Save(p, $"# Doc\n\n{sentinel}\n", "pw", Fast, testHookAfterTempWrite: tmp =>
        {
            // Capture the temp's bytes at the moment they exist on disk.
            seen.Add(Encoding.Latin1.GetString(File.ReadAllBytes(tmp)));
        });
        seen.AddRange(Directory.GetFiles(_dir).Select(f => Encoding.Latin1.GetString(File.ReadAllBytes(f))));
        Assert.All(seen, content => Assert.DoesNotContain(sentinel, content));
    }

    [Fact]
    public void LoadWithTheWrongPasswordFailsClosed()
    {
        var p = PathFor("doc.mdenc");
        SecureMarkdownFile.Save(p, "secret", "right", Fast);
        var ex = Assert.Throws<SecureMarkdownException>(() => SecureMarkdownFile.Load(p, "wrong"));
        Assert.Equal(SecureMarkdownError.WrongPasswordOrCorrupt, ex.Error);
    }

    // ---- the session-key path (what crash-backups use) ----

    [Fact]
    public void SaveWithKeyProducesAContainerThePasswordOpens()
    {
        // The whole point of the cached-key path: the container must be
        // indistinguishable from a password-derived one, so recovery can open
        // it with nothing but the password.
        var p = PathFor("doc.mdenc");
        var salt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        var key = SecureMarkdownFormat.DeriveSessionKey("pw", salt, Fast);
        SecureMarkdownFile.SaveWithKey(p, "snapshot content", key, salt, Fast);
        Assert.Equal("snapshot content", SecureMarkdownFile.Load(p, "pw"));
    }

    [Fact]
    public void AMismatchedSaltProducesAFileThatNeverDecrypts()
    {
        // The documented contract: EncryptWithKey trusts the caller to pass the
        // salt the key came from. Passing a different salt yields a container
        // whose password-derived key won't match - pinned so the trap stays
        // visible rather than theoretical.
        var salt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        var otherSalt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        var key = SecureMarkdownFormat.DeriveSessionKey("pw", salt, Fast);
        var container = SecureMarkdownFormat.EncryptWithKey("content", key, otherSalt, Fast);
        var ex = Assert.Throws<SecureMarkdownException>(() => SecureMarkdownFormat.Decrypt(container, "pw"));
        Assert.Equal(SecureMarkdownError.WrongPasswordOrCorrupt, ex.Error);
    }

    [Fact]
    public void TwoKeySavesUseFreshNonces()
    {
        var salt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        var key = SecureMarkdownFormat.DeriveSessionKey("pw", salt, Fast);
        var a = SecureMarkdownFormat.EncryptWithKey("same", key, salt, Fast);
        var b = SecureMarkdownFormat.EncryptWithKey("same", key, salt, Fast);
        // Same key by design - so the nonce MUST differ, or GCM breaks.
        Assert.NotEqual(a.AsSpan(32, 12).ToArray(), b.AsSpan(32, 12).ToArray());
        Assert.Equal(a.AsSpan(16, 16).ToArray(), b.AsSpan(16, 16).ToArray());   // same salt, by contract
    }

    [Fact]
    public void DecryptWithKeyMatchesDecryptWithPassword()
    {
        var salt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        var key = SecureMarkdownFormat.DeriveSessionKey("pw", salt, Fast);
        var container = SecureMarkdownFormat.EncryptWithKey("content", key, salt, Fast);
        Assert.Equal("content", SecureMarkdownFormat.DecryptWithKey(container, key));
        Assert.Equal("content", SecureMarkdownFormat.Decrypt(container, "pw"));
    }
}
