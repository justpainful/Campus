using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Campus.Domain;

/// <summary>
/// Lexicographically sortable 26-character identifier (ULID layout: 48-bit timestamp + 80-bit randomness).
/// Sortable ids keep the change journal and any id-ordered listing in creation order for free.
/// </summary>
public readonly struct CampusId : IEquatable<CampusId>, IComparable<CampusId>
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"; // Crockford base32, no I L O U
    private readonly string _value;

    private CampusId(string value) => _value = value;

    public static CampusId Empty => new(new string('0', 26));

    public string Value => _value ?? Empty._value;

    public static CampusId New() => New(DateTimeOffset.UtcNow);

    public static CampusId New(DateTimeOffset timestamp)
    {
        Span<byte> bytes = stackalloc byte[16];
        var ms = timestamp.ToUnixTimeMilliseconds();
        // 48-bit big-endian timestamp
        bytes[0] = (byte)(ms >> 40);
        bytes[1] = (byte)(ms >> 32);
        bytes[2] = (byte)(ms >> 24);
        bytes[3] = (byte)(ms >> 16);
        bytes[4] = (byte)(ms >> 8);
        bytes[5] = (byte)ms;
        RandomNumberGenerator.Fill(bytes[6..]);
        return new CampusId(Encode(bytes));
    }

    public DateTimeOffset Timestamp
    {
        get
        {
            var bytes = Decode(Value);
            long ms = ((long)bytes[0] << 40) | ((long)bytes[1] << 32) | ((long)bytes[2] << 24)
                      | ((long)bytes[3] << 16) | ((long)bytes[4] << 8) | bytes[5];
            return DateTimeOffset.FromUnixTimeMilliseconds(ms);
        }
    }

    public static CampusId Parse(string value)
    {
        if (!TryParse(value, out var id))
            throw new FormatException($"'{value}' is not a valid CampusId.");
        return id;
    }

    public static bool TryParse(string? value, out CampusId id)
    {
        id = Empty;
        if (value is null || value.Length != 26) return false;
        foreach (var c in value)
        {
            if (Alphabet.IndexOf(char.ToUpperInvariant(c)) < 0) return false;
        }
        id = new CampusId(value.ToUpperInvariant());
        return true;
    }

    private static string Encode(ReadOnlySpan<byte> bytes)
    {
        Span<char> chars = stackalloc char[26];
        int bitBuffer = 0, bitCount = 0, outIndex = 25;
        for (int i = bytes.Length - 1; i >= 0; i--)
        {
            bitBuffer |= bytes[i] << bitCount;
            bitCount += 8;
            while (bitCount >= 5 && outIndex >= 0)
            {
                chars[outIndex--] = Alphabet[bitBuffer & 31];
                bitBuffer >>= 5;
                bitCount -= 5;
            }
        }
        while (outIndex >= 0)
        {
            chars[outIndex--] = Alphabet[bitBuffer & 31];
            bitBuffer >>= 5;
        }
        return new string(chars);
    }

    private static byte[] Decode(string value)
    {
        var bytes = new byte[16];
        int bitBuffer = 0, bitCount = 0, outIndex = 15;
        for (int i = value.Length - 1; i >= 0; i--)
        {
            var v = Alphabet.IndexOf(char.ToUpperInvariant(value[i]));
            if (v < 0) v = 0;
            bitBuffer |= v << bitCount;
            bitCount += 5;
            while (bitCount >= 8 && outIndex >= 0)
            {
                bytes[outIndex--] = (byte)(bitBuffer & 0xFF);
                bitBuffer >>= 8;
                bitCount -= 8;
            }
        }
        return bytes;
    }

    public bool Equals(CampusId other) => string.Equals(Value, other.Value, StringComparison.Ordinal);
    public override bool Equals(object? obj) => obj is CampusId other && Equals(other);
    public override int GetHashCode() => Value.GetHashCode(StringComparison.Ordinal);
    public int CompareTo(CampusId other) => string.CompareOrdinal(Value, other.Value);
    public override string ToString() => Value;

    public static bool operator ==(CampusId a, CampusId b) => a.Equals(b);
    public static bool operator !=(CampusId a, CampusId b) => !a.Equals(b);
    public static implicit operator string(CampusId id) => id.Value;
}
