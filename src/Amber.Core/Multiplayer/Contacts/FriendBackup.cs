using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amber.Core.Identity;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Multiplayer.Contacts;

/// <summary>
/// A friend list backup that only the same Jay account can open: verified contacts (key, name, Jay account, when added),
/// encrypted with AES-256-GCM under a key derived from the account (m/3'/0'). The file can sit anywhere, a USB stick or
/// a cloud folder: without the recovery phrase it is noise, and any change to it is refused.
/// <para>
/// Layout: "JAYFB1\n" ‖ nonce (12) ‖ ciphertext ‖ tag (16). The associated data is the magic and the account's public key,
/// so a backup made by one account can't be passed off as another's. The plaintext is padded to a multiple of
/// <see cref="PadBlock"/> bytes so the file size doesn't tell how many contacts there are.
/// </para>
/// </summary>
public static class FriendBackup
{
    public const string FileExtension = ".jayfriends";
    private static ReadOnlySpan<byte> Magic => "JAYFB1\n"u8;
    private const int NonceLength = 12;
    private const int TagLength = 16;
    /// <summary>Generous for any real friend list, small enough that a hostile file can't eat memory.</summary>
    public const int MaxSize = 4 * 1024 * 1024;
    public const int PadBlock = 4096;

    /// <summary>
    /// Encrypts the verified contacts in <paramref name="contacts"/> (unverified ones are left out) and the devices I removed,
    /// so a restore doesn't let those back in without the emoji. Null when the list is too big for <see cref="MaxSize"/>.
    /// </summary>
    public static byte[]? Export(JayAccount account, IEnumerable<Contact> contacts, DateTimeOffset now, IEnumerable<RemovedDevice>? removed = null)
    {
        var list = new JsonArray();
        foreach (var c in contacts.Where(c => c.Verified))
        {
            var item = new JsonObject
            {
                ["pub"] = Convert.ToHexStringLower(c.PublicKey),
                ["name"] = c.Name,
                ["added"] = c.Added.ToUnixTimeSeconds(),
            };
            if (c.JayAccount is not null) item["jay"] = c.JayAccount;
            if (c.DeviceName.Length > 0) item["device"] = c.DeviceName;
            if (c.Epoch > 0) item["epoch"] = c.Epoch;
            list.Add((JsonNode)item);
        }
        var gone = new JsonArray();
        foreach (var r in removed ?? [])
        {
            var item = new JsonObject { ["pub"] = Convert.ToHexStringLower(r.PublicKey) };
            if (r.JayAccount is not null) item["jay"] = r.JayAccount;
            gone.Add((JsonNode)item);
        }
        var root = new JsonObject { ["v"] = 1, ["account"] = account.Address, ["created"] = now.ToUnixTimeSeconds(), ["contacts"] = list, ["removed"] = gone, ["pad"] = "" };
        var bare = Encoding.UTF8.GetByteCount(root.ToJsonString());
        root["pad"] = new string('0', (PadBlock - bare % PadBlock) % PadBlock);
        var plain = Encoding.UTF8.GetBytes(root.ToJsonString());
        if (Magic.Length + NonceLength + plain.Length + TagLength > MaxSize)
        {
            CryptographicOperations.ZeroMemory(plain);
            return null;
        }
        var key = account.DeriveFriendBackupKey();
        try
        {
            var output = new byte[Magic.Length + NonceLength + plain.Length + TagLength];
            Magic.CopyTo(output);
            var nonce = output.AsSpan(Magic.Length, NonceLength);
            RandomNumberGenerator.Fill(nonce);
            using var aes = new AesGcm(key, TagLength);
            aes.Encrypt(nonce, plain, output.AsSpan(Magic.Length + NonceLength, plain.Length), output.AsSpan(output.Length - TagLength), Aad(account.Id));
            return output;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    /// <summary>True when the bytes look like a friend backup (it may still belong to another account).</summary>
    public static bool LooksLikeBackup(ReadOnlySpan<byte> data) => data.Length >= Magic.Length + NonceLength + TagLength && data.StartsWith(Magic);

    /// <summary>
    /// Opens a backup with this account. Null when it was made by another account, was changed, or isn't a backup at all.
    /// The contacts come back verified: they were verified when the backup was made, and only this account could have made it.
    /// </summary>
    public static FriendBackupContents? Import(JayAccount account, ReadOnlySpan<byte> data)
    {
        if (!LooksLikeBackup(data) || data.Length > MaxSize) return null;
        var cipher = data[(Magic.Length + NonceLength)..^TagLength];
        var plain = new byte[cipher.Length];
        var key = account.DeriveFriendBackupKey();
        try
        {
            using var aes = new AesGcm(key, TagLength);
            aes.Decrypt(data.Slice(Magic.Length, NonceLength), cipher, data[^TagLength..], plain, Aad(account.Id));
            if (JsonNode.Parse(plain) is not JsonObject root || (int?)root["v"] != 1 || (string?)root["account"] != account.Address) return null;
            var result = new List<Contact>();
            foreach (var item in root["contacts"] as JsonArray ?? [])
            {
                if (item is not JsonObject o || MailMessage.Hex(o["pub"], X25519KeyPair.Length) is not { } pub) continue;
                if (result.Any(c => c.PublicKey.AsSpan().SequenceEqual(pub))) continue;
                var jay = (string?)o["jay"];
                result.Add(new Contact(pub, KnockProtocol.SanitizeName((string?)o["name"]), Verified: true,
                    DateTimeOffset.FromUnixTimeSeconds(Math.Max(0, (long?)o["added"] ?? 0)))
                {
                    JayAccount = JayAddress.TryDecode(jay, out var k) ? JayAddress.Encode(k) : null,
                    DeviceName = DeviceCertificate.CleanName((string?)o["device"]),
                    Epoch = Math.Max(0, (int?)o["epoch"] ?? 0),
                });
            }
            var removed = new List<RemovedDevice>();
            foreach (var item in root["removed"] as JsonArray ?? [])
            {
                if (item is not JsonObject o || MailMessage.Hex(o["pub"], X25519KeyPair.Length) is not { } pub) continue;
                var jay = (string?)o["jay"];
                removed.Add(new RemovedDevice(pub, JayAddress.TryDecode(jay, out var k) ? JayAddress.Encode(k) : null));
            }
            var created = DateTimeOffset.FromUnixTimeSeconds(Math.Clamp((long?)root["created"] ?? 0, 0, 253402300799));
            return new FriendBackupContents(result, removed, created);
        }
        catch (Exception e) when (e is CryptographicException or JsonException or InvalidOperationException or FormatException or ArgumentOutOfRangeException)
        {
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    private static byte[] Aad(ReadOnlySpan<byte> accountId) => [.. Magic, .. accountId];
}

/// <summary>What a friend list backup holds: the verified contacts, the devices I had removed, and when it was made.</summary>
public sealed record FriendBackupContents(IReadOnlyList<Contact> Contacts, IReadOnlyList<RemovedDevice> Removed, DateTimeOffset Created);
