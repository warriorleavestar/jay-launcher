using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amber.Core.Localization;

namespace Amber.Core.Multiplayer.Public;

/// <summary>网络配置里的一个链接（比如网络主页、Discord、规则说明），只认 https。</summary>
public sealed record NetworkLink(string Name, string Url);

/// <summary>
/// 网络的安全策略。只能比 Jay 默认的更严，不能放宽：
/// 允许的房间方式只能少不能多，传文件只能关掉或限大小，设备指纹只能从“可选”变成“必须”。
/// </summary>
/// <param name="AllowedModes">这个网络上允许的房间方式（默认四种都行）。</param>
/// <param name="RequireDevice">在这个网络上开的房间都要求设备指纹。</param>
/// <param name="AllowFiles">房间里成员之间能不能传文件。</param>
/// <param name="MaxFileBytes">单个文件最大多少字节（null = 不限）。</param>
/// <param name="MinVersion">最低 Jay 版本（低于它不让连这个网络）。</param>
public sealed record NetworkPolicy(IReadOnlySet<RoomVisibility> AllowedModes, bool RequireDevice = false, bool AllowFiles = true,
    long? MaxFileBytes = null, Version? MinVersion = null)
{
    public static readonly NetworkPolicy Default = new(new HashSet<RoomVisibility>(Enum.GetValues<RoomVisibility>()));

    public bool Allows(RoomVisibility mode) => AllowedModes.Contains(mode);

    /// <summary>这个大小的文件能不能传。</summary>
    public bool AllowsFile(long size) => AllowFiles && (MaxFileBytes is not { } max || size <= max);

    /// <summary>最低安全要求（policy.security：设备验证、熔断、重启倒计时）；没写是 null。和本机设置合并时只会更严。</summary>
    public Transport.SecurityPolicy? Security { get; init; }
}

/// <summary>
/// 网络的服务器配置文件：网络维护者放在自己的 https 地址上，连接串里写着地址和 SHA-256。
/// Jay 下载后先对哈希，对不上就不用（所以配置文件和连接串一样改不了）。只是声明式的数据，没有脚本。
/// <code>
/// { "v": 1, "network": "nz-friends", "notice": "欢迎……", "links": [{ "name": "规则", "url": "https://..." }],
///   "policy": { "modes": ["public", "password"], "requireDevice": true, "files": true, "maxFileMB": 2048, "minVersion": "0.8.0",
///               "security": { "attestation": "required", "breaker": "on", "restartDelayMin": 30 } },
///   "ui": { ...界面主题，见 docs/themes.md... } }
/// </code>
/// </summary>
public sealed record NetworkConfig(string Network, string? Notice, IReadOnlyList<NetworkLink> Links, NetworkPolicy Policy, JsonObject? Ui)
{
    /// <summary>账号密码登录（网络节点管账号；没有就是不用登录）。</summary>
    public NetworkLogin? Login { get; init; }

    public const int Version = 1;
    public const int MaxBytes = 256 * 1024;
    public const int MaxNotice = 2000;
    public const int MaxLinks = 8;

    /// <summary>读配置文件。格式不对、网络编号对不上、内容不合规都抛 <see cref="InvalidDataException"/>。</summary>
    public static NetworkConfig Parse(ReadOnlySpan<byte> json, string networkId)
    {
        if (json.Length > MaxBytes) throw Bad();
        JsonObject o;
        try { o = JsonNode.Parse(json) as JsonObject ?? throw Bad(); }
        catch (JsonException) { throw Bad(); }
        try
        {
            var v = o["v"]?.GetValue<int>() ?? 0;
            if (v > Version) throw new InvalidDataException(L.F("core.mp.netconfig.new_version", v));
            if (v < 1 || o["network"]?.GetValue<string>() != networkId) throw Bad();
            var notice = o["notice"]?.GetValue<string>() is { } n ? RoomChat.Clean(n, MaxNotice) : null;
            var links = new List<NetworkLink>();
            if (o["links"] is JsonArray array)
                foreach (var item in array.Take(MaxLinks))
                {
                    if (item is not JsonObject l || l["url"]?.GetValue<string>() is not { } url || !IsHttps(url)) throw Bad();
                    links.Add(new NetworkLink(KnockProtocol.SanitizeName(l["name"]?.GetValue<string>() ?? url), url));
                }
            return new NetworkConfig(networkId, string.IsNullOrWhiteSpace(notice) ? null : notice, links, ParsePolicy(o["policy"] as JsonObject), o["ui"] as JsonObject)
            {
                Login = NetworkLogin.Parse(o["login"] as JsonObject),
            };
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException or OverflowException) { throw Bad(); }
    }

