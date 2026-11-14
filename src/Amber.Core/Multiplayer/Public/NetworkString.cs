using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Amber.Core.Localization;

namespace Amber.Core.Multiplayer.Public;

/// <summary>
/// A public network's connection string: name, EasyTier secret, nodes, optional config hash, and one or more hybrid
/// Ed25519 + ML-DSA-65 signatures (<see cref="TrustSignature"/>).
/// Format: <c>JAYNET2-</c> + base64url(content JSON) + <c>.</c> + base64url(signature entries), each entry being one
/// encoded <see cref="TrustSignature"/> (both public keys and both signatures, 5,357 bytes). Every entry signs
/// <c>"jay-network-v2\n"</c> + the raw content JSON bytes. A string with the owner and two trustees is about 23 KB, so
/// reviewed networks travel as files (.jaynet) or through the network list rather than being typed.
/// <para>
/// The first entry is the network owner's key: it identifies the network (a string with the same id and another owner
/// key is a conflict). Further entries are co-signatures. A string counts as reviewed when <see cref="JayTrust.Need"/> of
/// the Jay trust keys (<see cref="JayTrust.Keys"/>) are among the signers. The goal is 2 of 3 trustees; for now there are
/// two keys, one trustee's own and their offline cold backup, and both must sign, so no single key can review a network
/// alone. Keys sign offline, one after another, with <c>jay-server network cosign</c>. Unreviewed strings need the user's
/// confirmation.
/// </para>
/// </summary>
public static partial class NetworkString
{
    public const string Prefix = "JAYNET";
    public const int Version = 2;
    public const string Current = "JAYNET2-";
    public const int MaxLength = 40960;
    public const int MaxNodes = 16;
    /// <summary>
    /// At most this many directory keys: one per directory node (up to four) plus room for a new key per node while
    /// rotating. Deliberately larger than <see cref="PublicNetworkProfile.MaxDirectories"/>: every shipped Jay refuses a
    /// string with more keys than this, so it can't grow later without breaking older versions.
    /// </summary>
    public const int MaxDirectoryKeys = 8;

    /// <summary>At most this many directory root keys ("dirRoots"): the current one and a few for rotation.</summary>
    public const int MaxDirectoryRoots = 4;

    /// <summary>At most this many signatures: the owner plus three trustees.</summary>
    public const int MaxSignatures = 4;

