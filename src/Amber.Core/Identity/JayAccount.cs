using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace Amber.Core.Identity;

/// <summary>
/// A Jay account: an Ed25519 key pair derived from a recovery phrase. The public key is the account address
/// (<see cref="JayAddress"/>); no server stores accounts, and the same phrase restores the same account anywhere.
/// The account key itself stays offline in day-to-day use: it signs <see cref="DeviceCertificate"/>s that say
/// "this device's multiplayer identity belongs to me", and devices keep using their own keys.
/// </summary>
/// <remarks>
/// Derivation (all hardened, SLIP-0010 with <see cref="Slip10.JayRootKey"/>):
/// m/0'/0' account key · m/1'/n' per-network identities (later) · m/2'/0' inbox key (later) · m/3'/0' friend list backup key
/// (<see cref="DeriveFriendBackupKey"/>) · m/9'/… reserved for post-quantum keys.
/// </remarks>
public sealed class JayAccount : IDisposable
{
    public const int SeedLength = 64;
    private static readonly uint[] AccountPath = [0, 0];

    private readonly byte[] _seed;
    private readonly Ed25519PrivateKeyParameters _key;
    private bool _disposed;

    private JayAccount(byte[] seed)
    {
        if (seed.Length != SeedLength) throw new ArgumentException("seed must be 64 bytes", nameof(seed));
        _seed = (byte[])seed.Clone();
        var (key, chain) = Slip10.Derive(_seed, Slip10.JayRootKey, AccountPath);
        try
        {
            _key = new Ed25519PrivateKeyParameters(key);
            PublicKey = _key.GeneratePublicKey().GetEncoded();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(chain);
        }
        Address = JayAddress.Encode(PublicKey);
    }

    public byte[] PublicKey { get; }

    /// <summary>The account address, e.g. jay1q…; it contains the public key.</summary>
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

    /// <summary>The 32-byte symmetric key that encrypts this account's friend list backups (m/3'/0'). Zero it after use.</summary>
    internal byte[] DeriveFriendBackupKey()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var (key, chain) = Slip10.Derive(_seed, Slip10.JayRootKey, 3, 0);
        CryptographicOperations.ZeroMemory(chain);
        return key;
    }

    /// <summary>Signs <paramref name="domain"/> ‖ <paramref name="message"/>. Every use has its own domain string so a signature can't be replayed elsewhere.</summary>
    public byte[] Sign(ReadOnlySpan<byte> domain, ReadOnlySpan<byte> message)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var signer = new Ed25519Signer();
        signer.Init(true, _key);
        var all = new byte[domain.Length + message.Length];
        domain.CopyTo(all);
        message.CopyTo(all.AsSpan(domain.Length));
        signer.BlockUpdate(all, 0, all.Length);
        return signer.GenerateSignature();
    }

    public static bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> domain, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        if (publicKey.Length != 32 || signature.Length != 64) return false;
        try
        {
            var verifier = new Ed25519Signer();
            verifier.Init(false, new Ed25519PublicKeyParameters(publicKey.ToArray()));
            var all = new byte[domain.Length + message.Length];
            domain.CopyTo(all);
            message.CopyTo(all.AsSpan(domain.Length));
            verifier.BlockUpdate(all, 0, all.Length);
            return verifier.VerifySignature(signature.ToArray());
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Signs a certificate for one device's long-term multiplayer key (the X25519 key in identity.key).</summary>
    public DeviceCertificate CertifyDevice(ReadOnlySpan<byte> devicePublicKey, string deviceName, int epoch, DateTimeOffset now)
    {
        var unsigned = new DeviceCertificate(Address, devicePublicKey.ToArray(), DeviceCertificate.CleanName(deviceName), epoch,
            DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds()), []);
        return unsigned with { Signature = Sign(DeviceCertificate.Domain, unsigned.SignedBytes(PublicKey)) };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CryptographicOperations.ZeroMemory(_seed);
    }
}

/// <summary>Jay account addresses: "jay1" + Bech32m(version 0 ‖ 32-byte Ed25519 public key), 63 characters.</summary>
public static class JayAddress
{
    public const string Hrp = "jay";
    public const int Version = 0;

    public static string Encode(ReadOnlySpan<byte> publicKey)
    {
        if (publicKey.Length != 32) throw new ArgumentException("public key must be 32 bytes", nameof(publicKey));
        var bits = Bech32m.ConvertBits(publicKey, 8, 5, pad: true)!;
        return Bech32m.Encode(Hrp, [(byte)Version, .. bits]);
    }

