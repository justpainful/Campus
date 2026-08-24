using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Campus.Vault;

/// <summary>
/// The on-disk container format for a vault object.
///
/// Header:  "CVLT" | version | chunkSizeLog2 | reserved(2) | plaintextLength(8 LE)
/// Chunks:  [nonce(12)][tag(16)][ciphertext]  repeated
///
/// Splitting into chunks keeps memory flat for a 2 GB video and lets a PDF viewer seek to page
/// 400 without decrypting the first 399. Each chunk authenticates its own index and the object's
/// content hash, so chunks cannot be reordered, duplicated or spliced between objects.
/// </summary>
public static class ChunkedVaultFile
{
    private static ReadOnlySpan<byte> Magic => "CVLT"u8;
    public const byte Version = 1;
    public const byte DefaultChunkSizeLog2 = 20; // 1 MiB
    public const int HeaderSize = 16;

    public readonly record struct FileHeader(byte Version, int ChunkSize, long PlaintextLength)
    {
        public int ChunkCount => ChunkSize == 0 ? 0 : (int)((PlaintextLength + ChunkSize - 1) / ChunkSize);
        public int EncryptedChunkSize => ChunkSize + VaultCrypto.NonceSize + VaultCrypto.TagSize;
    }

    private static byte[] BuildAad(string contentHash, int chunkIndex)
    {
        var hashBytes = Encoding.UTF8.GetBytes(contentHash);
        var aad = new byte[hashBytes.Length + 4];
        hashBytes.CopyTo(aad, 0);
        BinaryPrimitives.WriteInt32LittleEndian(aad.AsSpan(hashBytes.Length), chunkIndex);
        return aad;
    }

    public static async Task WriteAsync(
        Stream source,
        Stream destination,
        SecureBuffer objectKey,
        string contentHash,
        long plaintextLength,
        byte chunkSizeLog2 = DefaultChunkSizeLog2,
        CancellationToken ct = default)
    {
        var chunkSize = 1 << chunkSizeLog2;

        var header = new byte[HeaderSize];
        Magic.CopyTo(header);
        header[4] = Version;
        header[5] = chunkSizeLog2;
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(8), plaintextLength);
        await destination.WriteAsync(header, ct).ConfigureAwait(false);

        var plain = new byte[chunkSize];
        var cipher = new byte[chunkSize + VaultCrypto.NonceSize + VaultCrypto.TagSize];
        var index = 0;

        while (true)
        {
            var read = await source.ReadAtLeastAsync(plain, chunkSize, throwOnEndOfStream: false, ct)
                .ConfigureAwait(false);
            if (read == 0) break;

            var nonce = cipher.AsSpan(0, VaultCrypto.NonceSize);
            var tag = cipher.AsSpan(VaultCrypto.NonceSize, VaultCrypto.TagSize);
            var body = cipher.AsSpan(VaultCrypto.NonceSize + VaultCrypto.TagSize, read);

            RandomNumberGenerator.Fill(nonce);
            using (var aes = new AesGcm(objectKey.Span, VaultCrypto.TagSize))
                aes.Encrypt(nonce, plain.AsSpan(0, read), body, tag, BuildAad(contentHash, index));

            await destination.WriteAsync(
                cipher.AsMemory(0, VaultCrypto.NonceSize + VaultCrypto.TagSize + read), ct).ConfigureAwait(false);

            index++;
            if (read < chunkSize) break;
        }

        CryptographicOperations.ZeroMemory(plain);
    }

    public static FileHeader ReadHeader(Stream source)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        source.ReadExactly(header);
        if (!header[..4].SequenceEqual(Magic))
            throw new CryptographicException("Not a Campus vault object.");
        var version = header[4];
        if (version != Version)
            throw new CryptographicException($"Unsupported vault object version {version}.");
        var chunkSize = 1 << header[5];
        var length = BinaryPrimitives.ReadInt64LittleEndian(header[8..]);
        return new FileHeader(version, chunkSize, length);
    }

    /// <summary>Decrypts one chunk by index. The caller owns seeking; this does not assume order.</summary>
    public static int ReadChunk(
        Stream source,
        in FileHeader header,
        SecureBuffer objectKey,
        string contentHash,
        int chunkIndex,
        Span<byte> destination)
    {
        var isLast = chunkIndex == header.ChunkCount - 1;
        var plainLength = isLast
            ? (int)(header.PlaintextLength - (long)chunkIndex * header.ChunkSize)
            : header.ChunkSize;
        if (plainLength <= 0) return 0;

        source.Seek(HeaderSize + (long)chunkIndex * header.EncryptedChunkSize, SeekOrigin.Begin);

        var buffer = new byte[VaultCrypto.NonceSize + VaultCrypto.TagSize + plainLength];
        source.ReadExactly(buffer);

        var nonce = buffer.AsSpan(0, VaultCrypto.NonceSize);
        var tag = buffer.AsSpan(VaultCrypto.NonceSize, VaultCrypto.TagSize);
        var body = buffer.AsSpan(VaultCrypto.NonceSize + VaultCrypto.TagSize, plainLength);

        using var aes = new AesGcm(objectKey.Span, VaultCrypto.TagSize);
        aes.Decrypt(nonce, body, tag, destination[..plainLength], BuildAad(contentHash, chunkIndex));
        return plainLength;
    }
}
