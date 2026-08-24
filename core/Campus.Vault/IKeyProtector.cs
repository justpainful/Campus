namespace Campus.Vault;

/// <summary>
/// Wraps and unwraps the master key using a platform facility. On Windows this is DPAPI gated
/// behind a Windows Hello consent prompt; the abstraction keeps the vault itself testable and
/// keeps platform code out of the core.
/// </summary>
public interface IKeyProtector
{
    /// <summary>Stable identifier written into the vault header so the right protector is used on unlock.</summary>
    string ProtectorId { get; }

    /// <summary>Whether this protector can be used right now (hardware present, user enrolled).</summary>
    ValueTask<bool> IsAvailableAsync(CancellationToken ct = default);

    /// <summary>Wraps key material. Called once at vault creation and again when the protector is re-enrolled.</summary>
    ValueTask<byte[]> ProtectAsync(ReadOnlyMemory<byte> keyMaterial, CancellationToken ct = default);

    /// <summary>
    /// Prompts the user if the protector requires consent, then unwraps the key material.
    /// Returns null when the user cancels or verification fails.
    /// </summary>
    ValueTask<SecureBuffer?> UnprotectAsync(ReadOnlyMemory<byte> wrapped, string reason, CancellationToken ct = default);
}

/// <summary>Result of an unlock attempt, kept separate from exceptions so cancel is not an error.</summary>
public enum UnlockOutcome
{
    Success = 0,
    Cancelled = 1,
    VerificationFailed = 2,
    ProtectorUnavailable = 3,
    VaultCorrupt = 4,
    NotInitialised = 5,
}
