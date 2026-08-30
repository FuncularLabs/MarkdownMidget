using System;
using System.Linq;
using System.Text;
using MarkdownMidget.Secure;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>
/// The .mdenc container's contract, per docs/plans/secure-markdown.md §9.
/// Most tests run the fast KDF profile — weaker work factors, identical
/// structure — so the suite stays quick; two tests run the real default
/// profile to prove the shipping path end to end.
/// </summary>
public class SecureMarkdownFormatTests
{
    private static readonly SecureMarkdownFormat.KdfProfile Fast =
        SecureMarkdownFormat.KdfProfile.FastForTests;

    private static byte[] EncryptFast(string text, string password) =>
        SecureMarkdownFormat.Encrypt(text, password, Fast);

    // ---- round-trip & correctness ----

    [Theory]
    [InlineData("plain ascii text")]
    [InlineData("")]
    [InlineData("unicode: naïve café — ≤≥ 中文 русский")]
    [InlineData("emoji: 🔒🗝️👩‍👩‍👧‍👧 and a zero-width​joiner")]
    [InlineData("# Doc\n\nCRLF\r\nand LF\nand a trailing newline\n")]
    public void RoundTripsExactly(string text)
    {
        var container = EncryptFast(text, "correct horse battery staple");
        Assert.Equal(text, SecureMarkdownFormat.Decrypt(container, "correct horse battery staple"));
    }

    [Fact]
    public void RoundTripsAMegabyteDocument()
    {
        var text = string.Concat(Enumerable.Repeat("A paragraph of sensitive account numbers 1234-5678.\n", 20000));
        var container = EncryptFast(text, "pw");
        Assert.Equal(text, SecureMarkdownFormat.Decrypt(container, "pw"));
    }

    [Fact]
    public void TheDefaultProfileRoundTrips()
    {
        // The real Argon2id work factors (64 MiB, t=3, p=4) — the shipping path.
        var container = SecureMarkdownFormat.Encrypt("secret", "pw");
        Assert.Equal("secret", SecureMarkdownFormat.Decrypt(container, "pw"));
    }

    [Fact]
    public void ThePbkdf2ProfileRoundTrips()
    {
        // kdf_id 1 is a registered format citizen even though new files default
        // to Argon2id — a future build must be able to read either.
        var profile = SecureMarkdownFormat.KdfProfile.Pbkdf2(iterations: 1000);
        var container = SecureMarkdownFormat.Encrypt("secret", "pw", profile);
        Assert.Equal("secret", SecureMarkdownFormat.Decrypt(container, "pw"));
    }

    [Fact]
    public void TwoEncryptionsOfTheSameContentDiffer()
    {
        // Fresh salt AND nonce per save: identical input must never produce
        // identical output, or GCM nonce reuse becomes reachable in principle.
        var a = EncryptFast("same content", "pw");
        var b = EncryptFast("same content", "pw");
        Assert.NotEqual(a.AsSpan(16, 16).ToArray(), b.AsSpan(16, 16).ToArray());   // salt
        Assert.NotEqual(a.AsSpan(32, 12).ToArray(), b.AsSpan(32, 12).ToArray());   // nonce
        Assert.NotEqual(a.Skip(44).ToArray(), b.Skip(44).ToArray());               // ciphertext
    }

    [Fact]
    public void TheContainerIsRecognisableAndCarriesItsParameters()
    {
        var container = EncryptFast("x", "pw");
        Assert.True(SecureMarkdownFormat.LooksLikeContainer(container));
        Assert.Equal((byte)1, container[6]);   // format version
        Assert.Equal((byte)2, container[7]);   // Argon2id
        Assert.Equal(8192u, BitConverter.ToUInt32(container, 8));  // fast profile memory KiB
    }

    // ---- fail-closed ----

