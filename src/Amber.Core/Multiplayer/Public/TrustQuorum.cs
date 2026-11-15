using System.Security.Cryptography;

namespace Amber.Core.Multiplayer.Public;

/// <summary>
/// "At least <see cref="Need"/> of these <see cref="Keys"/> must have signed." Used for everything the Jay project vouches for
/// (reviewed networks, directory certificates, node hardware certificates), so no single key can do it alone. (Jay's
/// keys: two trustees plus the first trustee's offline cold backup, any two of the three; see <see cref="JayTrust"/>.)
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
/// The Jay project's trust keys. Each key set has one hybrid Ed25519 + ML-DSA-65 key for networks, directories and hardware
/// (<see cref="Keys"/>) and a separate one for releases (<see cref="ReleaseKeys"/>) (<see cref="TrustSigner"/>), each
/// generated offline by its holder. Any <see cref="Need"/> of the keys together can review a network, certify a directory
/// node or bind a node to its hardware.
/// The private keys never go into the repository, CI, a server or a chat. There are three trust keys (two trustees and the
/// first trustee's offline cold backup), any two of which must sign, so losing or leaking one key is survivable. The
/// release keys (<see cref="ReleaseKeys"/>) are still two of two until the second trustee's separate release key is added.
/// With release keys set, EasyTier installs only when the embedded build carries their valid signature.
/// </summary>
public static class JayTrust
{
    /// <summary>
    /// The trustees' full public keys (<see cref="TrustSigner.PublicKey"/>, base64: Ed25519 ‖ ML-DSA-65). Full keys rather
    /// than ids so release signatures (which carry only ids) can be checked against them.
    /// </summary>
    public static readonly IReadOnlyList<string> Keys =
    [
        // Trustee 1 (fingerprint 9D47 BBDA 24BF 2956)
        "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",
        // Trustee 1's cold backup, kept offline (fingerprint 75CD 82DB E5DF 8E4D)
        "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",
        // Trustee 2 (fingerprint 1BD3 AE2E 87D7 C5A2)
        "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",
    ];

    /// <summary>How many of <see cref="Keys"/> must sign (2 of 3).</summary>
    public const int Need = 2;

