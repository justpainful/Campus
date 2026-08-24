using System.Security.Cryptography;
using System.Text;

namespace Campus.Vault;

/// <summary>
/// The vault as the rest of the app sees it: create it once, unlock it with Windows Hello or the
/// recovery key, and lock it to put every key beyond reach again.
/// </summary>
public sealed class CampusVault : IDisposable
{
    private readonly VaultPaths _paths;
    private readonly IKeyProtector? _platformProtector;
    private VaultHeader? _header;

    public CampusVault(VaultPaths paths, IKeyProtector? platformProtector = null)
    {
        _paths = paths;
        _platformProtector = platformProtector;
        Keys = new VaultKeyRing();
        Objects = new VaultObjectStore(paths, Keys);
    }

    public VaultPaths Paths => _paths;
    public VaultKeyRing Keys { get; }
    public VaultObjectStore Objects { get; }

    public bool IsInitialised => _paths.Exists;
    public bool IsUnlocked => Keys.IsUnlocked;

    /// <summary>Raised whenever the vault transitions between locked and unlocked.</summary>
    public event EventHandler<bool>? LockStateChanged;

    /// <summary>
    /// Creates a new vault. Returns the recovery key, which is the only time it is ever
    /// available in plaintext — Campus never stores it.
    /// </summary>
    public async Task<string> CreateAsync(CancellationToken ct = default)
    {
        if (IsInitialised)
            throw new InvalidOperationException("A vault already exists at this location.");

        _paths.EnsureCreated();

        var master = SecureBuffer.Random(VaultCrypto.KeySize);
        var salt = new byte[32];
        RandomNumberGenerator.Fill(salt);

        var header = new VaultHeader { RecoverySalt = Convert.ToBase64String(salt) };
        var aad = Encoding.UTF8.GetBytes(header.VaultId);
        header.Verifier = Convert.ToBase64String(
            VaultCrypto.Encrypt(master, Encoding.UTF8.GetBytes(VaultHeader.VerifierPlaintext), aad));

        // Recovery protector — always present, so losing Windows Hello never loses the workspace.
        var recovery = RecoveryKey.Generate();
        using (var kek = RecoveryKey.DeriveKek(recovery, salt))
        {
            header.Protectors.Add(new ProtectorEntry
            {
                Id = "recovery",
                Kind = ProtectorKind.Recovery,
                Label = "Recovery Key",
                Wrapped = Convert.ToBase64String(VaultCrypto.Encrypt(kek, master.Span, aad)),
            });
        }

        // Platform protector — Windows Hello, when the machine can do it.
        if (_platformProtector is not null && await _platformProtector.IsAvailableAsync(ct).ConfigureAwait(false))
        {
            var wrapped = await _platformProtector.ProtectAsync(master.Span.ToArray(), ct).ConfigureAwait(false);
            header.Protectors.Add(new ProtectorEntry
            {
                Id = _platformProtector.ProtectorId,
                Kind = ProtectorKind.Platform,
                Label = "Windows Hello",
                Wrapped = Convert.ToBase64String(wrapped),
            });
        }

        await File.WriteAllTextAsync(_paths.Header, header.ToJson(), ct).ConfigureAwait(false);
        _header = header;

        Keys.Adopt(master, header.VaultId);
        LockStateChanged?.Invoke(this, true);
        return recovery;
    }

    /// <summary>Unlocks with the platform protector, prompting for Windows Hello.</summary>
    public async Task<UnlockOutcome> UnlockAsync(string reason = "Unlock Campus", CancellationToken ct = default)
    {
        var header = await LoadHeaderAsync(ct).ConfigureAwait(false);
        if (header is null) return UnlockOutcome.NotInitialised;

        if (_platformProtector is null || !await _platformProtector.IsAvailableAsync(ct).ConfigureAwait(false))
            return UnlockOutcome.ProtectorUnavailable;

        var entry = header.Find(_platformProtector.ProtectorId);
        if (entry is null) return UnlockOutcome.ProtectorUnavailable;

        SecureBuffer? master;
        try
        {
            master = await _platformProtector
                .UnprotectAsync(Convert.FromBase64String(entry.Wrapped), reason, ct)
                .ConfigureAwait(false);
        }
        catch (CryptographicException) { return UnlockOutcome.VerificationFailed; }

        if (master is null) return UnlockOutcome.Cancelled;

        if (!VaultKeyRing.VerifyMaster(master, header))
        {
            master.Dispose();
            return UnlockOutcome.VaultCorrupt;
        }

        entry.LastUsedAt = DateTimeOffset.UtcNow;
        await SaveHeaderAsync(header, ct).ConfigureAwait(false);

        Keys.Adopt(master, header.VaultId);
        LockStateChanged?.Invoke(this, true);
        return UnlockOutcome.Success;
    }

