using System.Security.Cryptography;
using System.Text;

namespace Campus.Vault;

/// <summary>
/// The cryptographic primitives the vault is built from. Everything is AES-256-GCM with a
/// per-object key derived from the master key, so no two objects share a key or a nonce space.
/// </summary>
public static class VaultCrypto
{
    public const int KeySize = 32;      // AES-256
    public const int NonceSize = 12;    // GCM standard nonce
    public const int TagSize = 16;      // GCM tag

    /// <summary>HKDF-SHA256 expansion of the master key into a purpose-specific subkey.</summary>
    public static SecureBuffer DeriveKey(SecureBuffer masterKey, string purpose, ReadOnlySpan<byte> context = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(purpose);
        var info = new byte[Encoding.UTF8.GetByteCount(purpose) + context.Length];
        var written = Encoding.UTF8.GetBytes(purpose, info);
        context.CopyTo(info.AsSpan(written));

        var derived = new SecureBuffer(KeySize);
        HKDF.Expand(HashAlgorithmName.SHA256, masterKey.Span, derived.WritableSpan, info);
        CryptographicOperations.ZeroMemory(info);
        return derived;
    }

    /// <summary>Derives a key encryption key from a passphrase or recovery key.</summary>
    public static SecureBuffer DeriveFromSecret(ReadOnlySpan<byte> secret, ReadOnlySpan<byte> salt, int iterations = 600_000)
    {
        var derived = new SecureBuffer(KeySize);
        Rfc2898DeriveBytes.Pbkdf2(secret, salt, derived.WritableSpan, iterations, HashAlgorithmName.SHA256);
        return derived;
    }

    /// <summary>
    /// Encrypts a payload. Layout is [nonce][tag][ciphertext] so a reader can stream without
    /// seeking backwards. <paramref name="associatedData"/> is authenticated but not encrypted.
    /// </summary>
    public static byte[] Encrypt(SecureBuffer key, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associatedData = default)
    {
        var output = new byte[NonceSize + TagSize + plaintext.Length];
        var nonce = output.AsSpan(0, NonceSize);
        var tag = output.AsSpan(NonceSize, TagSize);
        var ciphertext = output.AsSpan(NonceSize + TagSize);

        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key.Span, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
        return output;
    }

    /// <summary>Decrypts a payload produced by <see cref="Encrypt"/>. Throws if the tag does not verify.</summary>
    public static byte[] Decrypt(SecureBuffer key, ReadOnlySpan<byte> envelope, ReadOnlySpan<byte> associatedData = default)
    {
        if (envelope.Length < NonceSize + TagSize)
            throw new CryptographicException("Vault payload is truncated.");

        var nonce = envelope[..NonceSize];
        var tag = envelope.Slice(NonceSize, TagSize);
        var ciphertext = envelope[(NonceSize + TagSize)..];

        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(key.Span, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);
        return plaintext;
    }

    public static string EncryptToBase64(SecureBuffer key, string plaintext, string? associatedData = null)
    {
        var aad = associatedData is null ? default : Encoding.UTF8.GetBytes(associatedData);
        return Convert.ToBase64String(Encrypt(key, Encoding.UTF8.GetBytes(plaintext), aad));
    }

    public static string DecryptFromBase64(SecureBuffer key, string envelope, string? associatedData = null)
    {
        var aad = associatedData is null ? default : Encoding.UTF8.GetBytes(associatedData);
        return Encoding.UTF8.GetString(Decrypt(key, Convert.FromBase64String(envelope), aad));
    }

    /// <summary>
    /// Keyed name for an object on disk. The plaintext content hash never appears in the file
    /// system, so the vault leaks neither names nor a hash an attacker could look up.
    /// </summary>
    public static string BlindName(SecureBuffer nameKey, string contentHash)
    {
        var mac = HMACSHA256.HashData(nameKey.Span, Encoding.UTF8.GetBytes(contentHash));
        return Convert.ToHexStringLower(mac);
    }

    public static string Sha256Hex(ReadOnlySpan<byte> data)
        => Convert.ToHexStringLower(SHA256.HashData(data));

    public static async Task<string> Sha256HexAsync(Stream stream, CancellationToken ct = default)
    {
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }
}
