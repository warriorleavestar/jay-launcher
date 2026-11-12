using Amber.Core.Localization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Public;
using Amber.NetworkServer;
using Amber.Server;

namespace Amber.Core.Tests;

/// <summary>
/// Volunteer nodes: address rules (public only), the directory's registry (probe before listing, expiry, bans, cap),
/// the "volunteer" / "nodes" ops through the real client, EasyTier arguments for volunteers, the per-network cache, and
/// the node server's switches (config, --no-volunteers, admin API, node bans). Loopback only, no EasyTier.
/// </summary>
public sealed class VolunteerNodesTests : IAsyncDisposable
{
    private DateTimeOffset _now = new(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jay-volunteer-test-" + Guid.NewGuid().ToString("N"));
    private readonly List<IAsyncDisposable> _cleanup = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var c in _cleanup) await c.DisposeAsync();
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

    private static bool Ok(JsonObject reply) => reply["ok"]?.GetValue<bool>() == true;

    [Theory]
    [InlineData("tcp://8.8.8.8:11010", "tcp://8.8.8.8:11010")]
    [InlineData("8.8.4.4:12000", "tcp://8.8.4.4:12000")]
    [InlineData(" tcp://Jay.Example.ORG:11010 ", "tcp://jay.example.org:11010")]
    [InlineData("tcp://[2606:4700::1111]:11010", "tcp://[2606:4700::1111]:11010")]
    [InlineData("tcp://192.168.1.5:11010", null)]
    [InlineData("tcp://10.0.0.1:11010", null)]
    [InlineData("tcp://172.20.0.1:11010", null)]
    [InlineData("tcp://100.64.1.1:11010", null)]
    [InlineData("tcp://127.0.0.1:11010", null)]
    [InlineData("tcp://169.254.1.1:11010", null)]
    [InlineData("tcp://203.0.113.5:11010", null)]
    [InlineData("tcp://[fe80::1]:11010", null)]
    [InlineData("tcp://[fd00::1]:11010", null)]
    [InlineData("tcp://localhost:11010", null)]
    [InlineData("tcp://printer.local:11010", null)]
    [InlineData("tcp://nas:11010", null)]
    [InlineData("udp://8.8.8.8:11010", null)]
    [InlineData("tcp://8.8.8.8:11010/x", null)]
    [InlineData("tcp://user@8.8.8.8:11010", null)]
    [InlineData("", null)]
    public void OnlyPublicTcpAddressesAreVolunteers(string input, string? expected) =>
        Assert.Equal(expected, VolunteerNodes.Normalize(input));

