using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Campus.Domain;
using Campus.Vault;

namespace Campus.Sync;

/// <summary>What a bundle says about itself, in the clear.</summary>
public sealed record BundleManifest
{
    public int FormatVersion { get; init; } = 1;
    public string FromDeviceId { get; init; } = string.Empty;
    public string FromDeviceName { get; init; } = string.Empty;
    public long FromSequence { get; init; }
    public long ToSequence { get; init; }
    public int EntryCount { get; init; }
    public int FileCount { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Salt for the transfer key, so the receiver can derive it from the pairing code.</summary>
    public string Salt { get; init; } = string.Empty;
}

/// <summary>What applying a bundle did.</summary>
public sealed record SyncResult(
    int Applied,
    int Ignored,
    int Conflicted,
    int FilesReceived,
    long ThroughSequence);

/// <summary>
/// The unit of sync: a zip holding a range of the change log and the file bytes those changes
/// refer to, encrypted under a key both devices derived from their pairing code.
///
/// One shape for every transport. Over a cable it is a file on a stick; over the local network it
/// is the same bytes on a socket; kept on a drive it is a backup you can carry. Nothing about the
/// format assumes a network exists, which is the point — Campus must work with the Wi-Fi off.
///
/// The manifest is plaintext so a bundle can say who it is from and how big it is before anyone
/// has typed a code. Everything else — the journal and every byte of every file — is inside the
/// encrypted payload.
/// </summary>
public static class SyncBundle
{
    private const string ManifestEntry = "manifest.json";
    private const string PayloadEntry = "payload.bin";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    // ------------------------------------------------------------------------ writing

    /// <summary>
    /// Writes everything after <paramref name="afterSequence"/> that did not originate on the
    /// receiving device.
    /// </summary>
    public static async Task<BundleManifest> WriteAsync(
        string destinationPath,
        IReadOnlyList<JournalEntry> entries,
        VaultObjectStore store,
        SecureBuffer transferKey,
        byte[] salt,
        string fromDeviceId,
        string fromDeviceName,
        long afterSequence,
        CancellationToken ct = default)
    {
        // Files are collected first so the manifest can state honestly how much is coming.
        var hashes = entries
            .Select(e => e.ContentHash)
            .Where(h => !string.IsNullOrEmpty(h))
            .Distinct(StringComparer.Ordinal)
            .Where(store.Exists!)
            .ToList()!;

        var manifest = new BundleManifest
        {
            FromDeviceId = fromDeviceId,
            FromDeviceName = fromDeviceName,
            FromSequence = afterSequence,
            ToSequence = entries.Count > 0 ? entries[^1].Sequence : afterSequence,
            EntryCount = entries.Count,
            FileCount = hashes.Count,
            Salt = Convert.ToBase64String(salt),
        };

        // The payload is built in memory as a zip of its own, then encrypted whole. Encrypting
        // the container rather than each entry means the receiver cannot even see how many files
        // there are, let alone their names.
        using var payload = new MemoryStream();

        using (var inner = new ZipArchive(payload, ZipArchiveMode.Create, leaveOpen: true))
        {
            var journal = inner.CreateEntry("journal.jsonl", CompressionLevel.Optimal);
            await using (var writer = new StreamWriter(journal.Open(), Encoding.UTF8))
            {
                foreach (var entry in entries)
                {
                    ct.ThrowIfCancellationRequested();
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new
                    {
                        seq = entry.Sequence,
                        op = (int)entry.Operation,
                        type = entry.EntityType,
                        id = entry.EntityId.Value,
                        at = entry.At,
                        device = entry.DeviceId,
                        snapshot = entry.Snapshot,
                        hash = entry.ContentHash,
                    }, Json)).ConfigureAwait(false);
                }
            }

            foreach (var hash in hashes)
            {
                ct.ThrowIfCancellationRequested();

                var file = inner.CreateEntry("objects/" + hash, CompressionLevel.NoCompression);
                await using var target = file.Open();

                // Read out of the vault decrypted; it is re-encrypted with the transfer key as
                // part of the payload, so plaintext exists only in memory in transit.
                using var source = store.OpenRead(hash);
                await source.CopyToAsync(target, ct).ConfigureAwait(false);
            }
        }

        var sealedPayload = VaultCrypto.Encrypt(
            transferKey,
            payload.ToArray(),
            Encoding.UTF8.GetBytes(fromDeviceId));

