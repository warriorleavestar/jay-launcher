using System.Security.Cryptography;

namespace Amber.Core.Multiplayer.Public;

/// <summary>
/// "At least <see cref="Need"/> of these <see cref="Keys"/> must have signed." Used for everything the Jay project vouches for
/// (reviewed networks, directory certificates, node hardware certificates), so no single person's key can do it alone.
/// Each signature is a hybrid Ed25519 + ML-DSA-65 signature (<see cref="TrustSignature"/>) over the same domain-separated
/// bytes; the quorum only counts how many distinct listed key ids signed. A key listed twice counts once.
/// </summary>
public sealed record TrustQuorum(IReadOnlyList<byte[]> Keys, int Need)
{
    /// <summary>No keys: never met. Used until the keys exist.</summary>
    public static readonly TrustQuorum None = new([], 1);

    /// <summary>How many distinct listed keys appear among <paramref name="signers"/> (whose signatures are already verified).</summary>
    public int Count(IEnumerable<byte[]> signers)
    {
        var seen = new List<byte[]>();
        foreach (var signer in signers)
        {
            if (!Contains(Keys, signer) || Contains(seen, signer)) continue;
            seen.Add(signer);
        }
        return seen.Count;
    }

    /// <summary>Enough listed keys signed. Never true for an empty quorum or a quorum asking for nothing.</summary>
    public bool IsMet(IEnumerable<byte[]> signers) => Need >= 1 && Keys.Count >= Need && Count(signers) >= Need;

    public static TrustQuorum FromBase64(IEnumerable<string> keys, int need) => new([.. keys.Select(Convert.FromBase64String)], need);

    internal static bool Contains(IEnumerable<byte[]> keys, byte[] key)
    {
        var match = false;
        foreach (var k in keys) match |= k.Length == key.Length && CryptographicOperations.FixedTimeEquals(k, key);
        return match;
    }
}

/// <summary>
/// One trust signature: an Ed25519 signature and an ML-DSA-65 (FIPS 204) signature over the same bytes, with both public
/// keys. It only counts when both verify, so forging it needs both algorithms broken (ML-DSA keeps it safe against a
/// quantum computer, Ed25519 against a flaw in the newer scheme). The signer is identified by <see cref="Key"/>, a
/// 32-byte hash of both public keys (<see cref="TrustSigner.IdOf"/>); trust lists hold these ids.
/// <para>Encoded as Ed25519 public key 32 ‖ ML-DSA public key 1952 ‖ Ed25519 signature 64 ‖ ML-DSA signature 3309.</para>
/// </summary>
public sealed record TrustSignature(byte[] EdKey, byte[] MlDsaKey, byte[] EdSignature, byte[] MlDsaSignature)
{
    public const int EdKeySize = 32, MlDsaKeySize = 1952, EdSignatureSize = 64, MlDsaSignatureSize = 3309;
    public const int Size = EdKeySize + MlDsaKeySize + EdSignatureSize + MlDsaSignatureSize;

    /// <summary>The signer's id: <see cref="TrustSigner.IdOf"/> of both public keys.</summary>
    public byte[] Key => EdKey.Length == EdKeySize && MlDsaKey.Length == MlDsaKeySize ? TrustSigner.IdOf(EdKey, MlDsaKey) : new byte[32];

    public byte[] Encode() => [.. EdKey, .. MlDsaKey, .. EdSignature, .. MlDsaSignature];

    /// <summary>Reads one encoded entry (exactly <see cref="Size"/> bytes). Doesn't verify.</summary>
    public static TrustSignature Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length != Size) throw new ArgumentException(null, nameof(data));
        var a = EdKeySize; var b = a + MlDsaKeySize; var c = b + EdSignatureSize;
        return new TrustSignature(data[..a].ToArray(), data[a..b].ToArray(), data[b..c].ToArray(), data[c..].ToArray());
    }

    public bool Verify(byte[] message)
    {
        if (EdKey.Length != EdKeySize || MlDsaKey.Length != MlDsaKeySize || EdSignature.Length != EdSignatureSize || MlDsaSignature.Length != MlDsaSignatureSize)
            return false;
        if (!RoomSigner.Verify(EdKey, message, EdSignature)) return false;
        try
        {
            var verifier = new Org.BouncyCastle.Crypto.Signers.MLDsaSigner(TrustSigner.MlDsa, deterministic: false);
            verifier.Init(false, Org.BouncyCastle.Crypto.Parameters.MLDsaPublicKeyParameters.FromEncoding(TrustSigner.MlDsa, MlDsaKey));
            verifier.BlockUpdate(message, 0, message.Length);
            return verifier.VerifySignature(MlDsaSignature);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return false; }
    }
}