    [Fact]
    public async Task RegistryListsOnlyAfterTheProbeAndForgetsUnrenewedNodes()
    {
        var reachable = new HashSet<string> { "tcp://8.8.8.8:11010" };
        var probes = 0;
        var registry = new VolunteerRegistry((url, _) => { Interlocked.Increment(ref probes); return Task.FromResult(reachable.Contains(url)); }, () => _now);

        var first = registry.Register("tcp://8.8.8.8:11010");
        Assert.True(Ok(first));
        Assert.False(first["listed"]!.GetValue<bool>());
        await registry.LastProbe;
        Assert.Equal(["tcp://8.8.8.8:11010"], registry.Pick());
        Assert.True(registry.Register("8.8.8.8:11010")["listed"]!.GetValue<bool>());
        Assert.Equal(1, probes);

        // Unreachable: probed, never listed, dropped.
        registry.Register("tcp://1.1.1.1:11010");
        await registry.LastProbe;
        Assert.DoesNotContain("tcp://1.1.1.1:11010", registry.Listed());
        Assert.Equal("bad", registry.Register("tcp://192.168.0.2:11010")["error"]!.GetValue<string>());

        // Re-probed after an hour of renewals.
        for (var i = 0; i < 21; i++) { _now += VolunteerNodes.RenewEvery; registry.Register("tcp://8.8.8.8:11010"); }
        await registry.LastProbe;
        Assert.Equal(3, probes);   // first probe, the unreachable one, and the hourly re-probe

        // Not renewed: gone after the TTL.
        _now += VolunteerNodes.Ttl + TimeSpan.FromSeconds(1);
        Assert.Empty(registry.Listed());

        registry.Blocked = url => url == "tcp://8.8.8.8:11010";
        Assert.Equal("banned", registry.Register("tcp://8.8.8.8:11010")["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task RegistryCapsEntriesAndPickReturnsAFewAtRandom()
    {
        var registry = new VolunteerRegistry((_, _) => Task.FromResult(true), () => _now);
        for (var i = 0; i < VolunteerRegistry.MaxEntries; i++)
        {
            // New addresses are rate limited for the whole directory.
            _now += TimeSpan.FromSeconds(1.1);
            Assert.True(Ok(registry.Register($"tcp://node{i}.example.org:11010")));
            await registry.LastProbe;
        }
        Assert.Equal("full", registry.Register("tcp://one-more.example.org:11010")["error"]!.GetValue<string>());
        Assert.Equal(VolunteerNodes.PerRequest, registry.Pick().Count);
        Assert.Equal(VolunteerRegistry.MaxEntries, registry.Listed().Count);
        Assert.True(registry.Remove("tcp://node0.example.org:11010"));
        Assert.True(Ok(registry.Register("tcp://one-more.example.org:11010")));
    }

    [Fact]
    public async Task NewAddressesAreRateLimitedAndCappedPerHost()
    {
        var registry = new VolunteerRegistry((_, _) => Task.FromResult(true), () => _now);
        // One host can't make the directory dial many of its ports.
        for (var port = 11010; port < 11010 + VolunteerRegistry.MaxPerHost; port++)
        {
            Assert.True(Ok(registry.Register($"tcp://host.example.org:{port}")));
            await registry.LastProbe;
        }
        var refused = registry.Register("tcp://host.example.org:12000");
        Assert.Equal("host", refused["error"]!.GetValue<string>());
        Assert.False(string.IsNullOrEmpty(refused["reason"]?.GetValue<string>()));
        // Renewing a known address is never limited.
        Assert.True(Ok(registry.Register("tcp://host.example.org:11010")));
        // A burst of new hosts is cut off at the per-minute budget, then let through again a minute later.
        var accepted = VolunteerRegistry.MaxPerHost;
        for (var i = 0; accepted < VolunteerRegistry.RegistrationsPerMinute; i++, accepted++)
            Assert.True(Ok(registry.Register($"tcp://burst{i}.example.org:11010")));
        Assert.Equal("rate", registry.Register("tcp://late.example.org:11010")["error"]!.GetValue<string>());
        _now += TimeSpan.FromMinutes(1.1);
        Assert.True(Ok(registry.Register("tcp://late.example.org:11010")));
        await registry.LastProbe;
    }

    [Fact]
    public async Task ManyNamesForOneMachineCountAsThatMachine()
    {
        // Fake resolver: every name under one.example.org is the same machine; the v6 names share one /64.
        var dialled = new List<string>();
        Task<IPAddress?> Resolve(string url, CancellationToken _)
        {
            var host = new Uri(url).Host;
            IPAddress? ip = host.EndsWith(".one.example.org") ? IPAddress.Parse("10.20.30.40")
                : host.EndsWith(".six.example.org") ? IPAddress.Parse($"fd00:1:2:3::{host.Length:x}")
                : host.EndsWith(".gone.example.org") ? null
                : IPAddress.Parse("10.99.0." + (host.Length % 200));
            return Task.FromResult(ip);
        }
        var registry = new VolunteerRegistry((url, _) => { lock (dialled) dialled.Add(url); return Task.FromResult(true); }, () => _now, Resolve);
        foreach (var name in new[] { "a", "bb", "ccc" })
        {
            Assert.True(Ok(registry.Register($"tcp://{name}.one.example.org:11010")));
            await registry.LastProbe;
            _now += TimeSpan.FromSeconds(1.1);
        }
        // Two listed; the third name of that machine was dropped without being dialled.
        Assert.Equal(VolunteerRegistry.MaxPerHost, registry.Listed().Count(u => u.Contains(".one.")));
        Assert.DoesNotContain("tcp://ccc.one.example.org:11010", dialled);
        // IPv6 counts by /64.
        foreach (var name in new[] { "a", "bb", "ccc" })
        {
            registry.Register($"tcp://{name}.six.example.org:11010");
            await registry.LastProbe;
            _now += TimeSpan.FromSeconds(1.1);
        }
        Assert.Equal(VolunteerRegistry.MaxPerHost, registry.Listed().Count(u => u.Contains(".six.")));
        // A name that doesn't resolve is never dialled or listed.
        registry.Register("tcp://x.gone.example.org:11010");
        await registry.LastProbe;
        Assert.DoesNotContain("tcp://x.gone.example.org:11010", dialled);
        Assert.DoesNotContain("tcp://x.gone.example.org:11010", registry.Listed());
        // Checks: one at a time per machine, whatever the name.
        _now += TimeSpan.FromMinutes(1);
        Assert.True((await registry.CheckAsync("tcp://p.one.example.org:11010"))["ok"]!.GetValue<bool>());
        Assert.Equal("rate", (await registry.CheckAsync("tcp://q.one.example.org:11011"))["error"]!.GetValue<string>());
        // Once the wait is over, another name of that machine can be checked.
        _now += VolunteerRegistry.CheckSameAddressEvery + TimeSpan.FromSeconds(1);
        Assert.True((await registry.CheckAsync("tcp://r.one.example.org:11012"))["reachable"]!.GetValue<bool>());
    }

    [Fact]
    public async Task AddressesThatArentListedDontTakeAHostsPlaces()
    {
        // Two ports of the host that never answer: still being probed, then dropped. Neither keeps the real one out.
        var hold = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new VolunteerRegistry((url, _) => url.EndsWith(":11010") ? Task.FromResult(true) : hold.Task, () => _now);
        Assert.True(Ok(registry.Register("tcp://host.example.org:1")));
        Assert.True(Ok(registry.Register("tcp://host.example.org:2")));
        // While both are being probed, a third waits its turn ("rate", not "host").
        Assert.Equal("rate", registry.Register("tcp://host.example.org:11010")["error"]!.GetValue<string>());
        hold.SetResult(false);
        await Task.WhenAll(registry.LastProbe, Task.Delay(50));
        Assert.True(Ok(registry.Register("tcp://host.example.org:11010")));
        await registry.LastProbe;
        Assert.Equal(["tcp://host.example.org:11010"], registry.Listed());
        // The same for many names of one machine.
        var hold2 = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var byMachine = new VolunteerRegistry((url, _) => url.Contains("real.") ? Task.FromResult(true) : hold2.Task, () => _now,
            (_, _) => Task.FromResult<IPAddress?>(IPAddress.Parse("10.40.0.1")));
        foreach (var name in new[] { "x1", "x2" }) byMachine.Register($"tcp://{name}.example.org:11010");
        await Task.Delay(50);
        hold2.SetResult(false);
        await byMachine.LastProbe;
        Assert.Empty(byMachine.Listed());
        Assert.True(Ok(byMachine.Register("tcp://real.example.org:11010")));
        await byMachine.LastProbe;
        Assert.Equal(["tcp://real.example.org:11010"], byMachine.Listed());
    }

    [Theory]
    [InlineData("10.1.2.3", "10.1.2.3")]
    [InlineData("::ffff:10.1.2.3", "10.1.2.3")]
    [InlineData("fd00:1:2:3:4:5:6:7", "fd00:1:2:3::/64")]
    public void MachinesAreIPv4AddressesOrIPv6Prefixes(string address, string machine) =>
        Assert.Equal(machine, VolunteerNodes.MachineOf(IPAddress.Parse(address)));

    [Fact]
    public async Task ClientRegistersAndFetchesVolunteersThroughTheDirectory()
    {
        await using var directory = new RoomDirectoryServer(IPAddress.Loopback, 0, clock: () => _now)
        {
            Volunteers = new VolunteerRegistry((_, _) => Task.FromResult(true), () => _now),
        };
        var client = new RoomDirectoryClient(new LoopbackNetwork(directory.Port));
        Assert.Empty(await client.NodesAsync(default));
        Assert.False(await client.VolunteerAsync("tcp://8.8.8.8:11010", default));
        await directory.Volunteers.LastProbe;
        Assert.True(await client.VolunteerAsync("tcp://8.8.8.8:11010", default));
        Assert.Equal(["tcp://8.8.8.8:11010"], await client.NodesAsync(default));
        await Assert.ThrowsAsync<ArgumentException>(() => client.VolunteerAsync("tcp://192.168.1.2:11010", default));

        directory.Volunteers = null;
        var e = await Assert.ThrowsAsync<DirectoryRejectedException>(() => client.VolunteerAsync("tcp://8.8.8.8:11010", default));
        Assert.Equal("off", e.Code);
        Assert.Empty(await client.NodesAsync(default));
    }

    [Fact]
    public void PublicAddressComesFromEasyTiersStunResult()
    {
        Assert.Equal("8.8.4.4", VolunteerNodes.PublicAddressFromNodeInfo("""{"stun_info":{"public_ip":["8.8.4.4"]},"ip_list":{"public_ipv4":null}}"""));
        // ip_list.public_ipv4 as EasyTier's numeric form (1.1.1.1 = 16843009).
        Assert.Equal("1.1.1.1", VolunteerNodes.PublicAddressFromNodeInfo("""{"stun_info":{"public_ip":[]},"ip_list":{"public_ipv4":{"addr":16843009}}}"""));
        // Private / CGNAT results and garbage give nothing.
        Assert.Null(VolunteerNodes.PublicAddressFromNodeInfo("""{"stun_info":{"public_ip":["100.64.3.4","192.168.1.2"]}}"""));
        Assert.Null(VolunteerNodes.PublicAddressFromNodeInfo("""{"stun_info":{"public_ip":[]},"ip_list":{"public_ipv4":null}}"""));
        Assert.Null(VolunteerNodes.PublicAddressFromNodeInfo("not json"));
        Assert.Null(VolunteerNodes.PublicAddressFromNodeInfo("""{"stun_info":{"public_ip":[42]}}"""));
    }

    [Fact]
    public async Task ProbeTellsAWouldBeVolunteerWhetherItsPortIsOpenAndIsRateLimited()
    {
        var open = new HashSet<string> { "tcp://8.8.8.8:11010" };
        await using var directory = new RoomDirectoryServer(IPAddress.Loopback, 0, clock: () => _now)
        {
            Volunteers = new VolunteerRegistry((url, _) => Task.FromResult(open.Contains(url)), () => _now),
        };
        var client = new RoomDirectoryClient(new LoopbackNetwork(directory.Port));
        Assert.True(await client.ProbeAsync("tcp://8.8.8.8:11010", default));
        Assert.False(await client.ProbeAsync("tcp://1.1.1.1:11010", default));
        // Same address again right away: refused.
        var e = await Assert.ThrowsAsync<DirectoryRejectedException>(() => client.ProbeAsync("tcp://8.8.8.8:11010", default));
        Assert.Equal("rate", e.Code);
        // "Test port" says it was the port test that was too soon, not the directory's "Publishing too often".
        Assert.Equal("multiplayer.volunteer.test_rate_limited", Amber.App.ViewModels.MultiplayerViewModel.PortTestRejected(e).Key);
        Assert.Equal(StatusKind.Warning, Amber.App.ViewModels.MultiplayerViewModel.PortTestRejected(e).Kind);
        var other = new DirectoryRejectedException("banned", "nope");
        Assert.Equal(["nope"], Amber.App.ViewModels.MultiplayerViewModel.PortTestRejected(other).Args);
        _now += VolunteerRegistry.CheckSameAddressEvery + TimeSpan.FromSeconds(1);
        Assert.True(await client.ProbeAsync("tcp://8.8.8.8:11010", default));
        // Probing never lists anything.
        Assert.Empty(directory.Volunteers.Listed());
        // Whole-directory cap per minute.
        for (var i = 0; i < VolunteerRegistry.ChecksPerMinute - 3; i++) Assert.True(Ok(await directory.Volunteers.CheckAsync($"tcp://n{i}.example.org:11010")));
        Assert.Equal("rate", (await directory.Volunteers.CheckAsync("tcp://last.example.org:11010"))["error"]!.GetValue<string>());
        _now += TimeSpan.FromMinutes(1.1);
        Assert.True(Ok(await directory.Volunteers.CheckAsync("tcp://last.example.org:11010")));
        Assert.Equal("bad", (await directory.Volunteers.CheckAsync("tcp://10.1.2.3:11010"))["error"]!.GetValue<string>());
        await Assert.ThrowsAsync<ArgumentException>(() => client.ProbeAsync("tcp://192.168.0.9:11010", default));

        directory.Volunteers = null;
        Assert.Equal("off", (await Assert.ThrowsAsync<DirectoryRejectedException>(() => client.ProbeAsync("tcp://8.8.4.4:11010", default))).Code);
    }

    [Fact]
    public void VolunteerArgsListenPubliclyRelayOnlyThisNetworkAndCapBandwidth()
    {
        var profile = PublicNetworkProfile.Jay(["tcp://8.8.4.4:11010"]);
        var me = new VolunteerSettings("jay.example.org", 12000, 512);
        var args = PublicNetworkArgs.Build(new PublicNetworkOptions(profile, 15000, 15001,
            ExtraPeers: ["tcp://1.1.1.1:11010", "tcp://jay.example.org:12000"], Volunteer: me));
        string After(string flag) => args[args.IndexOf(flag) + 1];
        Assert.Contains("tcp://0.0.0.0:12000", args);
        Assert.Contains("udp://0.0.0.0:12000", args);
        Assert.DoesNotContain("--no-listener", args);
        Assert.Equal("tcp://jay.example.org:12000", After("--mapped-listeners"));
        Assert.Equal(PublicNetworkProfile.JayNetworkName, After("--relay-network-whitelist"));
        Assert.Equal("524288", After("--instance-recv-bps-limit"));
        Assert.Contains("tcp://1.1.1.1:11010", args);
        // Never peers with itself.
        Assert.Equal(1, args.Count(a => a == "tcp://jay.example.org:12000"));
        Assert.Contains("--dhcp", args);

        var plain = PublicNetworkArgs.Build(new PublicNetworkOptions(profile, 15000, 15001));
        Assert.Contains("--no-listener", plain);
        Assert.DoesNotContain("--mapped-listeners", plain);
        Assert.Throws<ArgumentException>(() => new VolunteerSettings("192.168.1.10").Url);
    }

    [Fact]
    public void DirectoryNodeArgsCarryRelayLimitsOnlyWhenSet()
    {
        var profile = PublicNetworkProfile.Jay(["tcp://seed.example.org:11010"]);
        var listeners = new[] { "tcp://0.0.0.0:11010", "udp://0.0.0.0:11010" };
        var plain = PublicNetworkArgs.Build(new PublicNetworkOptions(profile, 15000, 15001, Directory: true, Listeners: listeners));
        Assert.DoesNotContain("--foreign-relay-bps-limit", plain);
        Assert.DoesNotContain("--relay-network-whitelist", plain);
        var limited = PublicNetworkArgs.Build(new PublicNetworkOptions(profile, 15000, 15001, Directory: true, Listeners: listeners,
            Relay: new RelayLimits(2048, "jay-* amber-*")));
        string After(string flag) => limited[limited.IndexOf(flag) + 1];
        Assert.Equal("2097152", After("--foreign-relay-bps-limit"));
        Assert.Equal("jay-* amber-*", After("--relay-network-whitelist"));
        // The node's own network is never capped as a whole.
        Assert.DoesNotContain("--instance-recv-bps-limit", limited);
    }

    [Fact]
    public void CacheRemembersVolunteersPerNetwork()
    {
        var cache = new VolunteerCache(Path.Combine(_dir, "volunteer-nodes.json"));
        Assert.Empty(cache.For("jay"));
        cache.Remember("jay", ["tcp://8.8.8.8:11010", "tcp://10.0.0.1:11010"]);
        cache.Remember("jay", Enumerable.Range(0, 20).Select(i => $"tcp://n{i}.example.org:11010"));
        cache.Remember("nz", ["tcp://1.1.1.1:11010"]);
        var jay = cache.For("jay");
        Assert.Equal(VolunteerNodes.Remember, jay.Count);
        Assert.Equal("tcp://n0.example.org:11010", jay[0]);
        Assert.DoesNotContain("tcp://10.0.0.1:11010", jay);
        Assert.Equal(["tcp://1.1.1.1:11010"], cache.For("nz"));
        cache.Forget("nz", "tcp://1.1.1.1:11010");
        Assert.Empty(cache.For("nz"));
        File.WriteAllText(Path.Combine(_dir, "volunteer-nodes.json"), "{ broken");
        Assert.Empty(cache.For("jay"));
    }

    private async Task<JayNetworkServer> StartAsync(Action<JayNetworkServerBuilder>? configure = null, bool admin = false)
    {
        var builder = new JayNetworkServerBuilder().UseDataDir(_dir).UseClock(() => _now).WithoutEasyTier(new IPEndPoint(IPAddress.Loopback, 0))
            .UseVolunteerProbe((_, _) => Task.FromResult(true));
        if (admin) builder.UseAdmin(0); else builder.WithoutAdmin();
        configure?.Invoke(builder);
        var server = builder.Build();
        _cleanup.Add(server);
        await server.StartAsync();
        return server;
    }

    [Fact]
    public async Task NodeServerTakesVolunteersUnlessTheConfigOrOperatorSaysNo()
    {
        var on = await StartAsync();
        Assert.NotNull(on.Volunteers);
        var off = NetworkConfig.Parse("""{"v":1,"network":"jay","policy":{"volunteers":false}}"""u8, "jay");
        Assert.False(off.Policy.Volunteers);
        Assert.True(NetworkConfig.Parse("""{"v":1,"network":"jay"}"""u8, "jay").Policy.Volunteers);
        await on.DisposeAsync();
        _cleanup.Remove(on);

        var byConfig = await StartAsync(b => b.UseConfig(off));
        Assert.Null(byConfig.Volunteers);
        Assert.Equal("off", byConfig.Directory!.Handle(new JsonObject { ["op"] = "volunteer", ["url"] = "tcp://8.8.8.8:11010" })["error"]!.GetValue<string>());
        await byConfig.DisposeAsync();
        _cleanup.Remove(byConfig);

        var builder = Program.DirectoryBuilder(_dir, Options.Parse(["directory", "--no-volunteers", "--no-admin"]), new StringWriter(), _ => { });
        var cli = builder.WithoutEasyTier(new IPEndPoint(IPAddress.Loopback, 0)).Build();
        _cleanup.Add(cli);
        await cli.StartAsync();
        Assert.Null(cli.Volunteers);
    }

    [Fact]
    public async Task OperatorSeesAndBansVolunteerNodes()
    {
        var server = await StartAsync(admin: true);
        Assert.True(Ok(server.Directory!.Handle(new JsonObject { ["op"] = "volunteer", ["url"] = "tcp://8.8.8.8:11010" })));
        await server.Volunteers!.LastProbe;
        Assert.Equal(1, server.Health()["volunteers"]!.GetValue<int>());

        using var http = new HttpClient();
        var port = server.AdminPort!.Value.ToString();
        var output = new StringWriter();
        Assert.Equal(0, await Program.NodeCommand(_dir, Options.Parse(["node", "nodes", "--admin-port", port]), output, http));
        Assert.Contains("tcp://8.8.8.8:11010", output.ToString());
        Assert.Equal(0, await Program.NodeCommand(_dir, Options.Parse(["node", "ban", "node", "8.8.8.8:11010", "--reason", "slow", "--admin-port", port]), output, http));
        Assert.Empty(server.Volunteers.Listed());
        Assert.Equal("banned", server.Directory.Handle(new JsonObject { ["op"] = "volunteer", ["url"] = "tcp://8.8.8.8:11010" })["error"]!.GetValue<string>());
        Assert.Throws<ArgumentException>(() => server.Ban("node", "tcp://192.168.1.1:11010", null));
        Assert.True(server.Bans!.Remove("node", "8.8.8.8:11010"));
        Assert.True(Ok(server.Directory.Handle(new JsonObject { ["op"] = "volunteer", ["url"] = "tcp://8.8.8.8:11010" })));
    }
}
