using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Amber.Core.Themes;

/// <summary>
/// 联机网络配置文件里带的界面主题（配置的 "ui" 字段）。格式和联机那边约定好：
/// <code>
/// "ui": { "sha256": "64 位十六进制", "size": 字节数, "url": "https://…/x.jaytheme", "scope": "network" | "global" }
/// "ui": { "inline": { tokens.json 的内容 }, "scope": "network" }   // 只有配色字号，≤ 64 KB
/// </code>
/// 网址只能是 https，由网络的主人自己托管（目录服务不存文件）。下载时限制大小，下完先核对大小和 sha256，
/// 对不上整个丢掉。主题包本身照常按导入规则检查（只有数据、没有代码）。
/// scope：network 只在联机页里用；global 要用户点头才换掉整个 Jay 的主题。
/// </summary>
public sealed record NetworkThemeSpec(string Scope, string? Url, string? Sha256, long? Size, string? InlineTokens)
{
    public bool IsInline => InlineTokens is not null;
}

public static partial class NetworkTheme
{
    public const int MaxInlineBytes = 64 * 1024;

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Pattern();

    /// <summary>读配置里的 ui 字段。格式不对抛 ThemePackException（调用方把它当“这个网络的主题不可用”，联机照常）。</summary>
    public static NetworkThemeSpec Parse(JsonElement ui)
    {
        if (ui.ValueKind != JsonValueKind.Object) throw new ThemePackException("bad_network_ui", "ui");
        var scope = ui.TryGetProperty("scope", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString()! : "network";
        if (scope is not ("network" or "global")) throw new ThemePackException("bad_network_ui", "scope");
        if (ui.TryGetProperty("inline", out var inline))
        {
            if (inline.ValueKind != JsonValueKind.Object) throw new ThemePackException("bad_network_ui", "inline");
            var text = inline.GetRawText();
            if (Encoding.UTF8.GetByteCount(text) > MaxInlineBytes) throw new ThemePackException("bad_network_ui", "inline too large");
            return new NetworkThemeSpec(scope, null, null, null, text);
        }
        var url = ui.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString()! : "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length > 0)
            throw new ThemePackException("bad_network_ui", "url");
        var sha = ui.TryGetProperty("sha256", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString()!.ToLowerInvariant() : "";
        if (!Sha256Pattern().IsMatch(sha)) throw new ThemePackException("bad_network_ui", "sha256");
        if (!ui.TryGetProperty("size", out var z) || z.ValueKind != JsonValueKind.Number || !z.TryGetInt64(out var size)
            || size <= 0 || size > ThemePackReader.MaxPackBytes)
            throw new ThemePackException("bad_network_ui", "size");
        return new NetworkThemeSpec(scope, uri.AbsoluteUri, sha, size, null);
    }

    /// <summary>
    /// 拿到这个网络的主题：内联的直接读；有网址的先看缓存（按 sha256 存在 cacheRoot 下），没有再下载。
    /// 下载最多读 size 字节，多一个字节就算不对；大小和 sha256 都对上才安装进 cacheRoot。
    /// </summary>
    public static async Task<ThemePack> GetAsync(HttpClient http, NetworkThemeSpec spec, string cacheRoot, CancellationToken ct = default)
    {
        using var scope = Amber.Core.Net.NetworkPolicy.Scope(Amber.Core.Net.NetworkCategory.Multiplayer);
        if (spec.IsInline) return ReadInline(spec.InlineTokens!);
        var store = new ThemeStore(Path.Combine(cacheRoot, spec.Sha256!));
        if (store.List().FirstOrDefault() is { } cached && store.TryLoad(cached.Id) is { } pack) return pack;

        Directory.CreateDirectory(cacheRoot);
        var temp = Path.Combine(cacheRoot, spec.Sha256 + ".download");
        try
        {
            using (var response = await http.GetAsync(spec.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                if (response.RequestMessage?.RequestUri is { } final && final.Scheme != Uri.UriSchemeHttps)
                    throw new ThemePackException("bad_network_ui", "redirected off https");
                if (response.Content.Headers.ContentLength is { } length && length != spec.Size)
                    throw new ThemePackException("network_theme_mismatch", "size");
                await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var file = File.Create(temp);
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await body.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > spec.Size) throw new ThemePackException("network_theme_mismatch", "size");
                    await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }
                if (total != spec.Size) throw new ThemePackException("network_theme_mismatch", "size");
            }
            if (!Verify(temp, spec.Sha256!)) throw new ThemePackException("network_theme_mismatch", "sha256");
            return store.Install(temp);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public static bool Verify(string path, string sha256)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream)) == sha256;
    }

    /// <summary>内联主题只有 tokens（配色、字号、圆角、系统字体名），没有字体文件、图片、样式和排版。</summary>
    public static ThemePack ReadInline(string tokensJson)
    {
        var files = new Dictionary<string, byte[]>
        {
            ["theme.json"] = Encoding.UTF8.GetBytes("""{ "format": 1, "id": "network-inline", "name": "Network" }"""),
            ["tokens.json"] = Encoding.UTF8.GetBytes(tokensJson),
        };
        // 内联的没有附带文件：引用字体文件、背景图的那几项读不到，会被当成缺文件丢掉；系统字体名照常可用。
        return ThemePackReader.ParseFiles(files);
    }
}