    /// <summary>Unlocks with the recovery key. Works on any machine, with no Hello enrolment.</summary>
    public async Task<UnlockOutcome> UnlockWithRecoveryKeyAsync(string recoveryKey, CancellationToken ct = default)
    {
        var header = await LoadHeaderAsync(ct).ConfigureAwait(false);
        if (header is null) return UnlockOutcome.NotInitialised;
        if (!RecoveryKey.IsWellFormed(recoveryKey)) return UnlockOutcome.VerificationFailed;

        var entry = header.Find("recovery");
        if (entry is null) return UnlockOutcome.VaultCorrupt;

        var salt = Convert.FromBase64String(header.RecoverySalt);
        var aad = Encoding.UTF8.GetBytes(header.VaultId);

        try
        {
            using var kek = RecoveryKey.DeriveKek(recoveryKey, salt);
            var raw = VaultCrypto.Decrypt(kek, Convert.FromBase64String(entry.Wrapped), aad);
            var master = new SecureBuffer(raw);
            CryptographicOperations.ZeroMemory(raw);

            if (!VaultKeyRing.VerifyMaster(master, header))
            {
                master.Dispose();
                return UnlockOutcome.VerificationFailed;
            }

            entry.LastUsedAt = DateTimeOffset.UtcNow;
            await SaveHeaderAsync(header, ct).ConfigureAwait(false);

            Keys.Adopt(master, header.VaultId);
            LockStateChanged?.Invoke(this, true);
            return UnlockOutcome.Success;
        }
        catch (CryptographicException) { return UnlockOutcome.VerificationFailed; }
        catch (FormatException) { return UnlockOutcome.VerificationFailed; }
    }

    /// <summary>
    /// Re-enrols the platform protector — after a Windows reinstall, a new PC, or the user
    /// turning Hello on for the first time. Requires the vault to be unlocked already.
    /// </summary>
    public async Task<bool> EnrolPlatformProtectorAsync(CancellationToken ct = default)
    {
        if (!IsUnlocked) throw new InvalidOperationException("Unlock the vault before enrolling a protector.");
        if (_platformProtector is null || !await _platformProtector.IsAvailableAsync(ct).ConfigureAwait(false))
            return false;

        var header = await LoadHeaderAsync(ct).ConfigureAwait(false);
        if (header is null) return false;

        using var scratch = new SecureBuffer(VaultCrypto.KeySize);
        // Wrap through the key ring so the master key is never materialised outside it.
        var probe = Keys.WrapMasterWith(scratch);
        var master = VaultCrypto.Decrypt(scratch, probe, Encoding.UTF8.GetBytes(Keys.VaultId));

        try
        {
            var wrapped = await _platformProtector.ProtectAsync(master, ct).ConfigureAwait(false);
            header.Protectors.RemoveAll(p => p.Id == _platformProtector.ProtectorId);
            header.Protectors.Add(new ProtectorEntry
            {
                Id = _platformProtector.ProtectorId,
                Kind = ProtectorKind.Platform,
                Label = "Windows Hello",
                Wrapped = Convert.ToBase64String(wrapped),
            });
            await SaveHeaderAsync(header, ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(master);
        }
    }

    /// <summary>Whether Windows Hello is enrolled for this vault.</summary>
    public async Task<bool> HasPlatformProtectorAsync(CancellationToken ct = default)
    {
        var header = await LoadHeaderAsync(ct).ConfigureAwait(false);
        return _platformProtector is not null && header?.Find(_platformProtector.ProtectorId) is not null;
    }

    /// <summary>Zeroes every key. Everything encrypted becomes unreadable until the next unlock.</summary>
    public void Lock()
    {
        if (!Keys.IsUnlocked) return;
        Keys.Lock();
        LockStateChanged?.Invoke(this, false);
    }

    private async Task<VaultHeader?> LoadHeaderAsync(CancellationToken ct)
    {
        if (_header is not null) return _header;
        if (!File.Exists(_paths.Header)) return null;
        var json = await File.ReadAllTextAsync(_paths.Header, ct).ConfigureAwait(false);
        _header = VaultHeader.FromJson(json);
        return _header;
    }

    private async Task SaveHeaderAsync(VaultHeader header, CancellationToken ct)
    {
        var temp = _paths.Header + ".part";
        await File.WriteAllTextAsync(temp, header.ToJson(), ct).ConfigureAwait(false);
        File.Move(temp, _paths.Header, overwrite: true);
        _header = header;
    }

    public void Dispose() => Keys.Dispose();
}
