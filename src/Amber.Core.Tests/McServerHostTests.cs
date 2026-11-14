using System.Net;
using Amber.Core.Multiplayer.Gate;
using Amber.Core.Net;
using Xunit;

namespace Amber.Core.Tests;

/// <summary>Jay-only server setup: the files it writes from downloaded or user-edited input.</summary>
public sealed class McServerHostTests : IDisposable
{
    // The server root sits one level inside a folder of the test's own, so a path that escapes it still lands there.
    private readonly string _outer = Directory.CreateTempSubdirectory("jay-mchost-").FullName;
    private string Root => Path.Combine(_outer, "root");

    /// <summary>
    /// Java's Properties loader keeps the last line for a key and accepts "key = v", "key:v" and "key v": every spelling
    /// of a forced key goes, and the forced line is the only one left.
    /// </summary>
    [Fact]
    public void ForcedPropertiesWinOverEverySpellingOfTheKey()
    {
        var host = new McServerHost(Root);
        Directory.CreateDirectory(host.McDirectory);
        var path = Path.Combine(host.McDirectory, "server.properties");
        File.WriteAllLines(path,
        [
            "# server-ip=0.0.0.0",
            "server-ip=127.0.0.1",
            "motd=hi",
            "server-ip=",
            "enable-rcon = true",
            "  enable-rcon:true",
            "enable-query true",
            "online-mode=true",
        ]);
        host.WriteProperties(25570);
        var lines = File.ReadAllLines(path);
        string[] Lines(string key) => [.. lines.Where(l => McServerHost.PropertyKey(l) == key)];
        Assert.Equal(["server-ip=127.0.0.1"], Lines("server-ip"));
        Assert.Equal(["enable-rcon=false"], Lines("enable-rcon"));
        Assert.Equal(["enable-query=false"], Lines("enable-query"));
        Assert.Equal(["server-port=25570"], Lines("server-port"));
        // Keys Jay doesn't force, comments and the owner's own online-mode stay as they were.
        Assert.Contains("motd=hi", lines);
        Assert.Contains("# server-ip=0.0.0.0", lines);
        Assert.Equal(["online-mode=true"], Lines("online-mode"));
    }

    [Theory]
    [InlineData("key=value", "key")]
    [InlineData("  key = value", "key")]
    [InlineData("key:value", "key")]
    [InlineData("\tkey value", "key")]
    [InlineData("key", "key")]
    [InlineData("# key=value", null)]
    [InlineData("! key=value", null)]
    [InlineData("   ", null)]
    public void PropertyKeysAreReadAsJavaReadsThem(string line, string? key) => Assert.Equal(key, McServerHost.PropertyKey(line));

    /// <summary>The version id comes from the downloaded manifest; one that is not a plain file name writes nothing.</summary>
    [Theory]
    [InlineData("../gate/state")]
    [InlineData("../../escaped")]
    [InlineData("a/b")]
    public async Task AVersionIdThatIsNotAFileNameIsRefused(string id)
    {
        var manifest = $$"""{"latest":{"release":"{{id}}"},"versions":[{"id":"{{id}}","type":"release","url":"https://meta.test/v.json","sha1":null}]}""";
        var requests = new List<string>();
        var handler = new Handler(url =>
        {
            lock (requests) requests.Add(url);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(url.Contains("version_manifest") ? manifest : "{}") };
        });
        var downloader = new Downloader(new HttpClient(handler), new SourceChain([new OfficialSource()])) { RetriesPerSource = 1 };
        var host = new McServerHost(Root);
        await Assert.ThrowsAsync<InvalidDataException>(() => host.SetupAsync(downloader, "latest", 1024, _ => { }));
        lock (requests) Assert.DoesNotContain(requests, r => r.Contains("v.json", StringComparison.Ordinal));
        Assert.False(Directory.Exists(Path.Combine(Root, "gate")));
        Assert.Empty(Directory.EnumerateFiles(_outer, "*", SearchOption.AllDirectories).Where(f => !f.StartsWith(Path.Combine(Root, "launcher-cache"), StringComparison.Ordinal)));
    }

    private sealed class Handler(Func<string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request.RequestUri!.ToString()));
    }

    public void Dispose()
    {
        try { Directory.Delete(_outer, true); }
        catch (IOException) { }
    }
}
