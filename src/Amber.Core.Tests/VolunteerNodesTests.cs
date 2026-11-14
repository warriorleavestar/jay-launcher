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
    [InlineData("tcp://node1.example.org:11010", "tcp://node1.example.org:11010")]
    [InlineData("node2.example.org:12000", "tcp://node2.example.org:12000")]
    [InlineData(" tcp://Jay.Example.ORG:11010 ", "tcp://jay.example.org:11010")]
    [InlineData("tcp://192.168.1.5:11010", null)]
    [InlineData("tcp://10.0.0.1:11010", null)]
    [InlineData("tcp://172.20.0.1:11010", null)]
    [InlineData("tcp://100.64.1.1:11010", null)]
    [InlineData("tcp://127.0.0.1:11010", null)]
    [InlineData("tcp://169.254.1.1:11010", null)]
    [InlineData("tcp://203.0.113.5:11010", null)]
    [InlineData("tcp://[2001:db8::1]:11010", null)]
    [InlineData("tcp://[fe80::1]:11010", null)]
    [InlineData("tcp://[fd00::1]:11010", null)]
    [InlineData("tcp://localhost:11010", null)]
    [InlineData("tcp://printer.local:11010", null)]
    [InlineData("tcp://nas:11010", null)]
    [InlineData("udp://node1.example.org:11010", null)]
    [InlineData("tcp://node1.example.org:11010/x", null)]
    [InlineData("tcp://user@node1.example.org:11010", null)]
    [InlineData("", null)]
    public void OnlyPublicTcpAddressesAreVolunteers(string input, string? expected) =>
        Assert.Equal(expected, VolunteerNodes.Normalize(input));

    [Fact]
    public async Task RegistryListsOnlyAfterTheProbeAndForgetsUnrenewedNodes()
    {
        var reachable = new HashSet<string> { "tcp://node1.example.org:11010" };
        var probes = 0;
        var registry = new VolunteerRegistry((url, _) => { Interlocked.Increment(ref probes); return Task.FromResult(reachable.Contains(url)); }, () => _now);

        var first = registry.Register("tcp://node1.example.org:11010");
        Assert.True(Ok(first));
        Assert.False(first["listed"]!.GetValue<bool>());
        await registry.LastProbe;
        Assert.Equal(["tcp://node1.example.org:11010"], registry.Pick().Select(n => n.Url));
        Assert.True(registry.Register("node1.example.org:11010")["listed"]!.GetValue<bool>());
        Assert.Equal(1, probes);

        // Unreachable: probed, never listed, dropped.
        registry.Register("tcp://node3.example.org:11010");
        await registry.LastProbe;
        Assert.DoesNotContain("tcp://node3.example.org:11010", registry.Listed());
        Assert.Equal("bad", registry.Register("tcp://192.168.0.2:11010")["error"]!.GetValue<string>());

        // Re-probed after an hour of renewals.
        for (var i = 0; i < 21; i++) { _now += VolunteerNodes.RenewEvery; registry.Register("tcp://node1.example.org:11010"); }
        await registry.LastProbe;
        Assert.Equal(3, probes);   // first probe, the unreachable one, and the hourly re-probe

        // Not renewed: gone after the TTL.
        _now += VolunteerNodes.Ttl + TimeSpan.FromSeconds(1);
        Assert.Empty(registry.Listed());

        registry.Blocked = url => url == "tcp://node1.example.org:11010";
        Assert.Equal("banned", registry.Register("tcp://node1.example.org:11010")["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task RegistryCapsEntriesAndPickReturnsAFewAtRandom()
    {
        var registry = new VolunteerRegistry((_, _) => Task.FromResult(true), () => _now);
        for (var i = 0; i < VolunteerRegistry.MaxEntries; i++)
        {
            // New addresses are taken at a limited rate per minute.
            if (i > 0 && i % (VolunteerRegistry.NewPerMinute / 2) == 0) _now += TimeSpan.FromSeconds(61);
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
        for (var i = 0; accepted < VolunteerRegistry.NewPerMinute; i++, accepted++)
            Assert.True(Ok(registry.Register($"tcp://burst{i}.example.org:11010")));
        Assert.Equal("rate", registry.Register("tcp://late.example.org:11010")["error"]!.GetValue<string>());
        _now += TimeSpan.FromMinutes(1.1);
        Assert.True(Ok(registry.Register("tcp://late.example.org:11010")));
        await registry.LastProbe;
    }

    [Fact]
    public async Task AFailedAddressIsntDialledAgainRightAway()
    {
        var dialled = new List<string>();
        var up = false;
        var registry = new VolunteerRegistry((url, _) =>
        {
            lock (dialled) dialled.Add(url);
            return Task.FromResult(up || url.EndsWith(":11011"));
        }, () => _now, (_, _) => Task.FromResult<IPAddress?>(IPAddress.Parse("10.50.0.1")));
        registry.Register("tcp://a.example.org:11010");
        await registry.LastProbe;
        Assert.Single(dialled);
        // The failed entry is gone, but registering it again (or another name for that machine and port) dials nothing.
        Assert.Equal("rate", registry.Register("tcp://a.example.org:11010")["error"]!.GetValue<string>());
        registry.Register("tcp://b.example.org:11010");
        await registry.LastProbe;
        Assert.Single(dialled);
        Assert.Empty(registry.Listed());
        // Another port of the same machine isn't held back by the failing one.
        Assert.True(Ok(registry.Register("tcp://a.example.org:11011")));
        await registry.LastProbe;
        Assert.Equal(["tcp://a.example.org:11011"], registry.Listed());
        // After the cool-down it is dialled again.
        up = true;
        _now += VolunteerRegistry.FailedCooldown + TimeSpan.FromSeconds(1);
        Assert.True(Ok(registry.Register("tcp://a.example.org:11010")));
        await registry.LastProbe;
        Assert.Contains("tcp://a.example.org:11010", registry.Listed());
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
        var port = 11010;
        foreach (var name in new[] { "a", "bb", "ccc" })
        {
            Assert.True(Ok(registry.Register($"tcp://{name}.one.example.org:{port++}")));
            await registry.LastProbe;
            _now += TimeSpan.FromSeconds(1.1);
        }
        // Two listed; the third name of that machine was dropped without being dialled.
        Assert.Equal(VolunteerRegistry.MaxPerHost, registry.Listed().Count(u => u.Contains(".one.")));
        Assert.DoesNotContain("tcp://ccc.one.example.org:11012", dialled);
        // IPv6 counts by /64.
        port = 11010;
        foreach (var name in new[] { "a", "bb", "ccc" })
        {
            registry.Register($"tcp://{name}.six.example.org:{port++}");
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
    public async Task NamesOthersPointAtANodeDontCrowdItOut()
    {
        // Two names somebody else registered for the node's own address and port, then the node's own name: one listener,
        // one place. The same for an IPv6 node.
        var dialled = new List<string>();
        Task<IPAddress?> Resolve(string url, CancellationToken _) =>
            Task.FromResult<IPAddress?>(IPAddress.Parse(new Uri(url).Host.EndsWith(".six.example.org") ? "fd00:1:2:3::5" : "10.20.30.40"));
        var registry = new VolunteerRegistry((url, _) => { lock (dialled) dialled.Add(url); return Task.FromResult(true); }, () => _now, Resolve);
        foreach (var url in new[] { "tcp://x1.example.org:11010", "tcp://x2.example.org:11010", "tcp://node.example.org:11010",
                     "tcp://y1.six.example.org:11010", "tcp://y2.six.example.org:11010", "tcp://node.six.example.org:11010" })
        {
            Assert.True(Ok(registry.Register(url)));
            await registry.LastProbe;
            _now += TimeSpan.FromSeconds(1.1);
        }
        Assert.Contains("tcp://node.example.org:11010", registry.Listed());
        Assert.Contains("tcp://node.six.example.org:11010", registry.Listed());
        // The listener was dialled once; its other names weren't dialled again.
        Assert.Equal(["tcp://x1.example.org:11010", "tcp://y1.six.example.org:11010"], dialled);
        // Players are given each listener once.
        for (var i = 0; i < 20; i++) Assert.Equal(2, registry.Pick(10).Count);
        // Renewals keep every name listed, and each listener is still dialled again once its check is old.
        var names = registry.Listed();
        for (var round = 0; round < 3; round++)
        {
            _now += VolunteerRegistry.ReprobeEvery + TimeSpan.FromSeconds(1);
            foreach (var url in names)
            {
                Assert.True(Ok(registry.Register(url)));
                await registry.LastProbe;
            }
        }
        Assert.Equal(names, registry.Listed());
        Assert.Equal(2 + 3 * 2, dialled.Count);
        // Other ports of the machine still count against it.
        foreach (var url in new[] { "tcp://p1.example.org:22", "tcp://p2.example.org:80" })
        {
            registry.Register(url);
            await registry.LastProbe;
            _now += TimeSpan.FromSeconds(1.1);
        }
        Assert.DoesNotContain("tcp://p2.example.org:80", registry.Listed());
    }

    [Fact]
    public async Task OtherAddressesInOneIPv6PrefixAreOtherListeners()
    {
        // Same /64 and port, different addresses: each is dialled on its own; only the one that answers is listed, and
        // the one that doesn't can't take the other's place in the list.
        var answers = new HashSet<string> { "tcp://node.example.org:11010", "tcp://a7.example.org:11010" };
        var dialled = new List<string>();
        Task<IPAddress?> Resolve(string url, CancellationToken _) => Task.FromResult<IPAddress?>(new Uri(url).Host switch
        {
            "node.example.org" => IPAddress.Parse("fd00:1:2:3::5"),
            var host => IPAddress.Parse("fd00:1:2:3::" + host[1]),
        });
        var registry = new VolunteerRegistry((url, _) => { lock (dialled) dialled.Add(url); return Task.FromResult(answers.Contains(url)); }, () => _now, Resolve);
        Assert.True(Ok(registry.Register("tcp://node.example.org:11010")));
        await registry.LastProbe;
        _now += TimeSpan.FromSeconds(1.1);
        Assert.True(Ok(registry.Register("tcp://a6.example.org:11010")));
        await registry.LastProbe;
        Assert.Contains("tcp://a6.example.org:11010", dialled);
        Assert.Equal(["tcp://node.example.org:11010"], registry.Listed());
        for (var i = 0; i < 20; i++) Assert.Equal("tcp://node.example.org:11010", Assert.Single(registry.Pick(10)).Url);
        // Two that both answer are two listeners, and both are given out.
        _now += VolunteerRegistry.FailedCooldown + TimeSpan.FromSeconds(1);
        Assert.True(Ok(registry.Register("tcp://a7.example.org:11010")));
        await registry.LastProbe;
        Assert.Equal(2, registry.Listed().Count);
        Assert.Equal(2, registry.Pick(10).Count);
    }

    [Fact]
    public async Task AFailingAddressDoesntHoldBackItsNeighbours()
    {
        // q (answers) and bad (doesn't) share one IPv6 /64 and port; bad2 is another name for bad's exact address.
        var dialled = new List<string>();
        Task<IPAddress?> Resolve(string url, CancellationToken _) => Task.FromResult<IPAddress?>(new Uri(url).Host switch
        {
            "q.example.org" => IPAddress.Parse("fd00:1:2:3::5"),
            _ => IPAddress.Parse("fd00:1:2:3::6"),
        });
        var registry = new VolunteerRegistry((url, _) => { lock (dialled) dialled.Add(url); return Task.FromResult(url.Contains("//q.")); },
            () => _now, Resolve);
        // A neighbour's failure right before: q is still dialled and listed.
        registry.Register("tcp://bad.example.org:11010");
        await registry.LastProbe;
        _now += TimeSpan.FromSeconds(1.1);
        Assert.True(Ok(registry.Register("tcp://q.example.org:11010")));
        await registry.LastProbe;
        Assert.Contains("tcp://q.example.org:11010", dialled);
        Assert.Equal(["tcp://q.example.org:11010"], registry.Listed());
        // Another name for the address that just failed waits out its cooldown without being dialled.
        Assert.True(Ok(registry.Register("tcp://bad2.example.org:11010")));
        await registry.LastProbe;
        Assert.DoesNotContain("tcp://bad2.example.org:11010", dialled);
        // When q is due to be checked again, a neighbour failing just before doesn't drop it.
        _now += VolunteerRegistry.ReprobeEvery + TimeSpan.FromSeconds(1);
        registry.Register("tcp://bad3.example.org:11010");
        await registry.LastProbe;
        _now += TimeSpan.FromSeconds(1.1);
        Assert.True(Ok(registry.Register("tcp://q.example.org:11010")));
        await registry.LastProbe;
        Assert.Equal(2, dialled.Count(u => u == "tcp://q.example.org:11010"));
        Assert.Equal(["tcp://q.example.org:11010"], registry.Listed());
    }

    [Theory]
    [InlineData("tcp://node.example.org:11010", "tcp://node.example.org:{0}")]
    [InlineData("tcp://node.example.org:11010", "tcp://alias{0}.example.org:{0}")]
    [InlineData("tcp://node.six.example.org:11010", "tcp://h{0}.six.example.org:{0}")]
    public async Task AListedNodeKeepsItsPlaceWhileOthersAreProbed(string node, string otherFormat)
    {
        // The node is listed; when it is due to be checked again, two other ports of its machine are still being probed
        // (they hang). Its renewal is still dialled, and it stays listed and given out.
        var hang = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialled = new List<string>();
        Task<IPAddress?> Resolve(string url, CancellationToken _) =>
            Task.FromResult<IPAddress?>(IPAddress.Parse(new Uri(url).Host.EndsWith(".six.example.org")
                ? (new Uri(url).Host.StartsWith("node.") ? "fd00:1:2:3::5" : $"fd00:1:2:3::{new Uri(url).Port % 1000:x}") : "10.20.30.40"));
        var registry = new VolunteerRegistry((url, _) =>
        {
            lock (dialled) dialled.Add(url);
            return url == node ? Task.FromResult(true) : hang.Task;
        }, () => _now, Resolve);
        Assert.True(Ok(registry.Register(node)));
        await registry.LastProbe;
        Assert.Equal([node], registry.Listed());
        // Renewed as usual until its check is about to be old.
        var checkedAt = _now;
        while (_now + VolunteerNodes.RenewEvery - checkedAt < VolunteerRegistry.ReprobeEvery)
        {
            _now += VolunteerNodes.RenewEvery;
            Assert.True(Ok(registry.Register(node)));
        }
        var others = new List<Task>();
        foreach (var port in new[] { 12001, 12002 })
        {
            Assert.True(Ok(registry.Register(string.Format(System.Globalization.CultureInfo.InvariantCulture, otherFormat, port))));
            others.Add(registry.LastProbe);
        }
        await Task.Delay(50);
        _now += VolunteerNodes.RenewEvery;
        var renewed = registry.Register(node);
        Assert.True(Ok(renewed), renewed.ToJsonString());
        await registry.LastProbe;
        Assert.True(2 == dialled.Count(u => u == node), string.Join(",", dialled));
        Assert.Equal([node], registry.Listed());
        Assert.Equal(node, Assert.Single(registry.Pick(10)).Url);
        hang.SetResult(false);
        await Task.WhenAll(others);
        Assert.Equal([node], registry.Listed());
    }

    [Fact]
    public async Task ANodeOfTheNetworkMustAnswer()
    {
        // A fake node: reads the handshake and answers with its own under the given network name.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string? asked = null;
        async Task Serve(string answerAs, bool answer)
        {
            using var c = await listener.AcceptTcpClientAsync();
            var stream = c.GetStream();
            var length = new byte[4];
            await stream.ReadExactlyAsync(length);
            var body = new byte[BitConverter.ToUInt32(length)];
            await stream.ReadExactlyAsync(body);
            asked = NodeHandshake.IsReply(body, "jay-net-test") ? "handshake" : "other";
            if (answer) await stream.WriteAsync(NodeHandshake.Request(answerAs, 7));
        }
        var serve = Serve("jay-net-test", answer: true);
        Assert.True(await VolunteerNodes.AnswersAsNodeAsync(IPAddress.Loopback, port, "jay-net-test", default));
        await serve;
        Assert.Equal("handshake", asked);
        // Another network's node, or a service that says nothing, isn't one.
        serve = Serve("someone-else", answer: true);
        Assert.False(await VolunteerNodes.AnswersAsNodeAsync(IPAddress.Loopback, port, "jay-net-test", default));
        await serve;
        serve = Serve("", answer: false);
        using (var quick = new CancellationTokenSource(300))
            Assert.False(await VolunteerNodes.AnswersAsNodeAsync(IPAddress.Loopback, port, "jay-net-test", quick.Token));
        await serve;
        // Not a public address: never dialled.
        Assert.False(await VolunteerNodes.IsNetworkNodeAsync(IPAddress.Loopback, port, "jay-net-test", default));
    }

    [Fact]
    public void AReplyFromJaysEasyTierIsRead()
    {
        // Captured from Jay's EasyTier (jay/v2.6.4 branch, a volunteer-style node of network "jay-net-probe", TCP
        // listener) answering the request NodeHandshake.Request sends: the length, then this body.
        var body = Convert.FromHexString(
            "b0ba976500000000020001003f00000008e1cb868f0d10b0f5deac0618012a0d6a61792d6e65742d70726f6265322000000000" +
            "00000000000000000000000000000000000000000000000000000000");
        Assert.Equal(79, body.Length);
        Assert.True(NodeHandshake.IsReply(body, "jay-net-probe"));
        Assert.False(NodeHandshake.IsReply(body, "jay-net-other"));
    }

    [Fact]
    public void HandshakeRepliesAreReadStrictly()
    {
        var good = NodeHandshake.Request("jay-net-test", 9)[4..];
        Assert.True(NodeHandshake.IsReply(good, "jay-net-test"));
        Assert.False(NodeHandshake.IsReply(good[..^1], "jay-net-test"));
        var wrongType = (byte[])good.Clone();
        wrongType[8] = 1;
        Assert.False(NodeHandshake.IsReply(wrongType, "jay-net-test"));
        var wrongMagic = (byte[])good.Clone();
        wrongMagic[NodeHandshake.HeaderSize + 1] ^= 1;
        Assert.False(NodeHandshake.IsReply(wrongMagic, "jay-net-test"));
        Assert.False(NodeHandshake.IsReply("SSH-2.0-OpenSSH_9.6\r\n"u8, "jay-net-test"));
    }

    [Fact]
    public async Task AddressesThatArentListedDontTakeAHostsPlaces()
    {
        // More ports of the host than may be dialled at once never answer. The real one waits its turn and is listed.
        var hold = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new VolunteerRegistry((url, _) => url.EndsWith(":11010") ? Task.FromResult(true) : hold.Task, () => _now)
            { WaitStep = TimeSpan.FromMilliseconds(5) };
        for (var port = 1; port <= VolunteerRegistry.MaxProbingPerHost + 2; port++)
            Assert.True(Ok(registry.Register($"tcp://host.example.org:{port}")));
        var real = registry.Register("tcp://host.example.org:11010");
        Assert.True(Ok(real), real.ToJsonString());
        var probe = registry.LastProbe;
        await Task.Delay(50);
        Assert.Empty(registry.Listed());
        hold.SetResult(false);
        await probe.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(["tcp://host.example.org:11010"], registry.Listed());
    }

    public static TheoryData<string> Machines => ["10.40.0.1", "fd00:1:2:3::"];

    [Theory]
    [MemberData(nameof(Machines))]
    public async Task ChecksThatHangDontStarveTheRealNode(string machine)
    {
        // Ports and names of one machine (the same IPv4 address, or one IPv6 /64 under different addresses) hang while
        // new ones keep coming. The real node, registered after the first lot, is dialled before every one that came after
        // it, and never more than MaxProbingPerHost checks of the machine run at once.
        var (running, most, order) = (0, 0, new List<string>());
        var gates = new Dictionary<string, TaskCompletionSource<bool>>();
        TaskCompletionSource<bool> Gate(string url)
        {
            lock (gates)
                return gates.TryGetValue(url, out var g) ? g : gates[url] = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        var addresses = 0;
        Task<IPAddress?> Resolve(string url, CancellationToken _) => Task.FromResult<IPAddress?>(IPAddress.Parse(
            machine.EndsWith("::") ? machine + (Interlocked.Increment(ref addresses) % 4000 + 1).ToString("x") : machine));
        var registry = new VolunteerRegistry(async (url, _) =>
        {
            lock (order) { order.Add(url); most = Math.Max(most, ++running); }
            try { return url.Contains("//real.") || await Gate(url).Task; }
            finally { lock (order) running--; }
        }, () => _now, Resolve) { WaitStep = TimeSpan.FromMilliseconds(5) };
        async Task Dialled(int count)
        {
            for (var i = 0; i < 1000; i++) { lock (order) if (order.Count >= count) return; await Task.Delay(5); }
            throw new TimeoutException(string.Join(",", order));
        }
        var early = Enumerable.Range(1, VolunteerRegistry.MaxProbingPerHost + 3).Select(i => $"tcp://a{i}.example.org:{12000 + i}").ToList();
        foreach (var url in early) Assert.True(Ok(registry.Register(url)));
        await Dialled(VolunteerRegistry.MaxProbingPerHost);
        Assert.True(Ok(registry.Register("tcp://real.example.org:11010")));
        var real = registry.LastProbe;
        var late = Enumerable.Range(1, 12).Select(i => $"tcp://b{i}.example.org:{13000 + i}").ToList();
        foreach (var url in late)
        {
            _now += TimeSpan.FromSeconds(1.1);
            Assert.True(Ok(registry.Register(url)));
        }
        await Task.Delay(50);
        // The hanging checks time out one by one: each frees one place, for whoever has waited longest.
        for (var i = 0; !real.IsCompleted; i++)
        {
            Gate(early[i]).SetResult(false);
            await Dialled(VolunteerRegistry.MaxProbingPerHost + i + 1);
            await Task.Delay(20);
        }
        await real;
        Assert.Equal(["tcp://real.example.org:11010"], registry.Listed());
        lock (order)
        {
            Assert.Equal([.. early, "tcp://real.example.org:11010"], order.Take(early.Count + 1));
            Assert.InRange(most, 1, VolunteerRegistry.MaxProbingPerHost);
        }
        foreach (var url in late) Gate(url).TrySetResult(false);
        foreach (var url in early) Gate(url).TrySetResult(false);
    }

    [Fact]
    public async Task AnEntryStillWaitingIsKeptForItsNextRenewal()
    {
        var hold = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new VolunteerRegistry((url, _) => url.EndsWith(":11010") ? Task.FromResult(true) : hold.Task, () => _now)
            { WaitStep = TimeSpan.FromMilliseconds(5), MaxWait = TimeSpan.FromMilliseconds(30) };
        for (var port = 1; port <= VolunteerRegistry.MaxProbingPerHost; port++) registry.Register($"tcp://host.example.org:{port}");
        Assert.True(Ok(registry.Register("tcp://host.example.org:11010")));
        await registry.LastProbe.WaitAsync(TimeSpan.FromSeconds(5));
        // Gave up waiting for now: not dialled, not dropped.
        Assert.Empty(registry.Listed());
        hold.SetResult(false);
        await Task.Delay(50);
        _now += VolunteerNodes.RenewEvery;
        var renewed = registry.Register("tcp://host.example.org:11010");
        Assert.True(Ok(renewed), renewed.ToJsonString());
        await registry.LastProbe;
        Assert.Equal(["tcp://host.example.org:11010"], registry.Listed());
    }

    [Fact]
    public async Task ACheckThatNeverAnswersEndsInTime()
    {
        // The dial is given up after ListingDialTimeout even when the connection ignores being cancelled.
        var never = new TaskCompletionSource<bool>();
        var registry = new VolunteerRegistry((_, _) => never.Task, () => _now);
        var started = System.Diagnostics.Stopwatch.StartNew();
        Assert.True(Ok(registry.Register("tcp://slow.example.org:11010")));
        await registry.LastProbe.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.InRange(started.Elapsed, VolunteerRegistry.ListingDialTimeout - TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(8));
        Assert.Empty(registry.Listed());
    }

    [Fact]
    public void MoreChecksMayRunPerHostThanNewAddressesCanKeepBusy()
    {
        // Unsigned and signed registrations each take NewPerMinute new addresses; each check is dialled for at most
        // ListingDialTimeout. Hanging checks can't hold every place of a host all the time.
        var busy = 2 * VolunteerRegistry.NewPerMinute * VolunteerRegistry.ListingDialTimeout.TotalMinutes;
        Assert.True(VolunteerRegistry.MaxProbingPerHost > busy, $"{VolunteerRegistry.MaxProbingPerHost} <= {busy}");
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
        Assert.False(await client.VolunteerAsync("tcp://node1.example.org:11010", null, default));
        await directory.Volunteers.LastProbe;
        Assert.True(await client.VolunteerAsync("tcp://node1.example.org:11010", null, default));
        Assert.Equal(["tcp://node1.example.org:11010"], await client.NodesAsync(default));
        await Assert.ThrowsAsync<ArgumentException>(() => client.VolunteerAsync("tcp://192.168.1.2:11010", null, default));

        directory.Volunteers = null;
        var e = await Assert.ThrowsAsync<DirectoryRejectedException>(() => client.VolunteerAsync("tcp://node1.example.org:11010", null, default));
        Assert.Equal("off", e.Code);
        Assert.Empty(await client.NodesAsync(default));
    }

    /// <summary>Stands in for "on the public internet" in tests, so no real public address is needed.</summary>
    private static bool TestRangeIsPublic(IPAddress ip) => ip.ToString().StartsWith("10.235.", StringComparison.Ordinal) || ip.ToString() == "fd00::235";

    [Theory]
    [InlineData("tcp://10.235.0.9:11010", "tcp://10.235.0.9:11010")]
    [InlineData("10.235.0.9:12000", "tcp://10.235.0.9:12000")]
    [InlineData("tcp://[fd00::235]:11010", "tcp://[fd00::235]:11010")]
    [InlineData("tcp://10.0.0.1:11010", null)]
    [InlineData("udp://10.235.0.9:11010", null)]
    public void PublicAddressLiteralsAreKept(string input, string? expected) =>
        Assert.Equal(expected, VolunteerNodes.Normalize(input, TestRangeIsPublic));

    [Fact]
    public void PublicAddressComesFromEasyTiersStunResult()
    {
        Assert.Equal("10.235.0.4", VolunteerNodes.PublicAddressFromNodeInfo("""{"stun_info":{"public_ip":["10.235.0.4"]},"ip_list":{"public_ipv4":null}}""", TestRangeIsPublic));
        // ip_list.public_ipv4 as EasyTier's numeric form (10.235.0.9 = 183173129).
        Assert.Equal("10.235.0.9", VolunteerNodes.PublicAddressFromNodeInfo("""{"stun_info":{"public_ip":[]},"ip_list":{"public_ipv4":{"addr":183173129}}}""", TestRangeIsPublic));
        // Private / CGNAT results and garbage give nothing.
        Assert.Null(VolunteerNodes.PublicAddressFromNodeInfo("""{"stun_info":{"public_ip":["100.64.3.4","192.168.1.2"]}}"""));
        Assert.Null(VolunteerNodes.PublicAddressFromNodeInfo("""{"stun_info":{"public_ip":[]},"ip_list":{"public_ipv4":null}}"""));
        Assert.Null(VolunteerNodes.PublicAddressFromNodeInfo("not json"));
        Assert.Null(VolunteerNodes.PublicAddressFromNodeInfo("""{"stun_info":{"public_ip":[42]}}"""));
    }

    [Fact]
    public async Task ProbeTellsAWouldBeVolunteerWhetherItsPortIsOpenAndIsRateLimited()
    {
        var open = new HashSet<string> { "tcp://node1.example.org:11010" };
        await using var directory = new RoomDirectoryServer(IPAddress.Loopback, 0, clock: () => _now)
        {
            Volunteers = new VolunteerRegistry((url, _) => Task.FromResult(open.Contains(url)), () => _now),
        };
        var client = new RoomDirectoryClient(new LoopbackNetwork(directory.Port));
        Assert.True(await client.ProbeAsync("tcp://node1.example.org:11010", default));
        Assert.False(await client.ProbeAsync("tcp://node3.example.org:11010", default));
        // Same address again right away: refused.
        var e = await Assert.ThrowsAsync<DirectoryRejectedException>(() => client.ProbeAsync("tcp://node1.example.org:11010", default));
        Assert.Equal("rate", e.Code);
        // "Test port" says it was the port test that was too soon, not the directory's "Publishing too often".
        Assert.Equal("multiplayer.volunteer.test_rate_limited", Amber.App.ViewModels.MultiplayerViewModel.PortTestRejected(e).Key);
        Assert.Equal(StatusKind.Warning, Amber.App.ViewModels.MultiplayerViewModel.PortTestRejected(e).Kind);
        var other = new DirectoryRejectedException("banned", "nope");
        Assert.Equal(["nope"], Amber.App.ViewModels.MultiplayerViewModel.PortTestRejected(other).Args);
        _now += VolunteerRegistry.CheckSameAddressEvery + TimeSpan.FromSeconds(1);
        Assert.True(await client.ProbeAsync("tcp://node1.example.org:11010", default));
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
        Assert.Equal("off", (await Assert.ThrowsAsync<DirectoryRejectedException>(() => client.ProbeAsync("tcp://node2.example.org:11010", default))).Code);
    }

    [Fact]
    public void VolunteerArgsListenPubliclyRelayOnlyThisNetworkAndCapBandwidth()
    {
        var profile = PublicNetworkProfile.Jay(["tcp://node2.example.org:11010"]);
        var me = new VolunteerSettings("jay.example.org", 12000, 512);
        var args = PublicNetworkArgs.Build(new PublicNetworkOptions(profile, 15000, 15001,
            ExtraPeers: ["tcp://node3.example.org:11010", "tcp://jay.example.org:12000"], Volunteer: me));
        string After(string flag) => args[args.IndexOf(flag) + 1];
        Assert.Contains("tcp://0.0.0.0:12000", args);
        Assert.Contains("udp://0.0.0.0:12000", args);
        Assert.DoesNotContain("--no-listener", args);
        Assert.Equal("tcp://jay.example.org:12000", After("--mapped-listeners"));
        Assert.Equal(PublicNetworkProfile.JayNetworkName, After("--relay-network-whitelist"));
        Assert.Equal("524288", After("--instance-recv-bps-limit"));
        Assert.Contains("tcp://node3.example.org:11010", args);
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
        cache.Remember("jay", ["tcp://node1.example.org:11010", "tcp://10.0.0.1:11010"]);
        cache.Remember("jay", Enumerable.Range(0, 20).Select(i => $"tcp://n{i}.example.org:11010"));
        cache.Remember("nz", ["tcp://node3.example.org:11010"]);
        var jay = cache.For("jay");
        Assert.Equal(VolunteerNodes.Remember, jay.Count);
        Assert.Equal("tcp://n0.example.org:11010", jay[0]);
        Assert.DoesNotContain("tcp://10.0.0.1:11010", jay);
        Assert.Equal(["tcp://node3.example.org:11010"], cache.For("nz"));
        cache.Forget("nz", "tcp://node3.example.org:11010");
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
        Assert.Equal("off", byConfig.Directory!.Handle(new JsonObject { ["op"] = "volunteer", ["url"] = "tcp://node1.example.org:11010" })["error"]!.GetValue<string>());
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
        Assert.True(Ok(server.Directory!.Handle(new JsonObject { ["op"] = "volunteer", ["url"] = "tcp://node1.example.org:11010" })));
        await server.Volunteers!.LastProbe;
        Assert.Equal(1, server.Health()["volunteers"]!.GetValue<int>());

        using var http = new HttpClient();
        var port = server.AdminPort!.Value.ToString();
        var output = new StringWriter();
        Assert.Equal(0, await Program.NodeCommand(_dir, Options.Parse(["node", "nodes", "--admin-port", port]), output, http));
        Assert.Contains("tcp://node1.example.org:11010", output.ToString());
        Assert.Equal(0, await Program.NodeCommand(_dir, Options.Parse(["node", "ban", "node", "node1.example.org:11010", "--reason", "slow", "--admin-port", port]), output, http));
        Assert.Empty(server.Volunteers.Listed());
        Assert.Equal("banned", server.Directory.Handle(new JsonObject { ["op"] = "volunteer", ["url"] = "tcp://node1.example.org:11010" })["error"]!.GetValue<string>());
        Assert.Throws<ArgumentException>(() => server.Ban("node", "tcp://192.168.1.1:11010", null));
        Assert.True(server.Bans!.Remove("node", "node1.example.org:11010"));
        Assert.True(Ok(server.Directory.Handle(new JsonObject { ["op"] = "volunteer", ["url"] = "tcp://node1.example.org:11010" })));
    }
}
