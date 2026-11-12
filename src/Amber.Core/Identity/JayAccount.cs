using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Parameters;

namespace Amber.Core.Identity;

/// <summary>
/// A Jay account: two signing keys derived from a recovery phrase, an Ed25519 key and an ML-DSA-65 key, used together
/// (<see cref="HybridSignature"/>). The address (<see cref="JayAddress"/>) is a hash of both public keys; no server stores
/// accounts, and the same phrase restores the same account anywhere. The account keys stay offline in day-to-day use: they
/// sign <see cref="DeviceCertificate"/>s that say "this device's keys belong to me", and devices keep using their own keys.
/// </summary>
/// <remarks>
/// Derivation (all hardened, SLIP-0010 with <see cref="Slip10.JayRootKey"/>):
/// m/0'/0' account Ed25519 key · m/1'/n' per-network identities (later) · m/2'/0' inbox key (later) · m/3'/0' friend list backup key
/// (<see cref="DeriveFriendBackupKey"/>) · m/9'/0' the ML-DSA-65 account key (HKDF-SHA256 over the node, info "jay-account-mldsa-v1",
/// gives the 32-byte ML-DSA seed) · m/9'/… otherwise reserved for post-quantum keys.
/// </remarks>
public sealed class JayAccount : IDisposable
{
    public const int SeedLength = 64;
    private static readonly uint[] AccountPath = [0, 0];
    private static readonly uint[] PostQuantumPath = [9, 0];

    private readonly byte[] _seed;
    private readonly byte[] _edPrivate;
    private readonly byte[] _mlDsaSeed;
    private bool _disposed;

    private JayAccount(byte[] seed)
    {
        if (seed.Length != SeedLength) throw new ArgumentException("seed must be 64 bytes", nameof(seed));
        _seed = (byte[])seed.Clone();
        var (key, chain) = Slip10.Derive(_seed, Slip10.JayRootKey, AccountPath);
        CryptographicOperations.ZeroMemory(chain);
        _edPrivate = key;
        PublicKey = new Ed25519PrivateKeyParameters(_edPrivate).GeneratePublicKey().GetEncoded();
        var (node, pqChain) = Slip10.Derive(_seed, Slip10.JayRootKey, PostQuantumPath);
        CryptographicOperations.ZeroMemory(pqChain);
        try { _mlDsaSeed = HKDF.DeriveKey(HashAlgorithmName.SHA256, node, MlDsa65.SeedSize, [], "jay-account-mldsa-v1"u8.ToArray()); }
        finally { CryptographicOperations.ZeroMemory(node); }
        MlDsaPublicKey = MlDsa65.PublicKey(_mlDsaSeed);
        Id = JayAddress.AccountId(PublicKey, MlDsaPublicKey);
        Address = JayAddress.Encode(Id);
    }

    /// <summary>The Ed25519 account public key.</summary>
    public byte[] PublicKey { get; }

    /// <summary>The ML-DSA-65 account public key (1952 bytes).</summary>
    public byte[] MlDsaPublicKey { get; }

    /// <summary>The 32-byte account id: a hash of both public keys (<see cref="JayAddress.AccountId"/>).</summary>
    public byte[] Id { get; }

    /// <summary>The account address, e.g. jay1p…; it holds <see cref="Id"/>.</summary>
    public string Address { get; }

    /// <summary>Validates the phrase (throws <see cref="MnemonicException"/>) and opens the account.</summary>
    public static JayAccount FromMnemonic(IReadOnlyList<string> words, string? passphrase)
    {
        CryptographicOperations.ZeroMemory(Bip39.ToEntropy(words, out _));
        var seed = Bip39.ToSeed(words, passphrase);
        try { return new JayAccount(seed); }
        finally { CryptographicOperations.ZeroMemory(seed); }
    }

    public static JayAccount FromSeed(ReadOnlySpan<byte> seed) => new(seed.ToArray());

