using System.Text.Json;
using System.Text.Json.Serialization;

namespace Campus.Vault;

/// <summary>
/// The only part of the vault that is stored in the clear. It holds no secrets — just the
/// wrapped copies of the master key and the parameters needed to unwrap them.
/// </summary>
public sealed class VaultHeader
{
    public const int CurrentVersion = 1;

    [JsonPropertyName("version")]
    public int Version { get; set; } = CurrentVersion;

    [JsonPropertyName("vaultId")]
    public string VaultId { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Salt for recovery-key derivation, base64.</summary>
    [JsonPropertyName("recoverySalt")]
    public string RecoverySalt { get; set; } = string.Empty;

    /// <summary>Each way the master key can be unwrapped. At least one is always a recovery key.</summary>
    [JsonPropertyName("protectors")]
    public List<ProtectorEntry> Protectors { get; init; } = [];

    /// <summary>
    /// A fixed plaintext encrypted under the master key. Decrypting it proves an unwrap produced
    /// the right key before anything else is touched.
    /// </summary>
    [JsonPropertyName("verifier")]
    public string Verifier { get; set; } = string.Empty;

    public const string VerifierPlaintext = "campus.vault.verifier.v1";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static VaultHeader? FromJson(string json)
        => JsonSerializer.Deserialize<VaultHeader>(json, Json);

    public ProtectorEntry? Find(string protectorId)
        => Protectors.FirstOrDefault(p => string.Equals(p.Id, protectorId, StringComparison.Ordinal));
}

public sealed class ProtectorEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("kind")]
    public ProtectorKind Kind { get; set; }

    [JsonPropertyName("label")]
    public string? Label { get; set; }

    /// <summary>Master key wrapped by this protector, base64.</summary>
    [JsonPropertyName("wrapped")]
    public string Wrapped { get; set; } = string.Empty;

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("lastUsedAt")]
    public DateTimeOffset? LastUsedAt { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProtectorKind
{
    Platform = 0,   // Windows Hello via DPAPI
    Recovery = 1,   // Recovery key
    Passphrase = 2, // Optional user passphrase
}