    private static readonly byte[] Domain = "jay-network-v2\n"u8.ToArray();

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,31}$")]
    private static partial Regex IdPattern();

    /// <summary>网络编号能不能用：2~32 个小写字母、数字、减号，不能以减号开头。</summary>
    public static bool IsValidId(string? id) => id is not null && IdPattern().IsMatch(id);

    /// <summary>Makes a connection string signed by its owner, then by any co-signers in order.</summary>
    public static string Create(NetworkInfo info, TrustSigner owner, params TrustSigner[] cosigners)
    {
        if (Validate(info) is { } problem) throw new InvalidDataException(problem);
        var payload = Encoding.UTF8.GetBytes(info.ToJson().ToJsonString());
        var text = Format(payload, [owner.Sign([.. Domain, .. payload])]);
        foreach (var c in cosigners) text = Cosign(text, c);
        return text;
    }

    /// <summary>
    /// Adds one more signature to an existing string without changing its content. Checks every existing signature first.
    /// A key that already signed is refused.
    /// </summary>
    public static string Cosign(string text, TrustSigner signer)
    {
        var (payload, signatures) = Split(text);
        if (signatures.Any(s => s.Key.AsSpan().SequenceEqual(signer.Id))) throw new InvalidDataException(L.T("core.mp.network.already_signed"));
        if (signatures.Count >= MaxSignatures) throw Bad("core.mp.network.bad_string");
        return Format(payload, [.. signatures, signer.Sign([.. Domain, .. payload])]);
    }

    private static string Format(byte[] payload, IReadOnlyList<TrustSignature> signatures) =>
        Current + Base64Url(payload) + "." + Base64Url(signatures.SelectMany(s => s.Encode()).ToArray());

    /// <summary>Reads the envelope and verifies every signature. Doesn't look at the content (signing review uses it).</summary>
    public static (byte[] Payload, List<TrustSignature> Signatures) Split(string text)
    {
        text = (text ?? "").Trim();
        if (text.Length is 0 or > MaxLength || !text.StartsWith(Prefix, StringComparison.Ordinal)) throw Bad("core.mp.network.bad_string");
        var dash = text.IndexOf('-');
        if (dash < 0 || !int.TryParse(text.AsSpan(Prefix.Length, dash - Prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var version))
            throw Bad("core.mp.network.bad_string");
        if (version < Version) throw new InvalidDataException(L.F("core.mp.network.old_version", version));
        if (version > Version) throw new InvalidDataException(L.F("core.mp.network.new_version", version));
        var parts = text[(dash + 1)..].Split('.');
        if (parts.Length != 2 || FromBase64Url(parts[0]) is not { } payload || FromBase64Url(parts[1]) is not { } tail
            || tail.Length == 0 || tail.Length % TrustSignature.Size != 0 || tail.Length / TrustSignature.Size > MaxSignatures)
            throw Bad("core.mp.network.bad_string");
        var signatures = new List<TrustSignature>();
        for (var i = 0; i < tail.Length; i += TrustSignature.Size)
        {
            var entry = TrustSignature.Decode(tail.AsSpan(i, TrustSignature.Size));
            if (signatures.Any(s => s.Key.AsSpan().SequenceEqual(entry.Key))) throw Bad("core.mp.network.bad_string");
            if (!entry.Verify([.. Domain, .. payload])) throw Bad("core.mp.network.bad_signature");
            signatures.Add(entry);
        }
        return (payload, signatures);
    }

    /// <summary>
    /// 读连接串并验签。格式不对、签名不对、内容不合规、已经过期都抛 <see cref="InvalidDataException"/>（消息给用户看）。
    /// </summary>
    /// <param name="trusted">
    /// Quorums that make a string reviewed (any one of them met is enough). Null: the Jay trustees (<see cref="JayTrust"/>).
    /// Tests and test machines pass their own.
    /// </param>
    public static SignedNetwork Parse(string text, IReadOnlyList<TrustQuorum>? trusted = null, DateTimeOffset? now = null)
    {
        text = (text ?? "").Trim();
        var (payload, signatures) = Split(text);
        NetworkInfo info;
        try { info = NetworkInfo.FromJson(JsonNode.Parse(payload) as JsonObject ?? throw Bad("core.mp.network.bad_string")); }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException) { throw Bad("core.mp.network.bad_string"); }
        if (Validate(info) is { } problem) throw new InvalidDataException(problem);
        if (info.Expires is { } exp && exp <= (now ?? DateTimeOffset.UtcNow)) throw Bad("core.mp.network.expired");
        var signers = signatures.Select(s => s.Key).ToList();
        var reviewed = (trusted ?? [JayTrust.Quorum]).Any(q => q.IsMet(signers));
        return new SignedNetwork(info, signers[0], reviewed, text) { Signers = signers };
    }

    /// <summary>不抛异常的版本：读不了返回 null。</summary>
    public static SignedNetwork? TryParse(string? text, IReadOnlyList<TrustQuorum>? trusted = null, DateTimeOffset? now = null)
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
        // A network string is shared widely: its nodes go by domain name, never by a public IP address.
        if (info.Nodes.Any(RelayNodes.HasPublicIpHost)) return L.T("core.mp.network.ip_node");
        if (info.Home is { } home && !IsHttps(home)) return L.T("core.mp.network.bad_link");
        if (info.Config is { } config && (!IsHttps(config.Url) || config.Sha256.Length != 64 || !config.Sha256.All(Uri.IsHexDigit)))
            return L.T("core.mp.network.bad_config");
        if (info.DirectoryKeys is { } dirKeys && (dirKeys.Count is 0 or > MaxDirectoryKeys || dirKeys.Any(k => k.Length != DirectoryChannel.KeyLength)))
            return L.T("core.mp.network.bad_dir_keys");
        if (info.DirectoryRoots is { } roots && (roots.Count is 0 or > MaxDirectoryRoots || roots.Any(k => k.Length != 32)))
            return L.T("core.mp.network.bad_dir_keys");
        if (info.DirectoryRootsNeed < 1 || info.DirectoryRootsNeed > Math.Max(1, info.DirectoryRoots?.Count ?? 0))
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
/// <param name="DirectoryRoots">
/// Ed25519 root keys that certify directory keys ("dirRoots", optional, at most <see cref="NetworkString.MaxDirectoryRoots"/>),
/// see <see cref="DirectoryCertificate"/>.
/// </param>
/// <param name="DirectoryRootsNeed">How many of <paramref name="DirectoryRoots"/> must sign a directory certificate ("dirRootsNeed", default 1).</param>
public sealed record NetworkInfo(string Id, string Name, string NetworkName, string Secret, IReadOnlyList<string> Nodes, long Seq = 1,
    DateTimeOffset? Expires = null, string? Home = null, NetworkConfigRef? Config = null, IReadOnlyList<byte[]>? DirectoryKeys = null,
    IReadOnlyList<byte[]>? DirectoryRoots = null, int DirectoryRootsNeed = 1)
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
        if (DirectoryRoots is { Count: > 0 } roots) o["dirRoots"] = new JsonArray([.. roots.Select(k => (JsonNode)JsonValue.Create(Convert.ToBase64String(k)))]);
        if (DirectoryRootsNeed != 1) o["dirRootsNeed"] = DirectoryRootsNeed;
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
            o["dirKeys"] is JsonArray dk ? [.. dk.Select(k => Convert.FromBase64String(k?.GetValue<string>() ?? throw new FormatException("dirKeys")))] : null,
            o["dirRoots"] is JsonArray dr ? [.. dr.Select(k => Convert.FromBase64String(k?.GetValue<string>() ?? throw new FormatException("dirRoots")))] : null,
            o["dirRootsNeed"]?.GetValue<int>() ?? 1);
    }
}

/// <summary>验过签的连接串。</summary>
/// <param name="SignerKey">The owner's key id (<see cref="TrustSigner.Id"/>), the first signature (a network whose owner key changed must be removed and re-added).</param>
/// <param name="Reviewed">Whether a trusted quorum (normally the Jay trustees) signed it.</param>
/// <param name="Text">原始连接串（保存在设置里，每次启动重新验签）。</param>
public sealed record SignedNetwork(NetworkInfo Info, byte[] SignerKey, bool Reviewed, string Text)
{
    /// <summary>Every key that signed, owner first.</summary>
    public IReadOnlyList<byte[]> Signers { get; init; } = [SignerKey];

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
            DirectoryRoots = Info.DirectoryRoots ?? [], DirectoryRootsNeed = Info.DirectoryRootsNeed,
        };
}

/// <summary>An Ed25519 signing key (a network owner's, or a trustee's). The private key is a 32-byte seed.</summary>
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
