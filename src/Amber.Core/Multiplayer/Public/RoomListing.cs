using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Multiplayer.Public;

/// <summary>房间在公共网络上怎么出现。</summary>
public enum RoomVisibility
{
    /// <summary>隐藏：不进房间列表，只有拿着邀请码的人能经公共网络找到房主敲门。</summary>
    Hidden = 0,
    /// <summary>公开：列表里带着一张长期邀请，谁都能申请加入（是否要房主批准由房主定）。</summary>
    Public = 1,
    /// <summary>密码：列表里显示带锁的房间，输入密码才能敲门。</summary>
    Password = 2,
    /// <summary>直连：列表里带着房主的直连邀请码，走打洞 / IPv6 直连，不经过公共网络的节点。</summary>
    Direct = 3,
}

/// <summary>
/// 房间列表里的一项（房主发给目录服务，目录服务原样转给看列表的人）。
/// 列表里没有任何房间凭据：公开房间带的是敲门用的长期邀请，密码房间只带盐，进房间仍要握手、房主批准后在加密通道里拿钥匙。
/// </summary>
public sealed record RoomListing
{
    public const int MaxNameLength = 32;
    public const int MaxTextLength = 4096;

    /// <summary>房间编号（16 字节随机数的十六进制），房间关掉前不变。</summary>
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Host { get; init; }
    public required RoomVisibility Mode { get; init; }
    /// <summary>房主的联机身份公钥（X25519）。敲门后核对：握手里的房主公钥必须是这个，免得有人在目录里冒用别人的房间。</summary>
    public required byte[] HostKey { get; init; }
    /// <summary>房主当前在公共网络里的地址（EasyTier 自动分配，可能变，房主每次续期时更新）。</summary>
    public string Ip { get; init; } = "";
    /// <summary>游戏版本（只是显示用）。</summary>
    public string Game { get; init; } = "";
    public string Version { get; init; } = "";
    public int Players { get; init; }
    /// <summary>加入是否要房主点批准（公开、密码房间）。</summary>
    public bool Approval { get; init; } = true;
    /// <summary>公开房间：长期邀请（JAY2-）。</summary>
    public string? Invite { get; init; }
    /// <summary>密码房间：房间的大厅密钥 L（十六进制）、盐和封装模式；令牌由密码派生。</summary>
    public string? Lobby { get; init; }
    public string? Salt { get; init; }
    public TunnelSettings Tunnel { get; init; } = TunnelSettings.Default;
    /// <summary>直连房间：直连邀请码（JAY2D-）。</summary>
    public string? Direct { get; init; }
    /// <summary>目录服务最后一次收到续期的时间（目录服务填，不在签名里）。</summary>
    public DateTimeOffset Updated { get; init; }
    /// <summary>
    /// 房间的签名公钥（Ed25519，32 字节）。房间编号 = 它的 SHA-256 前 16 字节，所以编号天生属于拿着私钥的房主：
    /// 目录（以后多个节点互相同步列表时也一样）只认签名对的、序号更大的版本，谁也改不了、顶替不了别人的房间。
    /// </summary>
    public byte[] SignKey { get; init; } = [];
    /// <summary>序号（房主发布时的 Unix 毫秒时间），新版本必须比旧版本大，防止拿旧的签名版本重放。</summary>
    public long Seq { get; init; }
    public byte[] Signature { get; init; } = [];

    public string HostFingerprint => MemberIdentity.FingerprintOf(HostKey);

    /// <summary>公开 / 密码房间：敲门用的邀请。密码房间要给密码；密码不对时能造出邀请，但敲门握手会失败。</summary>
    public JayInvite? ToInvite(string? password = null)
    {
        switch (Mode)
        {
            case RoomVisibility.Public:
                return JayInvite.TryParse(Invite, out var invite) ? invite : null;
            case RoomVisibility.Password:
                if (password is null || Lobby is null || Salt is null) return null;
                byte[] lobby, salt;
                try
                {
                    lobby = Convert.FromHexString(Lobby);
                    salt = Convert.FromHexString(Salt);
                }
                catch (FormatException) { return null; }
                if (lobby.Length != JayInvite.KeyLength || salt.Length != RoomPassword.SaltLength) return null;
                return new JayInvite(lobby, RoomPassword.Token(password, salt), Tunnel);
            default:
                return null;
        }
    }

    /// <summary>签名覆盖的内容：除目录填的 Updated 和签名本身以外的全部字段。</summary>
    internal byte[] SignedBytes() => Encoding.UTF8.GetBytes("jay-room-v1\n" + Fields(includeSignature: false).ToJsonString());

    public JsonObject ToJson() => Fields(includeSignature: true);

