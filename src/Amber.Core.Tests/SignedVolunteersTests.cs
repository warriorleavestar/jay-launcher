using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Amber.Core.Identity;
using Amber.Core.Multiplayer.Public;
using Amber.NetworkServer;
using Amber.Server;

namespace Amber.Core.Tests;

/// <summary>
/// Volunteer registrations signed with a Jay account: the directory checks the device signature and certificate, keeps
/// the account as the node's operator, hands out one node per operator first, gives members only the addresses, and
/// lets the network's operator require signatures and ban an operator. Loopback only.
/// </summary>
public sealed class SignedVolunteersTests : IAsyncDisposable
{
    private DateTimeOffset _now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jay-signed-volunteer-test-" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _keys = [];
    private readonly List<IAsyncDisposable> _cleanup = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var c in _cleanup) await c.DisposeAsync();
        _keys.ForEach(k => k.Dispose());
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch (IOException) { }
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

    /// <summary>A person: a Jay account and one device it certified.</summary>
    private JayProof Person(JayAccount? account = null)
    {
        account ??= JayAccount.FromMnemonic(Bip39.Generate(MnemonicLanguage.English), null);
        var device = DeviceKeys.Generate();
        _keys.Add(account);
        _keys.Add(device);
        return new JayProof(account.CertifyDevice(device, "Laptop", 0, _now), device);
    }

    private static string? ErrorOf(JsonObject reply) => reply["error"]?.GetValue<string>();
    private static bool Ok(JsonObject reply) => reply["ok"]?.GetValue<bool>() == true;

    private const string Net = "test-net";

    private VolunteerRegistry Registry() => new((_, _) => Task.FromResult(true), () => _now) { NetworkId = Net };

    private async Task<JsonObject> RegisterAsync(VolunteerRegistry registry, string url, JayProof? by)
    {
        // Each renewal is signed anew, a little later than the last.
        _now += TimeSpan.FromSeconds(1);
        var reply = registry.Register(url, by is null ? null : VolunteerOperator.Sign(by, Net, VolunteerNodes.Normalize(url)!, _now));
        await registry.LastProbe;
        return reply;
    }

    [Fact]
    public void ASignatureOnlyCountsForItsNetworkAddressAndTime()
    {
        var alice = Person();
        const string address = "tcp://x.example.org:11010";
        var signed = VolunteerOperator.Sign(alice, Net, address, _now);
        Assert.Equal((alice.Certificate.Account, _now.ToUnixTimeSeconds()), VolunteerOperator.Verify(signed, Net, address, _now));
        // Another network, another address, too far from the directory's clock, or a certificate for another device: not counted.
        Assert.Null(VolunteerOperator.Verify(signed, "other-net", address, _now));
        Assert.Null(VolunteerOperator.Verify(signed, Net, "tcp://y.example.org:11010", _now));
        Assert.Null(VolunteerOperator.Verify(signed, Net, address, _now + VolunteerOperator.MaxClockSkew + TimeSpan.FromSeconds(1)));
        var bob = Person();
        var swapped = (JsonObject)signed.DeepClone();
        swapped["cert"] = bob.Certificate.ToJson();
        Assert.Null(VolunteerOperator.Verify(swapped, Net, address, _now));
        var tampered = (JsonObject)signed.DeepClone();
        tampered["at"] = _now.ToUnixTimeSeconds() + 1;
        Assert.Null(VolunteerOperator.Verify(tampered, Net, address, _now));
        Assert.Null(VolunteerOperator.Verify(new JsonObject { ["cert"] = "x", ["sig"] = 5 }, Net, address, _now));
        // The whole request still fits in one directory request.
        var request = new JsonObject { ["op"] = "volunteer", ["url"] = address, ["operator"] = signed, ["jay"] = "0.8.0" };
        Assert.True(Encoding.UTF8.GetByteCount(request.ToJsonString()) < RoomDirectoryProtocol.MaxLine);
    }

    [Fact]
    public async Task AnAddressKeepsItsOperatorUntilItLapses()
    {
        var registry = Registry();
        var alice = Person();
        var bob = Person();
        Assert.True(Ok(await RegisterAsync(registry, "tcp://x.example.org:11010", alice)));
        Assert.Equal(alice.Certificate.Account, registry.Operators().Single().Operator);
        // Someone else can't take the address over, or strip the signature off it. The network's operator still sees
        // who registered it, and the latest conflicting claim; members' spreading no longer counts it as Alice's.
        var disputed = new List<string>();
        registry.Disputed += disputed.Add;
        Assert.Equal("owner", ErrorOf(await RegisterAsync(registry, "tcp://x.example.org:11010", bob)));
        Assert.Equal("owner", ErrorOf(await RegisterAsync(registry, "tcp://x.example.org:11010", null)));
        Assert.True(Ok(await RegisterAsync(registry, "tcp://x.example.org:11010", alice)));
        var x = registry.Operators().Single();
        Assert.Equal((alice.Certificate.Account, true), (x.Operator, x.Disputed));
        Assert.Equal([bob.Certificate.Account, ""], x.ClaimedBy!);
        Assert.Equal(["tcp://x.example.org:11010"], disputed);
        Assert.Equal("", registry.Pick().Single().Operator);
        // A broken signature is refused outright.
        var broken = VolunteerOperator.Sign(alice, Net, "tcp://y.example.org:11010", _now);
        Assert.Equal("operator", ErrorOf(registry.Register("tcp://z.example.org:11010", broken)));
        // After it lapses the address is free again.
        _now += VolunteerNodes.Ttl + TimeSpan.FromSeconds(1);
        Assert.True(Ok(await RegisterAsync(registry, "tcp://x.example.org:11010", bob)));
        Assert.Equal(bob.Certificate.Account, registry.Operators().Single().Operator);
        // An unsigned address can't be claimed by an account before it lapses either.
        Assert.True(Ok(await RegisterAsync(registry, "tcp://z.example.org:11010", null)));
        Assert.Equal("owner", ErrorOf(await RegisterAsync(registry, "tcp://z.example.org:11010", alice)));
        Assert.True(Ok(await RegisterAsync(registry, "tcp://z.example.org:11010", null)));
        var z = registry.Operators().Single(n => n.Url == "tcp://z.example.org:11010");
        Assert.Null(z.Operator);
        Assert.Equal([alice.Certificate.Account], z.ClaimedBy!);
    }

    [Fact]
    public async Task AClaimNeitherHidesNorTakesTheOperator()
    {
        var registry = Registry();
        var alice = Person();
        var mallory = Person();
        // Alice registering again under another identity doesn't hide who registered the address.
        Assert.True(Ok(await RegisterAsync(registry, "tcp://x.example.org:11010", alice)));
        Assert.Equal("owner", ErrorOf(await RegisterAsync(registry, "tcp://x.example.org:11010", Person())));
        Assert.Equal(alice.Certificate.Account, registry.Operators().Single().Operator);
        // Someone registering Bob's address first keeps it, but Bob's claim shows, and once the network's operator
        // drops the address Bob gets it.
        var bob = Person();
        Assert.True(Ok(await RegisterAsync(registry, "tcp://b.example.org:11010", mallory)));
        Assert.Equal("owner", ErrorOf(await RegisterAsync(registry, "tcp://b.example.org:11010", bob)));
        var entry = registry.Operators().Single(n => n.Url == "tcp://b.example.org:11010");
        Assert.Equal((mallory.Certificate.Account, true), (entry.Operator, entry.Disputed));
        // Later claims can't push Bob's out: the first few are kept.
        for (var i = 0; i < 10; i++) await RegisterAsync(registry, "tcp://b.example.org:11010", Person());
        entry = registry.Operators().Single(n => n.Url == "tcp://b.example.org:11010");
        Assert.Equal(VolunteerRegistry.MaxClaims, entry.ClaimedBy!.Count);
        Assert.Equal(bob.Certificate.Account, entry.ClaimedBy[0]);
        Assert.True(registry.Remove("tcp://b.example.org:11010"));
        Assert.True(Ok(await RegisterAsync(registry, "tcp://b.example.org:11010", bob)));
        var mine = registry.Operators().Single(n => n.Url == "tcp://b.example.org:11010");
        Assert.Equal((bob.Certificate.Account, false), (mine.Operator, mine.Disputed));
    }

    [Theory]
    [InlineData(long.MinValue)]
    [InlineData(long.MaxValue)]
    [InlineData(0L)]
    public void AnOutOfRangeTimeIsRefused(long offset)
    {
        var signed = VolunteerOperator.Sign(Person(), Net, "tcp://x.example.org:11010", _now);
        // Far from now in either direction, including values whose difference from now doesn't fit in 64 bits.
        signed["at"] = offset == 0 ? long.MinValue + _now.ToUnixTimeSeconds() : offset;
        Assert.Null(VolunteerOperator.Verify(signed, Net, "tcp://x.example.org:11010", _now));
        Assert.Equal("operator", ErrorOf(Registry().Register("tcp://x.example.org:11010", signed)));
    }

    [Fact]
    public async Task ASignedRegistrationCountsOnce()
    {
        var registry = Registry();
        var alice = Person();
        const string address = "tcp://x.example.org:11010";
        var first = VolunteerOperator.Sign(alice, Net, address, _now);
        Assert.True(Ok(registry.Register(address, first)));
        await registry.LastProbe;
        // The same registration again, or an older one, is refused; a newer one renews.
        Assert.Equal("stale", ErrorOf(registry.Register(address, first)));
        Assert.Equal("stale", ErrorOf(registry.Register(address, VolunteerOperator.Sign(alice, Net, address, _now - TimeSpan.FromSeconds(5)))));
        Assert.True(Ok(registry.Register(address, VolunteerOperator.Sign(alice, Net, address, _now + TimeSpan.FromSeconds(1)))));
        // Even after the address dropped out, an old registration can't bring it back.
        Assert.True(registry.Remove(address));
        Assert.Equal("stale", ErrorOf(registry.Register(address, first)));
        // A registration for another network is refused.
        Assert.Equal("operator", ErrorOf(registry.Register("tcp://z.example.org:11010", VolunteerOperator.Sign(alice, "other-net", "tcp://z.example.org:11010", _now))));
    }

    [Fact]
    public async Task MembersGetOneNodePerOperatorFirst()
    {
        var registry = Registry();
        var alice = Person();
        var bob = Person();
        // Alice runs six nodes, Bob one, and two are unsigned.
        for (var i = 0; i < 6; i++) await RegisterAsync(registry, $"tcp://a{i}.example.org:11010", alice);
        await RegisterAsync(registry, "tcp://b.example.org:11010", bob);
        await RegisterAsync(registry, "tcp://u1.example.org:11010", null);
        await RegisterAsync(registry, "tcp://u2.example.org:11010", null);
        for (var round = 0; round < 20; round++)
        {
            var four = registry.Pick(4);
            Assert.Equal(4, four.Count);
            // One of Alice's, Bob's, and the two unsigned ones (each counts as its own operator).
            Assert.Single(four, n => n.Url.StartsWith("tcp://a", StringComparison.Ordinal));
            Assert.Contains(four, n => n.Url == "tcp://b.example.org:11010");
            Assert.Equal(2, four.Count(n => n.Operator.Length == 0));
        }
        var all = registry.Pick(9);
        Assert.Equal(9, all.Count);
        Assert.Equal(6, all.Count(n => n.Operator == alice.Certificate.Account));
    }

    [Fact]
    public async Task TheClientSignsAndMembersGetOnlyAddresses()
    {
        await using var directory = new RoomDirectoryServer(IPAddress.Loopback, 0, clock: () => _now) { Volunteers = Registry() };
        var client = new RoomDirectoryClient(new LoopbackNetwork(directory.Port)) { NetworkId = Net };
        var alice = Person();
        var bob = Person();
        await client.VolunteerAsync("tcp://x.example.org:11010", default, alice);
        await client.VolunteerAsync("tcp://y.example.org:11010", default, alice);
        await client.VolunteerAsync("tcp://z.example.org:11010", default, bob);
        await directory.Volunteers.LastProbe;
        Assert.Equal(alice.Certificate.Account, directory.Volunteers.Operators().Single(n => n.Url == "tcp://x.example.org:11010").Operator);
        // Members get the addresses, one per operator first, and nothing about who runs them.
        var reply = directory.Handle(new JsonObject { ["op"] = "nodes" });
        Assert.Equal(["nodes", "ok"], reply.Select(p => p.Key).Order());
        for (var round = 0; round < 10; round++)
        {
            var first = (await client.NodesAsync(default)).Take(2).ToList();
            Assert.Contains("tcp://z.example.org:11010", first);
        }
        // Someone else's account on Alice's address: told why.
        var e = await Assert.ThrowsAsync<DirectoryRejectedException>(() => client.VolunteerAsync("tcp://x.example.org:11010", default, Person()));
        Assert.Equal("owner", e.Code);
    }

    [Fact]
    public void SigningTimesStayBounded()
    {
        // Many accounts signing for many addresses, each accepted once and probed away: the remembered signing times
        // stay bounded, refused registrations leave nothing behind, and the work per request doesn't grow with them.
        var registry = new VolunteerRegistry((_, _) => Task.FromResult(false), () => _now) { NetworkId = Net };
        var people = Enumerable.Range(0, 4).Select(_ => Person()).ToList();
        var refused = registry.Register("tcp://r.example.org:11010", VolunteerOperator.Sign(people[0], "other-net", "tcp://r.example.org:11010", _now));
        Assert.Equal("operator", ErrorOf(refused));
        Assert.Equal(0, registry.SignedRecords);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var full = 0;
        for (var i = 0; i < VolunteerRegistry.MaxSignedRecords + 200; i++)
        {
            var url = $"tcp://n{i}.example.org:11010";
            var reply = registry.Register(url, VolunteerOperator.Sign(people[i % people.Count], Net, url, _now));
            if (ErrorOf(reply) == "full") full++;
            registry.LastProbe.GetAwaiter().GetResult();
        }
        Assert.True(registry.SignedRecords <= VolunteerRegistry.MaxSignedRecords);
        Assert.True(full >= 200);
        // Once the clock window has passed, they are dropped and signing works again.
        _now += VolunteerOperator.MaxClockSkew + TimeSpan.FromSeconds(1);
        Assert.True(Ok(registry.Register("tcp://later.example.org:11010", VolunteerOperator.Sign(people[0], Net, "tcp://later.example.org:11010", _now))));
        Assert.Equal(1, registry.SignedRecords);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task TheClientNeverSignsTheSameSecondTwice()
    {
        await using var directory = new RoomDirectoryServer(IPAddress.Loopback, 0, clock: () => DateTimeOffset.UtcNow) { Volunteers = new VolunteerRegistry((_, _) => Task.FromResult(true)) { NetworkId = Net } };
        var client = new RoomDirectoryClient(new LoopbackNetwork(directory.Port)) { NetworkId = Net };
        var alice = Person();
        // Renewals right after each other (same second) are all accepted.
        for (var i = 0; i < 3; i++) await client.VolunteerAsync("tcp://x.example.org:11010", default, alice);
        // A directory that already has a newer one (clock went back) says so with its own code.
        var e = await Assert.ThrowsAsync<DirectoryRejectedException>(() =>
            new RoomDirectoryClient(new LoopbackNetwork(directory.Port)) { NetworkId = Net }.VolunteerAsync("tcp://x.example.org:11010", default, alice));
        Assert.Equal("stale", e.Code);
    }

    private async Task<JayNetworkServer> StartAsync(Action<JayNetworkServerBuilder>? configure = null)
    {
        var builder = new JayNetworkServerBuilder().UseDataDir(_dir).UseClock(() => _now).WithoutEasyTier(new IPEndPoint(IPAddress.Loopback, 0))
            .UseVolunteerProbe((_, _) => Task.FromResult(true)).UseAdmin(0);
        configure?.Invoke(builder);
        var server = builder.Build();
        _cleanup.Add(server);
        await server.StartAsync();
        return server;
    }

    private static JsonObject Volunteer(JayNetworkServer server, string url, JsonObject? signed) =>
        server.Directory!.Handle(new JsonObject { ["op"] = "volunteer", ["url"] = url, ["operator"] = signed });

    [Fact]
    public async Task TheNetworksOperatorCanRequireSignaturesAndBanAPerson()
    {
        var alice = Person();
        var server = await StartAsync(b => b.RequireSignedVolunteers());
        Assert.Equal("operator", ErrorOf(Volunteer(server, "tcp://x.example.org:11010", null)));
        Assert.True(Ok(Volunteer(server, "tcp://x.example.org:11010", VolunteerOperator.Sign(alice, server.Profile.Id, "tcp://x.example.org:11010", _now))));
        Assert.True(Ok(Volunteer(server, "tcp://y.example.org:11010", VolunteerOperator.Sign(alice, server.Profile.Id, "tcp://y.example.org:11010", _now))));
        await server.Volunteers!.LastProbe;

        // The admin API shows who runs each node.
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", File.ReadAllText(Path.Combine(_dir, "admin.token")).Trim());
        var nodes = JsonNode.Parse(await http.GetStringAsync($"http://127.0.0.1:{server.AdminPort}/nodes"))!;
        Assert.All(nodes["operators"]!.AsArray(), n => Assert.Equal(alice.Certificate.Account, n!["operator"]!.GetValue<string>()));

        // Banning the person drops all their nodes and keeps them out, whatever address they come back with; their
        // addresses are banned too, so coming back from them unsigned doesn't work either.
        Assert.Equal(2, server.Ban("operator", alice.Certificate.Account, "abuse"));
        Assert.Contains(server.Bans!.List(), b => b.Kind == "node" && b.Value == "tcp://x.example.org:11010");
        Assert.Empty(server.Volunteers.Listed());
        Assert.Equal("banned", ErrorOf(Volunteer(server, "tcp://z.example.org:11010", VolunteerOperator.Sign(alice, server.Profile.Id, "tcp://z.example.org:11010", _now))));
        Assert.Throws<ArgumentException>(() => server.Ban("operator", "not-an-address", null));
        Assert.True(server.Bans!.Remove("operator", alice.Certificate.Account));
        _now += TimeSpan.FromSeconds(1);
        Assert.True(Ok(Volunteer(server, "tcp://z.example.org:11010", VolunteerOperator.Sign(alice, server.Profile.Id, "tcp://z.example.org:11010", _now))));

        // The CLI switch turns the same requirement on, and leaves the other switches alone.
        var builder = Program.DirectoryBuilder(Path.Combine(_dir, "cli"), Options.Parse(["directory", "--signed-volunteers", "--rate-limit", "3,10,100", "--no-admin"]), new StringWriter(), _ => { });
        Assert.Equal((3, 10, 100), builder.RateLimit!.Limits);
        var cli = builder.WithoutEasyTier(new IPEndPoint(IPAddress.Loopback, 0)).Build();
        _cleanup.Add(cli);
        await cli.StartAsync();
        Assert.True(cli.Volunteers!.RequireOperator);
    }
}
