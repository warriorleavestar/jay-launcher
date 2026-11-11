using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amber.Core.Localization;

namespace Amber.Core.Multiplayer;

/// <summary>
/// Sealed direct codes: a direct link with no server at all, where the code still shows no IP address.
/// <para>
/// The host's code <c>JAYDS1-</c> carries its addresses (<see cref="DirectAddresses"/>) and a one-use <see cref="JayInvite"/>,
/// encrypted with a key from a passphrase that Jay makes up (16 digits). The passphrase goes to the friend by another
/// way (a call, in person). The friend's reply <c>JAYDR1-</c> carries the friend's addresses, encrypted with the same
/// passphrase, so the reply needs no new passphrase. Someone who sees only a code (a chat app, a screenshot) learns no
/// address. Someone who has both the code and the passphrase does, so they must not travel together.
/// </para>
/// <para>
/// Key: Argon2id(passphrase digits, 16-byte salt from the offer; 64 MiB, 3 passes, 1 lane). Subkeys by HKDF-SHA256:
/// "jay-sealed-direct-v1 offer", "... reply", and a 4-byte key id "... id" that lets the host match a reply to its offer.
/// Encryption: ChaCha20-Poly1305, 12-byte random nonce, associated data = prefix + salt (offer) or prefix + key id (reply).
/// Each code expires 10 minutes after it was made (the time is inside the ciphertext); the invite inside is single use.
/// </para>
/// </summary>
public static class SealedDirect
{
    public const string OfferPrefix = "JAYDS1-";
    public const string ReplyPrefix = "JAYDR1-";
    public const int PassphraseDigits = 16;
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private const int SaltSize = 16, NonceSize = 12, TagSize = 16, KeyIdSize = 4;

    /// <summary>Argon2id cost. Tests lower it.</summary>
    internal static (int MemoryKiB, int Passes) Cost { get; set; } = (64 * 1024, 3);

    /// <summary>A new random passphrase, grouped by four: <c>4827 1930 5561 0742</c>.</summary>
    public static string NewPassphrase()
    {
        var digits = new char[PassphraseDigits];
        for (var i = 0; i < digits.Length; i++) digits[i] = (char)('0' + RandomNumberGenerator.GetInt32(10));
        return string.Join(' ', Enumerable.Range(0, PassphraseDigits / 4).Select(i => new string(digits, i * 4, 4)));
    }

    /// <summary>The passphrase's digits (spaces, dashes and full-width digits allowed); null when it isn't 16 digits.</summary>
    public static string? NormalizePassphrase(string? text)
    {
        if (text is null) return null;
        var digits = new StringBuilder();
        foreach (var c in text.Normalize(NormalizationForm.FormKC))
        {
            if (char.IsAsciiDigit(c)) digits.Append(c);
            else if (!char.IsWhiteSpace(c) && c != '-') return null;
        }
        return digits.Length == PassphraseDigits ? digits.ToString() : null;
    }

    /// <summary>Host: seals an offer. Keep the returned <see cref="SealedOffer"/> to open the friend's reply.</summary>
    public static SealedOffer Seal(DirectAddresses host, JayInvite invite, DateTimeOffset now, string? passphrase = null)
    {
        passphrase ??= NewPassphrase();
        var digits = NormalizePassphrase(passphrase) ?? throw new ArgumentException(null, nameof(passphrase));
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var keys = Keys.Derive(digits, salt);
        var payload = new JsonObject
        {
            ["a"] = host.ToJson(),
            ["i"] = invite.ToString(),
            ["e"] = (now + Lifetime).ToUnixTimeSeconds(),
        };
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var body = Aead(true, keys.Offer, nonce, Encoding.UTF8.GetBytes(payload.ToJsonString()), Ad(OfferPrefix, salt));
        var code = OfferPrefix + Base64Url([.. salt, .. nonce, .. body]);
        return new SealedOffer(code, passphrase, keys, now + Lifetime);
    }

