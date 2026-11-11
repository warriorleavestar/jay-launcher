using System.Security.Cryptography;

namespace Amber.Core.Multiplayer.Public;

/// <summary>
/// One-shot rendezvous records on the room directory (ops <c>rv.put</c> / <c>rv.take</c>), so a code can carry a random
/// token instead of a public address. The holder of the token derives a lookup id and a seal key from it; the publisher
/// puts the sealed record under the id, the reader takes it once and the directory forgets it. The directory sees only
/// the id and an opaque blob, never the token, so it can't open the record or link it to a code it hasn't seen.
/// </summary>
public static class Rendezvous
{
    /// <summary>Shortest token accepted (128 bits).</summary>
    public const int MinTokenLength = 16;

    /// <summary>Length of the lookup id the directory indexes by.</summary>
    public const int IdLength = 32;

    /// <summary>Largest sealed record the directory keeps (nonce + ciphertext + tag).</summary>
    public const int MaxSealed = 2048;

    /// <summary>Largest plaintext that still fits in <see cref="MaxSealed"/>.</summary>
    public const int MaxPlaintext = MaxSealed - NonceLength - TagLength;

    /// <summary>Longest a record may live on the directory.</summary>
    public static readonly TimeSpan MaxTtl = TimeSpan.FromMinutes(10);

    private const int NonceLength = 12;
    private const int TagLength = 16;
    private static readonly byte[] IdInfo = "jay-rv-id-v1"u8.ToArray();
    private static readonly byte[] SealInfo = "jay-rv-seal-v1"u8.ToArray();

    /// <summary>The directory lookup id for <paramref name="token"/>.</summary>
    public static byte[] Id(ReadOnlySpan<byte> token)
    {
        CheckToken(token);
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, token.ToArray(), IdLength, [], IdInfo);
    }

    /// <summary>Seals <paramref name="plaintext"/> for whoever holds <paramref name="token"/>. The id is bound as associated data.</summary>
    public static byte[] Seal(ReadOnlySpan<byte> token, ReadOnlySpan<byte> plaintext)
    {
        if (plaintext.Length > MaxPlaintext) throw new ArgumentException("Rendezvous record too large", nameof(plaintext));
        var id = Id(token);
        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, token.ToArray(), 32, [], SealInfo);
        try
        {
            using var aead = new ChaCha20Poly1305(key);
            var output = new byte[NonceLength + plaintext.Length + TagLength];
            RandomNumberGenerator.Fill(output.AsSpan(0, NonceLength));
            aead.Encrypt(output.AsSpan(0, NonceLength), plaintext, output.AsSpan(NonceLength, plaintext.Length), output.AsSpan(NonceLength + plaintext.Length), id);
            return output;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>Opens a record sealed by <see cref="Seal"/>. Throws <see cref="CryptographicException"/> when it was altered or isn't for this token.</summary>
    public static byte[] Open(ReadOnlySpan<byte> token, ReadOnlySpan<byte> sealedRecord)
    {
        if (sealedRecord.Length < NonceLength + TagLength || sealedRecord.Length > MaxSealed) throw new CryptographicException("Rendezvous record size");
        var id = Id(token);
        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, token.ToArray(), 32, [], SealInfo);
        try
        {
            using var aead = new ChaCha20Poly1305(key);
            var length = sealedRecord.Length - NonceLength - TagLength;
            var plaintext = new byte[length];
            aead.Decrypt(sealedRecord[..NonceLength], sealedRecord.Slice(NonceLength, length), sealedRecord[(NonceLength + length)..], plaintext, id);
            return plaintext;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static void CheckToken(ReadOnlySpan<byte> token)
    {
        if (token.Length < MinTokenLength) throw new ArgumentException("Rendezvous token too short", nameof(token));
    }
}