    private JsonObject Fields(bool includeSignature)
    {
        var o = new JsonObject
        {
            ["id"] = Id,
            ["name"] = Name,
            ["host"] = Host,
            ["mode"] = (int)Mode,
            ["key"] = Convert.ToHexStringLower(HostKey),
            ["ip"] = Ip,
            ["game"] = Game,
            ["ver"] = Version,
            ["players"] = Players,
            ["approval"] = Approval,
            ["tm"] = (int)Tunnel.Mode,
            ["td"] = Tunnel.Disguise,
        };
        if (Invite is not null) o["invite"] = Invite;
        if (Lobby is not null) o["lobby"] = Lobby;
        if (Salt is not null) o["salt"] = Salt;
        if (Direct is not null) o["direct"] = Direct;
        o["sk"] = Convert.ToHexStringLower(SignKey);
        o["seq"] = Seq;
        if (!includeSignature) return o;
        o["sig"] = Convert.ToHexStringLower(Signature);
        if (Updated != default) o["updated"] = Updated.ToUnixTimeSeconds();
        return o;
    }

    /// <summary>读一项；字段不对、太长或缺东西时返回 null（目录服务和客户端都这样过滤别人发来的数据）。</summary>
    public static RoomListing? FromJson(JsonNode? node)
    {
        if (node is not JsonObject o) return null;
        try
        {
            var id = Str(o, "id", 32);
            var key = Str(o, "key", 64);
            if (id is null || id.Length != 32 || !IsHex(id) || key is null || key.Length != 64 || !IsHex(key)) return null;
            var mode = o["mode"]?.GetValue<int>() ?? -1;
            if (mode is < 0 or > 3) return null;
            var tm = o["tm"]?.GetValue<int>() ?? 0;
            if (tm is < 0 or > 3) return null;
            var ip = Str(o, "ip", 15) ?? "";
            if (ip.Length > 0 && !IsPublicNetworkIp(ip)) return null;
            var listing = new RoomListing
            {
                Id = id.ToLowerInvariant(),
                Name = KnockProtocol.SanitizeName(Str(o, "name", 256) ?? ""),
                Host = KnockProtocol.SanitizeName(Str(o, "host", 256) ?? ""),
                Mode = (RoomVisibility)mode,
                HostKey = Convert.FromHexString(key),
                Ip = ip,
                Game = KnockProtocol.SanitizeName(Str(o, "game", 64) ?? ""),
                Version = KnockProtocol.SanitizeName(Str(o, "ver", 64) ?? ""),
                Players = Math.Clamp(o["players"]?.GetValue<int>() ?? 0, 0, 999),
                Approval = o["approval"]?.GetValue<bool>() ?? true,
                Tunnel = new TunnelSettings((TransportMode)tm, o["td"]?.GetValue<bool>() ?? false),
                Invite = Str(o, "invite", 200),
                Lobby = Str(o, "lobby", 32),
                Salt = Str(o, "salt", 32),
                Direct = Str(o, "direct", MaxTextLength),
                Updated = o["updated"]?.GetValue<long>() is { } t ? DateTimeOffset.FromUnixTimeSeconds(t) : default,
                SignKey = Hex(Str(o, "sk", 64), 32),
                Seq = o["seq"]?.GetValue<long>() ?? 0,
                Signature = Hex(Str(o, "sig", 128), 64),
            };
            // 签名不对、编号不是签名公钥派生的：一律丢掉。
            if (!listing.Verify()) return null;
            return listing.Mode switch
            {
                RoomVisibility.Public when listing.ToInvite() is null => null,
                RoomVisibility.Password when listing.Lobby is not { Length: 32 } || !IsHex(listing.Lobby) || listing.Salt is not { Length: RoomPassword.SaltLength * 2 } || !IsHex(listing.Salt) => null,
                RoomVisibility.Direct when string.IsNullOrWhiteSpace(listing.Direct) => null,
                _ => listing,
            };
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException or ArgumentException) { return null; }
    }

    /// <summary>公共网络网段里的 IPv4 地址（10.235.x.y），别的地址一律不认：目录里的地址只能指向公共网络里的人。</summary>
    public static bool IsPublicNetworkIp(string ip) =>
        System.Net.IPAddress.TryParse(ip, out var a) && a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
        && a.GetAddressBytes() is [10, 235, _, _] && ip == a.ToString();

    private static string? Str(JsonObject o, string name, int max)
    {
        var v = o[name]?.GetValue<string>();
        return v is null ? null : v.Length <= max ? v : throw new FormatException();
    }

    private static bool IsHex(string s) => s.All(char.IsAsciiHexDigit);

