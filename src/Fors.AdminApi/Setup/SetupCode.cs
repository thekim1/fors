using System.Security.Cryptography;
using System.Text;

namespace Fors.AdminApi.Setup;

/// <summary>
/// The setup code: the first 128 bits of HMAC-SHA-256 under the setup secret over the opening's nonce, as 26 Crockford base32
/// characters in five groups (<c>XXXXX-XXXXX-XXXXX-XXXXX-XXXXXX</c>), so every instance prints the same code for an opening.
/// Input is read case-insensitively, with or without separators, and with Crockford's readings of O as 0 and I, L as 1.
/// </summary>
public static class SetupCode
{
    /// <summary>Crockford's base32 alphabet: no I, L, O or U.</summary>
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>Characters without separators: 128 bits in 5-bit characters.</summary>
    public const int Length = 26;

    /// <summary>The longest input looked at; anything longer is a wrong code.</summary>
    public const int MaxInputLength = 64;

    private static readonly int[] Groups = [5, 5, 5, 5, 6];

    /// <summary>The code of the opening with <paramref name="nonce"/>, grouped. Never logged or answered.</summary>
    public static string Compute(string secret, string nonce)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        ArgumentException.ThrowIfNullOrEmpty(nonce);
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes("fors-gateway-setup-code:" + nonce));
        var plain = Encode(mac.AsSpan(0, 16));
        var grouped = new StringBuilder(Length + Groups.Length);
        var at = 0;
        foreach (var size in Groups)
        {
            grouped.Append(grouped.Length == 0 ? "" : "-").Append(plain, at, size);
            at += size;
        }

        return grouped.ToString();
    }

    /// <summary>Whether <paramref name="input"/> is <paramref name="expected"/> (as <see cref="Compute"/> gives it), compared in constant time.</summary>
    public static bool Matches(string? input, string expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var wanted = Encoding.ASCII.GetBytes(expected.Replace("-", "", StringComparison.Ordinal));
        var normalized = Normalize(input);
        // A malformed input is compared too (as zero bytes, never equal), so both paths take the same time.
        var given = normalized is null ? new byte[wanted.Length] : Encoding.ASCII.GetBytes(normalized);
        return CryptographicOperations.FixedTimeEquals(given, wanted) && normalized is not null;
    }

    /// <summary>The 26 characters of <paramref name="input"/> in the alphabet, or null when it is not a well-formed code.</summary>
    public static string? Normalize(string? input)
    {
        if (input is null || input.Length > MaxInputLength)
        {
            return null;
        }

        var chars = new StringBuilder(Length);
        foreach (var c in input)
        {
            if (c is '-' or ' ')
            {
                continue;
            }

            var upper = char.ToUpperInvariant(c) switch
            {
                'O' => '0',
                'I' or 'L' => '1',
                var other => other,
            };
            if (!Alphabet.Contains(upper, StringComparison.Ordinal) || chars.Length == Length)
            {
                return null;
            }

            chars.Append(upper);
        }

        return chars.Length == Length ? chars.ToString() : null;
    }

    private static string Encode(ReadOnlySpan<byte> bytes)
    {
        var chars = new StringBuilder(Length);
        int buffer = 0, bits = 0;
        foreach (var b in bytes)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                chars.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }

            buffer &= (1 << bits) - 1;
        }

        if (bits > 0)
        {
            chars.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        }

        return chars.ToString();
    }
}