        await using var output = File.Create(destinationPath);
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifestEntry = archive.CreateEntry(ManifestEntry, CompressionLevel.Optimal);
            await using (var writer = new StreamWriter(manifestEntry.Open(), Encoding.UTF8))
            {
                await writer.WriteAsync(JsonSerializer.Serialize(manifest, Json)).ConfigureAwait(false);
            }

            var payloadEntry = archive.CreateEntry(PayloadEntry, CompressionLevel.NoCompression);
            await using var payloadStream = payloadEntry.Open();
            await payloadStream.WriteAsync(sealedPayload, ct).ConfigureAwait(false);
        }

        return manifest;
    }

    // ------------------------------------------------------------------------ reading

    /// <summary>Reads the manifest without needing the pairing code.</summary>
    public static async Task<BundleManifest?> ReadManifestAsync(
        string path, CancellationToken ct = default)
    {
        try
        {
            await using var file = File.OpenRead(path);
            using var archive = new ZipArchive(file, ZipArchiveMode.Read);

            var entry = archive.GetEntry(ManifestEntry);
            if (entry is null) return null;

            await using var stream = entry.Open();
            return await JsonSerializer.DeserializeAsync<BundleManifest>(stream, Json, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Opens a bundle's payload with the transfer key, handing back the journal entries and a
    /// reader for the file bytes.
    /// </summary>
    public static async Task<BundleContents?> OpenAsync(
        string path, SecureBuffer transferKey, string fromDeviceId, CancellationToken ct = default)
    {
        await using var file = File.OpenRead(path);
        using var archive = new ZipArchive(file, ZipArchiveMode.Read);

        var payloadEntry = archive.GetEntry(PayloadEntry);
        if (payloadEntry is null) return null;

        using var sealedPayload = new MemoryStream();
        await using (var stream = payloadEntry.Open())
        {
            await stream.CopyToAsync(sealedPayload, ct).ConfigureAwait(false);
        }

        byte[] plaintext;
        try
        {
            plaintext = VaultCrypto.Decrypt(
                transferKey, sealedPayload.ToArray(), Encoding.UTF8.GetBytes(fromDeviceId));
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // The wrong code, or a bundle that has been tampered with. Both mean the same thing
            // to the caller: this cannot be opened, and nothing has been changed.
            return null;
        }

        return new BundleContents(plaintext);
    }
}

/// <summary>An opened bundle: its journal entries, and the file bytes they refer to.</summary>
public sealed class BundleContents : IDisposable
{
    private readonly MemoryStream _payload;
    private readonly ZipArchive _archive;

    internal BundleContents(byte[] plaintext)
    {
        _payload = new MemoryStream(plaintext, writable: false);
        _archive = new ZipArchive(_payload, ZipArchiveMode.Read);
    }

    /// <summary>The changes, oldest first.</summary>
    public async Task<IReadOnlyList<JournalEntry>> ReadJournalAsync(CancellationToken ct = default)
    {
        var entry = _archive.GetEntry("journal.jsonl");
        if (entry is null) return [];

        var entries = new List<JournalEntry>();

        await using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (line.Trim().Length == 0) continue;

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;

                if (!CampusId.TryParse(root.GetProperty("id").GetString(), out var id)) continue;

                entries.Add(new JournalEntry
                {
                    Sequence = root.GetProperty("seq").GetInt64(),
                    Operation = (ChangeOperation)root.GetProperty("op").GetInt32(),
                    EntityType = root.GetProperty("type").GetString() ?? "object",
                    EntityId = id,
                    At = root.GetProperty("at").GetDateTimeOffset(),
                    DeviceId = root.GetProperty("device").GetString() ?? string.Empty,
                    Snapshot = Optional(root, "snapshot"),
                    ContentHash = Optional(root, "hash"),
                });
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException)
            {
                // One unreadable line does not invalidate the rest of the bundle.
            }
        }

        return entries;
    }

    /// <summary>Opens one file from the bundle, or null when it did not carry that one.</summary>
    public Stream? OpenFile(string contentHash)
        => _archive.GetEntry("objects/" + contentHash)?.Open();

    private static string? Optional(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public void Dispose()
    {
        _archive.Dispose();
        _payload.Dispose();
    }
}
