using System.Security.Cryptography;
using System.Text;
using Campus.Vault;

namespace Campus.Sync;

/// <summary>
/// What two devices agree on when they are paired: a name, a salt, and the shared secret their
/// transfer key is derived from.
/// </summary>
public sealed record PairingSecret(string DeviceId, string DisplayName, byte[] Salt, byte[] Secret);

/// <summary>
/// Pairing two devices without a server, an account, or anything that can discover you.
///
/// One device shows a code; the other types it. Both sides derive the same transfer key from it,
/// and everything that crosses between them is encrypted under that key. The vault's own master
/// key never leaves the machine it was made on — a bundle is decrypted from one vault and
/// re-encrypted into the other, so the phone and the laptop keep separate keys even while sharing
/// the same notes.
///
/// The code is short because a person has to type it, and short is safe here: it is used once,
/// over a cable or a local network, to establish a key that then does the real work.
/// </summary>
public static class Pairing
{
    // Crockford base32 without the letters that look like digits, so a code read off a phone
    // screen and typed on a keyboard cannot be got wrong.
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const int CodeLength = 12;
    private const int SaltSize = 16;

    /// <summary>Makes a fresh code. Shown once, on the device being paired to.</summary>
    public static string GenerateCode()
    {
        Span<char> code = stackalloc char[CodeLength];
        Span<byte> random = stackalloc byte[CodeLength];
        RandomNumberGenerator.Fill(random);

        for (var i = 0; i < CodeLength; i++) code[i] = Alphabet[random[i] % Alphabet.Length];

        // Grouped in fours: the same reason phone numbers are grouped.
        return $"{new string(code[..4])}-{new string(code[4..8])}-{new string(code[8..])}";
    }

    public static string Normalise(string code)
    {
        var cleaned = new StringBuilder(CodeLength);

        foreach (var c in code.ToUpperInvariant())
        {
            if (Alphabet.IndexOf(c) >= 0) cleaned.Append(c);
        }

        return cleaned.ToString();
    }

    public static bool IsWellFormed(string code) => Normalise(code).Length == CodeLength;

    /// <summary>
    /// Derives the key that protects a bundle. Deliberately expensive: a twelve-character code
    /// is small enough to guess offline if deriving from it were cheap.
    /// </summary>
    public static SecureBuffer DeriveTransferKey(string code, ReadOnlySpan<byte> salt)
    {
        var normalised = Normalise(code);
        if (normalised.Length != CodeLength)
            throw new ArgumentException("That is not a pairing code.", nameof(code));

        return VaultCrypto.DeriveFromSecret(Encoding.UTF8.GetBytes(normalised), salt);
    }

    public static byte[] NewSalt()
    {
        var salt = new byte[SaltSize];
        RandomNumberGenerator.Fill(salt);
        return salt;
    }

    /// <summary>
    /// Encodes what the other device needs in order to answer: who is offering, and the salt.
    /// Small enough to fit in a QR code, and readable without the code itself — the salt is not
    /// a secret, the code is.
    /// </summary>
    public static string Offer(string deviceId, string displayName, byte[] salt)
        => $"campus1:{deviceId}:{Convert.ToBase64String(salt)}:{Convert.ToBase64String(Encoding.UTF8.GetBytes(displayName))}";

    public static (string DeviceId, string DisplayName, byte[] Salt)? ParseOffer(string offer)
    {
        var parts = offer.Split(':');
        if (parts.Length != 4 || parts[0] != "campus1") return null;

        try
        {
            return (
                parts[1],
                Encoding.UTF8.GetString(Convert.FromBase64String(parts[3])),
                Convert.FromBase64String(parts[2]));
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