    /// <summary>A copy of the seed, for encrypted storage. Zero it after use.</summary>
    internal byte[] ExportSeed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return (byte[])_seed.Clone();
    }

    /// <summary>
    /// The 32-byte symmetric key that encrypts this account's friend list backups: HKDF-SHA256 over the m/3'/0' node with
    /// info "jay-friend-backup-v1", so the key is tied to this one format and a later use of m/3' can't share it. Zero it after use.
    /// </summary>
    internal byte[] DeriveFriendBackupKey()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var (node, chain) = Slip10.Derive(_seed, Slip10.JayRootKey, 3, 0);
        CryptographicOperations.ZeroMemory(chain);
        try { return HKDF.DeriveKey(HashAlgorithmName.SHA256, node, 32, [], "jay-friend-backup-v1"u8.ToArray()); }
        finally { CryptographicOperations.ZeroMemory(node); }
    }

    /// <summary>Signs <paramref name="domain"/> ‖ <paramref name="message"/> with both account keys. Every use has its own domain string so a signature can't be replayed elsewhere.</summary>
    public HybridSignature Sign(ReadOnlySpan<byte> domain, ReadOnlySpan<byte> message)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return HybridSignature.Sign(_edPrivate, _mlDsaSeed, domain, message);
    }

    /// <summary>Signs a certificate for one device's keys (<see cref="DeviceKeys"/>, kept in identity.key).</summary>
    public DeviceCertificate CertifyDevice(DeviceKeys device, string deviceName, int epoch, DateTimeOffset now)
    {
        var unsigned = new DeviceCertificate(PublicKey, MlDsaPublicKey, device.Dh.PublicKey.ToArray(), device.EdPublicKey, device.MlDsaPublicKey,
            DeviceCertificate.CleanName(deviceName), epoch, DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds()), null);
        return unsigned with { Signature = Sign(DeviceCertificate.Domain, unsigned.SignedBytes()) };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CryptographicOperations.ZeroMemory(_seed);
        CryptographicOperations.ZeroMemory(_edPrivate);
        CryptographicOperations.ZeroMemory(_mlDsaSeed);
    }
}

/// <summary>
/// Jay account addresses: "jay1" + Bech32m(version 1 ‖ 32-byte account id), where the id is
/// SHA-256("jay-account-id-v1\n" ‖ Ed25519 public key ‖ ML-DSA-65 public key). The address names the account; checking a
/// signature needs the public keys, which travel in <see cref="DeviceCertificate"/>s.
/// </summary>
public static class JayAddress
{
    public const string Hrp = "jay";
    public const int Version = 1;
    public const int IdLength = 32;

    public static byte[] AccountId(ReadOnlySpan<byte> edPublicKey, ReadOnlySpan<byte> mlDsaPublicKey) =>
        SHA256.HashData([.. "jay-account-id-v1\n"u8, .. edPublicKey, .. mlDsaPublicKey]);

    public static string Encode(ReadOnlySpan<byte> accountId)
    {
        if (accountId.Length != IdLength) throw new ArgumentException("account id must be 32 bytes", nameof(accountId));
        var bits = Bech32m.ConvertBits(accountId, 8, 5, pad: true)!;
        return Bech32m.Encode(Hrp, [(byte)Version, .. bits]);
    }

    /// <summary>Parses an address (case-insensitive, surrounding spaces ignored) into its account id.</summary>
    public static bool TryDecode(string? text, out byte[] accountId)
    {
        accountId = [];
        if (!Bech32m.TryDecode(text?.Trim(), out var hrp, out var data) || hrp != Hrp || data.Length < 2 || data[0] != Version) return false;
        var id = Bech32m.ConvertBits(data.AsSpan(1), 5, 8, pad: false);
        if (id is not { Length: IdLength }) return false;
        accountId = id;
        return true;
    }

    public static bool IsValid(string? text) => TryDecode(text, out _);

    /// <summary>
    /// Reads a version 0 address (the bare Ed25519 account key, from before addresses covered both account keys). Only
    /// for moving saved accounts to their new address; version 0 addresses are not valid anywhere else.
    /// </summary>
    /// <summary>The version 0 form of an account (for reading things saved before version 1 addresses).</summary>
    internal static string EncodeVersion0(ReadOnlySpan<byte> edPublicKey) =>
        Bech32m.Encode(Hrp, [0, .. Bech32m.ConvertBits(edPublicKey, 8, 5, pad: true)!]);