    /// <summary>
    /// Guest: opens an offer with the passphrase. A wrong passphrase, a damaged code and an expired code all throw
    /// <see cref="InvalidDataException"/> with a message for the person.
    /// </summary>
    public static OpenedOffer Open(string text, string passphrase, DateTimeOffset now)
    {
        var blob = Body(text, OfferPrefix) ?? throw new InvalidDataException(L.T("core.mp.sealed.damaged"));
        if (blob.Length < SaltSize + NonceSize + TagSize) throw new InvalidDataException(L.T("core.mp.sealed.damaged"));
        var digits = NormalizePassphrase(passphrase) ?? throw new InvalidDataException(L.T("core.mp.sealed.passphrase_format"));
        var salt = blob[..SaltSize];
        var keys = Keys.Derive(digits, salt);
        JsonObject payload;
        try
        {
            payload = Parse(Aead(false, keys.Offer, blob[SaltSize..(SaltSize + NonceSize)], blob[(SaltSize + NonceSize)..], Ad(OfferPrefix, salt)));
        }
        catch (InvalidDataException)
        {
            keys.Dispose();
            throw new InvalidDataException(L.T("core.mp.sealed.wrong_passphrase"));
        }
        try
        {
            var expires = Expiry(payload, now);
            if (!DirectAddresses.TryParse(payload["a"] as JsonObject, out var host)
                || (payload["i"] as JsonValue)?.TryGetValue<string>(out var inviteText) != true || !JayInvite.TryParse(inviteText, out var invite))
                throw new InvalidDataException(L.T("core.mp.sealed.damaged"));
            return new OpenedOffer(host, invite, keys, expires);
        }
        catch
        {
            keys.Dispose();
            throw;
        }
    }

    /// <summary>Guest: seals the reply with the offer's passphrase (no new passphrase needed).</summary>
    public static string SealReply(OpenedOffer offer, DirectAddresses guest, DateTimeOffset now)
    {
        var payload = new JsonObject
        {
            ["a"] = (guest with { Session = offer.Host.Session }).ToJson(),
            ["e"] = (now + Lifetime).ToUnixTimeSeconds(),
        };
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var keyId = offer.Keys.Id;
        var body = Aead(true, offer.Keys.Reply, nonce, Encoding.UTF8.GetBytes(payload.ToJsonString()), Ad(ReplyPrefix, keyId));
        return ReplyPrefix + Base64Url([.. keyId, .. nonce, .. body]);
    }

    internal static DirectAddresses OpenReply(Keys keys, string text, DateTimeOffset now)
    {
        var blob = Body(text, ReplyPrefix) ?? throw new InvalidDataException(L.T("core.mp.sealed.damaged"));
        if (blob.Length < KeyIdSize + NonceSize + TagSize) throw new InvalidDataException(L.T("core.mp.sealed.damaged"));
        if (!CryptographicOperations.FixedTimeEquals(blob[..KeyIdSize], keys.Id)) throw new InvalidDataException(L.T("core.mp.sealed.other_offer"));
        JsonObject payload;
        try
        {
            payload = Parse(Aead(false, keys.Reply, blob[KeyIdSize..(KeyIdSize + NonceSize)], blob[(KeyIdSize + NonceSize)..], Ad(ReplyPrefix, blob[..KeyIdSize])));
        }
        catch (InvalidDataException) { throw new InvalidDataException(L.T("core.mp.sealed.damaged")); }
        Expiry(payload, now);
        return DirectAddresses.TryParse(payload["a"] as JsonObject, out var guest) ? guest : throw new InvalidDataException(L.T("core.mp.sealed.damaged"));
    }