    private static byte[] Hex(string? s, int length) =>
        s is not null && s.Length == length * 2 && IsHex(s) ? Convert.FromHexString(s) : throw new FormatException();

    /// <summary>房间编号：签名公钥的 SHA-256 前 16 字节。</summary>
    public static string IdOf(ReadOnlySpan<byte> signKey) => Convert.ToHexStringLower(SHA256.HashData(signKey)[..16]);

    /// <summary>签名对、编号和签名公钥对得上。</summary>
    public bool Verify() =>
        SignKey.Length == 32 && Signature.Length == 64 && Id == IdOf(SignKey) && RoomSigner.Verify(SignKey, SignedBytes(), Signature);
}

/// <summary>房间的签名钥匙（Ed25519，用 BouncyCastle 的实现）：每个房间一把，房间关掉就扔。</summary>
public sealed class RoomSigner
{
    private readonly Org.BouncyCastle.Crypto.Parameters.Ed25519PrivateKeyParameters _key;
    private long _lastSeq;

    public RoomSigner()
    {
        _key = new Org.BouncyCastle.Crypto.Parameters.Ed25519PrivateKeyParameters(RandomNumberGenerator.GetBytes(32));
        PublicKey = _key.GeneratePublicKey().GetEncoded();
        RoomId = RoomListing.IdOf(PublicKey);
    }

    public byte[] PublicKey { get; }
    public string RoomId { get; }

    /// <summary>填上编号、签名公钥和更大的序号，然后签名。</summary>
    public RoomListing Sign(RoomListing listing, DateTimeOffset now)
    {
        var seq = Math.Max(now.ToUnixTimeMilliseconds(), Interlocked.Read(ref _lastSeq) + 1);
        Interlocked.Exchange(ref _lastSeq, seq);
        // 先按收件方的规则整理好文字字段，免得对方整理后和签名的内容对不上。
        var unsigned = listing with
        {
            Id = RoomId, SignKey = PublicKey, Seq = seq, Signature = [], Updated = default,
            Name = KnockProtocol.SanitizeName(listing.Name), Host = KnockProtocol.SanitizeName(listing.Host),
            Game = KnockProtocol.SanitizeName(listing.Game), Version = KnockProtocol.SanitizeName(listing.Version),
            Players = Math.Clamp(listing.Players, 0, 999),
        };
        return unsigned with { Signature = SignBytes(unsigned.SignedBytes()) };
    }

    /// <summary>撤下房间的签名（带序号，旧的撤下请求不能重放到以后的房间版本上）。</summary>
    public (long Seq, byte[] Signature) SignWithdraw(DateTimeOffset now)
    {
        var seq = Math.Max(now.ToUnixTimeMilliseconds(), Interlocked.Read(ref _lastSeq) + 1);
        Interlocked.Exchange(ref _lastSeq, seq);
        return (seq, SignBytes(WithdrawBytes(RoomId, seq)));
    }

    internal static byte[] WithdrawBytes(string id, long seq) => Encoding.UTF8.GetBytes($"jay-withdraw-v1\n{id}\n{seq.ToString(System.Globalization.CultureInfo.InvariantCulture)}");

    private byte[] SignBytes(byte[] message)
    {
        var signer = new Org.BouncyCastle.Crypto.Signers.Ed25519Signer();
        signer.Init(true, _key);
        signer.BlockUpdate(message, 0, message.Length);
        return signer.GenerateSignature();
    }

    public static bool Verify(byte[] publicKey, byte[] message, byte[] signature)
    {
        try
        {
            var verifier = new Org.BouncyCastle.Crypto.Signers.Ed25519Signer();
            verifier.Init(false, new Org.BouncyCastle.Crypto.Parameters.Ed25519PublicKeyParameters(publicKey));
            verifier.BlockUpdate(message, 0, message.Length);
            return verifier.VerifySignature(signature);
        }
        catch (ArgumentException) { return false; }
    }
}

/// <summary>
/// 密码房间：由密码和房间的盐派生敲门令牌（PBKDF2-HMAC-SHA256，60 万次）。密码本身不发给任何人；
/// 敲门握手用令牌派生的 psk，密码不对握手就失败，房主那边每分钟失败太多会暂停敲门（在线猜密码很慢）。
/// </summary>
public static class RoomPassword
{
    public const int SaltLength = 16;
    public const int Iterations = 600_000;
    public const int MinLength = 4;

    public static byte[] NewSalt() => RandomNumberGenerator.GetBytes(SaltLength);

    public static byte[] Token(string password, ReadOnlySpan<byte> salt) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password.Normalize(NormalizationForm.FormKC)), salt, Iterations, HashAlgorithmName.SHA256, JayInvite.KeyLength);
}
