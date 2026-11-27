using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amber.NetworkServer;

namespace Amber.Core.Tests;

/// <summary>
/// The node's web panel in a real browser (headless Edge or Chrome, whichever this machine has): what shows before
/// signing in, once signed in, and after signing out, at 600×420 and in English and Japanese. Without such a browser
/// there is nothing to drive and the test passes without checking. With AMBER_PANEL_SHOTS set to a folder, it also
/// saves a screenshot of each state there for review.
/// </summary>
public sealed class AdminPanelBrowserTests : IAsyncDisposable
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jay-panel-browser-" + Guid.NewGuid().ToString("N"));
    private JayNetworkServer? _server;

    public async ValueTask DisposeAsync()
    {
        if (_server is not null) await _server.DisposeAsync();
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>A Chromium browser on this machine, or null.</summary>
    private static string? FindBrowser()
    {
        string[] candidates = OperatingSystem.IsWindows()
            ?
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
            ]
            : OperatingSystem.IsMacOS()
                ? ["/Applications/Google Chrome.app/Contents/MacOS/Google Chrome", "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge"]
                : ["/usr/bin/google-chrome", "/usr/bin/google-chrome-stable", "/usr/bin/chromium", "/usr/bin/chromium-browser", "/usr/bin/microsoft-edge"];
        return candidates.FirstOrDefault(File.Exists);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ja")]
    public async Task SignInAndSignOutShowOnlyWhatTheStateAllows(string lang)
    {
        if (FindBrowser() is not { } browser) return;
        _server = new JayNetworkServerBuilder().UseDataDir(_dir).WithoutEasyTier(new IPEndPoint(IPAddress.Loopback, 0)).UseAdmin(0).Build();
        await _server.StartAsync();
        var url = $"http://127.0.0.1:{_server.AdminPort!.Value}/";
        var token = File.ReadAllText(_server.AdminTokenPath).Trim();

        await using var page = await Browser.StartAsync(browser, Path.Combine(_dir, "browser-" + lang), lang);
        await page.SendAsync("Emulation.setDeviceMetricsOverride", new JsonObject { ["width"] = 600, ["height"] = 420, ["deviceScaleFactor"] = 1, ["mobile"] = false });
        await page.SendAsync("Page.navigate", new JsonObject { ["url"] = url });

        // Signed out: only the sign-in card.
        await page.WaitAsync("!document.getElementById('login').hidden");
        var before = await Visible(page);
        Assert.True(before["login"], "sign-in card shows");
        Assert.False(before["app"] || before["logout"] || before["refresh"], Describe(before));
        Assert.Equal(lang, await page.EvalAsync<string>("document.documentElement.lang"));
        await FitsAsync(page);
        await ShotAsync(page, $"panel-signed-out-{lang}-600x420");

        // Signed in: the data and its header buttons, no sign-in card.
        await page.EvalAsync<bool>($"(() => {{ document.getElementById('token').value = {JsonSerializer.Serialize(token)}; document.getElementById('signin').click(); return true; }})()");
        await page.WaitAsync("!document.getElementById('app').hidden");
        var signedIn = await Visible(page);
        Assert.True(signedIn["app"] && signedIn["logout"] && signedIn["refresh"], Describe(signedIn));
        Assert.False(signedIn["login"], "sign-in card is gone");
        await FitsAsync(page);
        await ShotAsync(page, $"panel-signed-in-{lang}-600x420");

        // Signed out again: back to the sign-in card, nothing of the session left on the page.
        await page.EvalAsync<bool>("(() => { document.getElementById('logout').click(); return true; })()");
        await page.WaitAsync("!document.getElementById('login').hidden");
        var after = await Visible(page);
        Assert.True(after["login"], "sign-in card shows again");
        Assert.False(after["app"] || after["logout"] || after["refresh"], Describe(after));
        Assert.Equal("", await page.EvalAsync<string>("document.getElementById('token').value"));
        Assert.Equal(0, await page.EvalAsync<int>("document.getElementById('numbers').childElementCount + document.getElementById('rooms').tBodies[0].rows.length"));
        Assert.Null(await page.EvalAsync<string?>("localStorage.getItem('jay-node-token')"));
        await ShotAsync(page, $"panel-signed-out-again-{lang}-600x420");
    }

    private static readonly string[] Toggled = ["login", "app", "logout", "refresh"];

    /// <summary>Whether each sign-in-dependent element is on screen: not hidden, laid out, with a size.</summary>
    private static async Task<Dictionary<string, bool>> Visible(Browser page)
    {
        var result = new Dictionary<string, bool>();
        foreach (var id in Toggled)
            result[id] = await page.EvalAsync<bool>(
                $"(() => {{ const e = document.getElementById('{id}'); if (!e || e.hidden) return false; const r = e.getBoundingClientRect(); return getComputedStyle(e).display !== 'none' && r.width > 0 && r.height > 0; }})()");
        return result;
    }

    private static string Describe(Dictionary<string, bool> visible) => string.Join(", ", visible.Select(p => $"{p.Key}={p.Value}"));

    /// <summary>At 600 wide the page itself doesn't scroll sideways (wide tables scroll inside their own box).</summary>
    private static async Task FitsAsync(Browser page)
    {
        var overflow = await page.EvalAsync<int>("document.documentElement.scrollWidth - document.documentElement.clientWidth");
        Assert.True(overflow <= 1, $"page is {overflow}px wider than the window");
    }

    private static async Task ShotAsync(Browser page, string name)
    {
        if (Environment.GetEnvironmentVariable("AMBER_PANEL_SHOTS") is not { Length: > 0 } folder) return;
        Directory.CreateDirectory(folder);
        var shot = await page.SendAsync("Page.captureScreenshot", new JsonObject { ["format"] = "png" });
        await File.WriteAllBytesAsync(Path.Combine(folder, name + ".png"), Convert.FromBase64String(shot["data"]!.GetValue<string>()));
    }

    /// <summary>One headless browser with one page, driven over the DevTools protocol.</summary>
    private sealed class Browser : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly ClientWebSocket _socket;
        private int _id;

        private Browser(Process process, ClientWebSocket socket)
        {
            _process = process;
            _socket = socket;
        }

        public static async Task<Browser> StartAsync(string executable, string profile, string lang)
        {
            Directory.CreateDirectory(profile);
            var psi = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var arg in new[] { "--headless=new", "--disable-gpu", "--no-first-run", "--no-default-browser-check", "--disable-extensions",
                         "--disable-sync", "--remote-debugging-port=0", "--remote-debugging-address=127.0.0.1", $"--user-data-dir={profile}",
                         $"--lang={lang}", $"--accept-lang={lang}", "about:blank" })
                psi.ArgumentList.Add(arg);
            // CI runners on Linux can't use the browser's own sandbox; the page is a local test page.
            if (OperatingSystem.IsLinux()) psi.ArgumentList.Insert(0, "--no-sandbox");
            var process = Process.Start(psi)!;
            process.OutputDataReceived += (_, _) => { };
            process.ErrorDataReceived += (_, _) => { };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            try
            {
                var portFile = Path.Combine(profile, "DevToolsActivePort");
                var waited = Stopwatch.StartNew();
                string? port = null;
                while (port is null)
                {
                    if (waited.Elapsed > Deadline) throw new TimeoutException("the browser didn't open its DevTools port");
                    try { port = File.Exists(portFile) ? File.ReadLines(portFile).FirstOrDefault() : null; } catch (IOException) { }
                    if (port is null) await Task.Delay(100);
                }
                using var http = new HttpClient();
                string? target = null;
                while (target is null)
                {
                    if (waited.Elapsed > Deadline) throw new TimeoutException("the browser opened no page");
                    var list = JsonNode.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/json/list"))!.AsArray();
                    target = list.FirstOrDefault(t => t?["type"]?.GetValue<string>() == "page")?["webSocketDebuggerUrl"]?.GetValue<string>();
                    if (target is null) await Task.Delay(100);
                }
                var socket = new ClientWebSocket();
                await socket.ConnectAsync(new Uri(target), new CancellationTokenSource(Deadline).Token);
                return new Browser(process, socket);
            }
            catch
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                process.Dispose();
                throw;
            }
        }

        /// <summary>Sends one command and returns its result (events that arrive meanwhile are skipped).</summary>
        public async Task<JsonObject> SendAsync(string method, JsonObject? parameters = null)
        {
            var id = ++_id;
            var message = new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters ?? new JsonObject() };
            using var limit = new CancellationTokenSource(Deadline);
            await _socket.SendAsync(Encoding.UTF8.GetBytes(message.ToJsonString()), WebSocketMessageType.Text, true, limit.Token);
            var buffer = new byte[1 << 16];
            while (true)
            {
                using var text = new MemoryStream();
                WebSocketReceiveResult received;
                do
                {
                    received = await _socket.ReceiveAsync(buffer, limit.Token);
                    text.Write(buffer, 0, received.Count);
                } while (!received.EndOfMessage);
                var reply = JsonNode.Parse(text.ToArray())!.AsObject();
                if (reply["id"]?.GetValue<int>() != id) continue;
                if (reply["error"] is { } error) throw new InvalidOperationException($"{method}: {error.ToJsonString()}");
                return reply["result"]!.AsObject();
            }
        }

        public async Task<T> EvalAsync<T>(string expression)
        {
            var result = await SendAsync("Runtime.evaluate", new JsonObject { ["expression"] = expression, ["returnByValue"] = true, ["awaitPromise"] = true });
            if (result["exceptionDetails"] is { } thrown) throw new InvalidOperationException(thrown.ToJsonString());
            var value = result["result"]?["value"];
            return value is null ? default! : value.Deserialize<T>()!;
        }

        /// <summary>Waits until <paramref name="condition"/> is true on the page.</summary>
        public async Task WaitAsync(string condition)
        {
            var waited = Stopwatch.StartNew();
            while (!await EvalAsync<bool>($"(() => {{ try {{ return !!({condition}); }} catch {{ return false; }} }})()"))
            {
                if (waited.Elapsed > Deadline) throw new TimeoutException(condition);
                await Task.Delay(100);
            }
        }

        public async ValueTask DisposeAsync()
        {
            try { await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); } catch (WebSocketException) { }
            _socket.Dispose();
            try { _process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)).ContinueWith(_ => { });
            _process.Dispose();
        }
    }
}
