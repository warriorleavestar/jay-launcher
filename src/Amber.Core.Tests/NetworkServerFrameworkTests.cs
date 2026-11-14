using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Amber.Core.Localization;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Public;
using Amber.Core.Multiplayer.Transport;
using Amber.NetworkServer;
using Amber.Server;

namespace Amber.Core.Tests;

/// <summary>
/// 网络服务端框架（Jay.NetworkServer）：审核规则（网络策略、屏蔽词、封禁、限流）、事件、自定义请求、
/// 换账号库、本机管理接口、搭网络的脚手架，以及 jay-server 的 directory / node / network init 命令。全在本机回环上跑，不启动 EasyTier。
/// </summary>
public sealed class NetworkServerFrameworkTests : IAsyncDisposable
{
    private DateTimeOffset _now = new(2026, 11, 9, 0, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jay-netserver-test-" + Guid.NewGuid().ToString("N"));
    private readonly List<IAsyncDisposable> _cleanup = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var c in _cleanup) await c.DisposeAsync();
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static RoomListing Listing(X25519KeyPair host, RoomVisibility mode = RoomVisibility.Public, string name = "生存服", string hostName = "Alex") => new()
    {
        Id = "",
        Name = name,
        Host = hostName,
        Mode = mode,
        HostKey = host.PublicKey,
        Ip = "10.235.0.7",
        Game = "1.21.1",
        Players = 2,
        Invite = mode is RoomVisibility.Public or RoomVisibility.Direct ? new InviteBook().CreateStanding(false, TunnelSettings.Default).Invite.ToString() : null,
        Lobby = mode == RoomVisibility.Password ? new string('a', 32) : null,
        Salt = mode == RoomVisibility.Password ? new string('b', 32) : null,
    };

    private JsonObject Announce(RoomSigner signer, RoomListing listing, bool listed = true)
    {
        var signed = signer.Sign(listing, _now);
        return new()
        {
            ["op"] = "announce",
            ["room"] = signed.ToJson(),
            ["listed"] = listed,
            ["hidden"] = new JsonArray(listed ? [] : [HiddenFor(signed)]),
        };
    }

    /// <summary>A hidden entry under a fresh invite (each hidden test room gets its own index).</summary>
    private static JsonNode HiddenFor(RoomListing listing)
    {
        var lobby = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        var sealedEntry = HiddenEntry.Seal(lobby, new DirectoryHit("room", "10.235.0.7", new byte[32]));
        return new JsonObject
        {
            ["k"] = Convert.ToHexStringLower(HiddenEntry.Index(lobby)),
            ["b"] = Convert.ToBase64String(sealedEntry),
            ["p"] = Convert.ToHexStringLower(HiddenEntry.PublicKey(lobby)),
            ["s"] = Convert.ToHexStringLower(HiddenEntry.SignPublish(lobby, sealedEntry, listing.Id, listing.Seq)),
        };
    }

    private static bool Ok(JsonObject reply) => reply["ok"]?.GetValue<bool>() == true;

    private async Task<JayNetworkServer> StartAsync(Action<JayNetworkServerBuilder>? configure = null, bool admin = false)
    {
        var builder = new JayNetworkServerBuilder().UseDataDir(_dir).UseClock(() => _now).WithoutEasyTier(new IPEndPoint(IPAddress.Loopback, 0));
        if (admin) builder.UseAdmin(0); else builder.WithoutAdmin();
        configure?.Invoke(builder);
        var server = builder.Build();
        _cleanup.Add(server);
        await server.StartAsync();
        return server;
    }

    private sealed class Recorder : IDirectoryObserver
    {
        public readonly List<string> Events = [];
        public void OnAnnounced(RoomListing listing, bool listed, bool isNew) { lock (Events) Events.Add(isNew ? "new" : "renew"); }
        public void OnRemoved(string roomId, RoomRemoval reason) { lock (Events) Events.Add("removed:" + reason); }
        public void OnRejected(RoomListing listing, RoomVerdict verdict) { lock (Events) Events.Add("rejected:" + verdict.Code); }
        public void OnLogin(bool ok, string? error) { lock (Events) Events.Add("login:" + (ok ? "ok" : error)); }
    }

    private sealed class Throwing : IDirectoryObserver
    {
        public void OnAnnounced(RoomListing listing, bool listed, bool isNew) => throw new InvalidOperationException("boom");
    }

    [Fact]
    public async Task PolicyGateEnforcesNetworkModesButAlwaysAllowsHidden()
    {
        var config = NetworkConfig.Parse("""{"v":1,"network":"nz","policy":{"modes":["public"]}}"""u8, "nz");
        var server = await StartAsync(b => b.UseConfig(config));
        var host = X25519KeyPair.Generate();
        Assert.True(Ok(server.Directory!.Handle(Announce(new RoomSigner(), Listing(host)))));
        var reply = server.Directory.Handle(Announce(new RoomSigner(), Listing(host, RoomVisibility.Password)));
        Assert.Equal("mode", reply["error"]!.GetValue<string>());
        Assert.False(string.IsNullOrEmpty(reply["reason"]?.GetValue<string>()));
        Assert.True(Ok(server.Directory.Handle(Announce(new RoomSigner(), Listing(host, RoomVisibility.Hidden), listed: false))));
    }

    [Fact]
    public async Task RejectionReasonReachesTheHostThroughTheClient()
    {
        var server = await StartAsync(b => b.UseBlockedWords(["坏 词"]));
        var net = new LoopbackNetwork(server.Directory!.Port);
        var client = new RoomDirectoryClient(net) { NetworkSecret = server.Directory.NetworkSecret };
        var host = X25519KeyPair.Generate();
        var signer = new RoomSigner();
        var e = await Assert.ThrowsAsync<DirectoryRejectedException>(() =>
            client.AnnounceAsync(signer.Sign(Listing(host, name: "我的坏词房"), _now), true, [], default));
        Assert.Equal("name", e.Code);
        Assert.Contains("不允许的词", e.Message);
        // 隐藏房间不进列表，屏蔽词不管。
        Assert.True(Ok(server.Directory.Handle(Announce(new RoomSigner(), Listing(host, RoomVisibility.Hidden, name: "坏词"), listed: false))));
    }

    private sealed class LoopbackNetwork(int port) : IPublicNetwork
    {
        public async Task<Stream> ConnectAsync(string ip, int p, CancellationToken ct)
        {
            var c = new TcpClient();
            await c.ConnectAsync(IPAddress.Loopback, port, ct);
            return c.GetStream();
        }

        public Task<string?> MyIpAsync(CancellationToken ct) => Task.FromResult<string?>("10.235.0.7");
    }

    [Fact]
    public async Task BanByHostRemovesLiveRoomsBlocksRenewalAndPersists()
    {
        var server = await StartAsync();
        var host = X25519KeyPair.Generate();
        var signer = new RoomSigner();
        Assert.True(Ok(server.Directory!.Handle(Announce(signer, Listing(host)))));
        Assert.Single(server.Directory.ListedRooms());

        var fingerprint = MemberIdentity.FingerprintOf(host.PublicKey);
        Assert.Equal(1, server.Ban("host", fingerprint, "刷屏"));
        Assert.Empty(server.Directory.ListedRooms());
        _now += TimeSpan.FromSeconds(30);
        Assert.Equal("banned", server.Directory.Handle(Announce(signer, Listing(host)))["error"]!.GetValue<string>());
        Assert.Equal("banned", server.Directory.Handle(Announce(new RoomSigner(), Listing(host)))["error"]!.GetValue<string>());
        // 别人不受影响。
        Assert.True(Ok(server.Directory.Handle(Announce(new RoomSigner(), Listing(X25519KeyPair.Generate())))));

        var reloaded = new BanList(Path.Combine(_dir, "bans.json"));
        Assert.Contains(reloaded.List(), b => b.Kind == "host" && b.Value == fingerprint && b.Reason == "刷屏");
        Assert.True(server.Bans!.Remove("host", fingerprint));
        Assert.True(Ok(server.Directory.Handle(Announce(new RoomSigner(), Listing(host)))));
        Assert.Throws<ArgumentException>(() => server.Bans.Add("ip", "1.2.3.4", null, _now));
    }

