using System.Security.Cryptography;
using System.Text;
using Amber.Core.Localization;

namespace Amber.Core.Multiplayer.Public;

/// <summary>
/// A trust key file (a trustee's, a network owner's or a directory root's): the 32-byte seed of a <see cref="TrustSigner"/>,
/// normally sealed with a passphrase so a lost USB stick gives nothing away.
/// <para>
/// Sealed: one line <c>jay-key-v1:</c> + base64(salt 16 ‖ nonce 12 ‖ ciphertext 32 ‖ tag 16). The key is Argon2id
/// (256 MiB, 3 passes, 1 lane) of the passphrase and salt; the seed is encrypted with ChaCha20-Poly1305, the
/// <c>jay-key-v1</c> line prefix as associated data. Unsealed: the seed as one line of base64 (older files, test keys).
/// </para>
/// </summary>
public static class TrustKeyFile
{
    public const string SealedPrefix = "jay-key-v1:";
    private const int SaltSize = 16, NonceSize = 12, TagSize = 16, SeedSize = 32;

    /// <summary>Argon2id cost. Tests lower it; files record nothing about it, so it must stay the same for real keys.</summary>
    internal static (int MemoryKiB, int Passes) Cost { get; set; } = (256 * 1024, 3);

    /// <summary>Whether the file's text is sealed with a passphrase.</summary>
    public static bool IsSealed(string text) => text.TrimStart().StartsWith(SealedPrefix, StringComparison.Ordinal);

