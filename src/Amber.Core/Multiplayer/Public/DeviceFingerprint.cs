using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace Amber.Core.Multiplayer.Public;

/// <summary>
/// 设备指纹码：JAYF1-XXXX-XXXX-XXXX-XXXX-XXXX。
/// 版本号之后是 12 字节：5 字节随机数 + 7 字节标签，标签 = HMAC-SHA256(本机的指纹密钥, "jay-fp-v1" ‖ 随机数) 的前 7 字节。
/// 所以一台电脑可以随时生成新的码（随机数有 2^40 种），不同的码之间看不出是同一台电脑；
/// 但拿着指纹密钥的一方（本机，以及房主登记过的目录服务）能认出每一个码都属于这台电脑。
/// 版本号决定算法：以后要换算法就加一个新版本，旧码按旧算法照样认。
/// </summary>
public static class FingerprintCode
{
    public const string Prefix = "JAYF";
    public const int Current = 1;
    public const int NonceLength = 5;
    public const int TagLength = 7;
    private const int BodyLength = NonceLength + TagLength;
    private const string Crockford = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>各版本的标签算法。新版本加在这里；不认识的版本号的码一律当无效。</summary>
    private static readonly Dictionary<int, Func<byte[], byte[], byte[]>> Algorithms = new()
    {
        [1] = (key, nonce) =>
        {
            var message = new byte[9 + nonce.Length];
            "jay-fp-v1"u8.CopyTo(message);
            nonce.CopyTo(message, 9);
            return HMACSHA256.HashData(key, message)[..TagLength];
        },
    };

    public static bool IsSupported(int version) => Algorithms.ContainsKey(version);

    public static byte[] Tag(int version, byte[] key, byte[] nonce) =>
        Algorithms.TryGetValue(version, out var tag) ? tag(key, nonce) : throw new ArgumentOutOfRangeException(nameof(version));

    public static string Create(byte[] key, int version = Current)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        return Format(new ParsedFingerprint(version, nonce, Tag(version, key, nonce)));
    }

    public static string Format(ParsedFingerprint code)
    {
        var body = new byte[BodyLength];
        code.Nonce.CopyTo(body, 0);
        code.Tag.CopyTo(body, NonceLength);
        var text = new StringBuilder($"{Prefix}{code.Version}");
        var bits = 0;
        var buffer = 0;
        var written = 0;
        foreach (var b in body)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                if (written % 4 == 0) text.Append('-');
                text.Append(Crockford[(buffer >> bits) & 31]);
                written++;
            }
        }
        // 96 位 = 19 个字符余 1 位，补 4 个 0 凑成第 20 个字符。
        if (bits > 0)
        {
            if (written % 4 == 0) text.Append('-');
            text.Append(Crockford[(buffer << (5 - bits)) & 31]);
        }
        return text.ToString();
    }

    /// <summary>读一个指纹码：不分大小写，忽略空格和横杠，O 当 0、I/L 当 1。格式不对或版本不认识返回 false。</summary>
    public static bool TryParse(string? text, out ParsedFingerprint code)
    {
        code = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var clean = new StringBuilder();
        foreach (var c in text.Trim().ToUpperInvariant())
        {
            if (c is '-' or ' ') continue;
            clean.Append(c switch { 'O' => '0', 'I' or 'L' => '1', _ => c });
        }
        var s = clean.ToString();
        if (!s.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        // 后 20 个字符是正文，前缀和正文之间是版本号。
        if (s.Length < Prefix.Length + 1 + 20) return false;
        var versionText = s[Prefix.Length..^20];
        if (versionText.Length > 3 || !versionText.All(char.IsAsciiDigit) || !int.TryParse(versionText, out var version) || !IsSupported(version)) return false;
        var chars = s[^20..];
        var body = new byte[BodyLength];
        var bits = 0;
        var buffer = 0;
        var at = 0;
        foreach (var c in chars)
        {
            var v = Crockford.IndexOf(c);
            if (v < 0) return false;
            buffer = (buffer << 5) | v;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                if (at < BodyLength) body[at++] = (byte)(buffer >> bits);
            }
        }
        // 补的那几位必须是 0（顺便挡掉一部分抄错的码）。
        if (at != BodyLength || (buffer & ((1 << bits) - 1)) != 0) return false;
        code = new ParsedFingerprint(version, body[..NonceLength], body[NonceLength..]);
        return true;
    }

    /// <summary>这个码是不是这把指纹密钥生成的。</summary>
    public static bool Matches(byte[] key, ParsedFingerprint code) =>
        IsSupported(code.Version) && CryptographicOperations.FixedTimeEquals(Tag(code.Version, key, code.Nonce), code.Tag);
}

