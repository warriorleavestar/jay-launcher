using System.Security.Cryptography;
using Amber.Core.Multiplayer.Transport;
using Org.BouncyCastle.Crypto.Parameters;

namespace Amber.Core.Identity;

/// <summary>
/// This device's long-term keys, all derived from one 32-byte random seed the device makes for itself (never from the
/// recovery phrase, so restoring an account elsewhere can't open anything this device sealed). HKDF-SHA256 with three
/// labels gives three keys that are never used for each other's job:
/// <list type="bullet">
/// <item>jay-device-dh-v1: the X25519 key for handshakes (the multiplayer identity, <see cref="Dh"/>);</item>
/// <item>jay-device-sig-ed-v1: an Ed25519 signing key;</item>
/// <item>jay-device-sig-mldsa-v1: the seed of an ML-DSA-65 signing key.</item>
/// </list>
/// A device signature is both signatures over the same bytes, and only counts when both verify.
/// </summary>
public sealed class DeviceKeys : IDisposable
{
    public const int SeedLength = 32;
    private static readonly byte[] DhLabel = "jay-device-dh-v1"u8.ToArray();
    private static readonly byte[] EdLabel = "jay-device-sig-ed-v1"u8.ToArray();
    private static readonly byte[] MlDsaLabel = "jay-device-sig-mldsa-v1"u8.ToArray();

    private readonly byte[] _seed;
    private readonly byte[] _edPrivate;
    private readonly byte[] _mlDsaSeed;
    private bool _disposed;

    private DeviceKeys(ReadOnlySpan<byte> seed)
    {
        if (seed.Length != SeedLength) throw new CryptographicException("device seed size");
        _seed = seed.ToArray();
        var dh = Derive(DhLabel);
        try { Dh = X25519KeyPair.FromPrivateKey(dh); }
        finally { CryptographicOperations.ZeroMemory(dh); }
        _edPrivate = Derive(EdLabel);
        EdPublicKey = new Ed25519PrivateKeyParameters(_edPrivate).GeneratePublicKey().GetEncoded();
        _mlDsaSeed = Derive(MlDsaLabel);
        MlDsaPublicKey = MlDsa65.PublicKey(_mlDsaSeed);
    }

    /// <summary>A new device: a fresh random seed.</summary>
    public static DeviceKeys Generate()
    {
        var seed = RandomNumberGenerator.GetBytes(SeedLength);
        try { return new DeviceKeys(seed); }
        finally { CryptographicOperations.ZeroMemory(seed); }
    }

    public static DeviceKeys FromSeed(ReadOnlySpan<byte> seed) => new(seed);

    /// <summary>The handshake key; its public half is what hosts and contacts know this device by.</summary>
    public X25519KeyPair Dh { get; }

    public byte[] EdPublicKey { get; }
    public byte[] MlDsaPublicKey { get; }

    /// <summary>The seed, for sealing to disk. Zero it after use.</summary>
    internal byte[] ExportSeed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return (byte[])_seed.Clone();
    }

    /// <summary>
    /// Signs <paramref name="domain"/> ‖ <paramref name="message"/> with both signing keys. Each use has its own domain
    /// string so a signature can't be replayed somewhere else.
    /// </summary>
    public HybridSignature Sign(ReadOnlySpan<byte> domain, ReadOnlySpan<byte> message)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return HybridSignature.Sign(_edPrivate, _mlDsaSeed, domain, message);
    }

    /// <summary>True only when both halves of <paramref name="signature"/> verify against the device's two public keys.</summary>
    public static bool Verify(ReadOnlySpan<byte> edPublicKey, ReadOnlySpan<byte> mlDsaPublicKey, ReadOnlySpan<byte> domain,
        ReadOnlySpan<byte> message, HybridSignature? signature) =>
        HybridSignature.Verify(edPublicKey, mlDsaPublicKey, domain, message, signature);

    private byte[] Derive(byte[] label) => HKDF.DeriveKey(HashAlgorithmName.SHA256, _seed, 32, [], label);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CryptographicOperations.ZeroMemory(_seed);
        CryptographicOperations.ZeroMemory(_edPrivate);
        CryptographicOperations.ZeroMemory(_mlDsaSeed);
        Dh.Dispose();
    }
}
