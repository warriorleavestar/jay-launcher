using System.Net;
using System.Text;
using Amber.Core.Content;

namespace Amber.Core.Tests;

/// <summary>CurseForge 条款不允许把 Key 交给第三方：只有官方接口地址才带 x-api-key。</summary>
public sealed class CurseForgeKeyScopeTests
{
    [Theory]
    [InlineData(CurseForgeProvider.DefaultApiRoot, true)]
    [InlineData("https://api.curseforge.com/v1/", true)]
    [InlineData("https://mod.mcimirror.top/curseforge/v1", false)]
    [InlineData("https://proxy.example.test/v1", false)]
    public async Task KeyIsSentOnlyToOfficialRoot(string root, bool expectKey)
    {
        using var handler = new HeaderRecorder();
        using var http = new HttpClient(handler);
        var provider = new CurseForgeProvider(http, () => "test-key", root);

        await provider.GetProjectAsync("123");

        Assert.Equal(expectKey, handler.SawKey);
    }

    private sealed class HeaderRecorder : HttpMessageHandler
    {
        public bool SawKey { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SawKey |= request.Headers.Contains("x-api-key");
            const string body = """{"data":{"id":123,"name":"Example","slug":"example","summary":"","authors":[],"links":{},"classId":6}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
