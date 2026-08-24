using System.Security.Cryptography;

namespace Campus.Vault;

/// <summary>
/// Content-addressed storage for file bytes. An object is keyed by the SHA-256 of its plaintext,
/// so importing the same textbook ten times stores it once and every reference points at the
/// same bytes.
/// </summary>
public sealed class VaultObjectStore(VaultPaths paths, VaultKeyRing keys)
{
    private readonly VaultPaths _paths = paths;
    private readonly VaultKeyRing _keys = keys;

    /// <summary>Result of an import: the address of the bytes and whether they were already present.</summary>
    public readonly record struct PutResult(string ContentHash, long SizeBytes, bool AlreadyExisted);

    public bool Exists(string contentHash)
        => File.Exists(_paths.ObjectPath(_keys.BlindName(contentHash)));

    /// <summary>
    /// Stores a file. The source is hashed first so the address is known before anything is
    /// written, which is what makes de-duplication free.
    /// </summary>
    public async Task<PutResult> PutFileAsync(string sourcePath, CancellationToken ct = default)
    {
        string hash;
        long length;
        await using (var probe = File.OpenRead(sourcePath))
        {
            length = probe.Length;
            hash = await VaultCrypto.Sha256HexAsync(probe, ct).ConfigureAwait(false);
        }

        if (Exists(hash)) return new PutResult(hash, length, AlreadyExisted: true);

        await using var source = File.OpenRead(sourcePath);
        await WriteObjectAsync(hash, source, length, ct).ConfigureAwait(false);
        return new PutResult(hash, length, AlreadyExisted: false);
    }

    /// <summary>Stores an in-memory payload — a note body, a generated PDF, a thumbnail.</summary>
    public async Task<PutResult> PutBytesAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        var hash = VaultCrypto.Sha256Hex(data.Span);
        if (Exists(hash)) return new PutResult(hash, data.Length, AlreadyExisted: true);

        using var source = new MemoryStream(data.ToArray(), writable: false);
        await WriteObjectAsync(hash, source, data.Length, ct).ConfigureAwait(false);
        return new PutResult(hash, data.Length, AlreadyExisted: false);
    }

    private async Task WriteObjectAsync(string hash, Stream source, long length, CancellationToken ct)
    {
        var target = _paths.ObjectPath(_keys.BlindName(hash));
        VaultPaths.EnsureParent(target);

        // Write to a temporary sibling and move into place, so a crash never leaves a half object
        // that would later fail to authenticate.
        var temp = target + ".part";
        using var objectKey = _keys.DeriveContentKey(hash);
        try
        {
            await using (var destination = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await ChunkedVaultFile.WriteAsync(source, destination, objectKey, hash, length, ct: ct)
                    .ConfigureAwait(false);
                await destination.FlushAsync(ct).ConfigureAwait(false);
            }
            File.Move(temp, target, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>Opens a decrypted, seekable view of an object.</summary>
    public VaultReadStream OpenRead(string contentHash)
    {
        var path = _paths.ObjectPath(_keys.BlindName(contentHash));
        if (!File.Exists(path))
            throw new FileNotFoundException("This object is not in the vault.", contentHash);

        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            return new VaultReadStream(file, _keys.DeriveContentKey(contentHash), contentHash);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    public async Task<byte[]> ReadAllBytesAsync(string contentHash, CancellationToken ct = default)
    {
        await using var stream = OpenRead(contentHash);
        var buffer = new byte[stream.Length];
        await stream.ReadExactlyAsync(buffer, ct).ConfigureAwait(false);
        return buffer;
    }

    /// <summary>Writes an object back out in the clear. This is the only sanctioned way bytes leave the vault.</summary>
    public async Task ExportAsync(string contentHash, string destinationPath, CancellationToken ct = default)
    {
        VaultPaths.EnsureParent(destinationPath);
        await using var source = OpenRead(contentHash);
        await using var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await source.CopyToAsync(destination, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes the bytes for an address. Callers must confirm no other object references the
    /// same hash first — de-duplication means one file can back many objects.
    /// </summary>
    public bool Delete(string contentHash)
    {
        var path = _paths.ObjectPath(_keys.BlindName(contentHash));
        return TryDelete(path);
    }

    /// <summary>Re-reads and re-authenticates an object, reporting corruption without throwing.</summary>
    public async Task<bool> VerifyAsync(string contentHash, CancellationToken ct = default)
    {
        try
        {
            await using var stream = OpenRead(contentHash);
            var actual = await VaultCrypto.Sha256HexAsync(stream, ct).ConfigureAwait(false);
            return string.Equals(actual, contentHash, StringComparison.OrdinalIgnoreCase);
        }
        catch (CryptographicException) { return false; }
        catch (FileNotFoundException) { return false; }
        catch (IOException) { return false; }
    }

    /// <summary>Total bytes the vault occupies on disk.</summary>
    public long MeasureOnDisk()
    {
        if (!Directory.Exists(_paths.Objects)) return 0;
        return new DirectoryInfo(_paths.Objects)
            .EnumerateFiles("*", SearchOption.AllDirectories)
            .Sum(f => f.Length);
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
