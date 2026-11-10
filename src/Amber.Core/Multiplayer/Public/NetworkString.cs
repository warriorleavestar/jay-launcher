using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Amber.Core.Localization;

namespace Amber.Core.Multiplayer.Public;

/// <summary>
/// 别的公共网络的“连接串”：网络名、密钥、节点、（以后的）服务器配置文件哈希，加一个 Ed25519 签名。
/// 格式：<c>JAYNET1-</c> + base64url(内容 JSON) + <c>.</c> + base64url(签名公钥 32 ‖ 签名 64)。
/// 签名的是 <c>"jay-network-v1\n"</c> + 内容 JSON 的原始字节，所以内容一个字节都不能改。
/// 前缀里的数字是格式版本，以后换算法就换数字，老版本照样能读。
/// <para>
/// 谁都能签一个连接串（自己搭的网络），但只有 Jay 注册表的钥匙签的才算“已审核”：
/// 网络先在 GitHub 的 networks/ 里提交、审核合并，再由 CI 用注册表钥匙签名。没审核的要用户自己确认才加。
/// </para>
/// </summary>
public static partial class NetworkString
{
    public const string Prefix = "JAYNET";
    public const int Version = 1;
    public const string Current = "JAYNET1-";
    public const int MaxLength = 8192;
    public const int MaxNodes = 16;
    /// <summary>
    /// At most this many directory keys: one per directory node (up to four) plus room for a new key per node while
    /// rotating. Deliberately larger than <see cref="PublicNetworkProfile.MaxDirectories"/>: every shipped Jay refuses a
    /// string with more keys than this, so it can't grow later without breaking older versions.
    /// </summary>
    public const int MaxDirectoryKeys = 8;

    /// <summary>
    /// Jay 注册表的签名公钥（base64）。可以有多把（换钥匙时新旧并存一段时间）。
    /// 还没生成注册表钥匙时为空：这时所有连接串都按“未审核”处理，要用户确认。
    /// </summary>
    public static readonly string[] RegistryKeys = [];