    private static NetworkPolicy ParsePolicy(JsonObject? p)
    {
        if (p is null) return NetworkPolicy.Default;
        var modes = new HashSet<RoomVisibility>(Enum.GetValues<RoomVisibility>());
        if (p["modes"] is JsonArray list)
        {
            modes.Clear();
            foreach (var m in list)
                modes.Add(m?.GetValue<string>() switch
                {
                    "public" => RoomVisibility.Public,
                    "password" => RoomVisibility.Password,
                    "hidden" => RoomVisibility.Hidden,
                    "direct" => RoomVisibility.Direct,
                    _ => throw Bad(),
                });
            // 隐藏房间（只有邀请码能进）总是允许：它不在公共列表里出现，网络管不着。
            modes.Add(RoomVisibility.Hidden);
        }
        long? max = p["maxFileMB"]?.GetValue<long>() is { } mb ? mb is >= 0 and <= 1L << 30 ? mb * 1024 * 1024 : throw Bad() : null;
        Version? min = p["minVersion"]?.GetValue<string>() is { } text ? System.Version.TryParse(text, out var parsed) ? parsed : throw Bad() : null;
        return new NetworkPolicy(modes, p["requireDevice"]?.GetValue<bool>() ?? false, p["files"]?.GetValue<bool>() ?? true, max, min)
        {
            Security = ParseSecurity(p["security"]),
        };
    }

    private static Transport.SecurityPolicy? ParseSecurity(JsonNode? node)
    {
        try { return Transport.SecurityPolicy.Parse(node); }
        catch (InvalidDataException) { throw Bad(); }
    }

    private static bool IsHttps(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && url.Length <= 512;

    private static InvalidDataException Bad() => new(L.T("core.mp.netconfig.bad"));

    /// <summary>
    /// 取网络配置：本机缓存（按哈希存）对得上就直接用，否则从 https 地址下载（最多 256 KB）、对 SHA-256、存缓存。
    /// 下载失败、哈希对不上抛 <see cref="InvalidDataException"/> 或 <see cref="HttpRequestException"/>。
    /// </summary>
    public static async Task<NetworkConfig> GetAsync(HttpClient http, NetworkConfigRef reference, string networkId, string cacheDir, CancellationToken ct)
    {
        using var scope = Amber.Core.Net.NetworkPolicy.Scope(Amber.Core.Net.NetworkCategory.Multiplayer);
        var sha = reference.Sha256.ToLowerInvariant();
        var cached = Path.Combine(cacheDir, sha + ".json");
        if (File.Exists(cached))
        {
            var bytes = await File.ReadAllBytesAsync(cached, ct);
            if (Convert.ToHexStringLower(SHA256.HashData(bytes)) == sha) return Parse(bytes, networkId);
        }
        if (!Uri.TryCreate(reference.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) throw Bad();
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxBytes) throw Bad();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxBytes) throw Bad();
            buffer.Write(chunk, 0, read);
        }
        var data = buffer.ToArray();
        if (Convert.ToHexStringLower(SHA256.HashData(data)) != sha) throw new InvalidDataException(L.T("core.mp.netconfig.hash"));
        var config = Parse(data, networkId);
        Directory.CreateDirectory(cacheDir);
        await File.WriteAllBytesAsync(cached, data, ct);
        return config;
    }
}