    /// <summary>
    /// The trustees' separate release-signing keys (full public keys, base64), used only for Jay releases and trust-chain
    /// changes (<c>Amber.Core.Launch.TrustList.Jay</c>). Each key set (a trustee's, or for now the cold backup) has one of
    /// these and one of <see cref="Keys"/>; the two are never the same key, so a key used to review networks can't sign a release and the other way round.
    /// </summary>
    public static readonly IReadOnlyList<string> ReleaseKeys =
    [
        // Trustee 1 (fingerprint 0803 9FD2 126D E719)
        "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",
        // Trustee 1's cold backup, kept offline (fingerprint 73FA 5202 C401 F163)
        "bteK05l6CXRxhDhaNw0nTqEVHZpaCCXDAyq4qBMZsQQ5ojWz1IIl2UWPa4P5ypQ5umWPe2/vZBjC2yEuERbbYvCaglW+wz3eH0dZ3Ef9iDFLlDJPMgyEsLLp1n0bJ/wexuntXyUPeqZH8njgG/o3IHEkFiYhzisezHZrVBniS4U0L/0dRrZF6pdbSz0aGDd9IPg1ZoyrwrEjEovxwk2E2qu82vjFa1ZhsKgdWkR+gs43coA4NnEpZxP0f2vzelM4WoBN2I8LT5liUATVuxBSmCtzzk9qAeLZ4JBPqthZUSwPRefmEnHDkoudzwEtgP5D11jbPPeYeVic1UhjGATUjcI2PuC44GpNZoV52FOj1SAXhw3p6j+USStFZSEIZTADHgRYpHniYdolTSmlNd5hJQWAFMBxOuVqVPZX+ey/inzR0F+vPySvXSQpoBja0WbJDsZRXZLamPleUh8gieKS/WF3O9+6lZ5aw1BU9lQQXrmfRxsxe3XU/RFe2YELzfRzeNVfzpc2oGCPjI7u+m56LzHxdtXnNkHoxBd9vz0pxdqG0FlFDpPngFffGy9Bzew8juPGxCVIF2JSxgE23afG3sOzFJCSIrknkg3VQU4y1hISFz6zQQDIOXpw6CbsLH7ZJXQlJYFNHqMYCk9OZs6VMSdoZzAoqB6isiSqe4ySqJsPZ6Fly8SQf/iweSSIGF1XXR9SdvAuz0W227j1UoJiMaDmb1aaUDrjwzZqEfEwVVi/1FORtBVlKG1v/UyrAI14NJGGkOBiENO21kWA+F6/JnIWUtYGabFRl46THF4yS77c4iWbU4YbY50GbtaGygET0qUdHufBQe+vWApMK/FdIgvUBBtfXsXCaoFFriUcePiwfZDLBq/8OdNRmX+MEM0QLHttZ62htJT+GBCdOMOA+V/kbiE+A65szKVRTEljfSegL0tOPHO0gT6TL3T9C9vREqA+72T82HonP8ys1VfHOfCQ8PcTMgwCgbxpxl/cFQgW9LqEnhtGxnQj+JLgpDh5wftTIXwQ4xGB30VdAtKpXLAZi3jdxLfELOEmAwUXG8qNPRb5Bz30oTEK1L2sNn+nnRFtEe7L4aeMcxqRPPPc3dakLGx188wFVYLr/K98am4j2nPVqDwnVUiAaxG27vCc/wzoRkZljqBZIbQdN+dxaQSg28H3saHqBF/EuQl9NfgF9Gb1ByOitUhFnMSamJA38EkGUoJvtO5/uOgN/Dabxi65cwd4xhWyGe38l1U4ZDqfm61KR1QOvvbLwISCMBIWMjS6nM6HqDh0ynjqfrKFLeXmdWH2MoPaHrxfkmnLwHLPzpZev0ljJQ06w7I5o40jHt83iGu6nw+9Yb2UODbvLkFVx5qgEZOwihUP8m/eMd1b27mCUw98S3uhuNRyTG9SO7zt1cMAlzF/mKlMfKTlkJnfFCph5XchO6mFSKsn+stJ+FOlxIVBxyL0VUeY2OB1zGcMKNSsIT58btyEQ5MFxEB0QHiws9L4dM45QXjHbBuLM9SS/24WrwgfwQxO9LtWiTGr9JNNp7GU/wdOa6mSW5viQexZN3rSHLkbv+rtTbyuhydHKs9S/9CbLQrLQtIZDFUtUs72t75Kuo7Ecb+L5iUtEhrzXp9d00D+jffOumrhmOnwW0PGPInltlIkTI6b5PhhkdLpboM5pTGOJA6CosIr0rexQ0UlfaSMP4b9MaLD+4W/XHXq1AWNk5w9gB1jTetq/XDLHw0OdBVGIBPbhle+VDpOCWHCbfVmIBiheXDZv9x/i6Q3a0Wwkx+ebEovAdgK7bKFbviHKiRdBZqtg6xGXknSL3i6SFyiGliU2qDQgKHH06mC8LUBCet18ifwat6ulCyTXkVFLAxP0RxT3+ILSdCAOjAO1hNQnRtSC6Ls/OorSDyuIbl77sWjjmmNzHlcRAb6s5pl3Ytkh+/RZeaqVWHqIEyAInmchGP1zuo2Df2z33iTc0by2eM7UXfYJQVZFC1Zyvyk6ad6bHvEDmoj2k+8yVA3LlYMYs+eQF9ZLiiAGBVm6BB1yADqGp7Eh/6CL3qMgzgxiaXaN3VbcXbTC6OTWO0IuzqkAi1axMVno0x6mExmz+uEml+n+xBe0jx0z3odXBCygVAlsQTmBjWMPHBMtFIn47Uv428+Wbk+gO73IyQZOfEHVm5TOL6LjJsYZbsLyIcB2h7lXEDWHBiQLOAzXN3bRrWEY7ObKdGhEtzsZYHvuIn8hM5lBd0yjDMjf9I9J7Njnii9mHz2UrrGYKEO+4JNV03uQv5Ma8TeplIDfPl1lBKiMw4lBnFjW5LSCDqzbMWgbDO98KMxfDPTufvJQT0UQmM/i+/bYVF/NHZrCUCmQJ2p/6LRAe1NqZCnTugXuooAYjQMt4K1UK0dGn/x7xzQ+1zzI7G8vZ3OWvnNUvf5RQgI6t3byn0U5xIYOTaFP1BX6Shjd5MZ+boU43EWn7XNeG16j1E6cehFNZCY2Oj793p+j7ly4bCcLe+MctNM9YctwhiqyizVyYC/nWVxJ3HrsHz95HPOy8aXwxvHcFSuU5y9YVM0HWkU3a1BAn4noJqYra2sP/8N3rH1oTorOVBYHxAtzUSTUjwFQWnAqwHTEInLNbBh8Rz491p045nEx3tQXCyq8x63DQ==",
    ];

    /// <summary>How many release keys must sign a release.</summary>
    public const int ReleaseNeed = 2;

    // Ids decoded once: a malformed key fails at first use of JayTrust (and in ReleaseSignatureTests.BuiltInKeysAreWellFormed), not deep inside a check.
    private static readonly byte[][] ReleaseIds = [.. ReleaseKeys.Select(k => TrustSigner.IdOfPublicKey(Convert.FromBase64String(k)))];
    private static readonly byte[][] KeyIds = [.. Keys.Select(k => TrustSigner.IdOfPublicKey(Convert.FromBase64String(k)))];

    /// <summary>Whether <paramref name="id"/> is one of the release keys (refused for network, directory and hardware signing).</summary>
    public static bool IsReleaseKey(byte[] id) => ReleaseIds.Any(k => k.AsSpan().SequenceEqual(id));

    /// <summary>Whether <paramref name="id"/> is one of <see cref="Keys"/> (refused for release signing).</summary>
    public static bool IsNetworkKey(byte[] id) => KeyIds.Any(k => k.AsSpan().SequenceEqual(id));

    /// <summary>The quorum over the trustees' ids. A fresh copy each time; the key lists themselves are read-only.</summary>
    public static TrustQuorum Quorum => KeyIds.Length == 0 ? TrustQuorum.None : new([.. KeyIds.Select(id => (byte[])id.Clone())], Need);
}