    private static readonly byte[] Domain = "jay-network-v1\n"u8.ToArray();

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,31}$")]
    private static partial Regex IdPattern();

    /// <summary>网络编号能不能用：2~32 个小写字母、数字、减号，不能以减号开头。</summary>
    public static bool IsValidId(string? id) => id is not null && IdPattern().IsMatch(id);

    /// <summary>用签名钥匙把网络信息做成连接串。</summary>
    public static string Create(NetworkInfo info, NetworkSigner signer)
    {
        if (Validate(info) is { } problem) throw new InvalidDataException(problem);
        var payload = Encoding.UTF8.GetBytes(info.ToJson().ToJsonString());
        var signature = signer.Sign([.. Domain, .. payload]);
        return Current + Base64Url(payload) + "." + Base64Url([.. signer.PublicKey, .. signature]);
    }

    /// <summary>
    /// 读连接串并验签。格式不对、签名不对、内容不合规、已经过期都抛 <see cref="InvalidDataException"/>（消息给用户看）。
    /// </summary>
    /// <param name="trusted">认哪些注册表钥匙；不给就用 <see cref="RegistryKeys"/>（测试里换成测试钥匙）。</param>
    public static SignedNetwork Parse(string text, IEnumerable<byte[]>? trusted = null, DateTimeOffset? now = null)
    {
        text = (text ?? "").Trim();
        if (text.Length is 0 or > MaxLength || !text.StartsWith(Prefix, StringComparison.Ordinal)) throw Bad("core.mp.network.bad_string");
        var dash = text.IndexOf('-');
        if (dash < 0 || !int.TryParse(text.AsSpan(Prefix.Length, dash - Prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var version))
            throw Bad("core.mp.network.bad_string");
        if (version != Version) throw new InvalidDataException(L.F("core.mp.network.new_version", version));
        var parts = text[(dash + 1)..].Split('.');
        if (parts.Length != 2 || FromBase64Url(parts[0]) is not { } payload || FromBase64Url(parts[1]) is not { Length: 96 } tail)
            throw Bad("core.mp.network.bad_string");
        var key = tail[..32];
        var signature = tail[32..];
        if (!RoomSigner.Verify(key, [.. Domain, .. payload], signature)) throw Bad("core.mp.network.bad_signature");
        NetworkInfo info;
        try { info = NetworkInfo.FromJson(JsonNode.Parse(payload) as JsonObject ?? throw Bad("core.mp.network.bad_string")); }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException) { throw Bad("core.mp.network.bad_string"); }
        if (Validate(info) is { } problem) throw new InvalidDataException(problem);
        if (info.Expires is { } exp && exp <= (now ?? DateTimeOffset.UtcNow)) throw Bad("core.mp.network.expired");
        var registry = (trusted ?? RegistryKeys.Select(Convert.FromBase64String)).Any(k => k.AsSpan().SequenceEqual(key));
        return new SignedNetwork(info, key, registry, text);
    }

    /// <summary>不抛异常的版本：读不了返回 null。</summary>
    public static SignedNetwork? TryParse(string? text, IEnumerable<byte[]>? trusted = null, DateTimeOffset? now = null)
    {
        try { return Parse(text ?? "", trusted, now); }
        catch (InvalidDataException) { return null; }
    }

    /// <summary>网络信息合不合规；合规返回 null，否则返回给用户看的原因。</summary>
    public static string? Validate(NetworkInfo info)
    {
        if (!IsValidId(info.Id)) return L.T("core.mp.network.bad_id");
        if (string.IsNullOrWhiteSpace(info.Name) || info.Name.Length > 40) return L.T("core.mp.network.bad_name");
        if (string.IsNullOrWhiteSpace(info.NetworkName) || info.NetworkName.Length > 64 || string.IsNullOrEmpty(info.Secret) || info.Secret.Length > 128)
            return L.T("core.mp.network.bad_easytier");
        if (info.NetworkName == PublicNetworkProfile.JayNetworkName && info.Id != "jay") return L.T("core.mp.network.bad_easytier");
        if (info.Nodes.Count is 0 or > MaxNodes || info.Nodes.Any(n => !RelayNodes.IsValid(n))) return L.T("core.mp.network.bad_nodes");
        if (info.Home is { } home && !IsHttps(home)) return L.T("core.mp.network.bad_link");
        if (info.Config is { } config && (!IsHttps(config.Url) || config.Sha256.Length != 64 || !config.Sha256.All(Uri.IsHexDigit)))
            return L.T("core.mp.network.bad_config");
        if (info.DirectoryKeys is { } dirKeys && (dirKeys.Count is 0 or > MaxDirectoryKeys || dirKeys.Any(k => k.Length != DirectoryChannel.KeyLength)))
            return L.T("core.mp.network.bad_dir_keys");
        return null;
    }

    private static bool IsHttps(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && url.Length <= 512;

    private static InvalidDataException Bad(string key) => new(L.T(key));

    internal static string Base64Url(ReadOnlySpan<byte> data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static byte[]? FromBase64Url(string text)
    {
        if (text.Length == 0 || text.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))) return null;
        var s = text.Replace('-', '+').Replace('_', '/');
        s += (s.Length % 4) switch { 2 => "==", 3 => "=", 0 => "", _ => "!" };
        try { return Convert.FromBase64String(s); }
        catch (FormatException) { return null; }
    }
}

/// <summary>网络服务器配置文件（第 5 期用）：地址和 SHA-256，下载后对不上哈希就不用。</summary>
public sealed record NetworkConfigRef(string Url, string Sha256);

/// <summary>连接串里的内容。</summary>
/// <param name="Id">网络编号（本机保存用，也是注册表里的文件名）。</param>
/// <param name="NetworkName">EasyTier 网络名。</param>
/// <param name="Secret">EasyTier 网络密钥（公共网络的密钥是公开的，只用来和别的网络分开）。</param>
/// <param name="Seq">版本号：同一把钥匙签的同一个网络，新的（更大的）替换旧的。</param>
/// <param name="Expires">过期时间（可选）。</param>
/// <param name="Home">网络主页（可选，https）。</param>
/// <param name="Config">服务器配置文件（可选）。</param>
/// <param name="DirectoryKeys">
/// The directory nodes' X25519 public keys ("dirKeys", optional, at most <see cref="NetworkString.MaxDirectoryKeys"/>).
/// When present, Jay only talks to a directory that proves it holds one of them (see <see cref="DirectoryChannel"/>).
/// Older Jay versions ignore the field.
/// </param>
public sealed record NetworkInfo(string Id, string Name, string NetworkName, string Secret, IReadOnlyList<string> Nodes, long Seq = 1,
    DateTimeOffset? Expires = null, string? Home = null, NetworkConfigRef? Config = null, IReadOnlyList<byte[]>? DirectoryKeys = null)
{
    public JsonObject ToJson()
    {
        var o = new JsonObject
        {
            ["v"] = NetworkString.Version,
            ["id"] = Id,
            ["name"] = Name,
            ["network"] = NetworkName,
            ["secret"] = Secret,
            ["nodes"] = new JsonArray([.. Nodes.Select(n => (JsonNode)JsonValue.Create(n))]),
            ["seq"] = Seq,
        };
        if (Expires is { } exp) o["exp"] = exp.ToUnixTimeSeconds();
        if (Home is not null) o["home"] = Home;
        if (Config is not null) o["config"] = new JsonObject { ["url"] = Config.Url, ["sha256"] = Config.Sha256.ToLowerInvariant() };
        if (DirectoryKeys is { Count: > 0 } keys) o["dirKeys"] = new JsonArray([.. keys.Select(k => (JsonNode)JsonValue.Create(Convert.ToBase64String(k)))]);
        return o;
    }

    /// <summary>从 JSON 读（注册表里的 networks/*.json 也是这个格式）。缺字段抛 <see cref="FormatException"/>。</summary>
    public static NetworkInfo FromJson(JsonObject o)
    {
        string Need(string name) => o[name]?.GetValue<string>() ?? throw new FormatException(name);
        var nodes = (o["nodes"] as JsonArray ?? throw new FormatException("nodes")).Select(n => n?.GetValue<string>() ?? throw new FormatException("nodes")).ToList();
        var config = o["config"] is JsonObject c ? new NetworkConfigRef(c["url"]?.GetValue<string>() ?? throw new FormatException("config"),
            c["sha256"]?.GetValue<string>() ?? throw new FormatException("config")) : null;
        return new NetworkInfo(Need("id"), Need("name"), Need("network"), Need("secret"), nodes,
            o["seq"]?.GetValue<long>() ?? 1,
            o["exp"] is { } exp ? DateTimeOffset.FromUnixTimeSeconds(exp.GetValue<long>()) : null,
            o["home"]?.GetValue<string>(), config,
            o["dirKeys"] is JsonArray dk ? [.. dk.Select(k => Convert.FromBase64String(k?.GetValue<string>() ?? throw new FormatException("dirKeys")))] : null);
    }
}