    internal static bool TryDecodeVersion0(string? text, out byte[] edPublicKey)
    {
        edPublicKey = [];
        if (!Bech32m.TryDecode(text?.Trim(), out var hrp, out var data) || hrp != Hrp || data.Length < 2 || data[0] != 0) return false;
        var key = Bech32m.ConvertBits(data.AsSpan(1), 5, 8, pad: false);
        if (key is not { Length: 32 }) return false;
        edPublicKey = key;
        return true;
    }

    /// <summary>The canonical (lowercase) form of an address, or null when it isn't one.</summary>
    public static string? Canonical(string? text) => TryDecode(text, out var id) ? Encode(id) : null;

    /// <summary>Short form for lists: jay1pxy7…k3pf.</summary>
    public static string Short(string address) => address.Length <= 14 ? address : $"{address[..8]}\u2026{address[^4..]}";

    /// <summary>
    /// Four words from the account id (first 44 bits of a SHA-256 over it) that people can read out to each other to confirm
    /// they see the same account. Not secret, and not part of the recovery phrase.
    /// </summary>
    public static string[] CheckWords(ReadOnlySpan<byte> accountId, MnemonicLanguage language)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData([.. "jay-check-words-v2\n"u8, .. accountId], hash);
        var list = Bip39.Wordlist(language);
        var value = BinaryPrimitives.ReadUInt64BigEndian(hash);
        var words = new string[4];
        for (var i = 0; i < 4; i++) words[i] = list[(int)((value >> (64 - 11 * (i + 1))) & 0x7ff)];
        return words;
    }
}

