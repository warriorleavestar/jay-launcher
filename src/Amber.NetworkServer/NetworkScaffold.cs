using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amber.Core.Multiplayer.Public;

namespace Amber.NetworkServer;

/// <summary>
/// 搭自己的公共网络时要准备的文件：签名钥匙、网络 JSON、网络配置文件。
/// <c>jay-server network init</c> 和 <c>network config-hash</c> 用它；第三方的部署工具也可以直接调。
/// </summary>
public static class NetworkScaffold
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public sealed record Result(string KeyPath, string NetworkPath, string ConfigPath, byte[] PublicKey);

    /// <summary>
    /// 在 <paramref name="dir"/> 里生成 network.key（签名私钥）、&lt;编号&gt;.json（网络信息）、config.json（网络配置模板）。
    /// 已经有的文件不覆盖。
    /// </summary>
    /// <param name="nodes">玩家连的节点地址（tcp://公网地址:11010），至少一个。</param>
    public static Result Init(string dir, string id, string name, IReadOnlyList<string> nodes)
    {
        if (!NetworkString.IsValidId(id) || id == "jay") throw new ArgumentException("编号要是 2~32 个小写字母、数字、减号，不能是 jay");
        Directory.CreateDirectory(dir);
        var keyPath = Path.Combine(dir, "network.key");
        var networkPath = Path.Combine(dir, id + ".json");
        var configPath = Path.Combine(dir, "config.json");
        foreach (var p in new[] { keyPath, networkPath, configPath })
            if (File.Exists(p)) throw new IOException($"{p} 已经存在，不覆盖");

        var info = new NetworkInfo(id, name, "jay-net-" + id, id + "-public-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4)), nodes);
        if (NetworkString.Validate(info) is { } problem) throw new ArgumentException(problem);

        var signer = NetworkSigner.Generate(out var seed);
        File.WriteAllText(keyPath, Convert.ToBase64String(seed) + "\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.WriteAllText(networkPath, info.ToJson().ToJsonString(Pretty) + "\n");
        File.WriteAllText(configPath, ConfigTemplate(id, name).ToJsonString(Pretty) + "\n");
        return new Result(keyPath, networkPath, configPath, signer.PublicKey);
    }

    /// <summary>网络配置模板：公告、链接、默认的安全策略（和 Jay 默认一样，按需收紧）。</summary>
    public static JsonObject ConfigTemplate(string id, string name) => new()
    {
        ["v"] = NetworkConfig.Version,
        ["network"] = id,
        ["notice"] = $"欢迎来到 {name}。",
        ["links"] = new JsonArray(),
        ["policy"] = new JsonObject
        {
            ["modes"] = new JsonArray("public", "password", "hidden", "direct"),
            ["requireDevice"] = false,
            ["files"] = true,
        },
    };

    /// <summary>检查配置文件能不能被 Jay 读懂，返回它的 SHA-256（写进连接串的 config.sha256）。</summary>
    public static (NetworkConfig Config, string Sha256) Hash(string configPath, string networkId)
    {
        var bytes = File.ReadAllBytes(configPath);
        var config = NetworkConfig.Parse(bytes, networkId);
        return (config, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    /// <summary>把配置文件的地址和哈希写进网络 JSON，版本号加一（玩家粘贴新连接串时会替换旧的）。</summary>
    public static NetworkInfo AttachConfig(string networkPath, string url, string sha256)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps) throw new ArgumentException("配置文件的地址要是 https://");
        var o = JsonNode.Parse(File.ReadAllText(networkPath)) as JsonObject ?? throw new InvalidDataException("网络 JSON 要是一个对象");
        var info = NetworkInfo.FromJson(o);
        info = info with { Config = new NetworkConfigRef(url, sha256), Seq = info.Seq + 1 };
        if (NetworkString.Validate(info) is { } problem) throw new InvalidDataException(problem);
        File.WriteAllText(networkPath, info.ToJson().ToJsonString(Pretty) + "\n");
        return info;
    }
}
