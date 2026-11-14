using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace Amber.Core.Multiplayer.Public;

/// <summary>
/// Device codes. The current version is JAYF2-XXXX-XXXX-XXXX-XXXX-XXXX-XXXX-XXXX-XXXX: after the version come 20 bytes,
/// a 5-byte random nonce, a 7-byte tag and an 8-byte check.
/// <list type="bullet">
/// <item>tag = HMAC-SHA256(fingerprint key, "jay-fp-v2" ‖ nonce)[..7]. Whoever holds the fingerprint key (this computer,
/// and a directory the host gave it to for search) can tell that the code belongs to this computer; nobody else can make one.</item>
/// <item>check = SHA-256("jay-fp-check-v2" ‖ nonce ‖ answer ‖ device signing key)[..8], with answer =
/// HMAC-SHA256(fingerprint key, "jay-fp-answer-v2" ‖ nonce). A host that has the code checks a knocking device with it: the
/// device sends the answer for the nonce and proves its signing key (<see cref="DeviceProof"/>). An answer is no tag, so a
/// host that asks for answers learns nothing it could make a code from.</item>
/// </list>
/// A computer can make a new code at any time (2^40 nonces), and codes can't be told apart as one computer's without the
/// fingerprint key, or without asking that computer. JAYF1 codes (nonce and a "jay-fp-v1" tag, no check) still find rooms;
/// a knocking device no longer answers for them, so a JAYF1 code on a host's list admits nobody.
/// </summary>
public static class FingerprintCode
{
    public const string Prefix = "JAYF";
    public const int Current = 2;
    public const int NonceLength = 5;
    public const int TagLength = 7;
    /// <summary>Length of the device check in a version 2 code.</summary>
    public const int CheckLength = 8;
    /// <summary>Length of a device's answer to a version 2 nonce.</summary>
    public const int AnswerLength = 32;
    private const string Crockford = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>The tag algorithm of each version. Codes of any other version are invalid.</summary>
    private static readonly Dictionary<int, Func<byte[], byte[], byte[]>> Algorithms = new()
    {
        [1] = (key, nonce) => HMACSHA256.HashData(key, (byte[])[.. "jay-fp-v1"u8, .. nonce])[..TagLength],
        [2] = (key, nonce) => HMACSHA256.HashData(key, (byte[])[.. "jay-fp-v2"u8, .. nonce])[..TagLength],
    };

    public static bool IsSupported(int version) => Algorithms.ContainsKey(version);

    /// <summary>Does a code of this version carry a device check (can it be put on a host's list)?</summary>
    public static bool HasCheck(int version) => version >= 2;

    private static int BodyLength(int version) => NonceLength + TagLength + (HasCheck(version) ? CheckLength : 0);

    private static int TextLength(int version) => (BodyLength(version) * 8 + 4) / 5;

    public static byte[] Tag(int version, byte[] key, byte[] nonce) =>
        Algorithms.TryGetValue(version, out var tag) ? tag(key, nonce) : throw new ArgumentOutOfRangeException(nameof(version));

    /// <summary>The device's answer to a version 2 nonce: only the holder of the fingerprint key can compute it.</summary>
    public static byte[] Answer(byte[] key, byte[] nonce) => HMACSHA256.HashData(key, (byte[])[.. "jay-fp-answer-v2"u8, .. nonce]);

    /// <summary>The check a version 2 code carries for this nonce, answer and device signing key.</summary>
    public static byte[] Check(byte[] nonce, byte[] answer, byte[] devicePublicKey) =>
        SHA256.HashData((byte[])[.. "jay-fp-check-v2"u8, .. nonce, .. answer, .. devicePublicKey])[..CheckLength];

    /// <summary>A new code of the current version for this fingerprint key and device signing key.</summary>
    public static string Create(byte[] key, byte[] devicePublicKey)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        return Format(new ParsedFingerprint(Current, nonce, Tag(Current, key, nonce), Check(nonce, Answer(key, nonce), devicePublicKey)));
    }

    public static string Format(ParsedFingerprint code)
    {
        var body = new byte[BodyLength(code.Version)];
        code.Nonce.CopyTo(body, 0);
        code.Tag.CopyTo(body, NonceLength);
        if (HasCheck(code.Version)) (code.Check ?? throw new ArgumentException("Check missing", nameof(code))).CopyTo(body, NonceLength + TagLength);
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
        // Version 1: 96 bits are 19 characters and 1 bit, padded with 4 zero bits to a 20th. Version 2's 160 bits fit exactly.
        if (bits > 0)
        {
            if (written % 4 == 0) text.Append('-');
            text.Append(Crockford[(buffer << (5 - bits)) & 31]);
        }
        return text.ToString();
    }

    /// <summary>读一个设备码：不分大小写，忽略空格和横杠，O 当 0、I/L 当 1。格式不对或版本不认识返回 false。</summary>
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
        if (!s.StartsWith(Prefix, StringComparison.Ordinal) || s.Length > Prefix.Length + 3 + TextLength(Current)) return false;
        // The version sits between the prefix and the body, whose length depends on the version.
        foreach (var version in Algorithms.Keys)
        {
            var versionText = version.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (s.Length != Prefix.Length + versionText.Length + TextLength(version) || !s.AsSpan(Prefix.Length).StartsWith(versionText)) continue;
            if (Decode(s[^TextLength(version)..], BodyLength(version)) is not { } body) return false;
            code = new ParsedFingerprint(version, body[..NonceLength], body[NonceLength..(NonceLength + TagLength)],
                HasCheck(version) ? body[(NonceLength + TagLength)..] : null);
            return true;
        }
        return false;
    }

    private static byte[]? Decode(string chars, int length)
    {
        var body = new byte[length];
        var bits = 0;
        var buffer = 0;
        var at = 0;
        foreach (var c in chars)
        {
            var v = Crockford.IndexOf(c);
            if (v < 0) return null;
            buffer = (buffer << 5) | v;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                if (at < length) body[at++] = (byte)(buffer >> bits);
            }
        }
        // The padding bits must be zero (which also catches some miscopied codes).
        return at == length && (buffer & ((1 << bits) - 1)) == 0 ? body : null;
    }

    /// <summary>Was this code made with this fingerprint key (what a directory checks to find a room)?</summary>
    public static bool Matches(byte[] key, ParsedFingerprint code) =>
        IsSupported(code.Version) && CryptographicOperations.FixedTimeEquals(Tag(code.Version, key, code.Nonce), code.Tag);
}

