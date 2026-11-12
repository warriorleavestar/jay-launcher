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
/// the account as the node's operator, hands out one node per operator first, gives members only per-directory tags,
/// and lets the network's operator require signatures and ban an operator. Loopback only.
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

    private VolunteerRegistry Registry() => new((_, _) => Task.FromResult(true), () => _now);

    private async Task<JsonObject> RegisterAsync(VolunteerRegistry registry, string url, JayProof? by)
    {
        var reply = registry.Register(url, by is null ? null : VolunteerOperator.Sign(by, VolunteerNodes.Normalize(url)!, _now));
        await registry.LastProbe;
        return reply;
    }

    [Fact]
    public void ASignatureOnlyCountsForItsAddressAndTime()
    {
        var alice = Person();
        const string address = "tcp://8.8.8.8:11010";
        var signed = VolunteerOperator.Sign(alice, address, _now);
        Assert.Equal(alice.Certificate.Account, VolunteerOperator.Verify(signed, address, _now));
        // Another address, too far from the directory's clock, or a certificate for another device: not counted.
        Assert.Null(VolunteerOperator.Verify(signed, "tcp://8.8.4.4:11010", _now));
        Assert.Null(VolunteerOperator.Verify(signed, address, _now + VolunteerOperator.MaxClockSkew + TimeSpan.FromSeconds(1)));
        var bob = Person();
        var swapped = (JsonObject)signed.DeepClone();
        swapped["cert"] = bob.Certificate.ToJson();
        Assert.Null(VolunteerOperator.Verify(swapped, address, _now));
        var tampered = (JsonObject)signed.DeepClone();
        tampered["at"] = _now.ToUnixTimeSeconds() + 1;
        Assert.Null(VolunteerOperator.Verify(tampered, address, _now));
        Assert.Null(VolunteerOperator.Verify(new JsonObject { ["cert"] = "x", ["sig"] = 5 }, address, _now));
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
        Assert.True(Ok(await RegisterAsync(registry, "tcp://8.8.8.8:11010", alice)));
        Assert.Equal(alice.Certificate.Account, registry.Operators().Single().Operator);
        // Someone else can't take the address over, or strip the signature off it.
        Assert.Equal("owner", ErrorOf(await RegisterAsync(registry, "tcp://8.8.8.8:11010", bob)));
        Assert.Equal("owner", ErrorOf(await RegisterAsync(registry, "tcp://8.8.8.8:11010", null)));
        Assert.True(Ok(await RegisterAsync(registry, "tcp://8.8.8.8:11010", alice)));
        // A broken signature is refused outright.
        var broken = VolunteerOperator.Sign(alice, "tcp://8.8.4.4:11010", _now);
        Assert.Equal("operator", ErrorOf(registry.Register("tcp://1.1.1.1:11010", broken)));
        // After it lapses the address is free again.
        _now += VolunteerNodes.Ttl + TimeSpan.FromSeconds(1);
        Assert.True(Ok(await RegisterAsync(registry, "tcp://8.8.8.8:11010", bob)));
        Assert.Equal(bob.Certificate.Account, registry.Operators().Single().Operator);
        // An unsigned address can be claimed by an account later.
        Assert.True(Ok(await RegisterAsync(registry, "tcp://1.1.1.1:11010", null)));
        Assert.True(Ok(await RegisterAsync(registry, "tcp://1.1.1.1:11010", alice)));
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
        // Tags show "same operator" without showing who.
        Assert.Single(all.Where(n => n.Operator.Length > 0).Select(n => n.Operator).Distinct().Where(t => all.Count(n => n.Operator == t) == 6));
        Assert.DoesNotContain(all, n => n.Operator.Contains("jay1", StringComparison.Ordinal));
        // Another directory (or a restart) keys its tags differently.
        var other = Registry();
        await RegisterAsync(other, "tcp://b.example.org:11010", bob);
        Assert.NotEqual(all.Single(n => n.Url == "tcp://b.example.org:11010").Operator, other.Pick().Single().Operator);
    }

    [Fact]
    public async Task TheClientSignsAndGetsScopedTags()
    {
        await using var directory = new RoomDirectoryServer(IPAddress.Loopback, 0, clock: () => _now) { Volunteers = Registry() };
        var client = new RoomDirectoryClient(new LoopbackNetwork(directory.Port));
        var alice = Person();
        await client.VolunteerAsync("tcp://8.8.8.8:11010", default, alice);
        await client.VolunteerAsync("tcp://8.8.4.4:11010", default, alice);
        await client.VolunteerAsync("tcp://1.1.1.1:11010", default);
        await directory.Volunteers.LastProbe;
        Assert.Equal(alice.Certificate.Account, directory.Volunteers.Operators().Single(n => n.Url == "tcp://8.8.8.8:11010").Operator);
        var nodes = await client.VolunteerNodesAsync(default);
        Assert.Equal(3, nodes.Count);
        // Alice's two share a tag (scoped by directory), the unsigned one has none; one per operator comes first.
        var tagged = nodes.Where(n => n.Operator.Length > 0).ToList();
        Assert.Equal(2, tagged.Count);
        Assert.Single(tagged.Select(n => n.Operator).Distinct());
        Assert.StartsWith("0/", tagged[0].Operator);
        Assert.NotEqual(nodes[0].Operator, nodes[1].Operator);
        Assert.Equal(tagged[0].Operator, nodes[2].Operator);
        Assert.Equal(nodes.Select(n => n.Url).Order(), (await client.NodesAsync(default)).Order());
        // Someone else's account on Alice's address: told why.
        var e = await Assert.ThrowsAsync<DirectoryRejectedException>(() => client.VolunteerAsync("tcp://8.8.8.8:11010", default, Person()));
        Assert.Equal("owner", e.Code);
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
        Assert.Equal("operator", ErrorOf(Volunteer(server, "tcp://8.8.8.8:11010", null)));
        Assert.True(Ok(Volunteer(server, "tcp://8.8.8.8:11010", VolunteerOperator.Sign(alice, "tcp://8.8.8.8:11010", _now))));
        Assert.True(Ok(Volunteer(server, "tcp://8.8.4.4:11010", VolunteerOperator.Sign(alice, "tcp://8.8.4.4:11010", _now))));
        await server.Volunteers!.LastProbe;

        // The admin API shows who runs each node.
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", File.ReadAllText(Path.Combine(_dir, "admin.token")).Trim());
        var nodes = JsonNode.Parse(await http.GetStringAsync($"http://127.0.0.1:{server.AdminPort}/nodes"))!;
        Assert.All(nodes["operators"]!.AsArray(), n => Assert.Equal(alice.Certificate.Account, n!["operator"]!.GetValue<string>()));

        // Banning the person drops all their nodes and keeps them out, whatever address they come back with.
        Assert.Equal(2, server.Ban("operator", alice.Certificate.Account, "abuse"));
        Assert.Empty(server.Volunteers.Listed());
        Assert.Equal("banned", ErrorOf(Volunteer(server, "tcp://1.1.1.1:11010", VolunteerOperator.Sign(alice, "tcp://1.1.1.1:11010", _now))));
        Assert.Throws<ArgumentException>(() => server.Ban("operator", "not-an-address", null));
        Assert.True(server.Bans!.Remove("operator", alice.Certificate.Account));
        Assert.True(Ok(Volunteer(server, "tcp://1.1.1.1:11010", VolunteerOperator.Sign(alice, "tcp://1.1.1.1:11010", _now))));

        // The CLI switch turns the same requirement on.
        var cli = Program.DirectoryBuilder(Path.Combine(_dir, "cli"), Options.Parse(["directory", "--signed-volunteers", "--no-admin"]), new StringWriter(), _ => { })
            .WithoutEasyTier(new IPEndPoint(IPAddress.Loopback, 0)).Build();
        _cleanup.Add(cli);
        await cli.StartAsync();
        Assert.True(cli.Volunteers!.RequireOperator);
    }
}
