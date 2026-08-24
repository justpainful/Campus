using System.Security.Cryptography;
using System.Text;

namespace Campus.Vault;

/// <summary>
/// The escape hatch for the vault. Windows Hello is bound to this machine and this Windows
/// profile; if either is lost the recovery key is the only way back to the master key, so it is
/// generated once, shown once, and never stored in plaintext.
/// </summary>
public static class RecoveryKey
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"; // Crockford base32
    private const int Groups = 6;
    private const int GroupSize = 4;
    public const int CharCount = Groups * GroupSize; // 24 chars = 120 bits

    /// <summary>Generates a fresh recovery key in display form (groups separated by hyphens).</summary>
    public static string Generate()
    {
        var chars = new char[CharCount];
        Span<byte> random = stackalloc byte[CharCount];
        RandomNumberGenerator.Fill(random);
        for (int i = 0; i < CharCount; i++)
            chars[i] = Alphabet[random[i] & 31];
        CryptographicOperations.ZeroMemory(random);
        return Format(new string(chars));
    }

    /// <summary>Formats raw characters as XXXX-XXXX-XXXX-XXXX-XXXX-XXXX.</summary>
    public static string Format(string raw)
    {
        var normalised = Normalise(raw);
        var sb = new StringBuilder(CharCount + Groups - 1);
        for (int i = 0; i < normalised.Length; i++)
        {
            if (i > 0 && i % GroupSize == 0) sb.Append('-');
            sb.Append(normalised[i]);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Strips formatting and folds the characters people commonly mistype: I and L to 1,
    /// O to 0, U to V. Crockford base32 is designed for exactly this.
    /// </summary>
    public static string Normalise(string input)
    {
        var sb = new StringBuilder(CharCount);
        foreach (var raw in input)
        {
            var c = char.ToUpperInvariant(raw);
            c = c switch { 'I' or 'L' => '1', 'O' => '0', 'U' => 'V', _ => c };
            if (Alphabet.IndexOf(c) >= 0) sb.Append(c);
        }
        return sb.ToString();
    }

    public static bool IsWellFormed(string input) => Normalise(input).Length == CharCount;

    /// <summary>Derives the key-encryption key that wraps the master key for recovery.</summary>
    public static SecureBuffer DeriveKek(string recoveryKey, ReadOnlySpan<byte> salt)
    {
        var normalised = Normalise(recoveryKey);
        if (normalised.Length != CharCount)
            throw new ArgumentException("Recovery key is not the expected length.", nameof(recoveryKey));

        var bytes = Encoding.ASCII.GetBytes(normalised);
        try
        {
            return VaultCrypto.DeriveFromSecret(bytes, salt);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}
