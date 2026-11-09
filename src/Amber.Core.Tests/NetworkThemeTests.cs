using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amber.Core.Themes;

namespace Amber.Core.Tests;

/// <summary>联机配置文件带来的主题：陌生人发来的东西，网址、大小、哈希都要核对，对不上整个不用。</summary>
public class NetworkThemeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jay-nettheme-" + Guid.NewGuid().ToString("N"));

    public NetworkThemeTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    private static byte[] Pack(string id = "net-theme")
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
            foreach (var (name, text) in new[] { ("theme.json", $$"""{ "id": "{{id}}", "name": "网络主题" }"""), ("tokens.json", """{ "seed": { "accent": "#2F80ED" } }""") })
            {
                using var s = zip.CreateEntry(name).Open();
                s.Write(Encoding.UTF8.GetBytes(text));
            }
        return ms.ToArray();
    }

    private sealed class Handler(byte[] body) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body), RequestMessage = request });
        }
    }

    private static string Spec(byte[] body, string? sha = null, long? size = null, string url = "https://example.org/a.jaytheme") =>
        $$"""{ "url": "{{url}}", "sha256": "{{sha ?? Convert.ToHexStringLower(SHA256.HashData(body))}}", "size": {{size ?? body.Length}} }""";

    [Fact]
    public void ParsesAndRejectsBadSpecs()
    {
        var body = Pack();
        var spec = NetworkTheme.Parse(Json(Spec(body)));
        Assert.Equal("network", spec.Scope);
        Assert.Equal(body.Length, spec.Size);
        Assert.Equal("global", NetworkTheme.Parse(Json("""{ "inline": { "seed": { "accent": "#FF0000" } }, "scope": "global" }""")).Scope);

        string Code(string json) => Assert.Throws<ThemePackException>(() => NetworkTheme.Parse(Json(json))).Code;
        Assert.Equal("bad_network_ui", Code(Spec(body, url: "http://example.org/a.jaytheme")));       // 只能 https
        Assert.Equal("bad_network_ui", Code(Spec(body, url: "https://user:pw@example.org/a")));      // 不带账号密码
        Assert.Equal("bad_network_ui", Code(Spec(body, sha: "abc")));
        Assert.Equal("bad_network_ui", Code(Spec(body, size: 30L * 1024 * 1024)));                  // 超过主题包上限
        Assert.Equal("bad_network_ui", Code("""{ "inline": { "x": 1 }, "scope": "everything" }"""));
        Assert.Equal("bad_network_ui", Code($$"""{ "inline": { "x": "{{new string('a', 70_000)}}" } }"""));
    }

    [Fact]
    public async Task DownloadsVerifiesAndCaches()
    {
        var body = Pack();
        var handler = new Handler(body);
        using var http = new HttpClient(handler);
        var spec = NetworkTheme.Parse(Json(Spec(body)));
        var pack = await NetworkTheme.GetAsync(http, spec, _dir);
        Assert.Equal("net-theme", pack.Id);
        Assert.Equal(0xFF2F80EDu, pack.DarkColors["Accent"]);
        Assert.NotNull(pack.Directory);
        // 第二次用缓存，不再下载。
        Assert.Equal("net-theme", (await NetworkTheme.GetAsync(http, spec, _dir)).Id);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task WrongHashOrSizeIsRejectedAndNothingIsKept()
    {
        var body = Pack();
        using var http = new HttpClient(new Handler(body));
        var wrongHash = NetworkTheme.Parse(Json(Spec(body, sha: new string('0', 64))));
        Assert.Equal("network_theme_mismatch", (await Assert.ThrowsAsync<ThemePackException>(() => NetworkTheme.GetAsync(http, wrongHash, _dir))).Code);
        var wrongSize = NetworkTheme.Parse(Json(Spec(body, size: body.Length - 1)));
        Assert.Equal("network_theme_mismatch", (await Assert.ThrowsAsync<ThemePackException>(() => NetworkTheme.GetAsync(http, wrongSize, _dir))).Code);
        Assert.Empty(Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void InlineOnlyHasTokens()
    {
        var spec = NetworkTheme.Parse(Json("""{ "inline": { "seed": { "accent": "#E0538A" }, "background": { "image": "assets/x.png" } } }"""));
        var pack = NetworkTheme.ReadInline(spec.InlineTokens!);
        Assert.Equal(0xFFE0538Au, pack.DarkColors["Accent"]);
        Assert.Null(pack.BackgroundImage);
        Assert.Null(pack.Shell);
        Assert.Empty(pack.Pages);
    }
}