/// <param name="Check">The device check of a version 2 code (<see cref="FingerprintCode.Check"/>); null for version 1.</param>
public readonly record struct ParsedFingerprint(int Version, byte[] Nonce, byte[] Tag, byte[]? Check = null);

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

    /// <summary>生成一个新的设备码（每次都不一样，都指向这台电脑）。</summary>
    public string NewCode() => FingerprintCode.Create(_key, PublicKey);

    /// <summary>Is this one of this computer's codes (a version 2 code also checks against this device's signing key)?</summary>
    public bool Owns(string code) =>
        FingerprintCode.TryParse(code, out var parsed) && FingerprintCode.Matches(_key, parsed)
        && (!FingerprintCode.HasCheck(parsed.Version)
            || CryptographicOperations.FixedTimeEquals(FingerprintCode.Check(parsed.Nonce, FingerprintCode.Answer(_key, parsed.Nonce), PublicKey), parsed.Check));

    /// <summary>
    /// 敲门时给房主的证明：设备签名钥匙签“房主公钥 ‖ 我的联机公钥”（换个房主、换个人都用不了这份证明），
    /// and for each version 2 nonce the host sent, this computer's answer (the host checks it against the code on its list,
    /// together with the signing key). Version 1 nonces get no answer: their answer would be a code's tag, and a host could
    /// make new codes from it.
    /// </summary>
    public DeviceProof Prove(byte[] hostKey, byte[] guestKey, IEnumerable<(int Version, byte[] Nonce)> challenges)
    {
        var signer = new Ed25519Signer();
        signer.Init(true, _sign);
        var message = DeviceProof.Message(hostKey, guestKey);
        signer.BlockUpdate(message, 0, message.Length);
        var answers = challenges.Take(DeviceProof.MaxChallenges)
            .Select(c => FingerprintCode.HasCheck(c.Version) && FingerprintCode.IsSupported(c.Version) && c.Nonce.Length == FingerprintCode.NonceLength
                ? FingerprintCode.Answer(_key, c.Nonce) : [])
            .ToArray();
        return new DeviceProof((byte[])PublicKey.Clone(), signer.GenerateSignature(), answers);
    }
}

/// <summary>敲门时附带的设备证明（在握手的加密消息里，只有房主看得到）。</summary>
/// <param name="Answers">The answers to the host's nonces, in the same order (empty where there is none).</param>
public sealed record DeviceProof(byte[] PublicKey, byte[] Signature, IReadOnlyList<byte[]> Answers)
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

    /// <summary>
    /// Does a code on the list match this proof (<paramref name="challenges"/> are the codes the host sent nonces for, in
    /// the same order)? Only version 2 codes can: the answer and this proof's signing key must give the code's check.
    /// </summary>
    public bool MatchesAny(IReadOnlyList<ParsedFingerprint> challenges)
    {
        for (var i = 0; i < challenges.Count && i < Answers.Count; i++)
            if (challenges[i] is { Check: { Length: FingerprintCode.CheckLength } check } code && FingerprintCode.HasCheck(code.Version)
                && Answers[i].Length == FingerprintCode.AnswerLength
                && CryptographicOperations.FixedTimeEquals(FingerprintCode.Check(code.Nonce, Answers[i], PublicKey), check)) return true;
        return false;
    }

    internal JsonObject ToJson() => new()
    {
        ["pub"] = Convert.ToHexStringLower(PublicKey),
        ["sig"] = Convert.ToHexStringLower(Signature),
        ["ans"] = new JsonArray(Answers.Select(t => (JsonNode?)Convert.ToHexStringLower(t)).ToArray()),
    };

    internal static DeviceProof? FromJson(JsonNode? node)
    {
        if (node is not JsonObject o) return null;
        try
        {
            var pub = o["pub"]?.GetValue<string>();
            var sig = o["sig"]?.GetValue<string>();
            if (pub is not { Length: 64 } || sig is not { Length: 128 }) return null;
            var answers = (o["ans"] as JsonArray ?? []).Take(MaxChallenges)
                .Select(t => t?.GetValue<string>() is { Length: FingerprintCode.AnswerLength * 2 } s ? Convert.FromHexString(s) : [])
                .ToArray();
            return new DeviceProof(Convert.FromHexString(pub), Convert.FromHexString(sig), answers);
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

    /// <summary>How many codes on the list are of a version without a device check (JAYF1): they admit nobody.</summary>
    public int OldCodes => Allowed.Count(c => !FingerprintCode.HasCheck(c.Version));

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