    [Fact]
    public void WrongPasswordFailsAsWrongPasswordOrCorrupt()
    {
        var container = EncryptFast("secret", "right");
        var ex = Assert.Throws<SecureMarkdownException>(() => SecureMarkdownFormat.Decrypt(container, "wrong"));
        Assert.Equal(SecureMarkdownError.WrongPasswordOrCorrupt, ex.Error);
    }

    [Theory]
    [InlineData(44)]    // first ciphertext byte
    [InlineData(-1)]    // last tag byte
    [InlineData(16)]    // first salt byte (header — AAD-bound)
    [InlineData(32)]    // first nonce byte (header — AAD-bound)
    public void AnyFlippedByteFailsIndistinguishablyFromAWrongPassword(int offset)
    {
        var container = EncryptFast("secret", "pw");
        var idx = offset < 0 ? container.Length + offset : offset;
        container[idx] ^= 0x01;
        var ex = Assert.Throws<SecureMarkdownException>(() => SecureMarkdownFormat.Decrypt(container, "pw"));
        Assert.Equal(SecureMarkdownError.WrongPasswordOrCorrupt, ex.Error);
    }

    [Fact]
    public void ADowngradedWorkFactorIsRejectedByTheTagNotSilentlyHonoured()
    {
        // The header is AAD: weakening kdf_param_a changes what the tag was
        // computed over, so decryption fails rather than proceeding with the
        // attacker's cheaper parameters.
        var container = EncryptFast("secret", "pw");
        container[8] ^= 0x01;
        var ex = Assert.Throws<SecureMarkdownException>(() => SecureMarkdownFormat.Decrypt(container, "pw"));
        Assert.Equal(SecureMarkdownError.WrongPasswordOrCorrupt, ex.Error);
    }

    [Fact]
    public void TruncationFailsClosed()
    {
        var container = EncryptFast("a longer secret so there is ciphertext to lose", "pw");
        var truncated = container.Take(container.Length - 5).ToArray();
        var ex = Assert.Throws<SecureMarkdownException>(() => SecureMarkdownFormat.Decrypt(truncated, "pw"));
        Assert.Equal(SecureMarkdownError.WrongPasswordOrCorrupt, ex.Error);
    }

    [Fact]
    public void APlaintextFileRenamedToMdencIsNotAContainer()
    {
        var bytes = Encoding.UTF8.GetBytes("# Just some markdown\n\nnot encrypted at all\n");
        Assert.False(SecureMarkdownFormat.LooksLikeContainer(bytes));
        var ex = Assert.Throws<SecureMarkdownException>(() => SecureMarkdownFormat.Decrypt(bytes, "pw"));
        Assert.Equal(SecureMarkdownError.NotAContainer, ex.Error);
    }

    [Fact]
    public void ATinyFragmentWithTheRightMagicIsStillNotAContainer()
    {
        var bytes = Encoding.UTF8.GetBytes("MDMSEC");
        var ex = Assert.Throws<SecureMarkdownException>(() => SecureMarkdownFormat.Decrypt(bytes, "pw"));
        Assert.Equal(SecureMarkdownError.NotAContainer, ex.Error);
    }

    [Fact]
    public void AFutureFormatVersionSaysUpdateRatherThanCorrupt()
    {
        var container = EncryptFast("secret", "pw");
        container[6] = 2;
        var ex = Assert.Throws<SecureMarkdownException>(() => SecureMarkdownFormat.Decrypt(container, "pw"));
        Assert.Equal(SecureMarkdownError.Unsupported, ex.Error);
        Assert.Contains("Update", ex.Message);
    }

    [Fact]
    public void AnUnknownKdfSaysUpdateRatherThanCorrupt()
    {
        var container = EncryptFast("secret", "pw");
        container[7] = 99;
        var ex = Assert.Throws<SecureMarkdownException>(() => SecureMarkdownFormat.Decrypt(container, "pw"));
        Assert.Equal(SecureMarkdownError.Unsupported, ex.Error);
    }

