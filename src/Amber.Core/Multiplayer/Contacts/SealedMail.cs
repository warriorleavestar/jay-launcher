using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Multiplayer.Contacts;

/// <summary>
/// Mail between two Jay identities, left at a room directory for the recipient to pick up.
/// <para>
/// Two layers. Inside is <see cref="PrivateSeal"/> from the sender to the recipient, which proves who sent it (it uses
/// the sender's static key). Around it is an anonymous layer to the recipient (a fresh X25519 key, HKDF-SHA256,
/// AES-256-GCM) that also hides the sender's public key, so the directory sees neither the sender nor the content: only
/// an opaque blob, the recipient's mailbox id for the day and a hash for deleting it later.
/// </para>
/// <para>Format: [version 2][ephemeral public key 32][nonce 12][ciphertext of (sender public key 32 ‖ inner seal)][tag 16].</para>
/// </summary>
public static class SealedMail
{
    public const byte Version = 2;
    private const int Header = 1 + X25519KeyPair.Length + 12;
    private const int TagLength = 16;
    /// <summary>Largest blob a directory keeps. A Jay invite with its envelope is a few hundred bytes.</summary>
    public const int MaxBlob = 4096;

    public static byte[] Seal(X25519KeyPair me, ReadOnlySpan<byte> recipient, MailMessage message)
    {
        var inner = PrivateSeal.Seal(me, recipient, message.ToBytes());
        byte[] plain = [.. me.PublicKey, .. inner];
        using var ephemeral = X25519KeyPair.Generate();
        var key = Derive(ephemeral.Dh(recipient), ephemeral.PublicKey, recipient);
        try
        {
            var output = new byte[Header + plain.Length + TagLength];
            if (output.Length > MaxBlob) throw new InvalidDataException("mail too large");
            output[0] = Version;
            ephemeral.PublicKey.CopyTo(output, 1);
            RandomNumberGenerator.Fill(output.AsSpan(1 + X25519KeyPair.Length, 12));
            using var aes = new AesGcm(key, TagLength);
            aes.Encrypt(output.AsSpan(1 + X25519KeyPair.Length, 12), plain, output.AsSpan(Header, plain.Length), output.AsSpan(Header + plain.Length), output.AsSpan(0, Header));
            return output;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    /// <summary>Opens mail addressed to <paramref name="me"/>. Null when it isn't for me, was changed, or isn't well formed.</summary>
    public static (byte[] Sender, MailMessage Message)? Open(X25519KeyPair me, ReadOnlySpan<byte> blob)
    {
        if (blob.Length < Header + TagLength + X25519KeyPair.Length + PrivateSeal.Overhead || blob.Length > MaxBlob || blob[0] != Version) return null;
        byte[]? key = null;
        var plain = new byte[blob.Length - Header - TagLength];
        try
        {
            var ephemeral = blob.Slice(1, X25519KeyPair.Length);
            key = Derive(me.Dh(ephemeral), ephemeral, me.PublicKey);
            using (var aes = new AesGcm(key, TagLength))
                aes.Decrypt(blob.Slice(1 + X25519KeyPair.Length, 12), blob.Slice(Header, plain.Length), blob[^TagLength..], plain, blob[..Header]);
            var sender = plain[..X25519KeyPair.Length];
            if (PrivateSeal.Open(me, sender, plain.AsSpan(X25519KeyPair.Length)) is not { } inner) return null;
            if (MailMessage.FromBytes(inner) is not { } message) return null;
            // The sender sealed it to this key; a copy re-addressed to someone else doesn't open, but check the field too.
            if (!CryptographicOperations.FixedTimeEquals(message.To, me.PublicKey)) return null;
            return (sender, message);
        }
        catch (CryptographicException)
        {
            return null;
        }
        finally
        {
            if (key is not null) CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    private static byte[] Derive(byte[] shared, ReadOnlySpan<byte> ephemeral, ReadOnlySpan<byte> recipient)
    {
        try { return HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, "jay-mail"u8.ToArray(), [.. "jay-mail-v1"u8, .. ephemeral, .. recipient]); }
        finally { CryptographicOperations.ZeroMemory(shared); }
    }

    /// <summary>
    /// The recipient's mailbox for a UTC day: 16 bytes of SHA-256 over the public key and the day number, hex.
    /// Anyone holding the contact code can work it out (that is how mail gets addressed); it changes every day so a
    /// directory can't follow one mailbox for long. Recipients check yesterday's, today's and tomorrow's.
    /// </summary>
    public static string MailboxId(ReadOnlySpan<byte> publicKey, long day) =>
        Convert.ToHexStringLower(SHA256.HashData([.. "jay-mailbox-v1"u8, .. publicKey, .. Encoding.ASCII.GetBytes(":" + day.ToString(CultureInfo.InvariantCulture))])[..16]);

    public static long Day(DateTimeOffset time) => time.ToUnixTimeSeconds() / 86400;

    public static string[] MailboxesToCheck(ReadOnlySpan<byte> publicKey, DateTimeOffset now)
    {
        var day = Day(now);
        return [MailboxId(publicKey, day - 1), MailboxId(publicKey, day), MailboxId(publicKey, day + 1)];
    }

    /// <summary>The hash a directory keeps so only the recipient (who learns the token inside the mail) can delete it.</summary>
    public static string DropHash(ReadOnlySpan<byte> token) => Convert.ToHexStringLower(SHA256.HashData([.. "jay-mail-drop-v1"u8, .. token]));
}

/// <summary>Kinds of mail.</summary>
public static class MailKind
{
    /// <summary>"Add me": the sender's name and its commitment to a nonce (<see cref="ContactHandshake"/>).</summary>
    public const string Hello = "hello";
    /// <summary>Answer to hello: the responder's name and nonce.</summary>
    public const string HelloReply = "hello-r";
    /// <summary>Last step: the initiator reveals its nonce. Both sides can now show the emoji.</summary>
    public const string HelloNonce = "hello-n";
    /// <summary>A room invite (a JAY2 invite code, the host's key, and the room's name), bound to the recipient.</summary>
    public const string Invite = "invite";
    /// <summary>"Got it": a signed receipt for a message id. No receipt means the mail may have been held back.</summary>
    public const string Receipt = "receipt";

    public static bool IsKnown(string? kind) => kind is Hello or HelloReply or HelloNonce or Invite or Receipt;
}

/// <summary>
/// One piece of mail: kind, a random id (for receipts and to drop replays), the time it was sent, the recipient's key,
/// the token that lets the recipient delete it from the directory, and the kind's own fields.
/// </summary>
public sealed record MailMessage(string Kind, byte[] Id, DateTimeOffset Sent, byte[] To, byte[] DropToken, JsonObject Body)
{
    public const int IdLength = 16;
    public const int TokenLength = 16;

    public string IdHex => Convert.ToHexStringLower(Id);

    public static MailMessage Create(string kind, ReadOnlySpan<byte> to, JsonObject body, DateTimeOffset now) =>
        new(kind, RandomNumberGenerator.GetBytes(IdLength), DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds()), to.ToArray(),
            RandomNumberGenerator.GetBytes(TokenLength), body);

    public byte[] ToBytes() => Encoding.UTF8.GetBytes(new JsonObject
    {
        ["t"] = Kind,
        ["id"] = IdHex,
        ["ts"] = Sent.ToUnixTimeSeconds(),
        ["to"] = Convert.ToHexStringLower(To),
        ["drop"] = Convert.ToHexStringLower(DropToken),
        ["b"] = Body.DeepClone(),
    }.ToJsonString());

    public static MailMessage? FromBytes(byte[] bytes)
    {
        try
        {
            if (JsonNode.Parse(bytes) is not JsonObject o) return null;
            var kind = o["t"]?.GetValue<string>();
            if (!MailKind.IsKnown(kind)) return null;
            var id = Hex(o["id"], IdLength);
            var to = Hex(o["to"], X25519KeyPair.Length);
            var drop = Hex(o["drop"], TokenLength);
            var ts = o["ts"]?.GetValue<long>();
            if (id is null || to is null || drop is null || ts is not { } seconds || o["b"] is not JsonObject body) return null;
            return new MailMessage(kind!, id, DateTimeOffset.FromUnixTimeSeconds(seconds), to, drop, (JsonObject)body.DeepClone());
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    internal static byte[]? Hex(JsonNode? node, int length)
    {
        var text = node?.GetValue<string>();
        if (text is null || text.Length != length * 2 || !text.All(char.IsAsciiHexDigit)) return null;
        return Convert.FromHexString(text);
    }

    public override string ToString() => $"MailMessage({Kind})";
}