/// <summary>验过签的连接串。</summary>
/// <param name="SignerKey">签名公钥（同一个网络换了钥匙要用户重新确认）。</param>
/// <param name="Reviewed">是不是 Jay 注册表签的（GitHub 审核过）。</param>
/// <param name="Text">原始连接串（保存在设置里，每次启动重新验签）。</param>
public sealed record SignedNetwork(NetworkInfo Info, byte[] SignerKey, bool Reviewed, string Text)
{
    /// <summary>签名钥匙的短指纹（给用户核对用）。</summary>
    public string SignerFingerprint => Convert.ToHexString(SHA256.HashData(SignerKey)[..6]);

    /// <summary>
    /// 能不能用 <paramref name="incoming"/> 替换这个：同一个网络、同一把钥匙、版本更新。
    /// 钥匙不同的要用户先删掉旧的（防止别人冒用同一个编号）。
    /// </summary>
    public bool IsReplacedBy(SignedNetwork incoming) =>
        incoming.Info.Id == Info.Id && incoming.SignerKey.AsSpan().SequenceEqual(SignerKey) && incoming.Info.Seq > Info.Seq;

    public PublicNetworkProfile ToProfile() =>
        new(Info.Id, Info.Name, Info.NetworkName, Info.Secret, Info.Nodes)
        {
            Reviewed = Reviewed, Config = Info.Config, Home = Info.Home, DirectoryKeys = Info.DirectoryKeys ?? [],
        };
}

/// <summary>签连接串的 Ed25519 钥匙（注册表 CI 或者自己搭网络的人用）。私钥是 32 字节种子。</summary>
public sealed class NetworkSigner
{
    private readonly Org.BouncyCastle.Crypto.Parameters.Ed25519PrivateKeyParameters _key;

    private NetworkSigner(byte[] seed)
    {
        if (seed.Length != 32) throw new InvalidDataException(L.T("core.mp.network.bad_key"));
        _key = new Org.BouncyCastle.Crypto.Parameters.Ed25519PrivateKeyParameters(seed);
        PublicKey = _key.GeneratePublicKey().GetEncoded();
    }

    public byte[] PublicKey { get; }

    public static NetworkSigner Generate(out byte[] seed)
    {
        seed = RandomNumberGenerator.GetBytes(32);
        return new NetworkSigner(seed);
    }

    /// <summary>从 base64 的种子读（文件或环境变量里的）。</summary>
    public static NetworkSigner FromSeed(string base64)
    {
        try { return new NetworkSigner(Convert.FromBase64String(base64.Trim())); }
        catch (FormatException) { throw new InvalidDataException(L.T("core.mp.network.bad_key")); }
    }

    public byte[] Sign(byte[] message)
    {
        var signer = new Org.BouncyCastle.Crypto.Signers.Ed25519Signer();
        signer.Init(true, _key);
        signer.BlockUpdate(message, 0, message.Length);
        return signer.GenerateSignature();
    }
}