public readonly record struct ParsedFingerprint(int Version, byte[] Nonce, byte[] Tag);

/// <summary>
/// 本机的设备指纹（默认没有：用户在联机页打开后才生成）。存两样东西：
/// 指纹密钥（32 字节随机数，生成指纹码用；房主开了“可以用指纹码找到我的房间”时交给目录服务）和设备签名钥匙（Ed25519，敲门时证明“我是这台设备”）。
/// 不绑硬件：换新（<see cref="Rotate"/>）之后以前给出去的码全部作废，房主那边看到的也是一台新设备。
/// 文件和联机身份一样加密保存（Windows 用 DPAPI，其他系统用本机包裹密钥）。
/// </summary>
public sealed class DeviceFingerprint
{
    public const string FileName = "device-fingerprint.key";
    private const int StoredLength = 64;

    private readonly byte[] _key;
    private readonly Ed25519PrivateKeyParameters _sign;

    private DeviceFingerprint(byte[] stored)
    {
        _key = stored[..32];
        _sign = new Ed25519PrivateKeyParameters(stored, 32);
        PublicKey = _sign.GeneratePublicKey().GetEncoded();
    }

    /// <summary>设备签名公钥。</summary>
    public byte[] PublicKey { get; }

    /// <summary>给人看的设备编号（设备签名公钥的指纹），房主的成员列表里显示它。</summary>
    public string DeviceId => MemberIdentity.FingerprintOf(PublicKey);

    /// <summary>交给目录服务的指纹密钥（复制一份）。</summary>
    public byte[] SearchKey => (byte[])_key.Clone();

    public static bool IsEnabled(string directory) => File.Exists(Path.Combine(directory, FileName));