    /// <summary>Parses an address (case-insensitive, surrounding spaces ignored) into its public key.</summary>
    public static bool TryDecode(string? text, out byte[] publicKey)
    {
        publicKey = [];
        if (!Bech32m.TryDecode(text?.Trim(), out var hrp, out var data) || hrp != Hrp || data.Length < 2 || data[0] != Version) return false;
        var key = Bech32m.ConvertBits(data.AsSpan(1), 5, 8, pad: false);
        if (key is not { Length: 32 }) return false;
        publicKey = key;
        return true;
    }

    public static bool IsValid(string? text) => TryDecode(text, out _);

    /// <summary>Short form for lists: jay1qxy7…k3pf.</summary>
    public static string Short(string address) => address.Length <= 14 ? address : $"{address[..8]}\u2026{address[^4..]}";

    /// <summary>
    /// Four words from the address (first 44 bits of SHA-256 of the public key) that people can read out to each other
    /// to confirm they see the same account. Not secret, and not part of the recovery phrase.
    /// </summary>
    public static string[] CheckWords(ReadOnlySpan<byte> publicKey, MnemonicLanguage language)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData([.. "jay-check-words-v1\n"u8, .. publicKey], hash);
        var list = Bip39.Wordlist(language);
        var value = BinaryPrimitives.ReadUInt64BigEndian(hash);
        var words = new string[4];
        for (var i = 0; i < 4; i++) words[i] = list[(int)((value >> (64 - 11 * (i + 1))) & 0x7ff)];
        return words;
    }
}

/// <summary>
/// "Device key X belongs to account A": signed by the account key. Hosts and chat peers check it against the device key the
/// handshake already proved, then show the account instead of an unknown device. <see cref="Epoch"/> lets a restored
/// account void every older certificate at once (phase 3 publishes the current epoch).
/// </summary>
public sealed record DeviceCertificate(string Account, byte[] DeviceKey, string DeviceName, int Epoch, DateTimeOffset Issued, byte[] Signature)
{
    public const int MaxNameLength = 48;
    internal static ReadOnlySpan<byte> Domain => "jay-device-cert-v1\n"u8;

    internal byte[] SignedBytes(ReadOnlySpan<byte> accountKey)
    {
        var name = Encoding.UTF8.GetBytes(DeviceName);
        var buffer = new byte[32 + 32 + 4 + 8 + name.Length];
        accountKey.CopyTo(buffer);
        DeviceKey.CopyTo(buffer.AsSpan(32));
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(64), Epoch);
        BinaryPrimitives.WriteInt64BigEndian(buffer.AsSpan(68), Issued.ToUnixTimeSeconds());
        name.CopyTo(buffer.AsSpan(76));
        return buffer;
    }

    /// <summary>True when the signature is the account's and the certificate names <paramref name="deviceKey"/>.</summary>
    public bool Verify(ReadOnlySpan<byte> deviceKey)
    {
        if (DeviceKey.Length != 32 || !deviceKey.SequenceEqual(DeviceKey) || Epoch < 0) return false;
        if (DeviceName != CleanName(DeviceName)) return false;
        if (!JayAddress.TryDecode(Account, out var accountKey)) return false;
        return JayAccount.Verify(accountKey, Domain, SignedBytes(accountKey), Signature);
    }

    /// <summary>
    /// Reads a certificate someone sent and checks it names <paramref name="deviceKey"/> (the key they just proved in a handshake).
    /// Returns the account address in its canonical lowercase form, or null when there is no certificate or it doesn't check out.
    /// </summary>
    public static string? VerifiedAccount(System.Text.Json.Nodes.JsonNode? node, ReadOnlySpan<byte> deviceKey)
    {
        if (FromJson(node) is not { } cert || !cert.Verify(deviceKey) || !JayAddress.TryDecode(cert.Account, out var key)) return null;
        return JayAddress.Encode(key);
    }

    public System.Text.Json.Nodes.JsonObject ToJson() => new()
    {
        ["account"] = Account,
        ["device"] = Convert.ToBase64String(DeviceKey),
        ["name"] = DeviceName,
        ["epoch"] = Epoch,
        ["issued"] = Issued.ToUnixTimeSeconds(),
        ["sig"] = Convert.ToBase64String(Signature),
    };

    public static DeviceCertificate? FromJson(System.Text.Json.Nodes.JsonNode? node)
    {
        try
        {
            if (node is not System.Text.Json.Nodes.JsonObject o) return null;
            var account = (string?)o["account"];
            var device = Convert.FromBase64String((string?)o["device"] ?? "");
            var name = (string?)o["name"] ?? "";
            var epoch = (int?)o["epoch"] ?? -1;
            var issued = (long?)o["issued"] ?? -1;
            var sig = Convert.FromBase64String((string?)o["sig"] ?? "");
            if (account is null || device.Length != 32 || sig.Length != 64 || epoch < 0 || issued < 0 || name.Length > MaxNameLength) return null;
            return new DeviceCertificate(account, device, name, epoch, DateTimeOffset.FromUnixTimeSeconds(issued), sig);
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
