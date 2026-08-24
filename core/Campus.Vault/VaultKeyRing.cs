using System.Security.Cryptography;
using System.Text;

namespace Campus.Vault;

/// <summary>
/// Holds the unlocked master key and hands out the purpose-specific subkeys the rest of Campus
/// needs. Locking zeroes every key it owns; nothing else in the app is allowed to cache one.
/// </summary>
public sealed class VaultKeyRing : IDisposable
{
    private SecureBuffer? _master;
    private SecureBuffer? _contentKey;
    private SecureBuffer? _nameKey;
    private SecureBuffer? _metadataKey;
    private SecureBuffer? _databaseKey;
    private SecureBuffer? _indexKey;
    private SecureBuffer? _thumbnailKey;
    private SecureBuffer? _syncKey;

    public bool IsUnlocked => _master is { IsDisposed: false };

    public string VaultId { get; private set; } = string.Empty;

    internal void Adopt(SecureBuffer master, string vaultId)
    {
        Lock();
        _master = master;
        VaultId = vaultId;
        _contentKey = VaultCrypto.DeriveKey(master, "campus/content/v1");
        _nameKey = VaultCrypto.DeriveKey(master, "campus/name/v1");
        _metadataKey = VaultCrypto.DeriveKey(master, "campus/metadata/v1");
        _databaseKey = VaultCrypto.DeriveKey(master, "campus/database/v1");
        _indexKey = VaultCrypto.DeriveKey(master, "campus/index/v1");
        _thumbnailKey = VaultCrypto.DeriveKey(master, "campus/thumbnail/v1");
        _syncKey = VaultCrypto.DeriveKey(master, "campus/sync/v1");
    }

    private SecureBuffer Require(SecureBuffer? key)
        => key is { IsDisposed: false } ? key : throw new InvalidOperationException("The vault is locked.");

    /// <summary>Base key for file content. Per-object keys are derived from it plus the content hash.</summary>
    public SecureBuffer ContentKey => Require(_contentKey);
    /// <summary>Key for blinding on-disk object names.</summary>
    public SecureBuffer NameKey => Require(_nameKey);
    /// <summary>Key for encrypted metadata columns.</summary>
    public SecureBuffer MetadataKey => Require(_metadataKey);
    /// <summary>SQLCipher key for the workspace database.</summary>
    public SecureBuffer DatabaseKey => Require(_databaseKey);
    /// <summary>Key for the full-text index.</summary>
    public SecureBuffer IndexKey => Require(_indexKey);
    /// <summary>Key for generated thumbnails.</summary>
    public SecureBuffer ThumbnailKey => Require(_thumbnailKey);
    /// <summary>Key for the sync channel with paired devices.</summary>
    public SecureBuffer SyncKey => Require(_syncKey);

    /// <summary>Per-object content key, bound to the object's content hash.</summary>
    public SecureBuffer DeriveContentKey(string contentHash)
        => VaultCrypto.DeriveKey(ContentKey, "object", Encoding.UTF8.GetBytes(contentHash));

    /// <summary>The on-disk name for an object, which reveals nothing about its content.</summary>
    public string BlindName(string contentHash) => VaultCrypto.BlindName(NameKey, contentHash);

    /// <summary>Wraps the master key for a new protector without exposing it to the caller.</summary>
    internal byte[] WrapMasterWith(SecureBuffer kek)
        => VaultCrypto.Encrypt(kek, Require(_master).Span, Encoding.UTF8.GetBytes(VaultId));

    /// <summary>Zeroes every key. Called by the lock command, auto-lock, and shutdown.</summary>
    public void Lock()
    {
        _syncKey?.Dispose();
        _thumbnailKey?.Dispose();
        _indexKey?.Dispose();
        _databaseKey?.Dispose();
        _metadataKey?.Dispose();
        _nameKey?.Dispose();
        _contentKey?.Dispose();
        _master?.Dispose();
        _syncKey = _thumbnailKey = _indexKey = _databaseKey = _metadataKey = _nameKey = _contentKey = _master = null;
    }

    public void Dispose() => Lock();

    /// <summary>Confirms a candidate master key against the header's verifier before adopting it.</summary>
    internal static bool VerifyMaster(SecureBuffer candidate, VaultHeader header)
    {
        try
        {
            var plaintext = VaultCrypto.Decrypt(candidate, Convert.FromBase64String(header.Verifier),
                Encoding.UTF8.GetBytes(header.VaultId));
            return Encoding.UTF8.GetString(plaintext) == VaultHeader.VerifierPlaintext;
        }
        catch (CryptographicException) { return false; }
        catch (FormatException) { return false; }
    }
}