    /// <summary>The file's text for <paramref name="seed"/>: sealed with <paramref name="passphrase"/>, or plain when it is null.</summary>
    public static string Format(ReadOnlySpan<byte> seed, string? passphrase)
    {
        if (seed.Length != SeedSize) throw new ArgumentException(null, nameof(seed));
        if (passphrase is null) return Convert.ToBase64String(seed) + "\n";
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var key = Stretch(passphrase, salt);
        try
        {
            var sealedSeed = Aead(true, key, nonce, seed.ToArray());
            return SealedPrefix + Convert.ToBase64String([.. salt, .. nonce, .. sealedSeed]) + "\n";
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    /// <summary>
    /// The seed in a key file's text. <paramref name="passphrase"/> is asked only for a sealed file. A wrong passphrase or
    /// a damaged file throws <see cref="InvalidDataException"/> (the same message for both).
    /// </summary>
    public static byte[] ReadSeed(string text, Func<string> passphrase)
    {
        text = text.Trim();
        if (!IsSealed(text))
        {
            try { return Convert.FromBase64String(text) is { Length: SeedSize } seed ? seed : throw new FormatException(); }
            catch (FormatException) { throw new InvalidDataException(L.T("core.mp.network.bad_key")); }
        }
        byte[] blob;
        try { blob = Convert.FromBase64String(text[SealedPrefix.Length..]); }
        catch (FormatException) { throw new InvalidDataException(L.T("core.mp.network.bad_key")); }
        if (blob.Length != SaltSize + NonceSize + SeedSize + TagSize) throw new InvalidDataException(L.T("core.mp.network.bad_key"));
        var key = Stretch(passphrase(), blob[..SaltSize]);
        try { return Aead(false, key, blob[SaltSize..(SaltSize + NonceSize)], blob[(SaltSize + NonceSize)..]); }
        catch (Org.BouncyCastle.Crypto.InvalidCipherTextException) { throw new InvalidDataException(L.T("core.mp.network.bad_passphrase")); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    /// <summary>Reads a key file's text into a signer (see <see cref="ReadSeed"/>).</summary>
    public static TrustSigner Read(string text, Func<string> passphrase)
    {
        var seed = ReadSeed(text, passphrase);
        try { return TrustSigner.FromSeed(Convert.ToBase64String(seed)); }
        finally { CryptographicOperations.ZeroMemory(seed); }
    }

    private static byte[] Stretch(string passphrase, byte[] salt)
    {
        var generator = new Org.BouncyCastle.Crypto.Generators.Argon2BytesGenerator();
        generator.Init(new Org.BouncyCastle.Crypto.Parameters.Argon2Parameters.Builder(Org.BouncyCastle.Crypto.Parameters.Argon2Parameters.Argon2id)
            .WithVersion(Org.BouncyCastle.Crypto.Parameters.Argon2Parameters.Version13)
            .WithSalt(salt).WithMemoryAsKB(Cost.MemoryKiB).WithIterations(Cost.Passes).WithParallelism(1).Build());
        var password = Encoding.UTF8.GetBytes(passphrase.Normalize(NormalizationForm.FormC));
        var key = new byte[32];
        try { generator.GenerateBytes(password, key); }
        finally { CryptographicOperations.ZeroMemory(password); }
        return key;
    }

    private static byte[] Aead(bool encrypt, byte[] key, byte[] nonce, byte[] input)
    {
        var cipher = new Org.BouncyCastle.Crypto.Modes.ChaCha20Poly1305();
        cipher.Init(encrypt, new Org.BouncyCastle.Crypto.Parameters.AeadParameters(
            new Org.BouncyCastle.Crypto.Parameters.KeyParameter(key), TagSize * 8, nonce, Encoding.ASCII.GetBytes(SealedPrefix)));
        var output = new byte[cipher.GetOutputSize(input.Length)];
        var n = cipher.ProcessBytes(input, 0, input.Length, output, 0);
        cipher.DoFinal(output, n);
        return output;
    }
}

/// <summary>
/// The hand-copied backup of a 32-byte seed: Crockford base32 (0-9 and A-Z without I L O U, any case), two lines of
/// 26 characters plus 2 check characters each. The check is the first 10 bits of SHA-256("jay-paper-v1|line|chars"),
/// so a typo is caught and the line it is on is named. Same format as the offline scripts' paper page.
/// </summary>
public static class PaperKey
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>The two lines, grouped by four: <c>XXXX XXXX XXXX XXXX XXXX XXXX XX - CC</c>.</summary>
    public static string[] ToLines(ReadOnlySpan<byte> seed)
    {
        if (seed.Length != 32) throw new ArgumentException(null, nameof(seed));
        var all = Encode(seed);
        return [Line(1, all[..26]), Line(2, all[26..])];

        static string Line(int n, string part) =>
            string.Join(' ', Enumerable.Range(0, (part.Length + 3) / 4).Select(i => part.Substring(i * 4, Math.Min(4, part.Length - i * 4)))) + " - " + Check(n, part);
    }

    /// <summary>The seed from two typed lines (spaces, dashes and case ignored). Throws naming the bad line.</summary>
    public static byte[] FromLines(string line1, string line2)
    {
        var parts = new StringBuilder();
        var n = 0;
        foreach (var line in new[] { line1, line2 })
        {
            n++;
            var t = Normalize(line);
            if (t.Length != 28) throw new InvalidDataException(L.F("core.mp.paper.length", n, t.Length));
            if (Check(n, t[..26]) != t[26..]) throw new InvalidDataException(L.F("core.mp.paper.check", n));
            parts.Append(t[..26]);
        }
        return Decode(parts.ToString(), 32);
    }

    private static string Normalize(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s.ToUpperInvariant())
        {
            var ch = c switch { 'O' => '0', 'I' or 'L' => '1', _ => c };
            if (ch is >= '0' and <= '9' or >= 'A' and <= 'Z') sb.Append(ch);
        }
        return sb.ToString();
    }

    private static string Check(int index, string chars)
    {
        var h = SHA256.HashData(Encoding.ASCII.GetBytes($"jay-paper-v1|{index}|{chars}"));
        var v = (h[0] << 2) | (h[1] >> 6);
        return $"{Alphabet[(v >> 5) & 31]}{Alphabet[v & 31]}";
    }

    private static string Encode(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var b in bytes)
        {
            buffer = ((buffer << 8) | b) & 0xFFFF;
            bits += 8;
            while (bits >= 5) { bits -= 5; sb.Append(Alphabet[(buffer >> bits) & 31]); }
        }
        if (bits > 0) sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    private static byte[] Decode(string text, int count)
    {
        var output = new byte[count];
        int buffer = 0, bits = 0, i = 0;
        foreach (var c in text)
        {
            var v = Alphabet.IndexOf(c);
            if (v < 0) throw new InvalidDataException(L.T("core.mp.network.bad_key"));
            buffer = ((buffer << 5) | v) & 0xFFFF;
            bits += 5;
            if (bits >= 8) { bits -= 8; if (i < count) output[i] = (byte)((buffer >> bits) & 255); i++; }
        }
        return output;
    }
}