    /// <summary>读本机的设备指纹；没打开过或读不出来时返回 null（读不出来不覆盖原文件，免得一时读不了就把指纹换掉）。</summary>
    public static DeviceFingerprint? Load(string directory)
    {
        var path = Path.Combine(directory, FileName);
        if (!File.Exists(path)) return null;
        try
        {
            var raw = MemberIdentity.Unprotect(directory, File.ReadAllBytes(path), StoredLength);
            try { return new DeviceFingerprint(raw); }
            finally { CryptographicOperations.ZeroMemory(raw); }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>打开设备指纹：已经有就读出来，没有就新建。</summary>
    public static DeviceFingerprint Enable(string directory) => Load(directory) ?? Rotate(directory);

    /// <summary>换新：生成新的指纹密钥和签名钥匙，以前的码全部作废。</summary>
    public static DeviceFingerprint Rotate(string directory)
    {
        Directory.CreateDirectory(directory);
        var raw = RandomNumberGenerator.GetBytes(StoredLength);
        try
        {
            var path = Path.Combine(directory, FileName);
            var temp = path + ".tmp";
            MemberIdentity.WritePrivate(temp, MemberIdentity.Protect(directory, raw));
            File.Move(temp, path, true);
            return new DeviceFingerprint(raw);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(raw);
        }
    }

    /// <summary>关掉设备指纹：删掉文件，以前的码全部作废。</summary>
    public static void Disable(string directory)
    {
        var path = Path.Combine(directory, FileName);
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>生成一个新的指纹码（每次都不一样，都指向这台电脑）。</summary>
    public string NewCode() => FingerprintCode.Create(_key);

    public bool Owns(string code) => FingerprintCode.TryParse(code, out var parsed) && FingerprintCode.Matches(_key, parsed);

    /// <summary>
    /// 敲门时给房主的证明：设备签名钥匙签“房主公钥 ‖ 我的联机公钥”（换个房主、换个人都用不了这份证明），
    /// 再按房主给的随机数算出标签（房主拿它和自己名单里的指纹码比，对上了就知道是名单里的那台电脑）。
    /// </summary>
    public DeviceProof Prove(byte[] hostKey, byte[] guestKey, IEnumerable<(int Version, byte[] Nonce)> challenges)
    {
        var signer = new Ed25519Signer();
        signer.Init(true, _sign);
        var message = DeviceProof.Message(hostKey, guestKey);
        signer.BlockUpdate(message, 0, message.Length);
        var tags = challenges.Take(DeviceProof.MaxChallenges)
            .Select(c => FingerprintCode.IsSupported(c.Version) && c.Nonce.Length == FingerprintCode.NonceLength ? FingerprintCode.Tag(c.Version, _key, c.Nonce) : [])
            .ToArray();
        return new DeviceProof((byte[])PublicKey.Clone(), signer.GenerateSignature(), tags);
    }
}

/// <summary>敲门时附带的设备证明（在握手的加密消息里，只有房主看得到）。</summary>
public sealed record DeviceProof(byte[] PublicKey, byte[] Signature, IReadOnlyList<byte[]> Tags)
{
    public const int MaxChallenges = 16;

    public string DeviceId => MemberIdentity.FingerprintOf(PublicKey);

    internal static byte[] Message(byte[] hostKey, byte[] guestKey) => [.. "jay-device-v1\n"u8, .. hostKey, .. guestKey];

    public bool Verify(byte[] hostKey, byte[] guestKey)
    {
        if (PublicKey.Length != 32 || Signature.Length != 64) return false;
        try
        {
            var verifier = new Ed25519Signer();
            verifier.Init(false, new Ed25519PublicKeyParameters(PublicKey));
            var message = Message(hostKey, guestKey);
            verifier.BlockUpdate(message, 0, message.Length);
            return verifier.VerifySignature(Signature);
        }
        catch (ArgumentException) { return false; }
    }

    /// <summary>名单里有没有一个码和这份证明对得上（<paramref name="challenges"/> 是房主发出去的那组，顺序一致）。</summary>
    public bool MatchesAny(IReadOnlyList<ParsedFingerprint> challenges)
    {
        for (var i = 0; i < challenges.Count && i < Tags.Count; i++)
            if (Tags[i].Length == FingerprintCode.TagLength && CryptographicOperations.FixedTimeEquals(Tags[i], challenges[i].Tag)) return true;
        return false;
    }

    internal JsonObject ToJson() => new()
    {
        ["pub"] = Convert.ToHexStringLower(PublicKey),
        ["sig"] = Convert.ToHexStringLower(Signature),
        ["tags"] = new JsonArray(Tags.Select(t => (JsonNode?)Convert.ToHexStringLower(t)).ToArray()),
    };

    internal static DeviceProof? FromJson(JsonNode? node)
    {
        if (node is not JsonObject o) return null;
        try
        {
            var pub = o["pub"]?.GetValue<string>();
            var sig = o["sig"]?.GetValue<string>();
            if (pub is not { Length: 64 } || sig is not { Length: 128 }) return null;
            var tags = (o["tags"] as JsonArray ?? []).Take(MaxChallenges)
                .Select(t => t?.GetValue<string>() is { Length: <= FingerprintCode.TagLength * 2 } s ? Convert.FromHexString(s) : [])
                .ToArray();
            return new DeviceProof(Convert.FromHexString(pub), Convert.FromHexString(sig), tags);
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException) { return null; }
    }
}

/// <summary>
/// 房主对敲门的人的设备要求。<see cref="Require"/>：必须打开设备指纹；<see cref="Allowed"/> 不空时还必须是名单里的电脑（名单是对方给的指纹码）。
/// </summary>
public sealed record DevicePolicy(bool Require, IReadOnlyList<ParsedFingerprint> Allowed)
{
    public static readonly DevicePolicy None = new(false, []);

    public bool IsActive => Require || Allowed.Count > 0;

    /// <summary>从用户填的文本里读名单（一行或用逗号、空格隔开的多个码），读不出来的跳过。</summary>
    public static IReadOnlyList<ParsedFingerprint> ParseCodes(string? text) =>
        [.. (text ?? "").Split(['\n', '\r', ',', '\uFF0C', ';', '\uFF1B', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => FingerprintCode.TryParse(t, out var c) ? c : (ParsedFingerprint?)null).OfType<ParsedFingerprint>()
            .Take(DeviceProof.MaxChallenges)];

    /// <summary>这份证明过不过得了这条要求。</summary>
    public bool Admits(DeviceProof? proof, byte[] hostKey, byte[] guestKey)
    {
        if (!IsActive) return true;
        if (proof is null || !proof.Verify(hostKey, guestKey)) return false;
        return Allowed.Count == 0 || proof.MatchesAny(Allowed);
    }
}