    [Fact]
    public async Task RenewalThatAGateNowRejectsTakesTheRoomOut()
    {
        var blocked = false;
        var server = await StartAsync(b => b.AddGate(new LambdaGate(_ => blocked ? RoomVerdict.Reject("closed", "维护中") : RoomVerdict.Allow)));
        var host = X25519KeyPair.Generate();
        var signer = new RoomSigner();
        Assert.True(Ok(server.Directory!.Handle(Announce(signer, Listing(host)))));
        blocked = true;
        _now += TimeSpan.FromSeconds(30);
        var reply = server.Directory.Handle(Announce(signer, Listing(host)));
        Assert.Equal("closed", reply["error"]!.GetValue<string>());
        Assert.Equal("维护中", reply["reason"]!.GetValue<string>());
        Assert.Equal(0, server.Directory.Count);
    }

    private sealed class LambdaGate(Func<RoomAnnouncement, RoomVerdict> check) : IRoomGate
    {
        public RoomVerdict Check(RoomAnnouncement announcement) => check(announcement);
    }

    [Fact]
    public async Task RateLimitCapsRoomsPerHostAndNewRoomsPerMinute()
    {
        var server = await StartAsync(b => b.UseRateLimit(maxRoomsPerHost: 2, newPerHostPerMinute: 3, newPerMinute: 100));
        var host = X25519KeyPair.Generate();
        var a = new RoomSigner();
        var b = new RoomSigner();
        Assert.True(Ok(server.Directory!.Handle(Announce(a, Listing(host)))));
        Assert.True(Ok(server.Directory.Handle(Announce(b, Listing(host)))));
        Assert.Equal("rate", server.Directory.Handle(Announce(new RoomSigner(), Listing(host)))["error"]!.GetValue<string>());
        // 续期不算新发布。
        _now += TimeSpan.FromSeconds(1);
        Assert.True(Ok(server.Directory.Handle(Announce(a, Listing(host)))));

        // 撤下一个就能再开，但这一分钟里新发布的次数已经用完了。
        var (seq, sig) = b.SignWithdraw(_now);
        Assert.True(Ok(server.Directory.Handle(new JsonObject { ["op"] = "withdraw", ["id"] = b.RoomId, ["seq"] = seq, ["sig"] = Convert.ToHexStringLower(sig) })));
        Assert.True(Ok(server.Directory.Handle(Announce(new RoomSigner(), Listing(host)))));
        var (seq2, sig2) = a.SignWithdraw(_now + TimeSpan.FromMilliseconds(5));
        Assert.True(Ok(server.Directory.Handle(new JsonObject { ["op"] = "withdraw", ["id"] = a.RoomId, ["seq"] = seq2, ["sig"] = Convert.ToHexStringLower(sig2) })));
        Assert.Equal("rate", server.Directory.Handle(Announce(new RoomSigner(), Listing(host)))["error"]!.GetValue<string>());
        _now += TimeSpan.FromMinutes(2);
        Assert.True(Ok(server.Directory.Handle(Announce(new RoomSigner(), Listing(host)))));
    }

    [Fact]
    public async Task ObserversSeeEveryEventAndAFaultyOneDoesNotBreakTheDirectory()
    {
        var recorder = new Recorder();
        var server = await StartAsync(b => b.AddObserver(new Throwing()).AddObserver(recorder).UseBlockedWords(["nope"]));
        var host = X25519KeyPair.Generate();
        var signer = new RoomSigner();
        Assert.True(Ok(server.Directory!.Handle(Announce(signer, Listing(host)))));
        _now += TimeSpan.FromSeconds(10);
        Assert.True(Ok(server.Directory.Handle(Announce(signer, Listing(host)))));
        server.Directory.Handle(Announce(new RoomSigner(), Listing(host, name: "nope")));
        _now += TimeSpan.FromMinutes(5);
        Assert.Equal(0, server.Directory.Count);
        Assert.Equal(["new", "renew", "rejected:name", "removed:Expired"], recorder.Events);
        var stats = server.Stats.ToJson();
        Assert.Equal(1, stats["published"]!.GetValue<long>());
        Assert.Equal(1, stats["renewed"]!.GetValue<long>());
        Assert.Equal(1, stats["expired"]!.GetValue<long>());
        Assert.Equal(1, stats["rejected"]!["name"]!.GetValue<long>());
    }

    private sealed class Motd : IDirectoryExtension
    {
        public string Op => "x-motd";
        public JsonObject Handle(JsonObject request, DirectoryView directory) => new() { ["text"] = "你好", ["rooms"] = directory.Rooms };
    }

    private sealed class Broken : IDirectoryExtension
    {
        public string Op => "x-broken";
        public JsonObject Handle(JsonObject request, DirectoryView directory) => throw new InvalidOperationException();
    }

    [Fact]
    public async Task ExtensionsAnswerTheirOwnOpsOnly()
    {
        Assert.Throws<ArgumentException>(() => new JayNetworkServerBuilder().AddExtension(new LambdaExtension("motd")));
        Assert.Throws<ArgumentException>(() => new JayNetworkServerBuilder().AddExtension(new Motd()).AddExtension(new Motd()));
        var server = await StartAsync(b => b.AddExtension(new Motd()).AddExtension(new Broken()));
        var reply = server.Directory!.Handle(new JsonObject { ["op"] = "x-motd" });
        Assert.True(Ok(reply));
        Assert.Equal("你好", reply["text"]!.GetValue<string>());
        Assert.Equal("ext", server.Directory.Handle(new JsonObject { ["op"] = "x-broken" })["error"]!.GetValue<string>());
        Assert.Equal("op", server.Directory.Handle(new JsonObject { ["op"] = "x-unknown" })["error"]!.GetValue<string>());
    }

    private sealed class LambdaExtension(string op) : IDirectoryExtension
    {
        public string Op => op;
        public JsonObject Handle(JsonObject request, DirectoryView directory) => [];
    }

    /// <summary>第三方的账号库：比如接自己的论坛账号。</summary>
    /// <param name="names">The account names it lists; none = it can't list accounts.</param>
    private sealed class ForumAccounts(params string[] names) : IAccountStore
    {
        public int Failures;
        public IReadOnlyCollection<string>? AccountNames() => names.Length == 0 ? null : names;
        public LoginResult Check(string name, string password, DateTimeOffset now) =>
            (names.Length == 0 ? name == "bob" : names.Contains(name)) && password == "forum-password" ? LoginResult.Ok : LoginResult.Wrong;
        public void RecordFailure(string name, DateTimeOffset now) => Failures++;
        public void Succeeded(string name) { }
        public byte[]? TotpSecret(string name) => null;
    }