/// <summary>
/// A trust key (a trustee's, a network owner's or a directory root's): one 32-byte seed gives an Ed25519 key (the seed
/// itself, as <see cref="NetworkSigner"/>) and an ML-DSA-65 key (from HKDF of the seed), so key files and the paper backup
/// stay 32 bytes. Signs with both.
/// </summary>
public sealed class TrustSigner
{
    internal static readonly Org.BouncyCastle.Crypto.Parameters.MLDsaParameters MlDsa = Org.BouncyCastle.Crypto.Parameters.MLDsaParameters.ml_dsa_65;
    private static readonly byte[] IdDomain = "jay-trust-key-v1\n"u8.ToArray();
    private readonly NetworkSigner _ed;
    private readonly Org.BouncyCastle.Crypto.Parameters.MLDsaPrivateKeyParameters _mlDsa;

    private TrustSigner(byte[] seed)
    {
        _ed = NetworkSigner.FromSeed(Convert.ToBase64String(seed));
        var mlSeed = HKDF.DeriveKey(HashAlgorithmName.SHA256, seed, 32, info: "jay-trust-ml-dsa-65-v1"u8.ToArray());
        _mlDsa = Org.BouncyCastle.Crypto.Parameters.MLDsaPrivateKeyParameters.FromSeed(MlDsa, mlSeed);
        CryptographicOperations.ZeroMemory(mlSeed);
        EdKey = _ed.PublicKey;
        MlDsaKey = _mlDsa.GetPublicKeyEncoded();
        Id = IdOf(EdKey, MlDsaKey);
    }

    /// <summary>The id that goes into trust lists (JayTrust.Keys, dirRoots): 32 bytes.</summary>
    public byte[] Id { get; }
    public byte[] EdKey { get; }
    public byte[] MlDsaKey { get; }

    /// <summary>The full public key as it goes into <see cref="JayTrust.Keys"/>: Ed25519 key ‖ ML-DSA-65 key (1,984 bytes).</summary>
    public byte[] PublicKey => [.. EdKey, .. MlDsaKey];

    /// <summary>The id of a full public key (<see cref="PublicKey"/>). Throws on a wrong length.</summary>
    public static byte[] IdOfPublicKey(ReadOnlySpan<byte> publicKey)
    {
        if (publicKey.Length != PublicKeySize) throw new InvalidDataException(Amber.Core.Localization.L.T("core.mp.network.bad_key"));
        return IdOf(publicKey[..TrustSignature.EdKeySize], publicKey[TrustSignature.EdKeySize..]);
    }

    public const int PublicKeySize = TrustSignature.EdKeySize + TrustSignature.MlDsaKeySize;

    /// <summary>SHA-256 of "jay-trust-key-v1\n" ‖ Ed25519 public key ‖ ML-DSA-65 public key.</summary>
    public static byte[] IdOf(ReadOnlySpan<byte> edKey, ReadOnlySpan<byte> mlDsaKey) => SHA256.HashData([.. IdDomain, .. edKey, .. mlDsaKey]);

    public static TrustSigner Generate(out byte[] seed)
    {
        seed = RandomNumberGenerator.GetBytes(32);
        return new TrustSigner(seed);
    }

    /// <summary>From a base64 seed (a key file's content). Same files as <see cref="NetworkSigner.FromSeed"/>.</summary>
    public static TrustSigner FromSeed(string base64)
    {
        byte[] seed;
        try { seed = Convert.FromBase64String(base64.Trim()); }
        catch (FormatException) { throw new InvalidDataException(Amber.Core.Localization.L.T("core.mp.network.bad_key")); }
        try { return new TrustSigner(seed); }
        finally { CryptographicOperations.ZeroMemory(seed); }
    }

    public TrustSignature Sign(byte[] message)
    {
        var signer = new Org.BouncyCastle.Crypto.Signers.MLDsaSigner(MlDsa, deterministic: false);
        signer.Init(true, _mlDsa);
        signer.BlockUpdate(message, 0, message.Length);
        return new TrustSignature(EdKey, MlDsaKey, _ed.Sign(message), signer.GenerateSignature());
    }
}

/// <summary>
/// The Jay project's trust keys: one hybrid Ed25519 + ML-DSA-65 key per trustee (<see cref="TrustSigner"/>), each generated offline on that person's own machine. Any
/// <see cref="Need"/> of them together can review a network, certify a directory node or bind a node to its hardware.
/// The private keys never go into the repository, CI, a server or a chat. Empty until the trustees have made their keys:
/// until then nothing counts as reviewed and the built-in network's directory is trusted on first use.
/// </summary>
public static class JayTrust
{
    /// <summary>
    /// The trustees' full public keys (<see cref="TrustSigner.PublicKey"/>, base64: Ed25519 ‖ ML-DSA-65). Full keys rather
    /// than ids so release signatures (which carry only ids) can be checked against them.
    /// </summary>
    public static readonly string[] Keys = [];

    /// <summary>How many trustees must sign.</summary>
    public const int Need = 2;

    /// <summary>The quorum over the trustees' ids.</summary>
    public static TrustQuorum Quorum => Keys.Length == 0 ? TrustQuorum.None
        : new([.. Keys.Select(k => TrustSigner.IdOfPublicKey(Convert.FromBase64String(k)))], Need);
}
