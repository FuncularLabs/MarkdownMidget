using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace MarkdownMidget.Secure;

/// <summary>
/// The .mdenc container: AES-256-GCM over the markdown, key derived from the
/// user's password. Pure bytes-in/bytes-out — no file I/O, no UI — so every
/// property is unit-testable, the way CssValidator and CustomDicImport are.
///
/// Layout (little-endian):
///   offset  size  field
///   0       6     magic         "MDMSEC"
///   6       1     format_ver    0x01
///   7       1     kdf_id        1 = PBKDF2-SHA256, 2 = Argon2id
///   8       4     kdf_param_a   PBKDF2: iterations; Argon2: memory KiB
///   12      4     kdf_param_b   PBKDF2: 0; Argon2: (timeCost &lt;&lt; 16) | parallelism
///   16      16    salt          random, fresh EVERY encryption
///   32      12    nonce         random, fresh EVERY encryption (GCM IV)
///   44      N     ciphertext
///   44+N    16    tag           GCM authentication tag
///
/// The header (bytes [0,44)) is the GCM Associated Authenticated Data, so the
/// version, KDF choice and its parameters are bound into the tag: an attacker
/// cannot downgrade the work factor, swap the salt, or roll the version back
/// without decryption failing. Tamper fails closed.
///
/// Fresh salt AND nonce per encryption re-derives the key each save, so GCM's
/// catastrophic nonce-reuse-under-one-key condition cannot arise even in
/// principle; the cost is one KDF run per save, which is the point of a KDF.
/// </summary>
internal static class SecureMarkdownFormat
{
    public const string Extension = ".mdenc";

    private static readonly byte[] Magic = "MDMSEC"u8.ToArray();
    private const byte FormatVersion = 1;
    private const byte KdfPbkdf2Sha256 = 1;
    private const byte KdfArgon2id = 2;
    private const int HeaderLength = 44;
    private const int SaltLength = 16;
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private const int KeyLength = 32;

    // Ceilings on ATTACKER-SUPPLIED work factors, enforced on decrypt. Without
    // them a crafted header could demand gigabytes of Argon2 memory or hours of
    // iteration before the tag check gets a chance to reject the file — the
    // header is authenticated, but authentication is only checked AFTER the KDF
    // runs, because the key is what checks it.
    private const uint MaxArgonMemoryKib = 1024 * 1024;  // 1 GiB
    private const uint MaxArgonTimeCost = 64;
    private const uint MaxArgonParallelism = 64;
    private const uint MaxPbkdf2Iterations = 10_000_000;

    /// <summary>
    /// KDF work factors. Defaults follow RFC 9106's second recommended option
    /// (64 MiB, t=3, p=4) — memory-hard enough to blunt GPU cracking while
    /// staying sub-second on ordinary hardware. Tests use a weaker profile so
    /// the suite stays fast; the profile changes cost, never logic.
    /// </summary>
    internal readonly record struct KdfProfile(byte KdfId, uint ParamA, uint ParamB)
    {
        public static KdfProfile Default { get; } = Argon2id(memoryKib: 65536, timeCost: 3, parallelism: 4);

        public static KdfProfile Argon2id(uint memoryKib, uint timeCost, uint parallelism) =>
            new(KdfArgon2id, memoryKib, (timeCost << 16) | (parallelism & 0xFFFF));

        public static KdfProfile Pbkdf2(uint iterations) => new(KdfPbkdf2Sha256, iterations, 0);

        /// <summary>Weak-but-structurally-identical parameters for unit tests.</summary>
        internal static KdfProfile FastForTests { get; } = Argon2id(memoryKib: 8192, timeCost: 1, parallelism: 1);
    }

    /// <summary>Cheap sniff for "is this file even claiming to be one of ours".</summary>
    public static bool LooksLikeContainer(ReadOnlySpan<byte> head) =>
        head.Length >= Magic.Length && head[..Magic.Length].SequenceEqual(Magic);

    public static byte[] Encrypt(string markdown, string password) =>
        Encrypt(markdown, password, KdfProfile.Default);

    internal static byte[] Encrypt(string markdown, string password, KdfProfile profile)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        ArgumentNullException.ThrowIfNull(password);