    /// <summary>
    /// Seals any small payload with a passphrase, for other codes that must not show an address (a Jay server invite
    /// for a server with no domain). Same construction as an offer: Argon2id over the digits with a fresh salt, then
    /// HKDF-SHA256 with <paramref name="info"/>, then ChaCha20-Poly1305 with associated data = prefix + salt.
    /// Code = prefix + base64url(salt | nonce | ciphertext).
    /// </summary>
    internal static string SealBlob(string prefix, string info, ReadOnlySpan<byte> plain, string passphrase)
    {
        var digits = NormalizePassphrase(passphrase) ?? throw new ArgumentException(null, nameof(passphrase));
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = BlobKey(digits, salt, info);
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(NonceSize);
            var body = Aead(true, key, nonce, plain.ToArray(), Ad(prefix, salt));
            return prefix + Base64Url([.. salt, .. nonce, .. body]);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>Opens a <see cref="SealBlob"/> code. Throws <see cref="InvalidDataException"/> with a message for the person.</summary>
    internal static byte[] OpenBlob(string? text, string prefix, string info, string? passphrase)
    {
        var blob = Body(text, prefix) ?? throw new InvalidDataException(L.T("core.mp.sealed.damaged"));
        if (blob.Length < SaltSize + NonceSize + TagSize) throw new InvalidDataException(L.T("core.mp.sealed.damaged"));
        var digits = NormalizePassphrase(passphrase) ?? throw new InvalidDataException(L.T("core.mp.sealed.passphrase_format"));
        var salt = blob[..SaltSize];
        var key = BlobKey(digits, salt, info);
        try
        {
            return Aead(false, key, blob[SaltSize..(SaltSize + NonceSize)], blob[(SaltSize + NonceSize)..], Ad(prefix, salt));
        }
        catch (InvalidDataException)
        {
            throw new InvalidDataException(L.T("core.mp.sealed.wrong_passphrase"));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static byte[] BlobKey(string digits, byte[] salt, string info)
    {
        var master = Keys.Master(digits, salt);
        try { return HKDF.Expand(HashAlgorithmName.SHA256, master, 32, Encoding.ASCII.GetBytes(info)); }
        finally { CryptographicOperations.ZeroMemory(master); }
    }

    private static DateTimeOffset Expiry(JsonObject payload, DateTimeOffset now)
    {
        if ((payload["e"] as JsonValue)?.TryGetValue<long>(out var seconds) != true) throw new InvalidDataException(L.T("core.mp.sealed.damaged"));
        var expires = DateTimeOffset.FromUnixTimeSeconds(seconds);
        // A clock a little behind on the other side is fine; a code from the far future is not.
        if (now > expires || expires - now > Lifetime + TimeSpan.FromMinutes(5)) throw new InvalidDataException(L.T("core.mp.sealed.expired"));
        return expires;
    }

    private static JsonObject Parse(byte[] plain)
    {
        try { return JsonNode.Parse(plain) as JsonObject ?? throw new InvalidDataException(); }
        catch (JsonException) { throw new InvalidDataException(); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    /// <summary>The bytes after the prefix (prefix found anywhere in the text, any case); null when absent or not base64url.</summary>
    private static byte[]? Body(string? text, string prefix)
    {
        if (text is null) return null;
        var at = text.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return null;
        var rest = text[(at + prefix.Length)..];
        var end = 0;
        while (end < rest.Length && (char.IsAsciiLetterOrDigit(rest[end]) || rest[end] is '-' or '_')) end++;
        var b64 = rest[..end].Replace('-', '+').Replace('_', '/');
        b64 += new string('=', (4 - b64.Length % 4) % 4);
        try { return Convert.FromBase64String(b64); }
        catch (FormatException) { return null; }
    }

    private static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Ad(string prefix, byte[] extra) => [.. Encoding.ASCII.GetBytes(prefix), .. extra];

    private static byte[] Aead(bool encrypt, byte[] key, byte[] nonce, byte[] input, byte[] ad)
    {
        var cipher = new Org.BouncyCastle.Crypto.Modes.ChaCha20Poly1305();
        cipher.Init(encrypt, new Org.BouncyCastle.Crypto.Parameters.AeadParameters(
            new Org.BouncyCastle.Crypto.Parameters.KeyParameter(key), TagSize * 8, nonce, ad));
        var output = new byte[cipher.GetOutputSize(input.Length)];
        try
        {
            var n = cipher.ProcessBytes(input, 0, input.Length, output, 0);
            cipher.DoFinal(output, n);
            return output;
        }
        catch (Org.BouncyCastle.Crypto.InvalidCipherTextException) { throw new InvalidDataException(); }
    }

    /// <summary>The subkeys from one passphrase. Zeroed on dispose.</summary>
    public sealed class Keys : IDisposable
    {
        internal byte[] Offer { get; }
        internal byte[] Reply { get; }
        internal byte[] Id { get; }

        private Keys(byte[] offer, byte[] reply, byte[] id) => (Offer, Reply, Id) = (offer, reply, id);

        internal static Keys Derive(string digits, byte[] salt)
        {
            var master = Master(digits, salt);
            try
            {
                return new Keys(
                    HKDF.Expand(HashAlgorithmName.SHA256, master, 32, "jay-sealed-direct-v1 offer"u8.ToArray()),
                    HKDF.Expand(HashAlgorithmName.SHA256, master, 32, "jay-sealed-direct-v1 reply"u8.ToArray()),
                    HKDF.Expand(HashAlgorithmName.SHA256, master, KeyIdSize, "jay-sealed-direct-v1 id"u8.ToArray()));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(master);
            }
        }

        /// <summary>Argon2id(digits, salt): the 32-byte master the subkeys come from. The caller zeroes it.</summary>
        internal static byte[] Master(string digits, byte[] salt)
        {
            var generator = new Org.BouncyCastle.Crypto.Generators.Argon2BytesGenerator();
            generator.Init(new Org.BouncyCastle.Crypto.Parameters.Argon2Parameters.Builder(Org.BouncyCastle.Crypto.Parameters.Argon2Parameters.Argon2id)
                .WithVersion(Org.BouncyCastle.Crypto.Parameters.Argon2Parameters.Version13)
                .WithSalt(salt).WithMemoryAsKB(Cost.MemoryKiB).WithIterations(Cost.Passes).WithParallelism(1).Build());
            var password = Encoding.ASCII.GetBytes(digits);
            var master = new byte[32];
            try
            {
                generator.GenerateBytes(password, master);
                return master;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(password);
            }
        }

        public void Dispose()
        {
            CryptographicOperations.ZeroMemory(Offer);
            CryptographicOperations.ZeroMemory(Reply);
        }
    }
}

/// <summary>Host side of a sealed offer: the code to send, the passphrase to tell by another way, and the keys to open the reply.</summary>
public sealed class SealedOffer(string code, string passphrase, SealedDirect.Keys keys, DateTimeOffset expires) : IDisposable
{
    public string Code { get; } = code;
    public string Passphrase { get; } = passphrase;
    public DateTimeOffset Expires { get; } = expires;

    /// <summary>Opens the friend's reply. Throws <see cref="InvalidDataException"/> (damaged, expired, for another offer).</summary>
    public DirectAddresses OpenReply(string text, DateTimeOffset now) => SealedDirect.OpenReply(keys, text, now);

    /// <summary>Never prints the code or the passphrase.</summary>
    public override string ToString() => "SealedOffer";

    public void Dispose() => keys.Dispose();
}

/// <summary>Guest side: what the offer held, and the keys to seal the reply.</summary>
public sealed class OpenedOffer(DirectAddresses host, JayInvite invite, SealedDirect.Keys keys, DateTimeOffset expires) : IDisposable
{
    public DirectAddresses Host { get; } = host;
    public JayInvite Invite { get; } = invite;
    public DateTimeOffset Expires { get; } = expires;
    internal SealedDirect.Keys Keys { get; } = keys;

    public override string ToString() => "OpenedOffer";

    public void Dispose() => Keys.Dispose();
}
