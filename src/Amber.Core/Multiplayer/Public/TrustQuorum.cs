using System.Security.Cryptography;

namespace Amber.Core.Multiplayer.Public;

/// <summary>
/// "At least <see cref="Need"/> of these <see cref="Keys"/> must have signed." Used for everything the Jay project vouches for
/// (reviewed networks, directory certificates, node hardware certificates), so no single person's key can do it alone.
/// Each signature is an ordinary Ed25519 signature over the same domain-separated bytes; the quorum only counts how many
/// distinct listed keys signed. A key listed twice counts once.
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

/// <summary>One Ed25519 signature and the public key that made it.</summary>
public sealed record TrustSignature(byte[] Key, byte[] Signature)
{
    public const int Size = 96;

    public bool Verify(byte[] message) => Key.Length == 32 && Signature.Length == 64 && RoomSigner.Verify(Key, message, Signature);
}

/// <summary>
/// The Jay project's trust keys: one Ed25519 key per trustee, each generated offline on that person's own machine. Any
/// <see cref="Need"/> of them together can review a network, certify a directory node or bind a node to its hardware.
/// The private keys never go into the repository, CI, a server or a chat. Empty until the trustees have made their keys:
/// until then nothing counts as reviewed and the built-in network's directory is trusted on first use.
/// </summary>
public static class JayTrust
{
    /// <summary>The trustees' public keys (base64).</summary>
    public static readonly string[] Keys = [];

    /// <summary>How many trustees must sign.</summary>
    public const int Need = 2;

    public static TrustQuorum Quorum => Keys.Length == 0 ? TrustQuorum.None : TrustQuorum.FromBase64(Keys, Need);
}