/// <summary>
/// "These device keys belong to account A", signed by both account keys (privacy plan item 7). It carries the account's
/// two public keys (the address is their hash) and the device's three: the X25519 key the handshake proves, and the
/// Ed25519 and ML-DSA-65 keys the device signs live proofs with (<see cref="JayProof"/>). About 7.4 KB, so it travels only
/// in live handshakes, never in 4 KiB mail. <see cref="Epoch"/> lets a restored account void every older certificate at once.
/// </summary>
public sealed record DeviceCertificate(byte[] AccountEd, byte[] AccountMlDsa, byte[] DeviceKey, byte[] DeviceEd, byte[] DeviceMlDsa,
    string DeviceName, int Epoch, DateTimeOffset Issued, HybridSignature? Signature)
{
    public const int MaxNameLength = 48;
    public const int FormatVersion = 2;
    internal static ReadOnlySpan<byte> Domain => "jay-device-cert-v2\n"u8;

    /// <summary>The account address the certificate's account keys hash to.</summary>
    public string Account => JayAddress.Encode(JayAddress.AccountId(AccountEd, AccountMlDsa));

    internal byte[] SignedBytes()
    {
        var name = Encoding.UTF8.GetBytes(DeviceName);
        var buffer = new byte[32 + MlDsa65.PublicKeySize + 32 + 32 + MlDsa65.PublicKeySize + 4 + 8 + name.Length];
        var at = 0;
        foreach (var part in new[] { AccountEd, AccountMlDsa, DeviceKey, DeviceEd, DeviceMlDsa })
        {
            part.CopyTo(buffer, at);
            at += part.Length;
        }
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(at), Epoch);
        BinaryPrimitives.WriteInt64BigEndian(buffer.AsSpan(at + 4), Issued.ToUnixTimeSeconds());
        name.CopyTo(buffer.AsSpan(at + 12));
        return buffer;
    }

    private bool WellFormed => AccountEd.Length == 32 && AccountMlDsa.Length == MlDsa65.PublicKeySize && DeviceKey.Length == 32
        && DeviceEd.Length == 32 && DeviceMlDsa.Length == MlDsa65.PublicKeySize && Epoch >= 0 && DeviceName == CleanName(DeviceName);

    /// <summary>True when both account signatures check out and the certificate names <paramref name="deviceKey"/>.</summary>
    public bool Verify(ReadOnlySpan<byte> deviceKey) =>
        WellFormed && deviceKey.SequenceEqual(DeviceKey) && HybridSignature.Verify(AccountEd, AccountMlDsa, Domain, SignedBytes(), Signature);

    /// <summary>True when this certificate is for exactly these device keys.</summary>
    public bool Names(DeviceKeys device) =>
        DeviceKey.AsSpan().SequenceEqual(device.Dh.PublicKey) && DeviceEd.AsSpan().SequenceEqual(device.EdPublicKey)
        && DeviceMlDsa.AsSpan().SequenceEqual(device.MlDsaPublicKey);

    public System.Text.Json.Nodes.JsonObject ToJson() => new()
    {
        ["v"] = FormatVersion,
        ["account"] = Convert.ToBase64String([.. AccountEd, .. AccountMlDsa]),
        ["device"] = Convert.ToBase64String([.. DeviceKey, .. DeviceEd, .. DeviceMlDsa]),
        ["name"] = DeviceName,
        ["epoch"] = Epoch,
        ["issued"] = Issued.ToUnixTimeSeconds(),
        ["sig"] = Signature is null ? "" : Convert.ToBase64String(Signature.Encode()),
    };

    /// <summary>Reads a certificate; anything but format 2 (older single-signature certificates included) gives null.</summary>
    public static DeviceCertificate? FromJson(System.Text.Json.Nodes.JsonNode? node)
    {
        try
        {
            if (node is not System.Text.Json.Nodes.JsonObject o || (int?)o["v"] != FormatVersion) return null;
            var account = Convert.FromBase64String((string?)o["account"] ?? "");
            var device = Convert.FromBase64String((string?)o["device"] ?? "");
            var name = (string?)o["name"] ?? "";
            var epoch = (int?)o["epoch"] ?? -1;
            var issued = (long?)o["issued"] ?? -1;
            var sig = HybridSignature.Decode(Convert.FromBase64String((string?)o["sig"] ?? ""));
            if (account.Length != 32 + MlDsa65.PublicKeySize || device.Length != 64 + MlDsa65.PublicKeySize || sig is null
                || epoch < 0 || issued < 0 || name.Length > MaxNameLength) return null;
            return new DeviceCertificate(account[..32], account[32..], device[..32], device[32..64], device[64..], name, epoch,
                DateTimeOffset.FromUnixTimeSeconds(issued), sig);
        }
        catch (Exception e) when (e is FormatException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Device names are shown to others: no control characters, trimmed, at most <see cref="MaxNameLength"/> characters.</summary>
    public static string CleanName(string? name)
    {
        var clean = new string((name ?? "").Where(c => !char.IsControl(c) && !char.IsSurrogate(c) && c is not ('‪' or '‫' or '‬' or '‭' or '‮' or '⁦' or '⁧' or '⁨' or '⁩')).ToArray()).Trim();
        return clean.Length > MaxNameLength ? clean[..MaxNameLength].TrimEnd() : clean;
    }
}

/// <summary>
/// What a device sends in a live handshake to show its Jay account: the certificate, plus a fresh device signature over
/// the handshake (<paramref name="context"/> in <see cref="ToJson"/>), so the proof can't be copied into another session
/// and holds even if X25519 alone were broken.
/// </summary>
public sealed record JayProof(DeviceCertificate Certificate, DeviceKeys Keys)
{
    internal static ReadOnlySpan<byte> LiveDomain => "jay-device-live-v1\n"u8;

    public System.Text.Json.Nodes.JsonObject ToJson(ReadOnlySpan<byte> context) => new()
    {
        ["cert"] = Certificate.ToJson(),
        ["proof"] = Convert.ToBase64String(Keys.Sign(LiveDomain, context).Encode()),
    };

    /// <summary>
    /// Checks what someone sent against <paramref name="deviceKey"/> (the key they just proved in the handshake) and this
    /// session's <paramref name="context"/>. Returns the certificate (its <see cref="DeviceCertificate.Account"/> is the
    /// account), or null when anything doesn't check out.
    /// </summary>
    public static DeviceCertificate? Verified(System.Text.Json.Nodes.JsonNode? node, ReadOnlySpan<byte> deviceKey, ReadOnlySpan<byte> context)
    {
        if (node is not System.Text.Json.Nodes.JsonObject o || DeviceCertificate.FromJson(o["cert"]) is not { } cert || !cert.Verify(deviceKey)) return null;
        HybridSignature? proof;
        try { proof = HybridSignature.Decode(Convert.FromBase64String((string?)o["proof"] ?? "")); }
        catch (Exception e) when (e is FormatException or InvalidOperationException) { return null; }
        return HybridSignature.Verify(cert.DeviceEd, cert.DeviceMlDsa, LiveDomain, context, proof) ? cert : null;
    }
}