    private sealed class PinFactor : ILoginFactor
    {
        public string Name => "pin";
        public string Prompt => "论坛私信里的 PIN";
        public bool AppliesTo(string user) => true;
        public bool Verify(string user, string response, DateTimeOffset now) => response == "4242";
    }

    [Fact]
    public async Task CustomAccountStoreAndFactorLogIn()
    {
        var store = new ForumAccounts();
        var recorder = new Recorder();
        var server = await StartAsync(b => b.UseAccounts(store).AddLoginFactor(new PinFactor()).AddObserver(recorder));
        var section = server.LoginSection!;
        Assert.Equal(["totp", "pin"], (section["factors"] as JsonArray)!.Select(f => f!.GetValue<string>()));
        var login = NetworkLogin.Parse(section)!;
        var me = X25519KeyPair.Generate();

        var wrong = server.Directory!.Handle(NetworkLoginClient.Request(me, login, "bob", "nope", _now));
        Assert.Equal("wrong", wrong["error"]!.GetValue<string>());
        var needPin = server.Directory.Handle(NetworkLoginClient.Request(me, login, "bob", "forum-password", _now));
        Assert.Equal("factor", needPin["error"]!.GetValue<string>());
        var reply = server.Directory.Handle(NetworkLoginClient.Request(me, login, "bob", "forum-password", _now, new Dictionary<string, string> { ["pin"] = "4242" }));
        var (_, token) = NetworkLoginClient.Read(reply, me, login, server.Profile.Id, _now);
        Assert.Equal("bob", token.User);
        Assert.Contains("login:ok", recorder.Events);
        Assert.Contains("login:wrong", recorder.Events);
        Assert.True(File.Exists(Path.Combine(_dir, "login.key")));
        Assert.Null(server.Builtin);
    }

