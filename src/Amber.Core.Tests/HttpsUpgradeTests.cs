using System.Net;
using System.Security.Cryptography;
using Amber.Core.Net;

namespace Amber.Core.Tests;

/// <summary>Plain-http download addresses (old Forge profiles and the like) go over https; http is only a hash-checked fallback.</summary>
public sealed class HttpsUpgradeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amber-https-upgrade-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] Payload = [1, 2, 3, 4];

    public HttpsUpgradeTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    [Theory]
    [InlineData("http://files.minecraftforge.net/maven/net/minecraftforge/forge/1.7.10/forge.jar", "https://maven.minecraftforge.net/net/minecraftforge/forge/1.7.10/forge.jar")]
    [InlineData("HTTP://repo.example.test/maven2/a/b/1/b-1.jar", "https://repo.example.test/maven2/a/b/1/b-1.jar")]
    [InlineData("http://repo.example.test:8080/a.jar", "https://repo.example.test:8080/a.jar")]
    public void PlainHttpBecomesHttps(string url, string expected) => Assert.Equal(expected, HttpsUpgrade.Upgrade(url));

    [Theory]
    [InlineData("https://libraries.minecraft.net/a.jar")]
    [InlineData("http://127.0.0.1:8080/a.jar")]
    [InlineData("http://localhost/a.jar")]
    [InlineData("http://[::1]:8080/a.jar")]
    [InlineData("ftp://example.test/a.jar")]
    [InlineData("")]
    public void OtherAddressesStayAsTheyAre(string url) => Assert.Null(HttpsUpgrade.Upgrade(url));

    [Fact]
    public async Task UnhashedFileIsFetchedOverHttpsOnly()
    {
        var seen = new List<string>();
        using var http = new HttpClient(new Handler(seen, url => url.StartsWith("https://", StringComparison.Ordinal)));
        var item = new DownloadItem("http://repo.example.test/maven2/a.jar", Path.Combine(_root, "a.jar"));

        await Make(http).DownloadOneAsync(item);

        Assert.Equal(["https://repo.example.test/maven2/a.jar"], seen);
        Assert.Equal(Payload, File.ReadAllBytes(item.LocalPath));
    }

    [Fact]
    public async Task UnhashedFileNeverFallsBackToHttp()
    {
        var seen = new List<string>();
        using var http = new HttpClient(new Handler(seen, url => url.StartsWith("http://", StringComparison.Ordinal)));
        var item = new DownloadItem("http://repo.example.test/maven2/a.jar", Path.Combine(_root, "a.jar"));

        await Assert.ThrowsAnyAsync<Exception>(() => Make(http).DownloadOneAsync(item));

        Assert.DoesNotContain(seen, u => u.StartsWith("http://", StringComparison.Ordinal));
        Assert.False(File.Exists(item.LocalPath));
    }

    [Fact]
    public async Task HashedFileFallsBackToHttpWhenHttpsFails()
    {
        var seen = new List<string>();
        using var http = new HttpClient(new Handler(seen, url => url.StartsWith("http://", StringComparison.Ordinal)));
        var item = new DownloadItem("http://repo.example.test/maven2/a.jar", Path.Combine(_root, "a.jar"),
            Convert.ToHexStringLower(SHA1.HashData(Payload)));

        await Make(http).DownloadOneAsync(item);

        Assert.Equal("http://repo.example.test/maven2/a.jar", seen[^1]);
        Assert.NotEmpty(seen[..^1]);
        Assert.All(seen[..^1], u => Assert.Equal("https://repo.example.test/maven2/a.jar", u));
        Assert.Equal(Payload, File.ReadAllBytes(item.LocalPath));
    }

    private static Downloader Make(HttpClient http) => new(http, new SourceChain([new OfficialSource()])) { RetriesPerSource = 1 };

    private sealed class Handler(List<string> seen, Func<string, bool> serves) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            lock (seen) seen.Add(url);
            return Task.FromResult(serves(url)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
