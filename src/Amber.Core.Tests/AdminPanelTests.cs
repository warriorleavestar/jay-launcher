using System.Net;
using System.Net.Sockets;
using System.Text;
using Amber.NetworkServer;
using Amber.Server;

namespace Amber.Core.Tests;

/// <summary>
/// The node's local web panel: static files with a strict content security policy, no data without the token, and
/// requests refused when the Host or Origin isn't this machine (DNS rebinding, cross-site pages).
/// </summary>
public sealed class AdminPanelTests : IAsyncDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jay-panel-test-" + Guid.NewGuid().ToString("N"));
    private JayNetworkServer? _server;

    public async ValueTask DisposeAsync()
    {
        if (_server is not null) await _server.DisposeAsync();
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private async Task<int> StartAsync()
    {
        _server = new JayNetworkServerBuilder().UseDataDir(_dir).WithoutEasyTier(new IPEndPoint(IPAddress.Loopback, 0)).UseAdmin(0).Build();
        await _server.StartAsync();
        return _server.AdminPort!.Value;
    }

    /// <summary>Sends one raw request (so the test controls Host and Origin) and returns the status line, headers and body.</summary>
    private static async Task<(int Status, string Head, string Body)> RawAsync(int port, string path, string? host, string? origin = null, string? token = null)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();
        var request = new StringBuilder($"GET {path} HTTP/1.1\r\n");
        if (host is not null) request.Append($"Host: {host}\r\n");
        if (origin is not null) request.Append($"Origin: {origin}\r\n");
        if (token is not null) request.Append($"Authorization: Bearer {token}\r\n");
        request.Append("Connection: close\r\n\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request.ToString()));
        var text = await new StreamReader(stream, Encoding.UTF8).ReadToEndAsync();
        var split = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        return (int.Parse(text.Split(' ')[1]), text[..split], text[(split + 4)..]);
    }

    [Fact]
    public async Task PanelIsServedWithAStrictPolicyAndHoldsNoData()
    {
        var port = await StartAsync();
        var (status, head, body) = await RawAsync(port, "/", $"127.0.0.1:{port}");
        Assert.Equal(200, status);
        Assert.Contains("Content-Type: text/html", head);
        Assert.Contains("Content-Security-Policy: default-src 'none'; script-src 'self'", head);
        Assert.Contains("X-Frame-Options: DENY", head);
        Assert.Contains("/panel.js", body);
        Assert.DoesNotContain(File.ReadAllText(_server!.AdminTokenPath).Trim(), body);
        Assert.Equal(200, (await RawAsync(port, "/panel.js", $"localhost:{port}")).Status);
        Assert.Equal(200, (await RawAsync(port, "/panel.css", "[::1]:8080")).Status);
        Assert.Equal(401, (await RawAsync(port, "/rooms", $"127.0.0.1:{port}")).Status);
        Assert.Equal(404, (await RawAsync(port, "/panel.map", $"127.0.0.1:{port}", token: File.ReadAllText(_server.AdminTokenPath).Trim())).Status);
    }

    [Fact]
    public async Task ForeignHostsAndOriginsAreRefused()
    {
        var port = await StartAsync();
        var token = File.ReadAllText(_server!.AdminTokenPath).Trim();
        // DNS rebinding: the browser sends the attacker's host name.
        Assert.Equal(403, (await RawAsync(port, "/", "evil.example:47669")).Status);
        Assert.Equal(403, (await RawAsync(port, "/health", "127.0.0.1.evil.example")).Status);
        Assert.Equal(403, (await RawAsync(port, "/rooms", null, token: token)).Status);
        // A cross-site page, even with the right token.
        Assert.Equal(403, (await RawAsync(port, "/rooms", $"127.0.0.1:{port}", "https://evil.example", token)).Status);
        Assert.Equal(200, (await RawAsync(port, "/rooms", $"127.0.0.1:{port}", $"http://127.0.0.1:{port}", token)).Status);
        Assert.Equal(200, (await RawAsync(port, "/rooms", "localhost:9000", "http://localhost:9000", token)).Status);
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.0.0.1:47669", true)]
    [InlineData("LOCALHOST:8080", true)]
    [InlineData("[::1]:47669", true)]
    [InlineData("localhost.", false)]
    [InlineData("127.0.0.2:47669", false)]
    [InlineData("127.0.0.1:abc", false)]
    [InlineData("127.0.0.1:", false)]
    [InlineData("evil.example", false)]
    [InlineData("", false)]
    public void HostHeaderMustNameThisMachine(string host, bool ok) => Assert.Equal(ok, AdminApi.IsLocalHost(host));

    [Fact]
    public void PanelScriptNeverBuildsHtmlFromData()
    {
        var js = Encoding.UTF8.GetString(AdminPanel.Find("/panel.js")!.Bytes);
        foreach (var sink in new[] { "innerHTML", "outerHTML", "insertAdjacentHTML", "document.write", "eval(", "new Function" })
            Assert.DoesNotContain(sink, js);
        var html = Encoding.UTF8.GetString(AdminPanel.Find("/")!.Bytes);
        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("http://", html.Replace("http://127.0.0.1", ""));
        Assert.DoesNotContain("https://", html);
    }

    [Fact]
    public void GuestDownloadsUseDirectGitHubReleaseAssetsWithoutAnAdminToken()
    {
        var html = Encoding.UTF8.GetString(AdminPanel.Find("/")!.Bytes);
        var js = Encoding.UTF8.GetString(AdminPanel.Find("/panel.js")!.Bytes);
        var css = Encoding.UTF8.GetString(AdminPanel.Find("/panel.css")!.Bytes);

        Assert.Contains("id=\"guest-downloads\"", html);
        Assert.Contains("data-download=\"android\"", html);
        Assert.Contains("github.com/warriorleavestar/jay-launcher/releases", js);
        Assert.Contains("/download/android-preview/Jay-android-experimental.apk", js);
        Assert.Contains("/latest/download/Jay-win-x64.zip", js);
        Assert.DoesNotContain("Authorization", html);
        Assert.Contains("[hidden] { display: none !important; }", css);
    }

    [Fact]
    public async Task NodePanelCommandPrintsALoginLink()
    {
        var port = await StartAsync();
        var output = new StringWriter();
        Assert.Equal(0, await Program.NodeCommand(_dir, Options.Parse(["node", "panel", "--admin-port", port.ToString()]), output));
        Assert.Contains($"http://127.0.0.1:{port}/#token={File.ReadAllText(_server!.AdminTokenPath).Trim()}", output.ToString());
    }
}