    [Fact]
    public async Task AdminApiNeedsTheTokenAndHidesHiddenRooms()
    {
        var server = await StartAsync(admin: true);
        var host = X25519KeyPair.Generate();
        Assert.True(Ok(server.Directory!.Handle(Announce(new RoomSigner(), Listing(host, name: "公开房")))));
        Assert.True(Ok(server.Directory.Handle(Announce(new RoomSigner(), Listing(host, RoomVisibility.Hidden, name: "秘密房"), listed: false))));

        using var http = new HttpClient();
        var port = server.AdminPort!.Value;
        var health = JsonNode.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/health"))!;
        Assert.True(health["ok"]!.GetValue<bool>());
        Assert.Equal(2, health["rooms"]!.GetValue<int>());
        Assert.Equal("external", health["easytier"]!.GetValue<string>());

        var anonymous = await http.GetAsync($"http://127.0.0.1:{port}/rooms");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        var forged = new AdminClient(http, port, new string('0', 64));
        Assert.Equal("token", (await forged.CallAsync("GET", "/stats"))["error"]!.GetValue<string>());

        var admin = AdminClient.ForDataDir(http, _dir, port);
        var rooms = await admin.CallAsync("GET", "/rooms");
        Assert.Equal(1, rooms["hidden"]!.GetValue<int>());
        var listed = Assert.Single((rooms["rooms"] as JsonArray)!);
        Assert.Equal("公开房", listed!["name"]!.GetValue<string>());
        Assert.DoesNotContain("秘密房", rooms.ToJsonString());

        var ban = await admin.CallAsync("POST", "/ban", new JsonObject { ["kind"] = "host", ["value"] = MemberIdentity.FingerprintOf(host.PublicKey) });
        Assert.Equal(2, ban["removed"]!.GetValue<int>());
        Assert.Single((await admin.CallAsync("GET", "/bans"))["bans"] as JsonArray ?? []);
        Assert.Equal("bad", (await admin.CallAsync("POST", "/ban", new JsonObject { ["kind"] = "nope", ["value"] = "x" }))["error"]!.GetValue<string>());
        Assert.Equal("not_found", (await admin.CallAsync("POST", "/remove", new JsonObject { ["id"] = "nope" }))["error"]!.GetValue<string>());

        // 口令文件只有本机这个用户能读。
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(server.AdminTokenPath));
    }

    [Fact]
    public async Task MetricsArePrometheusTextBehindTheTokenWithOnlyTotals()
    {
        var server = await StartAsync(b => b.UseBlockedWords(["nope"]), admin: true);
        var host = X25519KeyPair.Generate();
        Assert.True(Ok(server.Directory!.Handle(Announce(new RoomSigner(), Listing(host, name: "公开房", hostName: "Steve")))));
        Assert.True(Ok(server.Directory.Handle(Announce(new RoomSigner(), Listing(host, RoomVisibility.Hidden, name: "秘密房"), listed: false))));
        server.Directory.Handle(Announce(new RoomSigner(), Listing(host, name: "nope")));
        server.Ban("account", "someone@example", "test");

        using var http = new HttpClient();
        var port = server.AdminPort!.Value;
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync($"http://127.0.0.1:{port}/metrics")).StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/metrics");
        request.Headers.Authorization = new("Bearer", File.ReadAllText(server.AdminTokenPath).Trim());
        using var reply = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
        Assert.Equal("text/plain", reply.Content.Headers.ContentType!.MediaType);
        Assert.Equal("0.0.4", reply.Content.Headers.ContentType.Parameters.Single(p => p.Name == "version").Value);
        var text = await reply.Content.ReadAsStringAsync();
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Contains("jay_node_up 1", lines);
        Assert.Contains("jay_directory_rooms 2", lines);
        Assert.Contains("jay_directory_rooms_listed 1", lines);
        Assert.Contains("jay_directory_rooms_published_total 2", lines);
        Assert.Contains("jay_directory_rooms_rejected_total{reason=\"name\"} 1", lines);
        Assert.Contains("jay_bans 1", lines);
        Assert.Contains("jay_logins_total{result=\"ok\"} 0", lines);
        Assert.Contains(lines, l => l.StartsWith("jay_node_info{network=\"", StringComparison.Ordinal) && l.EndsWith("} 1", StringComparison.Ordinal));
        // Every sample has a TYPE line before it, and every TYPE is a gauge or a counter.
        var typed = new HashSet<string>();
        foreach (var line in lines)
        {
            if (line.StartsWith("# TYPE ", StringComparison.Ordinal))
            {
                var parts = line.Split(' ');
                Assert.Contains(parts[3], new[] { "gauge", "counter" });
                Assert.True(typed.Add(parts[2]), "duplicate TYPE " + parts[2]);
            }
            else if (!line.StartsWith('#')) Assert.Contains(line.Split('{', ' ')[0], typed);
        }
        // Totals only: nothing about who hosts what.
        foreach (var secret in new[] { "公开房", "秘密房", "Steve", "10.235.0.7", "someone@example", MemberIdentity.FingerprintOf(host.PublicKey) })
            Assert.DoesNotContain(secret, text);
    }

    [Fact]
    public async Task AdminApiRejectsOversizedAndMalformedRequests()
    {
        var server = await StartAsync(admin: true);
        async Task<string> Raw(string request)
        {
            using var c = new TcpClient();
            await c.ConnectAsync(IPAddress.Loopback, server.AdminPort!.Value);
            var s = c.GetStream();
            await s.WriteAsync(System.Text.Encoding.ASCII.GetBytes(request));
            using var reader = new StreamReader(s);
            return await reader.ReadToEndAsync();
        }
        Assert.Equal("", await Raw("GARBAGE\r\n\r\n"));
        Assert.Equal("", await Raw("POST /ban HTTP/1.1\r\nContent-Length: 999999\r\n\r\n"));
        Assert.StartsWith("HTTP/1.1 404", await Raw("GET /nope HTTP/1.1\r\nHost: 127.0.0.1\r\nAuthorization: Bearer " + File.ReadAllText(server.AdminTokenPath).Trim() + "\r\n\r\n"));
    }

    [Fact]
    public async Task SeedOnlyNodeHasNoDirectory()
    {
        var server = await StartAsync(b => b.SeedOnly());
        Assert.Null(server.Directory);
        Assert.Equal("seed", server.Health()["role"]!.GetValue<string>());
    }

    /// <summary>Public IPs are refused in a network string; LAN and loopback ones (test networks) say nothing about anyone.</summary>
    [Theory]
    [InlineData("tcp://node.example.net:11010", false)]
    [InlineData("tcp://203.0.113.5:11010", true)]
    [InlineData("udp://[2408:8000::1]:11010", true)]
    [InlineData("tcp://127.0.0.1:11010", false)]
    [InlineData("tcp://192.168.1.5:11010", false)]
    public void NetworkNodesGoByDomain(string node, bool refused)
    {
        Assert.Equal(refused, RelayNodes.HasPublicIpHost(node));
        var info = new NetworkInfo("mynet", "My net", "jay-net-mynet", "s", [node]);
        Assert.Equal(refused ? L.T("core.mp.network.ip_node") : null, NetworkString.Validate(info));
    }

    [Fact]
    public void ScaffoldCreatesASignableNetworkAndAttachesTheConfig()
    {
        // A network string never names a public IP: its nodes go by domain.
        var ip = Assert.Throws<ArgumentException>(() => NetworkScaffold.Init(Path.Combine(_dir, "ip"), "nz-friends", "x", ["tcp://203.0.113.5:11010"]));
        Assert.Equal(L.T("core.mp.network.ip_node"), ip.Message);
        var r = NetworkScaffold.Init(_dir, "nz-friends", "新西兰朋友服", ["tcp://node.example.net:11010"]);
        Assert.Throws<IOException>(() => NetworkScaffold.Init(_dir, "nz-friends", "x", ["tcp://node.example.net:11010"]));
        Assert.Throws<ArgumentException>(() => NetworkScaffold.Init(Path.Combine(_dir, "b"), "jay", "x", ["tcp://node.example.net:11010"]));
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(r.KeyPath));

        var (config, sha) = NetworkScaffold.Hash(r.ConfigPath, "nz-friends");
        Assert.Equal(64, sha.Length);
        Assert.True(config.Policy.Allows(RoomVisibility.Direct));
        Assert.Equal(Version.Parse(NetworkScaffold.DefaultMinVersion), config.Policy.MinVersion);
        Assert.Throws<InvalidDataException>(() => NetworkScaffold.Hash(r.ConfigPath, "other"));

        var info = NetworkScaffold.AttachConfig(r.NetworkPath, "https://example.org/config.json", sha);
        Assert.Equal(2, info.Seq);
        Assert.Throws<ArgumentException>(() => NetworkScaffold.AttachConfig(r.NetworkPath, "http://example.org/config.json", sha));

        var signed = NetworkString.Parse(NetworkString.Create(info, TrustSigner.FromSeed(File.ReadAllText(r.KeyPath))));
        Assert.Equal(r.KeyId, signed.SignerKey);
        Assert.Equal(sha, signed.Info.Config!.Sha256);
        Assert.StartsWith("jay-net-", signed.ToProfile().NetworkName);
    }

    [Fact]
    public async Task CliInitConfigHashDirectoryAndNodeCommandsWorkTogether()
    {
        var output = new StringWriter();
        var net = Path.Combine(_dir, "net");
        Assert.Equal(0, Program.NetworkCommand(Options.Parse(["network", "init", "nz-friends", "--name", "朋友服", "--host", "node.example.net", "--out", net]), output));
        Assert.Contains("签名钥匙编号", output.ToString());
        var config = Path.Combine(net, "config.json");
        var network = Path.Combine(net, "nz-friends.json");
        // 只允许公开房间。
        File.WriteAllText(config, """{"v":1,"network":"nz-friends","policy":{"modes":["public"]}}""");
        Assert.Equal(0, Program.NetworkCommand(Options.Parse(["network", "config-hash", config, "--network", network, "--url", "https://example.org/c.json"]), output));
        Assert.Contains("版本号 2", output.ToString());
        Assert.Equal(0, Program.NetworkCommand(Options.Parse(["network", "sign", network, "--key", Path.Combine(net, "network.key")]), output));
        var jaynet = output.ToString().Split('\n').Single(l => l.StartsWith(NetworkString.Current, StringComparison.Ordinal)).Trim();

        Assert.Throws<ArgumentException>(() => Program.DirectoryBuilder(_dir, Options.Parse(["directory", "--seed-only", "--accounts"]), output, _ => { }));
        Assert.Throws<ArgumentException>(() => Program.DirectoryBuilder(_dir, Options.Parse(["directory", "--rate-limit", "3,x"]), output, _ => { }));

        var words = Path.Combine(_dir, "words.txt");
        File.WriteAllText(words, "# 屏蔽词\n广告\n");
        var builder = Program.DirectoryBuilder(_dir, Options.Parse(["directory", "--network", jaynet, "--config", config, "--blocked-words", words,
            "--admin-port", "0", "--rate-limit", "5,10,100"]), output, _ => { });
        builder.WithoutEasyTier(new IPEndPoint(IPAddress.Loopback, 0)).UseClock(() => _now);
        var server = builder.Build();
        _cleanup.Add(server);
        await server.StartAsync();
        Assert.Equal("nz-friends", server.Profile.Id);

        var host = X25519KeyPair.Generate();
        Assert.Equal("mode", server.Directory!.Handle(Announce(new RoomSigner(), Listing(host, RoomVisibility.Password)))["error"]!.GetValue<string>());
        Assert.Equal("name", server.Directory.Handle(Announce(new RoomSigner(), Listing(host, name: "广告房")))["error"]!.GetValue<string>());
        var signer = new RoomSigner();
        Assert.True(Ok(server.Directory.Handle(Announce(signer, Listing(host, name: "好房间")))));

        using var http = new HttpClient();
        var port = server.AdminPort!.Value.ToString();
        var nodeOut = new StringWriter();
        Assert.Equal(0, await Program.NodeCommand(_dir, Options.Parse(["node", "rooms", "--admin-port", port]), nodeOut, http));
        Assert.Contains("好房间", nodeOut.ToString());
        Assert.Equal(0, await Program.NodeCommand(_dir, Options.Parse(["node", "ban", "room", signer.RoomId, "--reason", "测试", "--admin-port", port]), nodeOut, http));
        Assert.Equal(0, server.Directory.Count);
        Assert.Equal(0, await Program.NodeCommand(_dir, Options.Parse(["node", "status", "--admin-port", port]), nodeOut, http));
        Assert.Contains("\"mode\": 1", nodeOut.ToString());
        Assert.Equal(1, await Program.NodeCommand(_dir, Options.Parse(["node", "unban", "room", "nope", "--admin-port", port]), nodeOut, http));
        await Assert.ThrowsAsync<ArgumentException>(() => Program.NodeCommand(_dir, Options.Parse(["node", "explode", "--admin-port", port]), nodeOut, http));
    }

    [Fact]
    public async Task MinVersionFromTheNetworkConfigTurnsAwayOlderJay()
    {
        var config = NetworkConfig.Parse("""{"v":1,"network":"nz","policy":{"minVersion":"0.8.0"}}"""u8, "nz");
        var server = await StartAsync(b => b.UseConfig(config));
        JsonObject List(string? version)
        {
            var request = new JsonObject { ["op"] = "snap" };
            if (version is not null) request["jay"] = version;
            return server.Directory!.Handle(request);
        }
        Assert.Equal("version", List(null)["error"]!.GetValue<string>());
        var old = List("0.7.0");
        Assert.Equal("version", old["error"]!.GetValue<string>());
        Assert.Equal("0.8.0", old["min"]!.GetValue<string>());
        Assert.Equal("version", List("garbage")["error"]!.GetValue<string>());
        Assert.True(Ok(List("0.8.0")));
        Assert.True(Ok(List("v0.9.1-beta.2")));
        Assert.Equal("0.8.0", server.Health()["minVersion"]!.GetValue<string>());

        // Through the real client: the host is told which version to update to.
        var net = new LoopbackNetwork(server.Directory!.Port);
        var host = X25519KeyPair.Generate();
        var e = await Assert.ThrowsAsync<DirectoryRejectedException>(() =>
            new RoomDirectoryClient(net) { Version = "0.7.3", NetworkSecret = server.Directory.NetworkSecret }.AnnounceAsync(new RoomSigner().Sign(Listing(host), _now), true, [], default));
        Assert.Equal("version", e.Code);
        Assert.Contains("0.8.0", e.Message);
        await new RoomDirectoryClient(net) { Version = "0.8.0", NetworkSecret = server.Directory.NetworkSecret }.AnnounceAsync(new RoomSigner().Sign(Listing(host), _now), true, [], default);
        Assert.Equal(1, server.Directory.Count);

        // No minVersion in the config: any client is fine.
        Assert.Null(ClientVersion.Parse(null));
        Assert.True(ClientVersion.Meets("1.0", new Version(0, 9, 9)));
    }

    private (string Token, X25519KeyPair Me) LogIn(JayNetworkServer server, string user = "bob", X25519KeyPair? me = null)
    {
        me ??= X25519KeyPair.Generate();
        var login = NetworkLogin.Parse(server.LoginSection!)!;
        var reply = server.Directory!.Handle(NetworkLoginClient.Request(me, login, user, "forum-password", _now));
        return (NetworkLoginClient.Read(reply, me, login, server.Profile.Id, _now).Token, me);
    }

    private JsonObject AnnounceAs(RoomSigner signer, X25519KeyPair host, string? token)
    {
        var request = Announce(signer, Listing(host));
        if (token is not null) request["account"] = token;
        return request;
    }

    [Fact]
    public async Task LoginRequiredNetworksOnlyListRoomsOfLoggedInHosts()
    {
        var server = await StartAsync(b => b.UseAccounts(new ForumAccounts()));
        server.Directory!.RequireAccountToPublish = true;
        var (token, host) = LogIn(server);
        Assert.Equal("login", server.Directory.Handle(AnnounceAs(new RoomSigner(), host, null))["error"]!.GetValue<string>());
        Assert.Equal("login", server.Directory.Handle(AnnounceAs(new RoomSigner(), host, "not-a-token"))["error"]!.GetValue<string>());
        // A token is bound to the identity it was issued for: someone else can't publish with it.
        Assert.Equal("login", server.Directory.Handle(AnnounceAs(new RoomSigner(), X25519KeyPair.Generate(), token))["error"]!.GetValue<string>());
        Assert.True(Ok(server.Directory.Handle(AnnounceAs(new RoomSigner(), host, token))));
        Assert.Equal("bob", Assert.Single(server.Directory.ListedRooms()).Account);
    }

    [Fact]
    public async Task AccountBanTakesRoomsDownAndBlocksLogin()
    {
        var server = await StartAsync(b => b.UseAccounts(new ForumAccounts()));
        var (token, host) = LogIn(server);
        var signer = new RoomSigner();
        Assert.True(Ok(server.Directory!.Handle(AnnounceAs(signer, host, token))));
        Assert.True(Ok(server.Directory.Handle(AnnounceAs(new RoomSigner(), X25519KeyPair.Generate(), null))));

        Assert.Equal(1, server.Ban("account", "bob", "cheating"));
        Assert.Equal(1, server.Directory.Count);
        _now += TimeSpan.FromSeconds(30);
        Assert.Equal("banned", server.Directory.Handle(AnnounceAs(signer, host, token))["error"]!.GetValue<string>());
        var login = NetworkLogin.Parse(server.LoginSection!)!;
        var again = server.Directory.Handle(NetworkLoginClient.Request(X25519KeyPair.Generate(), login, "bob", "forum-password", _now));
        Assert.Equal("disabled", again["error"]!.GetValue<string>());

        Assert.True(server.Bans!.Remove("account", "bob"));
        Assert.True(Ok(server.Directory.Handle(NetworkLoginClient.Request(X25519KeyPair.Generate(), login, "bob", "forum-password", _now))));
    }

    [Fact]
    public async Task TemporaryBansExpireOnTheirOwn()
    {
        var server = await StartAsync();
        var host = X25519KeyPair.Generate();
        var fingerprint = MemberIdentity.FingerprintOf(host.PublicKey);
        server.Ban("host", fingerprint, "spam", TimeSpan.FromHours(1));
        var reply = server.Directory!.Handle(Announce(new RoomSigner(), Listing(host)));
        Assert.Equal("banned", reply["error"]!.GetValue<string>());
        Assert.Contains("2026-11-09 01:00 UTC", reply["reason"]!.GetValue<string>());
        Assert.Equal(_now + TimeSpan.FromHours(1), Assert.Single(server.Bans!.List()).Until);

        _now += TimeSpan.FromMinutes(61);
        Assert.Empty(server.Bans.List());
        Assert.True(Ok(server.Directory.Handle(Announce(new RoomSigner(), Listing(host)))));
        Assert.Throws<ArgumentException>(() => server.Bans.Add("host", fingerprint, null, _now, TimeSpan.Zero));
    }

    [Fact]
    public async Task BanningARoomsHostCoversTheirFingerprintAndAccount()
    {
        var server = await StartAsync(b => b.UseAccounts(new ForumAccounts()));
        var (token, host) = LogIn(server);
        var first = new RoomSigner();
        var second = new RoomSigner();
        Assert.True(Ok(server.Directory!.Handle(AnnounceAs(first, host, token))));
        Assert.True(Ok(server.Directory.Handle(AnnounceAs(second, host, token))));

        var hit = server.BanRoomHost(first.RoomId, "griefing")!.Value;
        Assert.Equal(2, hit.Removed);
        Assert.Equal(MemberIdentity.FingerprintOf(host.PublicKey), hit.Host);
        Assert.Equal("bob", hit.Account);
        Assert.Equal(["account", "host"], server.Bans!.List().Select(b => b.Kind).Order());
        Assert.Null(server.BanRoomHost("0000", null));
    }

    [Fact]
    public void AccountBansMatchTheExactNameOnly()
    {
        var bans = new BanList(clock: () => _now);
        bans.Add("account", "Bob", null, _now);
        Assert.True(bans.IsAccountBanned("Bob"));
        // Account names are case-sensitive, so "bob" is a different account.
        Assert.False(bans.IsAccountBanned("bob"));
        Assert.False(bans.Remove("account", "bob"));

        // Fingerprints and room ids still match whatever the case.
        var listing = Listing(X25519KeyPair.Generate());
        var flipped = new string([.. listing.HostFingerprint.Select(c => char.IsUpper(c) ? char.ToLowerInvariant(c) : char.ToUpperInvariant(c))]);
        Assert.NotEqual(listing.HostFingerprint, flipped);
        bans.Add("host", flipped, null, _now);
        Assert.True(bans.IsBanned(listing));
        bans.Add("room", "Room1", null, _now);
        Assert.True(bans.Remove("room", "ROOM1"));

        // A saved list keeps the same rules after it is read back.
        var path = Path.Combine(_dir, "case-bans.json");
        var saved = new BanList(path, () => _now);
        saved.Add("account", "Bob", null, _now);
        saved.Add("host", flipped, null, _now);
        var reloaded = new BanList(path, () => _now);
        Assert.True(reloaded.IsAccountBanned("Bob"));
        Assert.False(reloaded.IsAccountBanned("bob"));
        Assert.True(reloaded.IsBanned(listing));
    }

    /// <summary>Writes a ban list the way versions before account bans matched the exact name did.</summary>
    private void WriteOldBans(string path, params string[] accounts)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, new JsonObject
        {
            ["v"] = 1,
            ["bans"] = new JsonArray([.. accounts.Select(a => (JsonNode)new JsonObject
            {
                ["kind"] = "account", ["value"] = a, ["reason"] = "old", ["at"] = _now.ToUnixTimeSeconds(), ["until"] = null,
            })]),
        }.ToJsonString());
    }

    private static int FileVersion(string path) => JsonNode.Parse(File.ReadAllText(path))!["v"]!.GetValue<int>();

    private string LogInError(JayNetworkServer server, string user)
    {
        var login = NetworkLogin.Parse(server.LoginSection!)!;
        return server.Directory!.Handle(NetworkLoginClient.Request(X25519KeyPair.Generate(), login, user, "forum-password", _now))["error"]?.GetValue<string>() ?? "";
    }

    [Fact]
    public async Task OlderAccountBansStillApplyBeforeAndAfterMigration()
    {
        // The store can't list accounts, so nothing is migrated at start-up.
        var server = await StartAsync(b => b.UseAccounts(new ForumAccounts()));
        var (token, host) = LogIn(server);
        // An older node writes the shared list with the name in another case.
        var path = Path.Combine(_dir, "bans.json");
        WriteOldBans(path, "Bob");
        _now += TimeSpan.FromSeconds(6);
        Assert.True(Assert.Single(server.Bans!.List()).AnyCase);
        Assert.True(server.Bans.IsAccountBanned("bob"));
        Assert.Equal("disabled", LogInError(server, "bob"));
        Assert.Equal("banned", server.Directory!.Handle(AnnounceAs(new RoomSigner(), host, token))["error"]!.GetValue<string>());

        var result = server.Bans.MigrateAccountBans(["bob", "alice"]);
        Assert.Equal(1, result.Migrated);
        Assert.Empty(result.Unresolved);
        var ban = Assert.Single(server.Bans.List());
        Assert.Equal(("bob", false, "old"), (ban.Value, ban.AnyCase, ban.Reason));
        Assert.Equal("disabled", LogInError(server, "bob"));
        Assert.Equal("banned", server.Directory.Handle(AnnounceAs(new RoomSigner(), host, token))["error"]!.GetValue<string>());
        Assert.False(server.Bans.IsAccountBanned("BOB"));
        Assert.Equal(BanList.FormatVersion, FileVersion(path));
    }

    [Fact]
    public async Task NodeTiesOlderAccountBansToTheAccountAtStartUp()
    {
        var path = Path.Combine(_dir, "bans.json");
        WriteOldBans(path, "Bob");
        var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var server = await StartAsync(b => b.UseAccounts(new ForumAccounts("bob", "alice")).UseLog(log.Enqueue));
        Assert.Equal("disabled", LogInError(server, "bob"));
        Assert.Contains(log, l => l.Contains("1 account ban(s)"));
        Assert.DoesNotContain(log, l => l.Contains("review"));

        // The migrated file reads back with the exact name.
        Assert.Equal(BanList.FormatVersion, FileVersion(path));
        var reloaded = new BanList(path, () => _now);
        var ban = Assert.Single(reloaded.List());
        Assert.Equal(("bob", false), (ban.Value, ban.AnyCase));
        Assert.True(reloaded.IsAccountBanned("bob"));
        Assert.False(reloaded.IsAccountBanned("Bob"));
        Assert.Equal(0, reloaded.MigrateAccountBans(["bob"]).Migrated);
    }

    [Fact]
    public async Task OlderAccountBansThatNameNoSingleAccountKeepMatchingAnyCase()
    {
        var path = Path.Combine(_dir, "bans.json");
        WriteOldBans(path, "Bob", "Carol");
        var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
        // "Bob" and "bob" are two accounts; nobody is called Carol in any case.
        var server = await StartAsync(b => b.UseAccounts(new ForumAccounts("Bob", "bob")).UseLog(log.Enqueue), admin: true);
        Assert.All(server.Bans!.List(), b => Assert.True(b.AnyCase));
        Assert.Equal(2, server.Bans.List().Count);
        Assert.True(server.Bans.IsAccountBanned("Bob"));
        Assert.True(server.Bans.IsAccountBanned("bob"));
        Assert.True(server.Bans.IsAccountBanned("CAROL"));
        Assert.Equal("disabled", LogInError(server, "bob"));
        Assert.Contains(log, l => l.Contains("review") && l.Contains("\"Bob\"") && l.Contains("2 accounts"));
        Assert.Contains(log, l => l.Contains("review") && l.Contains("\"Carol\"") && l.Contains("no account"));

        // The admin sees them marked in the list.
        using var http = new HttpClient();
        var output = new StringWriter();
        Assert.Equal(0, await Program.NodeCommand(_dir, Options.Parse(["node", "bans", "--admin-port", server.AdminPort!.Value.ToString()]), output, http));
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Count(output.ToString(), "\"anyCase\": ?true"));

        // Saved in the new format, they keep the old rule.
        Assert.Equal(BanList.FormatVersion, FileVersion(path));
        var reloaded = new BanList(path, () => _now);
        Assert.True(reloaded.IsAccountBanned("bob"));
        Assert.True(reloaded.IsAccountBanned("carol"));
        Assert.Equal(2, reloaded.MigrateAccountBans(["Bob", "bob"]).Unresolved.Count);
        // Unbanning works by the name in any case, as before.
        Assert.True(reloaded.Remove("account", "carol"));
        Assert.False(reloaded.IsAccountBanned("Carol"));
    }

    [Fact]
    public void NewAccountBansMatchExactlyNextToOlderOnes()
    {
        var path = Path.Combine(_dir, "mixed-bans.json");
        WriteOldBans(path, "Bob");
        var bans = new BanList(path, () => _now);
        bans.Add("account", "Carol", null, _now);
        Assert.True(bans.IsAccountBanned("Carol"));
        Assert.False(bans.IsAccountBanned("carol"));
        Assert.True(bans.IsAccountBanned("bob"));
        // Saving writes the new format, and each ban keeps its rule when read back.
        Assert.Equal(BanList.FormatVersion, FileVersion(path));
        var reloaded = new BanList(path, () => _now);
        Assert.True(reloaded.IsAccountBanned("Carol"));
        Assert.False(reloaded.IsAccountBanned("carol"));
        Assert.True(reloaded.IsAccountBanned("bob"));

        // Migrating onto an exact ban of the same account keeps whichever lasts longer.
        reloaded.Add("account", "bob", null, _now, TimeSpan.FromHours(1));
        Assert.Equal(1, reloaded.MigrateAccountBans(["bob", "Carol"]).Migrated);
        Assert.Null(Assert.Single(reloaded.List(), b => b.Value == "bob").Until);
    }

    [Fact]
    public async Task LiftingAnOlderAccountBanKeepsTheExactBanOnTheSameName()
    {
        // "Bob" and "bob" are two accounts, both logged in before an older node writes a ban on "Bob" in any case.
        var server = await StartAsync(b => b.UseAccounts(new ForumAccounts("Bob", "bob")), admin: true);
        var (bigToken, bigHost) = LogIn(server, "Bob");
        var (smallToken, smallHost) = LogIn(server, "bob");
        var path = Path.Combine(_dir, "bans.json");
        WriteOldBans(path, "Bob");
        _now += TimeSpan.FromSeconds(6);
        Assert.True(server.Bans!.IsAccountBanned("bob"));

        // As the review hint says: ban the exact account, then lift the old entry.
        using var http = new HttpClient();
        var port = server.AdminPort!.Value.ToString();
        var output = new StringWriter();
        Assert.Equal(0, await Program.NodeCommand(_dir, Options.Parse(["node", "ban", "account", "Bob", "--admin-port", port]), output, http));
        Assert.Equal(2, server.Bans.List().Count);

        // A plain unban matches both entries: it changes nothing and asks which one.
        output = new StringWriter();
        Assert.Equal(1, await Program.NodeCommand(_dir, Options.Parse(["node", "unban", "account", "Bob", "--admin-port", port]), output, http));
        Assert.Contains("ambiguous", output.ToString());
        Assert.Contains("--any-case", output.ToString());
        Assert.Equal(2, server.Bans.List().Count);
        // --exact names the exact account only, so another case finds nothing.
        Assert.Equal(1, await Program.NodeCommand(_dir, Options.Parse(["node", "unban", "account", "BOB", "--exact", "--admin-port", port]), output, http));
        Assert.Equal(2, server.Bans.List().Count);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Program.NodeCommand(_dir, Options.Parse(["node", "unban", "account", "Bob", "--exact", "--any-case", "--admin-port", port]), output, http));
        // --any-case lifts only the old entry, by its name in any case.
        Assert.Equal(0, await Program.NodeCommand(_dir, Options.Parse(["node", "unban", "account", "bOB", "--any-case", "--admin-port", port]), output, http));

        var ban = Assert.Single(server.Bans.List());
        Assert.Equal(("Bob", false), (ban.Value, ban.AnyCase));
        // Bob stays out: no new login, and the token from before can't publish.
        Assert.Equal("disabled", LogInError(server, "Bob"));
        Assert.Equal("banned", server.Directory!.Handle(AnnounceAs(new RoomSigner(), bigHost, bigToken))["error"]!.GetValue<string>());
        // bob was only caught by the old entry and is back.
        Assert.Equal("", LogInError(server, "bob"));
        Assert.True(Ok(server.Directory.Handle(AnnounceAs(new RoomSigner(), smallHost, smallToken))));

        // The same after saving and reading the list back.
        var reloaded = new BanList(path, () => _now);
        Assert.True(reloaded.IsAccountBanned("Bob"));
        Assert.False(reloaded.IsAccountBanned("bob"));
        Assert.False(reloaded.Remove("account", "Bob", BanMatch.AnyCase));
        Assert.True(reloaded.IsAccountBanned("Bob"));
    }

    [Fact]
    public async Task AdminApiUnbanPicksOneAccountBanByMatch()
    {
        var server = await StartAsync(b => b.UseAccounts(new ForumAccounts("Bob", "bob")), admin: true);
        var path = Path.Combine(_dir, "bans.json");
        WriteOldBans(path, "Bob");
        _now += TimeSpan.FromSeconds(6);
        server.Ban("account", "Bob", null);
        using var http = new HttpClient();
        var client = AdminClient.ForDataDir(http, _dir, server.AdminPort!.Value);

        var plain = await client.CallAsync("POST", "/unban", new() { ["kind"] = "account", ["value"] = "Bob" });
        Assert.Equal("ambiguous", plain["error"]!.GetValue<string>());
        Assert.Equal("bad", (await client.CallAsync("POST", "/unban", new() { ["kind"] = "account", ["value"] = "Bob", ["match"] = "other" }))["error"]!.GetValue<string>());
        Assert.Equal("bad", (await client.CallAsync("POST", "/unban", new() { ["kind"] = "host", ["value"] = "abc", ["match"] = "anyCase" }))["error"]!.GetValue<string>());
        Assert.Equal(2, server.Bans!.List().Count);

        // Lifting the exact ban leaves the old entry, which still covers both names.
        Assert.True((await client.CallAsync("POST", "/unban", new() { ["kind"] = "account", ["value"] = "Bob", ["match"] = "exact" }))["ok"]!.GetValue<bool>());
        Assert.True(Assert.Single(server.Bans.List()).AnyCase);
        Assert.True(server.Bans.IsAccountBanned("Bob"));
        Assert.True(server.Bans.IsAccountBanned("bob"));
        // With only one entry left, a plain unban lifts it as before.
        Assert.True((await client.CallAsync("POST", "/unban", new() { ["kind"] = "account", ["value"] = "BOB" }))["ok"]!.GetValue<bool>());
        Assert.Empty(server.Bans.List());
    }

    [Fact]
    public void BanListRefusesAnUnbanThatMatchesBothAccountBans()
    {
        var path = Path.Combine(_dir, "both-bans.json");
        WriteOldBans(path, "Bob");
        var bans = new BanList(path, () => _now);
        bans.Add("account", "Bob", null, _now);
        Assert.Throws<AmbiguousUnbanException>(() => bans.Remove("account", "Bob"));
        Assert.Equal(2, bans.List().Count);
        // A different case only reaches the old entry, so there is nothing to choose between.
        Assert.True(bans.Remove("account", "bob"));
        var left = Assert.Single(bans.List());
        Assert.Equal(("Bob", false), (left.Value, left.AnyCase));
        Assert.Throws<ArgumentException>(() => bans.Remove("host", "abc", BanMatch.AnyCase));
        Assert.True(bans.Remove("account", "Bob"));
        Assert.Empty(bans.List());
    }

    [Fact]
    public void BanListPicksUpChangesFromAnotherNode()
    {
        var path = Path.Combine(_dir, "shared-bans.json");
        var a = new BanList(path, () => _now);
        var b = new BanList(path, () => _now);
        Assert.False(b.IsAccountBanned("mallory"));
        a.Add("account", "mallory", null, _now);
        _now += TimeSpan.FromSeconds(6);
        Assert.True(b.IsAccountBanned("mallory"));
        Assert.True(b.Remove("account", "mallory"));
        _now += TimeSpan.FromSeconds(6);
        Assert.False(a.IsAccountBanned("mallory"));
        // A broken file keeps the last good list.
        a.Add("account", "eve", null, _now);
        File.WriteAllText(path, "{ not json");
        _now += TimeSpan.FromSeconds(6);
        Assert.True(a.IsAccountBanned("eve"));
    }

    [Fact]
    public async Task NodeCommandBansAccountsTemporarilyAndRoomHosts()
    {
        var server = await StartAsync(b => b.UseAccounts(new ForumAccounts()), admin: true);
        var (token, host) = LogIn(server);
        var signer = new RoomSigner();
        Assert.True(Ok(server.Directory!.Handle(AnnounceAs(signer, host, token))));
        using var http = new HttpClient();
        var port = server.AdminPort!.Value.ToString();
        var output = new StringWriter();

        Assert.Equal(0, await Program.NodeCommand(_dir, Options.Parse(["node", "ban", "room-host", signer.RoomId, "--for", "7d", "--admin-port", port]), output, http));
        Assert.Equal(0, server.Directory.Count);
        Assert.All(server.Bans!.List(), b => Assert.Equal(_now + TimeSpan.FromDays(7), b.Until));
        Assert.Equal(0, await Program.NodeCommand(_dir, Options.Parse(["node", "ban", "account", "carol", "--for", "30m", "--admin-port", port]), output, http));
        Assert.Equal(0, await Program.NodeCommand(_dir, Options.Parse(["node", "bans", "--admin-port", port]), output, http));
        Assert.Contains("\"until\"", output.ToString());
        Assert.Equal(0, await Program.NodeCommand(_dir, Options.Parse(["node", "unban", "account", "carol", "--admin-port", port]), output, http));
        Assert.Equal(1, await Program.NodeCommand(_dir, Options.Parse(["node", "ban", "room-host", "missing", "--admin-port", port]), output, http));
        await Assert.ThrowsAsync<ArgumentException>(() => Program.NodeCommand(_dir, Options.Parse(["node", "ban", "account", "x", "--for", "0", "--admin-port", port]), output, http));
    }

    /// <summary>公共网络里的几个目录节点，各映射到本机一个端口；连不上的地址直接报错（像真的掉线）。</summary>
    private sealed class ReplicaNetwork : IPublicNetwork
    {
        public readonly Dictionary<string, int> Map = [];
        public readonly HashSet<string> Down = [];

        public async Task<Stream> ConnectAsync(string ip, int port, CancellationToken ct)
        {
            if (Down.Contains(ip) || !Map.TryGetValue(ip, out var local)) throw new IOException("unreachable");
            var c = new TcpClient();
            await c.ConnectAsync(IPAddress.Loopback, local, ct);
            return c.GetStream();
        }

        public Task<string?> MyIpAsync(CancellationToken ct) => Task.FromResult<string?>("10.235.0.7");

        public Task<IReadOnlyList<string>> DirectoriesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>([.. PublicNetworkProfile.DirectoryIps.Where(ip => Map.ContainsKey(ip) && !Down.Contains(ip))]);
    }

    private async Task<RoomDirectoryServer> ReplicaAsync(string name, Action<JayNetworkServerBuilder>? configure = null)
    {
        var builder = new JayNetworkServerBuilder().UseDataDir(Path.Combine(_dir, name)).UseClock(() => _now)
            .WithoutEasyTier(new IPEndPoint(IPAddress.Loopback, 0)).WithoutAdmin();
        configure?.Invoke(builder);
        var server = builder.Build();
        _cleanup.Add(server);
        await server.StartAsync();
        return server.Directory!;
    }

    [Fact]
    public async Task BackupDirectoriesKeepTheListWorkingWhenOneGoesDown()
    {
        var main = await ReplicaAsync("main");
        var backup = await ReplicaAsync("backup", b => b.UseDirectoryIndex(2));
        var net = new ReplicaNetwork { Map = { ["10.235.0.1"] = main.Port, ["10.235.255.254"] = backup.Port } };
        var client = new RoomDirectoryClient(net) { NetworkSecret = main.NetworkSecret };
        var host = X25519KeyPair.Generate();
        var signer = new RoomSigner();
        // A public room is found by its invite from the listing itself.
        var room = Listing(host);
        Assert.True(JayInvite.TryParse(room.Invite, out var invite));
        var lobby = invite.LobbyKey;
        await client.AnnounceAsync(signer.Sign(room, _now), true, [lobby], default);

        // 两个目录都有，列表合并后不重复。
        Assert.Equal(1, main.Count);
        Assert.Equal(1, backup.Count);
        Assert.Single(await client.ListAsync(default));

        // 主目录掉线：列表、查找、续期照常。
        net.Down.Add("10.235.0.1");
        Assert.Single(await client.ListAsync(default));
        Assert.Equal(signer.RoomId, (await client.LookupAsync(lobby, default))!.Id);
        _now += TimeSpan.FromSeconds(30);
        await client.AnnounceAsync(signer.Sign(Listing(host), _now), true, [], default);

        // 主目录回来：续期把房间补回去。
        net.Down.Clear();
        _now += TimeSpan.FromMinutes(3);
        Assert.Empty(await client.ListAsync(default));
        await client.AnnounceAsync(signer.Sign(Listing(host), _now), true, [], default);
        Assert.Equal(1, main.Count);

        // 全都掉线：报连不上。
        net.Down.UnionWith(["10.235.0.1", "10.235.255.254"]);
        await Assert.ThrowsAnyAsync<IOException>(() => client.ListAsync(default));
    }

    [Fact]
    public async Task ReplicaClientMergesRejectionsMailAndLogin()
    {
        var main = await ReplicaAsync("main", b => b.UseBlockedWords(["广告"]));
        // 登录请求带的是真实时间，这个节点也用真实时钟。
        var backup = await ReplicaAsync("backup", b => b.UseDirectoryIndex(2).UseBlockedWords(["广告"]).UseAccounts(new ForumAccounts()).UseClock(() => DateTimeOffset.UtcNow));
        var net = new ReplicaNetwork { Map = { ["10.235.0.1"] = main.Port, ["10.235.255.254"] = backup.Port } };
        var client = new RoomDirectoryClient(net) { NetworkSecret = main.NetworkSecret };
        var host = X25519KeyPair.Generate();

        // 两个目录都拒绝：报拒绝和原因，不是“连不上”。
        var e = await Assert.ThrowsAsync<DirectoryRejectedException>(() => client.AnnounceAsync(new RoomSigner().Sign(Listing(host, name: "广告"), _now), true, [], default));
        Assert.Equal("name", e.Code);

        // 只有备用目录开了登录：主目录回 op，客户端接着问备用的。
        var authority = new JayNetworkServerBuilder().UseDataDir(Path.Combine(_dir, "backup")).UseAccounts(new ForumAccounts()).WithoutAdmin()
            .WithoutEasyTier(new IPEndPoint(IPAddress.Loopback, 0)).Build();
        _cleanup.Add(authority);
        var login = NetworkLogin.Parse(authority.LoginSection)!;
        var (_, token) = await client.LoginAsync(X25519KeyPair.Generate(), login, "jay", "bob", "forum-password", default);
        Assert.Equal("bob", token.User);
    }

    [Fact]
    public void DirectoryIndexPicksTheBackupAddress()
    {
        var args = PublicNetworkArgs.Build(new PublicNetworkOptions(PublicNetworkProfile.Jay(), 1, 2, Directory: true, DirectoryIndex: 3));
        Assert.Equal("10.235.255.253/16", args[args.IndexOf("--ipv4") + 1]);
        Assert.Throws<ArgumentException>(() => new JayNetworkServerBuilder().UseDirectoryIndex(5));
        var output = new StringWriter();
        Assert.Throws<ArgumentException>(() => Program.DirectoryBuilder(_dir, Options.Parse(["directory", "--seed-only", "--directory-index", "2"]), output, _ => { }));
        Assert.Equal("10.235.255.254", Program.DirectoryBuilder(_dir, Options.Parse(["directory", "--directory-index", "2", "--no-admin"]), output, _ => { }).Build().DirectoryIp);
    }
}
