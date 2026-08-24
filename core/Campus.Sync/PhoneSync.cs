using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Campus.Vault;

namespace Campus.Sync;

/// <summary>
/// The conversation between Campus Pocket and this machine.
///
/// The phone holds captures, not the workspace, so this is one-directional by design: the phone
/// pushes what it caught and the PC says what it kept. Making it bidirectional would mean putting
/// a copy of the workspace on a device that goes in a pocket, which is a different product and a
/// worse promise.
///
/// Three properties are worth stating, because they are the whole of the security model:
///
///   * Being on the same network proves nothing. The phone signs a nonce with the secret both
///     sides took from the pairing code, and a device that never paired cannot produce it.
///   * Everything after the greeting is encrypted with that secret, so the school Wi-Fi carries
///     ciphertext.
///   * The PC listens only while somebody is looking at the sync page and has asked it to.
/// </summary>
public static class PhoneSync
{
    public const int Version = 1;

    /// <summary>The port Campus Pocket connects to. Registered to nothing; chosen once.</summary>
    public const int Port = 47_821;

    private const int MaxMessageBytes = 8 * 1024 * 1024;
    private const long MaxAttachmentBytes = 512L * 1024 * 1024;

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ------------------------------------------------------------------------ messages

    /// <summary>Sent in the clear: it carries no content, only who is calling and their proof.</summary>
    public sealed record Hello
    {
        public int Version { get; init; } = PhoneSync.Version;
        public string DeviceId { get; init; } = string.Empty;
        public string DeviceName { get; init; } = string.Empty;
        public string Nonce { get; init; } = string.Empty;
        public string Signature { get; init; } = string.Empty;
    }

    public sealed record HelloAck
    {
        public int Version { get; init; } = PhoneSync.Version;
        public bool Accepted { get; init; }
        public string? WorkspaceName { get; init; }
        public string? Reason { get; init; }
    }

    /// <summary>One thing caught on the phone.</summary>
    public sealed record Capture
    {
        public string Id { get; init; } = string.Empty;

        /// <summary>Matches ObjectKind on this side; the phone only uses a handful of them.</summary>
        public int Kind { get; init; }

        public string Title { get; init; } = string.Empty;
        public string? Body { get; init; }
        public string? SubjectName { get; init; }
        public DateTimeOffset? DueAt { get; init; }
        public DateTimeOffset CapturedAt { get; init; }

        /// <summary>File name of an attachment sent after this batch, in order.</summary>
        public string? Attachment { get; init; }
        public int? AttachmentBytes { get; init; }
    }

    public sealed record Push
    {
        public IReadOnlyList<Capture> Items { get; init; } = [];
    }

    /// <summary>What was actually stored. The phone deletes only what appears here.</summary>
    public sealed record PushAck
    {
        public IReadOnlyList<string> AcceptedIds { get; init; } = [];
        public Dictionary<string, string> Rejected { get; init; } = [];
    }

    // ------------------------------------------------------------------------ framing

    /// <summary>
    /// Writes one message: a four-byte length, then the bytes. Length-prefixed rather than
    /// delimited because attachments are binary and there is no byte that cannot appear in them.
    /// </summary>
    public static async Task WriteFrameAsync(
        Stream stream, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);

        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async Task<byte[]> ReadFrameAsync(
        Stream stream, long limit = MaxMessageBytes, CancellationToken ct = default)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);

        var length = BinaryPrimitives.ReadInt32BigEndian(header);

        // A length is the first thing an attacker controls, and allocating on it unchecked is how
        // a sync endpoint becomes a way to exhaust memory from the local network.
        if (length < 0 || length > limit)
            throw new InvalidDataException($"That message claims to be {length} bytes.");

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
        return payload;
    }

    public static Task WriteJsonAsync<T>(Stream stream, T value, CancellationToken ct = default)
        => WriteFrameAsync(stream, JsonSerializer.SerializeToUtf8Bytes(value, Json), ct);

    public static async Task<T?> ReadJsonAsync<T>(Stream stream, CancellationToken ct = default)
    {
        var payload = await ReadFrameAsync(stream, ct: ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(payload, Json);
    }

    /// <summary>Writes a message encrypted with the pairing secret.</summary>
    public static Task WriteSealedAsync<T>(
        Stream stream, T value, SecureBuffer key, string deviceId, CancellationToken ct = default)
        => WriteFrameAsync(
            stream,
            VaultCrypto.Encrypt(
                key,
                JsonSerializer.SerializeToUtf8Bytes(value, Json),
                Encoding.UTF8.GetBytes(deviceId)),
            ct);

    public static async Task<T?> ReadSealedAsync<T>(
        Stream stream, SecureBuffer key, string deviceId, CancellationToken ct = default)
    {
        var envelope = await ReadFrameAsync(stream, ct: ct).ConfigureAwait(false);
        var plaintext = VaultCrypto.Decrypt(key, envelope, Encoding.UTF8.GetBytes(deviceId));
        return JsonSerializer.Deserialize<T>(plaintext, Json);
    }

    /// <summary>Reads an encrypted attachment. Larger limit; still a limit.</summary>
    public static async Task<byte[]> ReadSealedBytesAsync(
        Stream stream, SecureBuffer key, string deviceId, CancellationToken ct = default)
    {
        var envelope = await ReadFrameAsync(stream, MaxAttachmentBytes, ct).ConfigureAwait(false);
        return VaultCrypto.Decrypt(key, envelope, Encoding.UTF8.GetBytes(deviceId));
    }

    // ------------------------------------------------------------------------ pairing

    /// <summary>
    /// What the PC shows and the phone scans:
    /// <c>campus-pair:v1:&lt;deviceId&gt;:&lt;url-encoded name&gt;:&lt;base64 key&gt;</c>.
    ///
    /// The key is in the code because the code is shown on a screen, read by a camera in the same
    /// room, and never sent anywhere. That is a stronger channel than anything the two devices
    /// could negotiate over a network they do not trust.
    /// </summary>
    public static string BuildPairingCode(string deviceId, string deviceName, byte[] sharedKey)
        => $"campus-pair:v1:{deviceId}:{Uri.EscapeDataString(deviceName)}:{Convert.ToBase64String(sharedKey)}";

    public static byte[] NewSharedKey()
    {
        var key = new byte[VaultCrypto.KeySize];
        RandomNumberGenerator.Fill(key);
        return key;
    }

    /// <summary>
    /// Checks that whoever is calling holds the pairing secret.
    ///
    /// Compared in constant time: a comparison that stops at the first wrong byte tells an
    /// attacker how much of a signature they guessed right, which is enough to guess the rest.
    /// </summary>
    public static bool Verify(Hello hello, string sharedKeyBase64)
    {
        try
        {
            var key = Convert.FromBase64String(sharedKeyBase64);
            var expected = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(hello.Nonce));
            var offered = Convert.FromBase64String(hello.Signature);

            return CryptographicOperations.FixedTimeEquals(expected, offered);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>The key the rest of the conversation is encrypted with.</summary>
    public static SecureBuffer TransferKey(string sharedKeyBase64)
    {
        var raw = Convert.FromBase64String(sharedKeyBase64);
        var key = new SecureBuffer(VaultCrypto.KeySize);
        raw.AsSpan(0, Math.Min(raw.Length, VaultCrypto.KeySize)).CopyTo(key.WritableSpan);
        CryptographicOperations.ZeroMemory(raw);
        return key;
    }
}
