using System.Security.Cryptography;

namespace Amber.Core.Multiplayer.Public;

/// <summary>
/// "At least <see cref="Need"/> of these <see cref="Keys"/> must have signed." Used for everything the Jay project vouches for
/// (reviewed networks, directory certificates, node hardware certificates), so no single key can do it alone. (For now
/// both of Jay's keys belong to one trustee, one of them an offline cold backup; see <see cref="JayTrust"/>.)
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
/// The Jay project's trust keys. Each key set has one hybrid Ed25519 + ML-DSA-65 key for networks, directories and hardware
/// (<see cref="Keys"/>) and a separate one for releases (<see cref="ReleaseKeys"/>) (<see cref="TrustSigner"/>), each
/// generated offline by its holder. Any <see cref="Need"/> of the keys together can review a network, certify a directory
/// node or bind a node to its hardware.
/// The private keys never go into the repository, CI, a server or a chat. For now there are two trust keys (one trustee and
/// an offline cold backup), so both must sign; a third trustee is added later. <see cref="ReleaseKeys"/> stay empty until
/// the embedded EasyTier build carries the release keys' signature, because with release keys set EasyTier installs only
/// with a valid one.
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
        "SHMZ3/8/X+IV5cWzXIFmULNgySyD39wFFCMjp3QwQY5jInmWs8FgRdsQQax5TlY7Lsutl3Z40TVTbOCZHOYbrvWRGJLPE80WLtwXtyyvirHsn1wkVDhUbTJg8pa1bSgcFtbZauQJ0aQJ3K5ocgeNjyd1fFgoOisr9isx/Iao82pSt786gwp3IxD80g4JxAS0p8Uw3RJR4daQ3993jTKRhIxmJ+LPXm6cghmX2yqup7BvCWn+iePBUPYSEkPD70SWncz5+cIb8HihO7bFsjYP7nmiBTT9IUAA3kOe1Ogyq4PBx/Ug+1a1AmgVEN+/1QlrYVeYMiuimI5G7e02yxcxC0pvgAb9Nh9IW5DGLtzHfgtkSBrpu4qijysvt+fte40LPt1etzHrEtnDm29zbm9LVzI3FBZ/NxPPzBq3WNfNpNfch7qScW136FoV6sEd1/lZ9iOshVxINl44rd2jl0nYNZYJZ/JMqB0DVK5ttPlFHnVrRewEuEQoSZsp4sVQqZ+GsMzCPY+YzxG+5K1PwuiZULlSFUbjyUtVPeqxQsNBZmqty7y4sA2SvqhwEhSJdFhOrWuZDTOGB19i8SXF2+n1TUhMVdcduaBXB+myJosbcApPECnqOvr+9277VlMSMruHea2VovGWBdWIfV/Wzb4hx/FqJcWZUkcuta0XSs3MEX6lDujrOPA9fItorTboXmji84HTP0MSy1nezpyrst9nv5ZlpXcureGv4FSUNHwfVMlcoLjCA5sZVt4y1u1nAGQ2v63aFDihDrbA/uUUHbyZxiwglBZu+/W3iB6xYaVI/GQdp/LWW636HfBF69+fFwgAZbFBa4kbgvccTsMvo8ZSmcH59fenLmQaxp+udttvINVbGQ+GBSgIN/dZeKgzHXfDdzSHyWBmJ252wAes4PVH5Asv42FNaSqeyJYs4AAV0cQj91ZTTsZTAnNAaPDoRM1dZ/Ufy5NQr+qfSM/jF7B/WTV47K9NosnIVb+c2Bxocv/JCeaLplY0WeLpUQu0hT5FrZLTZxOYGcceCeh9vLwqQwdYEH1iCORDU1EObdmnohNrpkciHzzBJP7tHMqvLyu909ZHSJerua1FP9C3YNGhH829FPsUoCGSMjCAp8aV3vDsmDL68oazcmlyMC8uc7H4F6ljur+D10B59A9iOnE8zM6Vf2E25Qwoi1HnGkfdpC/a0NjryM3Ch+Ic9WSwtIX7FDapGvffpMYm8Wmbr82dNt15tSwxquUtZ6/GNVMoBkXcSh1eGKY5rS/WOa5NHaRQJ+eLHYz/cfMcwEQAVGzUduYZDUWEwW2EhKPg7ooAHoL5GLTZZ9NveTS+NV62eJzINVqzlhgCH0PXxn+nFOyUfvWrBYhkP2/WNcLt5YgCvsgVA7zFOgb8dQvIVr0wPsUrBZYIftEsostgSGyLxCMGdqXLdNVS0xp+JKZ4xhHTQbHYDt0VFK/sus9cO3vWkwVr885z5UaC3dWYER5sHrC0QioJqZtCFcPJZFM8oFdEfmt7US0Y+/S2DndHWCxyRIImZomVfglUEZ/KMgonb7b4MVhpp7zz6Hp1jQVcsW12+jJIQ2meA+2/J0ellC+XJaIwVf9KYlqoVGXBa1W/YgK9b8mkn4qzRr7Pda2kl+43cOXojnSIk543kANHaEcVZUpwQQYw0Q4/Igc11Q3tDZIHvSeGNeBAL/Dc7vZ9SSNFJz0kLqCR3EcvLqzIEZRwr0VE0/YC2iYz5jXATXzmAnw6zWMSkZerGuyVpZzj1m42VtGew8L3Z+MNtRDmaKcA7uXBkUtE+EERYHV7V8m1P90S45L6KWx3ibQu4ybfYb7iQp2CA+ATij+Dz4dQLT2/baCXgQqgUGhFkGU8cIt3yqFe2SKRheC/pGI1OfGtSyWUJXV2wSyOUS0wHJgmDVCX6v6jweRACXuoUnN0uQ1icp/m5wjUskxpJr9pfoszY9mxkUNsfJXj47oGrIoAobty/UuzVGcX52VEQwQuL6/M56bksb0+17iTUDGATmzg3ifn6hJJG9JcYKTynjqDZTxDvhOZXEKnC8E9YyieF+gKfXrpaG+rSughangspf5avfBd58Tj60uXrZBeRlzHr/tv9lTvqM4kscCie3q8OpNeUWprU1H5BoPC6M9LH/oVPohCdCZVWHl7Zz8R/oXb/Jz1lu7a74G1I+Y2n3B23ya/bX6jaSNVJGuPV5CAktpL3cPHtF5jyOOnKFlZU2axI4gHJoUBT9GqUNXzuI53dBJO5Cip/TO4QoOGrRuDVkidQzx1FV+JCl8HIbROEGf9R7DUcoiGybUMaW6LkwKgNZ+s+dN2bTJmpYHFdlWLlZjiqW4Tqyx10JQzkaazaLpqxEKT+Vvk59WBV/HdEubedNQr0cg6JgWakrqN9e3m8GTi9vNqTu9O1hAISEdmdvhzsV1TJ5yeCKVof5YPOOPkT+hxMiju7bcoBGIyEzKmbau6om6smVgvzLr8di10yG3ru2/YNpb2TZ7f6Rg56BYwzMLVnxuTSfOiQ4gmhHyeoBdmXZ14MoEgOHPnnWcOkvZmsu/hMOskHI6y+eUCdyWZ9akgrEWwX9Ghp10cziEFJ7QolujsSDtOskRRoj7axXm8nyV5oAkMdnzHTIzcbhRKbqOaSBsrgQ==",
    ];

    /// <summary>How many of <see cref="Keys"/> must sign.</summary>
    public const int Need = 2;

    /// <summary>
    /// The trustees' separate release-signing keys (full public keys, base64), used only for Jay releases and trust-chain
    /// changes (<c>Amber.Core.Launch.TrustList.Jay</c>). Each key set (a trustee's, or for now the cold backup) has one of
    /// these and one of <see cref="Keys"/>; the two are never the same key, so a key used to review networks can't sign a release and the other way round.
    /// </summary>
    public static readonly IReadOnlyList<string> ReleaseKeys = [];

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
