using System.Net;
using Amber.Core.Net;

namespace Amber.Core.Tests;

public class NetworkPolicyTests
{
    private sealed class Ok : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
    }

    [Theory]
    [InlineData("https://api.modrinth.com/v2/search", NetworkCategory.ContentApi)]
    [InlineData("https://login.microsoftonline.com/consumers/oauth2", NetworkCategory.Login)]
    [InlineData("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json", NetworkCategory.Download)]
    [InlineData("https://cdn.modrinth.com/data/x.jar", NetworkCategory.Download)]
    public void KnownHostsAreClassified(string url, NetworkCategory expected) =>
        Assert.Equal(expected, NetworkPolicy.ByHost(new Uri(url)));

    [Fact]
    public void SubdomainFallsBackToRegisteredParent()
    {
        NetworkPolicy.Register("https://skins.example-test.org/api", NetworkCategory.Login);
        Assert.Equal(NetworkCategory.Login, NetworkPolicy.ByHost(new Uri("https://a.skins.example-test.org/x")));
        Assert.Null(NetworkPolicy.ByHost(new Uri("https://example-test.org/x")));
    }

    [Fact]
    public void ScopeWinsOverClientAndHost()
    {
        var uri = new Uri("https://api.modrinth.com/v2/project");
        Assert.Equal(NetworkCategory.Download, NetworkPolicy.Classify(uri, NetworkCategory.Download));
        using (NetworkPolicy.Scope(NetworkCategory.UpdateCheck))
            Assert.Equal(NetworkCategory.UpdateCheck, NetworkPolicy.Classify(uri, NetworkCategory.Download));
        Assert.Equal(NetworkCategory.ContentApi, NetworkPolicy.Classify(uri));
    }

    [Fact]
    public async Task StrictModeBlocksUnclassifiedRequest()
    {
        using (NetworkPolicy.StrictScope())
        {
            using var http = new HttpClient(new NetworkPolicyHandler(new Ok()));
            await Assert.ThrowsAsync<UnclassifiedRequestException>(() => http.GetStringAsync("https://unknown.invalid/telemetry"));
            Assert.Equal("{}", await http.GetStringAsync("https://api.curseforge.com/v1/games"));
            using (NetworkPolicy.Scope(NetworkCategory.Multiplayer))
                Assert.Equal("{}", await http.GetStringAsync("https://node.invalid/rooms"));
        }
    }

    [Fact]
    public async Task NonStrictCountsUnclassifiedAndRecordsLastUse()
    {
        using var http = new HttpClient(new NetworkPolicyHandler(new Ok()));
        var before = NetworkPolicy.UnclassifiedCount;
        await http.GetStringAsync("https://unknown.invalid/x");
        Assert.Equal(before + 1, NetworkPolicy.UnclassifiedCount);
        await http.GetStringAsync("https://api.modrinth.com/v2/tag/loader");
        Assert.NotNull(NetworkPolicy.LastUsed(NetworkCategory.ContentApi));
    }

    [Fact]
    public async Task ScopeFlowsIntoAwaitedCallsAndEndsAfter()
    {
        NetworkCategory? seen = null;
        void Handler(NetworkCategory? c, Uri? u) { if (u?.Host == "scoped.invalid") seen = c; }
        NetworkPolicy.Requested += Handler;
        try
        {
            using var http = new HttpClient(new NetworkPolicyHandler(new Ok()));
            async Task Inner() { using var s = NetworkPolicy.Scope(NetworkCategory.Multiplayer); await http.GetStringAsync("https://scoped.invalid/a"); }
            await Inner();
            Assert.Equal(NetworkCategory.Multiplayer, seen);
            Assert.Null(NetworkPolicy.Classify(new Uri("https://scoped.invalid/a")));
        }
        finally { NetworkPolicy.Requested -= Handler; }
    }
}