        var plaintext = Encoding.UTF8.GetBytes(markdown);
        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);

        var header = new byte[HeaderLength];
        Magic.CopyTo(header, 0);
        header[6] = FormatVersion;
        header[7] = profile.KdfId;
        BitConverter.GetBytes(profile.ParamA).CopyTo(header, 8);
        BitConverter.GetBytes(profile.ParamB).CopyTo(header, 12);
        salt.CopyTo(header, 16);
        nonce.CopyTo(header, 32);

        var key = DeriveKey(password, salt, profile.KdfId, profile.ParamA, profile.ParamB);
        try
        {
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[TagLength];
            using (var gcm = new AesGcm(key, TagLength))
                gcm.Encrypt(nonce, plaintext, ciphertext, tag, header);

            var container = new byte[HeaderLength + ciphertext.Length + TagLength];
            header.CopyTo(container, 0);
            ciphertext.CopyTo(container, HeaderLength);
            tag.CopyTo(container, HeaderLength + ciphertext.Length);
            return container;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>
    /// Fail-closed decrypt. Wrong password and a tampered/damaged file are
    /// cryptographically indistinguishable and are reported as one condition —
    /// deliberately, so the error never leaks which it was.
    /// </summary>
    public static string Decrypt(byte[] container, string password)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(password);

        if (!LooksLikeContainer(container))
            throw new SecureMarkdownException(SecureMarkdownError.NotAContainer,
                "This is not a Secure Markdown file.");
        if (container.Length < HeaderLength + TagLength)
            throw new SecureMarkdownException(SecureMarkdownError.NotAContainer,
                "This is not a complete Secure Markdown file.");

        var version = container[6];
        if (version != FormatVersion)
            throw new SecureMarkdownException(SecureMarkdownError.Unsupported,
                $"This file uses Secure Markdown format version {version}, which this version of the app doesn't support. Update Markdown Midget and try again.");

        var kdfId = container[7];
        var paramA = BitConverter.ToUInt32(container, 8);
        var paramB = BitConverter.ToUInt32(container, 12);
        ValidateKdf(kdfId, paramA, paramB);

        var salt = container.AsSpan(16, SaltLength).ToArray();
        var nonce = container.AsSpan(32, NonceLength).ToArray();
        var header = container.AsSpan(0, HeaderLength).ToArray();
        var ciphertext = container.AsSpan(HeaderLength, container.Length - HeaderLength - TagLength).ToArray();
        var tag = container.AsSpan(container.Length - TagLength, TagLength).ToArray();

        var key = DeriveKey(password, salt, kdfId, paramA, paramB);
        var plaintext = new byte[ciphertext.Length];
        try
        {
            using var gcm = new AesGcm(key, TagLength);
            gcm.Decrypt(nonce, ciphertext, tag, plaintext, header);
            return Encoding.UTF8.GetString(plaintext);
        }
        catch (AuthenticationTagMismatchException)
        {
            throw new SecureMarkdownException(SecureMarkdownError.WrongPasswordOrCorrupt,
                "Incorrect password, or the file has been damaged.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static void ValidateKdf(byte kdfId, uint paramA, uint paramB)
    {
        switch (kdfId)
        {
            case KdfArgon2id:
                var timeCost = paramB >> 16;
                var parallelism = paramB & 0xFFFF;
                if (paramA is 0 or > MaxArgonMemoryKib || timeCost is 0 or > MaxArgonTimeCost
                    || parallelism is 0 or > MaxArgonParallelism)
                    throw new SecureMarkdownException(SecureMarkdownError.Unsupported,
                        "This file asks for key-derivation settings outside the supported range.");
                break;
            case KdfPbkdf2Sha256:
                if (paramA is 0 or > MaxPbkdf2Iterations)
                    throw new SecureMarkdownException(SecureMarkdownError.Unsupported,
                        "This file asks for key-derivation settings outside the supported range.");
                break;
            default:
                throw new SecureMarkdownException(SecureMarkdownError.Unsupported,
                    "This file uses a key-derivation method this version of the app doesn't support. Update Markdown Midget and try again.");
        }
    }

    private static byte[] DeriveKey(string password, byte[] salt, byte kdfId, uint paramA, uint paramB)
    {
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        try
        {
            if (kdfId == KdfArgon2id)
            {
                using var argon = new Argon2id(passwordBytes)
                {
                    Salt = salt,
                    MemorySize = checked((int)paramA),
                    Iterations = checked((int)(paramB >> 16)),
                    DegreeOfParallelism = checked((int)(paramB & 0xFFFF)),
                };
                return argon.GetBytes(KeyLength);
            }
            return Rfc2898DeriveBytes.Pbkdf2(
                passwordBytes, salt, checked((int)paramA), HashAlgorithmName.SHA256, KeyLength);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }
}

internal enum SecureMarkdownError
{
    /// <summary>Not our format at all (wrong magic, or truncated below any valid size).</summary>
    NotAContainer,
    /// <summary>Recognisably ours, but a version/KDF/parameters this build can't process.</summary>
    Unsupported,
    /// <summary>The tag check failed — wrong password and tampering are deliberately one condition.</summary>
    WrongPasswordOrCorrupt,
}

internal sealed class SecureMarkdownException : Exception
{
    public SecureMarkdownError Error { get; }
    public SecureMarkdownException(SecureMarkdownError error, string message) : base(message)
        => Error = error;
}
