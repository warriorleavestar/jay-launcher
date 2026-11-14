using System.Net;
using System.Security.Cryptography;
using System.Text;
using Amber.Core.Net;

namespace Amber.Core.Tests;

/// <summary>Loader installers run outside the sandbox: their hash comes from the official repository, whichever source serves the file.</summary>
public sealed class OfficialHashTests : IDisposable
{
    private const string Installer = "https://maven.minecraftforge.net/net/minecraftforge/forge/1.20.1-47.2.0/forge-1.20.1-47.2.0-installer.jar";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jay-official-hash-" + Guid.NewGuid().ToString("N")[..8]);

    public OfficialHashTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static string Sha1(string text) => Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(text)));

    private sealed class Handler(Dictionary<string, string> bodies) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            lock (Requests) Requests.Add(url);
            return Task.FromResult(bodies.TryGetValue(url, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    [Fact]
    public async Task TamperedMirrorCopyIsRefusedAndTheOfficialFileIsKept()
    {
        var mirror = SourceChain.MirrorFirst.Candidates(Installer)[0];
        Assert.NotEqual(Installer, mirror);
        using var handler = new Handler(new()
        {
            [Installer + ".sha1"] = Sha1("genuine installer"),
            [mirror] = "tampered installer",
            [Installer] = "genuine installer",
        });
        var downloader = new Downloader(new HttpClient(handler), SourceChain.MirrorFirst) { RetriesPerSource = 1 };
        var path = Path.Combine(_dir, "installer.jar");

        await downloader.DownloadOneAsync(new DownloadItem(Installer, path, await downloader.GetOfficialSha1Async(Installer)));

        Assert.Equal("genuine installer", File.ReadAllText(path));
        // The hash itself was never asked of the mirror.
        Assert.DoesNotContain(handler.Requests, u => u.EndsWith(".sha1", StringComparison.Ordinal) && !u.StartsWith(Installer, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("ded43dd18b3a1dd5098b114c28432224d72bd9f7", "ded43dd18b3a1dd5098b114c28432224d72bd9f7")]
    [InlineData("DED43DD18B3A1DD5098B114C28432224D72BD9F7  forge-installer.jar\n", "ded43dd18b3a1dd5098b114c28432224d72bd9f7")]
    [InlineData("<html>not found</html>", null)]
    [InlineData("", null)]
    public async Task PublishedHashIsReadOrIgnored(string body, string? expected)
    {
        using var handler = new Handler(new() { [Installer + ".sha1"] = body });
        var downloader = new Downloader(new HttpClient(handler), SourceChain.OfficialFirst);
        Assert.Equal(expected, await downloader.GetOfficialSha1Async(Installer));
    }

    [Fact]
    public async Task UnreachableOfficialHostLeavesTheDownloadAsItWas()
    {
        using var handler = new Handler([]);
        var downloader = new Downloader(new HttpClient(handler), SourceChain.MirrorFirst);
        Assert.Null(await downloader.GetOfficialSha1Async(Installer));
        Assert.Single(handler.Requests);
    }
}
