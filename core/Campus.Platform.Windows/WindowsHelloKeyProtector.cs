using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Campus.Vault;
using Windows.Security.Credentials;
using Windows.Security.Cryptography;
using Windows.Storage.Streams;

namespace Campus.Platform.Windows;

/// <summary>
/// Unlocks the vault with Windows Hello.
///
/// The important part is what this does NOT do. A protector that merely asked Hello "was that
/// really the user?" and then handed over the key would be a check anyone could skip by reading
/// the wrapped key and calling the unwrap themselves. Instead the key-encryption key is derived
/// from a signature that only the Hello-protected private key can produce: without a successful
/// face, fingerprint or PIN there is no signature, and without the signature there is no key.
///
/// The private key lives in the TPM where available and never leaves it.
/// </summary>
public sealed class WindowsHelloKeyProtector : IKeyProtector
{
    /// <summary>
    /// The name of the Hello credential. Changing it orphans existing enrolments, which is why
    /// it is a constant rather than something derived from the vault.
    /// </summary>
    private const string CredentialName = "Campus.Vault";

    /// <summary>
    /// Signed to produce the key material. A fixed challenge is what makes the derivation
    /// repeatable; it is not a secret and does not need to be.
    /// </summary>
    private static ReadOnlySpan<byte> Challenge => "campus.vault.hello.challenge.v1"u8;

    public string ProtectorId => "windows-hello";

    public async ValueTask<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            return await KeyCredentialManager.IsSupportedAsync();
        }
        catch (Exception ex) when (ex is COMException or NotSupportedException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Enrols Hello and wraps the key material, costing exactly one verification prompt.
    ///
    /// Signing twice to prove the signature is repeatable would be a stronger check, but it would
    /// also mean two Hello prompts to set up and two to change the setting, and the recovery key
    /// already guarantees a way in. A Hello enrolment that later fails to reproduce the key is
    /// therefore recoverable rather than fatal, and <see cref="UnprotectAsync"/> says so.
    /// </summary>
    public async ValueTask<byte[]> ProtectAsync(ReadOnlyMemory<byte> keyMaterial, CancellationToken ct = default)
    {
        var credential = await OpenOrCreateAsync(ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Windows Hello is not available on this device.");

        var signature = await SignAsync(credential, ct).ConfigureAwait(false)
            ?? throw new OperationCanceledException("Windows Hello verification was cancelled.");

        try
        {
            using var kek = DeriveKek(signature);
            return VaultCrypto.Encrypt(kek, keyMaterial.Span, Encoding.UTF8.GetBytes(ProtectorId));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signature);
        }
    }

    /// <summary>
    /// Prompts for Hello and unwraps the key material. Returns null when the user cancels, which
    /// is an outcome rather than an error.
    /// </summary>
    public async ValueTask<SecureBuffer?> UnprotectAsync(
        ReadOnlyMemory<byte> wrapped, string reason, CancellationToken ct = default)
    {
        var result = await OpenAsync(ct).ConfigureAwait(false);
        if (result is null) return null;

        var signature = await SignAsync(result, ct).ConfigureAwait(false);
        if (signature is null) return null;

        try
        {
            using var kek = DeriveKek(signature);
            var plaintext = VaultCrypto.Decrypt(kek, wrapped.Span, Encoding.UTF8.GetBytes(ProtectorId));
            var buffer = new SecureBuffer(plaintext);
            CryptographicOperations.ZeroMemory(plaintext);
            return buffer;
        }
        catch (CryptographicException)
        {
            // The signature did not reproduce the key. That happens when Hello has been re-enrolled
            // on this machine, which invalidates the credential; the recovery key is the way back.
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signature);
        }
    }

    /// <summary>Removes the Hello enrolment. The vault stays reachable through its recovery key.</summary>
    public static async Task<bool> ForgetAsync()
    {
        try
        {
            await KeyCredentialManager.DeleteAsync(CredentialName);
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            return false;
        }
    }

    private static SecureBuffer DeriveKek(byte[] signature)
    {
        // The signature is long and structured rather than uniformly random, so it is run through
        // an extract-and-expand step instead of being used as a key directly.
        var prk = new SecureBuffer(HKDF.Extract(HashAlgorithmName.SHA256, signature));
        try
        {
            return VaultCrypto.DeriveKey(prk, "campus/hello/v1");
        }
        finally
        {
            prk.Dispose();
        }
    }

    private static async Task<KeyCredential?> OpenAsync(CancellationToken ct)
    {
        try
        {
            var result = await KeyCredentialManager.OpenAsync(CredentialName);
            return result.Status == KeyCredentialStatus.Success ? result.Credential : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            return null;
        }
    }

    private static async Task<KeyCredential?> OpenOrCreateAsync(CancellationToken ct)
    {
        var existing = await OpenAsync(ct).ConfigureAwait(false);
        if (existing is not null) return existing;

        try
        {
            var created = await KeyCredentialManager.RequestCreateAsync(
                CredentialName, KeyCredentialCreationOption.ReplaceExisting);
            return created.Status == KeyCredentialStatus.Success ? created.Credential : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            return null;
        }
    }

    private static async Task<byte[]?> SignAsync(KeyCredential credential, CancellationToken ct)
    {
        try
        {
            var buffer = CryptographicBuffer.CreateFromByteArray(Challenge.ToArray());
            var result = await credential.RequestSignAsync(buffer);
            if (result.Status != KeyCredentialStatus.Success) return null;

            CryptographicBuffer.CopyToByteArray(result.Result, out var bytes);
            return bytes;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            return null;
        }
    }
}
