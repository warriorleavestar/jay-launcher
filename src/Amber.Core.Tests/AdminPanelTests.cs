using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
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

    /// <summary>The elements the panel shows or hides by sign-in state.</summary>
    private static readonly string[] SignInToggled = ["login", "app", "logout", "refresh"];

    [Fact]
    public void PanelShowsOnlyWhatTheSignInStateAllows()
    {
        var html = Encoding.UTF8.GetString(AdminPanel.Find("/")!.Bytes);
        var js = Encoding.UTF8.GetString(AdminPanel.Find("/panel.js")!.Bytes);
        var css = Encoding.UTF8.GetString(AdminPanel.Find("/panel.css")!.Bytes);

        // Before the script decides, nothing that needs a sign-in shows (and no empty sign-in card flashes either).
        foreach (var id in new[] { "login", "app", "logout", "login-error", "toast" })
            Assert.True(HasHiddenAttribute(html, id), $"#{id} should start hidden");
        Assert.False(HasHiddenAttribute(html, "refresh"));

        // Not signed in, or signed out: only the sign-in card. Signed in: the app and its header buttons, no sign-in card.
        var signedOut = new Dictionary<string, bool> { ["login"] = false, ["app"] = true, ["logout"] = true, ["refresh"] = true };
        var signedIn = new Dictionary<string, bool> { ["login"] = true, ["app"] = false, ["logout"] = false, ["refresh"] = false };
        Assert.Equal(signedOut, HiddenAssignments(FunctionBody(js, "function showLogin(")));
        Assert.Equal(signedIn, HiddenAssignments(FunctionBody(js, "async function refresh(")));
        // Every way out of a session lands on the same sign-in state and forgets the token.
        var signOut = FunctionBody(js, "function signOut(");
        Assert.Contains("token = null", signOut);
        Assert.Contains("storeToken(null)", signOut);
        Assert.Contains("showLogin(", signOut);
        // ...and leaves nothing from the old session on the page.
        Assert.Contains("clearSession()", signOut);
        var clear = FunctionBody(js, "function clearSession(");
        foreach (var cleared in new[] { "$(\"network\").textContent = \"\"", "state.textContent = \"\"", "state.hidden = true",
                     "$(\"numbers\").replaceChildren()", "$(\"rooms\").tBodies[0].replaceChildren()", "$(\"nodes\").replaceChildren()",
                     "$(\"bans\").tBodies[0].replaceChildren()", "$(\"rooms-summary\").textContent = \"\"", "$(\"nodes-summary\").textContent = \"\"",
                     "$(\"token\").value = \"\"" })
            Assert.Contains(cleared, clear);
        Assert.True(HasHiddenAttribute(html, "state"), "the status pill should start hidden");
        // A refresh still on its way when the session ends must not bring the old data back.
        var refresh = FunctionBody(js, "async function refresh(");
        Assert.Contains("const asked = token;", refresh);
        Assert.True(refresh.IndexOf("if (!asked || token !== asked) return;", StringComparison.Ordinal)
                    < refresh.IndexOf("$(\"app\").hidden = false", StringComparison.Ordinal));
        Assert.Contains("$(\"logout\").addEventListener(\"click\", () => signOut())", js);
        Assert.Matches(@"response\.status === 401\) \{ signOut\(", FunctionBody(js, "async function api("));
        Assert.Contains("if (initial) signIn(initial); else showLogin();", js);
        // Only those two functions flip these elements, so no other path can show the app while signed out.
        foreach (var id in SignInToggled)
            Assert.Equal(2, Regex.Matches(js, $@"\$\(""{id}""\)\.hidden\s*=").Count);

        // The browser's own [hidden] rule loses to any author display rule (#app is a grid, .card and buttons get
        // display too), so the stylesheet restates it at the top level with !important and nothing overrides it.
        var rule = Regex.Match(css, @"(?<![\w-])\[hidden\]\s*\{\s*display:\s*none\s*!important;?\s*\}");
        Assert.True(rule.Success, "panel.css must keep [hidden] { display: none !important; }");
        Assert.Equal(0, css[..rule.Index].Count(c => c == '{') - css[..rule.Index].Count(c => c == '}'));
        Assert.Single(Regex.Matches(css, "!important"));
        Assert.DoesNotMatch(@":not\(\[hidden\]\)", css);
        Assert.Matches(@"#app\s*\{[^}]*display:\s*grid", css);
    }

    private static bool HasHiddenAttribute(string html, string id)
    {
        var tag = Regex.Match(html, $@"<\w+\b[^>]*\bid=""{Regex.Escape(id)}""[^>]*>");
        Assert.True(tag.Success, $"index.html has no #{id}");
        return Regex.IsMatch(tag.Value, @"\shidden(?=[\s>])");
    }

    /// <summary>The body of a top-level function in panel.js, by brace matching from its declaration.</summary>
    private static string FunctionBody(string js, string declaration)
    {
        var start = js.IndexOf(declaration, StringComparison.Ordinal);
        Assert.True(start >= 0, $"panel.js has no {declaration}");
        var open = js.IndexOf('{', js.IndexOf(')', start));
        for (int i = open, depth = 0; i < js.Length; i++)
        {
            depth += js[i] == '{' ? 1 : js[i] == '}' ? -1 : 0;
            if (depth == 0) return js[(open + 1)..i];
        }
        throw new InvalidDataException("unbalanced " + declaration);
    }

    /// <summary>The literal <c>$("id").hidden = true|false</c> assignments to the sign-in toggled elements in a code block.</summary>
    private static Dictionary<string, bool> HiddenAssignments(string body) =>
        Regex.Matches(body, @"\$\(""([\w-]+)""\)\.hidden\s*=\s*(true|false)\s*;")
            .Where(m => SignInToggled.Contains(m.Groups[1].Value))
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value == "true");

    [Fact]
    public async Task NodePanelCommandPrintsALoginLink()
    {
        var port = await StartAsync();
        var output = new StringWriter();
        Assert.Equal(0, await Program.NodeCommand(_dir, Options.Parse(["node", "panel", "--admin-port", port.ToString()]), output));
        Assert.Contains($"http://127.0.0.1:{port}/#token={File.ReadAllText(_server!.AdminTokenPath).Trim()}", output.ToString());
    }
}
