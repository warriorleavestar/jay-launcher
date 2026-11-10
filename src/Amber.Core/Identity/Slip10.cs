using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Amber.Core.Identity;

/// <summary>
/// SLIP-0010 Ed25519 key derivation (https://github.com/satoshilabs/slips/blob/master/slip-0010.md). Ed25519 only allows
/// hardened children, so every index here is hardened. The master step's HMAC key is a parameter: the standard
/// "ed25519 seed" is used only by the test vectors; Jay accounts use <see cref="JayRootKey"/>.
/// </summary>
public static class Slip10
{
    public const uint Hardened = 0x80000000;

    /// <summary>The standard SLIP-0010 Ed25519 curve key (for test vectors).</summary>
    public static ReadOnlySpan<byte> Ed25519RootKey => "ed25519 seed"u8;

    /// <summary>Jay's own root key: the same words give unrelated keys in Jay and in any cryptocurrency wallet.</summary>
    public static ReadOnlySpan<byte> JayRootKey => "jay seed v1"u8;

    /// <summary>Derives the private key (32 bytes) and chain code (32 bytes) at a path of hardened indices (pass them without the hardened bit).</summary>
    public static (byte[] Key, byte[] ChainCode) Derive(ReadOnlySpan<byte> seed, ReadOnlySpan<byte> rootKey, params ReadOnlySpan<uint> path)
    {
        var node = HMACSHA512.HashData(rootKey, seed);
        Span<byte> data = stackalloc byte[1 + 32 + 4];
        try
        {
            foreach (var index in path)
            {
                if (index >= Hardened) throw new ArgumentOutOfRangeException(nameof(path), "pass indices without the hardened bit");
                data[0] = 0;
                node.AsSpan(0, 32).CopyTo(data[1..]);
                BinaryPrimitives.WriteUInt32BigEndian(data[33..], index | Hardened);
                var next = HMACSHA512.HashData(node.AsSpan(32, 32), data);
                CryptographicOperations.ZeroMemory(node);
                node = next;
            }
            return (node[..32], node[32..]);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(data);
            CryptographicOperations.ZeroMemory(node);
        }
    }
}