    [Fact]
    public void AbsurdKdfParametersAreRejectedBeforeAnyWorkIsDone()
    {
        // A crafted header demanding gigabytes of Argon2 memory would otherwise
        // be a denial-of-service that runs BEFORE the tag check can refuse it.
        var container = EncryptFast("secret", "pw");
        BitConverter.GetBytes(uint.MaxValue).CopyTo(container, 8);
        var ex = Assert.Throws<SecureMarkdownException>(() => SecureMarkdownFormat.Decrypt(container, "pw"));
        Assert.Equal(SecureMarkdownError.Unsupported, ex.Error);
    }

    [Fact]
    public void ZeroKdfParametersAreRejected()
    {
        var container = EncryptFast("secret", "pw");
        BitConverter.GetBytes(0u).CopyTo(container, 12);   // timeCost=0, parallelism=0
        var ex = Assert.Throws<SecureMarkdownException>(() => SecureMarkdownFormat.Decrypt(container, "pw"));
        Assert.Equal(SecureMarkdownError.Unsupported, ex.Error);
    }

    [Fact]
    public void EncryptRefusesAProfileDecryptWouldReject()
    {
        // Producer-side half of the fail-closed contract: a file we write is
        // always a file we can read back. 2 GiB of Argon2 memory is beyond the
        // decrypt-side cap, so encrypt must refuse it up front - not produce a
        // container that fails forever.
        var absurd = SecureMarkdownFormat.KdfProfile.Argon2id(memoryKib: 2 * 1024 * 1024, timeCost: 3, parallelism: 4);
        var ex = Assert.Throws<SecureMarkdownException>(() => SecureMarkdownFormat.Encrypt("secret", "pw", absurd));
        Assert.Equal(SecureMarkdownError.Unsupported, ex.Error);
    }

    [Fact]
    public void TheProfileFactoryRefusesValuesThatDoNotFitTheirField()
    {
        // timeCost and parallelism share one 32-bit word; a value that doesn't
        // fit must throw, never silently mask to something weaker.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SecureMarkdownFormat.KdfProfile.Argon2id(memoryKib: 8192, timeCost: 65537, parallelism: 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SecureMarkdownFormat.KdfProfile.Argon2id(memoryKib: 8192, timeCost: 1, parallelism: 65537));
    }

    // ---- the dependency itself ----

    [Fact]
    public void TheArgon2idImplementationMatchesTheRfc9106TestVector()
    {
        // RFC 9106 section 5.3: proves the Konscious package computes THE
        // Argon2id, interoperable with every other conforming implementation --
        // not merely something self-consistent. If this ever fails after a
        // package update, no existing .mdenc file would decrypt.
        using var argon = new Konscious.Security.Cryptography.Argon2id(
            Enumerable.Repeat((byte)0x01, 32).ToArray())
        {
            Salt = Enumerable.Repeat((byte)0x02, 16).ToArray(),
            KnownSecret = Enumerable.Repeat((byte)0x03, 8).ToArray(),
            AssociatedData = Enumerable.Repeat((byte)0x04, 12).ToArray(),
            MemorySize = 32,
            Iterations = 3,
            DegreeOfParallelism = 4,
        };
        var tag = argon.GetBytes(32);
        Assert.Equal(
            "0d640df58d78766c08c037a34a8b53c9d01ef0452d75b65eb52520e96b01e659",
            Convert.ToHexStringLower(tag));
    }

    // ---- leakage ----

    [Fact]
    public void TheContainerNeverContainsThePlaintextOrThePassword()
    {
        const string sentinel = "EXTREMELY-SENSITIVE-ACCOUNT-NUMBER-9912";
        const string password = "hunter2-but-longer";
        var container = EncryptFast($"# Doc\n\n{sentinel}\n", password);
        var haystack = Encoding.Latin1.GetString(container);
        Assert.DoesNotContain(sentinel, haystack);
        Assert.DoesNotContain(password, haystack);
    }
}
