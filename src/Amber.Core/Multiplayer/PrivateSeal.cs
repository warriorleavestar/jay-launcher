using System.Security.Cryptography;
using System.Text;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Multiplayer;

/// <summary>
/// 成员之间的端到端加密（私聊、传文件）：房主只负责按指纹转交，看不到内容。
/// 每条消息新生成一把临时 X25519 钥匙 e，密钥 = HKDF-SHA256(DH(e, 收方) ‖ DH(发方, 收方))，
/// info 里带上发方、收方和临时公钥，AES-256-GCM 加密。
/// DH(发方, 收方) 让收方确认确实是这个发方发的（房主盖错了发方公钥就解不开）；临时钥匙让每条消息的密钥都不同。
/// 格式：[版本 1][临时公钥 32][nonce 12][密文][标签 16]。
/// </summary>
public static class PrivateSeal
{
    public const byte Version = 1;
    private const int Header = 1 + X25519KeyPair.Length + 12;
    private const int TagLength = 16;
    public const int Overhead = Header + TagLength;

    public static byte[] Seal(X25519KeyPair me, ReadOnlySpan<byte> recipient, ReadOnlySpan<byte> plain)
    {
        using var ephemeral = X25519KeyPair.Generate();
        var key = Derive(ephemeral.Dh(recipient), me.Dh(recipient), me.PublicKey, recipient, ephemeral.PublicKey);
        try
        {
            var output = new byte[Header + plain.Length + TagLength];
            output[0] = Version;
            ephemeral.PublicKey.CopyTo(output, 1);
            var nonce = output.AsSpan(1 + X25519KeyPair.Length, 12);
            RandomNumberGenerator.Fill(nonce);
            using var aes = new AesGcm(key, TagLength);
            aes.Encrypt(nonce, plain, output.AsSpan(Header, plain.Length), output.AsSpan(Header + plain.Length), output.AsSpan(0, Header));
            return output;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>解开发方 <paramref name="sender"/> 发给自己的消息；格式不对、被改过、发方不对都返回 null。</summary>
    public static byte[]? Open(X25519KeyPair me, ReadOnlySpan<byte> sender, ReadOnlySpan<byte> sealedData)
    {
        if (sealedData.Length < Overhead || sealedData[0] != Version || sender.Length != X25519KeyPair.Length) return null;
        byte[]? key = null;
        try
        {
            var ephemeral = sealedData.Slice(1, X25519KeyPair.Length);
            key = Derive(me.Dh(ephemeral), me.Dh(sender), sender, me.PublicKey, ephemeral);
            var plain = new byte[sealedData.Length - Overhead];
            using var aes = new AesGcm(key, TagLength);
            aes.Decrypt(sealedData.Slice(1 + X25519KeyPair.Length, 12), sealedData.Slice(Header, plain.Length), sealedData[^TagLength..], plain, sealedData[..Header]);
            return plain;
        }
        catch (CryptographicException)
        {
            return null;
        }
        finally
        {
            if (key is not null) CryptographicOperations.ZeroMemory(key);
        }
    }

    private static byte[] Derive(byte[] es, byte[] ss, ReadOnlySpan<byte> sender, ReadOnlySpan<byte> recipient, ReadOnlySpan<byte> ephemeral)
    {
        byte[] ikm = [.. es, .. ss];
        CryptographicOperations.ZeroMemory(es);
        CryptographicOperations.ZeroMemory(ss);
        byte[] info = [.. "jay-private-v1"u8, .. sender, .. recipient, .. ephemeral];
        try { return HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 32, Encoding.ASCII.GetBytes("jay-private"), info); }
        finally { CryptographicOperations.ZeroMemory(ikm); }
    }
}

/// <summary>房间里在线的一个人（房主发来的名单里的一项）。公钥和指纹对得上才收。</summary>
public sealed record RosterEntry(string Fingerprint, byte[] PublicKey, string Name);

/// <summary>房主转交来的一条端到端消息（发方由房主按成员表盖章，内容房主看不到）。</summary>
public sealed record PrivateFrame(string FromFingerprint, byte[] FromPublicKey, string FromName, byte[] Sealed);
