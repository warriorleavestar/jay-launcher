using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Multiplayer.Contacts;

/// <summary>
/// A contact code: someone's multiplayer identity public key, written so it can be pasted anywhere.
/// <c>JAYC1-</c> + Crockford Base32 of (X25519 public key 32 ‖ check 2), 55 characters.
/// <para>
/// It holds no secret. Whoever reads it on a chat platform or in a screenshot can only send that person sealed mail
/// (which they still have to accept), never read their mail or pose as them. What a leak can't protect against is a
/// platform that swaps the code for its own; that is what the four-emoji check in <see cref="ContactHandshake"/> catches.
/// </para>
/// </summary>
public static class ContactCode
{
    public const string Prefix = "JAYC1-";
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const int Bytes = X25519KeyPair.Length + 2;
    /// <summary>34 bytes = 272 bits, written as 55 five-bit symbols (the top 3 bits of the first are zero).</summary>
    public const int BodyLength = 55;

    public static string Format(ReadOnlySpan<byte> publicKey)
    {
        if (publicKey.Length != X25519KeyPair.Length) throw new ArgumentException(null, nameof(publicKey));
        Span<byte> data = stackalloc byte[Bytes];
        publicKey.CopyTo(data);
        Check(publicKey).CopyTo(data[X25519KeyPair.Length..]);
        var value = new BigInteger(data, isUnsigned: true, isBigEndian: true);
        var chars = new char[BodyLength];
        for (var i = BodyLength - 1; i >= 0; i--)
        {
            chars[i] = Alphabet[(int)(value & 31)];
            value >>= 5;
        }
        return Prefix + new string(chars);
    }

    /// <summary>
    /// Reads a contact code out of pasted text: ignores case, spaces and dashes inside the body, reads I/L as 1 and O as 0.
    /// The two check bytes must match, so a mistyped or truncated code is refused rather than read as another key.
    /// </summary>
    public static bool TryParse(string? text, out byte[] publicKey)
    {
        publicKey = [];
        if (string.IsNullOrWhiteSpace(text)) return false;
        var upper = text.ToUpperInvariant();
        var start = upper.IndexOf(Prefix, StringComparison.Ordinal);
        if (start < 0) return false;
        var body = new StringBuilder(BodyLength);
        var separated = false;
        foreach (var raw in upper.AsSpan(start + Prefix.Length))
        {
            if (raw is ' ' or '-')
            {
                separated = true;
                continue;
            }
            if (body.Length == BodyLength)
            {
                // Text after the code is fine ("JAYC1-… thanks"); a letter or digit glued to it means it was cut or changed.
                if (!separated && char.IsAsciiLetterOrDigit(raw)) return false;
                break;
            }
            separated = false;
            var c = raw switch { 'I' or 'L' => '1', 'O' => '0', _ => raw };
            if (Alphabet.IndexOf(c) < 0) break;
            body.Append(c);
        }
        if (body.Length != BodyLength) return false;
        var value = BigInteger.Zero;
        foreach (var c in body.ToString()) value = (value << 5) | Alphabet.IndexOf(c);
        if (value >> (Bytes * 8) != 0) return false;
        var data = new byte[Bytes];
        var bytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        bytes.CopyTo(data.AsSpan(Bytes - bytes.Length));
        var key = data[..X25519KeyPair.Length];
        if (!CryptographicOperations.FixedTimeEquals(Check(key), data.AsSpan(X25519KeyPair.Length))) return false;
        publicKey = key;
        return true;
    }

    private static byte[] Check(ReadOnlySpan<byte> publicKey)
    {
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData([.. "jay-contact-code-v1"u8, .. publicKey], digest);
        return digest[..2].ToArray();
    }
}
