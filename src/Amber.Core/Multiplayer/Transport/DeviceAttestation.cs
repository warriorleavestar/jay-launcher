using System.Security.Cryptography;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;

namespace Amber.Core.Multiplayer.Transport;

/// <summary>
/// 一套设备验证方案：编号、盐值和证明长度。编号和盐值决定怎么从设备密钥派生出证明，
/// 一旦某套被摸清了，房主换一套（改编号/盐值）就能作废旧的——换方案不用重新编译。
/// 仓库里只带一套默认方案（<see cref="Default"/>），真正用的那套从房主本机的私有索引里读，
/// 经加密的敲门回复发给被批准的成员，从不进仓库、不进日志。
/// </summary>
public sealed record AttestationScheme(int Id, byte[] Salt, int ProofLength)
{
    public const int MinProof = 16;
    public const int MaxProof = 32;
    public const int MinSalt = 8;
    public const int MaxSalt = 64;

    /// <summary>仓库自带的保底方案：没有私有索引时用它，保证全新构建也能互联。</summary>
    public static readonly AttestationScheme Default = new(1, "jay-attest-v1"u8.ToArray(), 24);

    public bool IsValid =>
        Id is > 0 and < 1 << 20 &&
        Salt.Length is >= MinSalt and <= MaxSalt &&
        ProofLength is >= MinProof and <= MaxProof;

    /// <summary>进敲门回复的部分（已经在加密通道里了）：编号、盐值、证明长度。</summary>
    internal JsonObject ToJson() => new()
    {
        ["id"] = Id,
        ["salt"] = Convert.ToBase64String(Salt),
        ["len"] = ProofLength,
    };

    /// <summary>从敲门回复里读；没有这段（旧房主）或格式不对就退回默认方案。</summary>
    internal static AttestationScheme FromJson(JsonNode? json)
    {
        if (json is null) return Default;
        try
        {
            var id = json["id"]?.GetValue<int>();
            var saltText = json["salt"]?.GetValue<string>();
            var len = json["len"]?.GetValue<int>();
            if (id is not { } i || saltText is null || len is not { } l) return Default;
            var salt = Convert.FromBase64String(saltText);
            var scheme = new AttestationScheme(i, salt, l);
            return scheme.IsValid ? scheme : Default;
        }
        catch (Exception e) when (e is FormatException or InvalidOperationException or OverflowException)
        {
            return Default;
        }
    }
}

/// <summary>
/// 设备验证的核心：用两台电脑长期设备密钥（<see cref="MemberIdentity"/>）之间的 X25519 共享密钥，
/// 对一个「绑定值」（每条连接的 Noise 握手哈希）算出一个短证明。
/// 只有真正握着设备私钥的人才能算出对的证明——光有房间的 psk 或邀请码是算不出来的。
/// 这把共享密钥不写盘、不进日志，用完 <see cref="Dispose"/> 清零。
/// </summary>
public sealed class DeviceAttestor : IDisposable
{
    /// <summary>证明的方向：发起方（加入的成员）和应答方（房主）各算各的，防止把对方的证明原样反射回去。</summary>
    public enum Role { Initiator = 0, Responder = 1 }

    private readonly byte[] _shared;
    private readonly AttestationScheme _scheme;
    private bool _disposed;

    /// <param name="localDevice">本机长期设备密钥。</param>
    /// <param name="remoteDevicePublic">对方的长期设备公钥（敲门时已经认证过）。</param>
    public DeviceAttestor(X25519KeyPair localDevice, ReadOnlySpan<byte> remoteDevicePublic, AttestationScheme scheme)
    {
        ArgumentNullException.ThrowIfNull(localDevice);
        if (!scheme.IsValid) throw new ArgumentException("invalid attestation scheme", nameof(scheme));
        _scheme = scheme;
        // X25519(a_priv, b_pub) == X25519(b_priv, a_pub)：两边算出同一把共享密钥。
        _shared = localDevice.Dh(remoteDevicePublic);
    }

    /// <summary>对这条连接的绑定值算一个证明。counter 让会话期的反复验证每轮都不一样。</summary>
    public byte[] Proof(ReadOnlySpan<byte> binding, Role role, ulong counter)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // 方案编号 + 方向进 HKDF 的 info：换方案或换方向，派生出的 key 就完全不同。
        var info = Encoding.ASCII.GetBytes($"jay-dev-attest|{_scheme.Id}|{(int)role}");
        Span<byte> key = stackalloc byte[32];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, _shared, key, _scheme.Salt, info);
        Span<byte> message = stackalloc byte[binding.Length + sizeof(ulong)];
        binding.CopyTo(message);
        BinaryPrimitives.WriteUInt64BigEndian(message[binding.Length..], counter);
        Span<byte> mac = stackalloc byte[32];
        HMACSHA256.HashData(key, message, mac);
        CryptographicOperations.ZeroMemory(key);
        return mac[.._scheme.ProofLength].ToArray();
    }

    /// <summary>常数时间比对对方送来的证明。长度不对或不匹配都返回 false，从不抛。</summary>
    public bool Verify(ReadOnlySpan<byte> binding, Role role, ulong counter, ReadOnlySpan<byte> proof)
    {
        if (_disposed || proof.Length != _scheme.ProofLength) return false;
        var expected = Proof(binding, role, counter);
        try { return CryptographicOperations.FixedTimeEquals(expected, proof); }
        finally { CryptographicOperations.ZeroMemory(expected); }
    }

    public int ProofLength => _scheme.ProofLength;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CryptographicOperations.ZeroMemory(_shared);
    }
}

/// <summary>设备验证没过：对方连上了房间的通道（有 psk），却证明不了自己是那台设备。调用方照常断开。</summary>
public sealed class DeviceAttestationException(string message) : CryptographicException(message);

/// <summary>
/// 房主本机的私有方案索引（绝不进仓库）：环境变量 JAY_ATTEST_INDEX 指向的文件，没有就是数据目录下的 attest.index。
/// 格式：{ "active": 2, "schemes": [ { "id": 2, "salt": "base64", "len": 24 }, ... ] }。
/// 某套方案被摸清了：加一套新的、把 active 改过去、重开房间即可；旧成员下次进房自动拿到新方案。
/// 文件不存在、坏了、active 指向不存在的方案，一律退回 <see cref="AttestationScheme.Default"/>，不崩。
/// </summary>
public static class AttestationIndex
{
    public const string FileName = "attest.index";
    public const string EnvironmentVariable = "JAY_ATTEST_INDEX";

    public static string PathFor(string dataDirectory) =>
        Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } custom ? custom : System.IO.Path.Combine(dataDirectory, FileName);

    public static AttestationScheme Active(string dataDirectory)
    {
        try
        {
            var path = PathFor(dataDirectory);
            return File.Exists(path) ? Parse(File.ReadAllText(path)) : AttestationScheme.Default;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return AttestationScheme.Default;
        }
    }

    internal static AttestationScheme Parse(string text)
    {
        try
        {
            if (JsonNode.Parse(text) is not JsonObject root || root["active"]?.GetValue<int>() is not { } active) return AttestationScheme.Default;
            if (root["schemes"] is not JsonArray schemes) return AttestationScheme.Default;
            foreach (var item in schemes)
            {
                var scheme = AttestationScheme.FromJson(item);
                if (scheme.Id == active && !ReferenceEquals(scheme, AttestationScheme.Default)) return scheme;
            }
            return AttestationScheme.Default;
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            return AttestationScheme.Default;
        }
    }
}
