using System.Net;
using System.Security.Cryptography;
using System.Text;
using Amber.Core.Multiplayer.Public;

namespace Amber.Core.Tests;

/// <summary>网络配置文件：解析、策略只能更严、按 SHA-256 下载和缓存、对不上哈希不用。</summary>
public sealed class NetworkConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jay-netcfg-" + Guid.NewGuid().ToString("N"));

    private const string Sample = """
        { "v": 1, "network": "nz-friends", "notice": "欢迎来到新西兰朋友服",
          "links": [{ "name": "规则", "url": "https://example.org/rules" }],
          "policy": { "modes": ["public"], "requireDevice": true, "files": true, "maxFileMB": 10, "minVersion": "0.8.0" },
          "ui": { "tokens": { "accent": "#3366ff" } } }
        """;

    [Fact]
    public void ParsesNoticeLinksPolicyAndUi()
    {
        var config = NetworkConfig.Parse(Encoding.UTF8.GetBytes(Sample), "nz-friends");
        Assert.Equal("欢迎来到新西兰朋友服", config.Notice);
        Assert.Equal(new NetworkLink("规则", "https://example.org/rules"), Assert.Single(config.Links));
        Assert.True(config.Policy.Allows(RoomVisibility.Public));
        Assert.True(config.Policy.Allows(RoomVisibility.Hidden));
        Assert.False(config.Policy.Allows(RoomVisibility.Password));
        Assert.False(config.Policy.Allows(RoomVisibility.Direct));
        Assert.True(config.Policy.RequireDevice);
        Assert.True(config.Policy.AllowsFile(10 * 1024 * 1024));
        Assert.False(config.Policy.AllowsFile(10 * 1024 * 1024 + 1));
        Assert.Equal(new Version(0, 8, 0), config.Policy.MinVersion);
        Assert.NotNull(config.Ui);
    }

    [Fact]
    public void EmptyConfigKeepsTheDefaults()
    {
        var config = NetworkConfig.Parse("""{ "v": 1, "network": "x1" }"""u8, "x1");
        Assert.Null(config.Notice);
        Assert.Empty(config.Links);
        Assert.Equal(NetworkPolicy.Default, config.Policy);
        Assert.True(config.Policy.AllowsFile(long.MaxValue));
        Assert.All(Enum.GetValues<RoomVisibility>(), m => Assert.True(config.Policy.Allows(m)));
    }

    [Theory]
    [InlineData("""{ "v": 1, "network": "other" }""")]
    [InlineData("""{ "v": 0, "network": "x1" }""")]
    [InlineData("""{ "v": 1, "network": "x1", "links": [{ "name": "a", "url": "http://insecure" }] }""")]
    [InlineData("""{ "v": 1, "network": "x1", "links": [{ "name": "a", "url": "javascript:alert(1)" }] }""")]
    [InlineData("""{ "v": 1, "network": "x1", "policy": { "modes": ["everyone"] } }""")]
    [InlineData("""{ "v": 1, "network": "x1", "policy": { "maxFileMB": -1 } }""")]
    [InlineData("""{ "v": 1, "network": "x1", "policy": { "minVersion": "soon" } }""")]
    [InlineData("""not json""")]
    public void RejectsBadConfigs(string json) =>
        Assert.Throws<InvalidDataException>(() => NetworkConfig.Parse(Encoding.UTF8.GetBytes(json), "x1"));

    [Fact]
    public void NewerFormatsAskForAnUpdate()
    {
        var e = Assert.Throws<InvalidDataException>(() => NetworkConfig.Parse("""{ "v": 7, "network": "x1" }"""u8, "x1"));
        Assert.Contains("7", e.Message);
    }

    [Fact]
    public void FilesCanBeTurnedOff()
    {
        var config = NetworkConfig.Parse("""{ "v": 1, "network": "x1", "policy": { "files": false } }"""u8, "x1");
        Assert.False(config.Policy.AllowsFile(1));
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(respond(request));
        }
    }

    [Fact]
    public async Task DownloadsOnceChecksTheHashAndCaches()
    {
        var bytes = Encoding.UTF8.GetBytes(Sample);
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        using var http = new HttpClient(handler);
        var reference = new NetworkConfigRef("https://example.org/network.json", sha);
        var first = await NetworkConfig.GetAsync(http, reference, "nz-friends", _dir, default);
        var second = await NetworkConfig.GetAsync(http, reference, "nz-friends", _dir, default);
        Assert.Equal(first.Notice, second.Notice);
        Assert.Equal(1, handler.Calls);
        Assert.True(File.Exists(Path.Combine(_dir, sha + ".json")));
    }

    [Fact]
    public async Task RefusesAChangedFile()
    {
        var bytes = Encoding.UTF8.GetBytes(Sample);
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var changed = Encoding.UTF8.GetBytes(Sample.Replace("\"public\"", "\"public\", \"direct\""));
        using var http = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(changed) }));
        await Assert.ThrowsAsync<InvalidDataException>(() => NetworkConfig.GetAsync(http, new NetworkConfigRef("https://example.org/n.json", sha), "nz-friends", _dir, default));
        Assert.False(Directory.Exists(_dir) && Directory.EnumerateFiles(_dir).Any());
        // 缓存被改过：重新下载（这里下载的也不对，所以还是不用）。
        Directory.CreateDirectory(_dir);
        await File.WriteAllBytesAsync(Path.Combine(_dir, sha + ".json"), changed);
        await Assert.ThrowsAsync<InvalidDataException>(() => NetworkConfig.GetAsync(http, new NetworkConfigRef("https://example.org/n.json", sha), "nz-friends", _dir, default));
    }

    [Fact]
    public async Task RefusesHugeFiles()
    {
        var huge = new byte[NetworkConfig.MaxBytes + 1];
        using var http = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(huge) }));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            NetworkConfig.GetAsync(http, new NetworkConfigRef("https://example.org/n.json", new string('0', 64)), "x1", _dir, default));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
