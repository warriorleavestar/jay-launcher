using System.Security.Cryptography;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Tests;

/// <summary>Stage 3 of the multi-path split: extra EasyTier instances, path planning and the readiness check (no processes started).</summary>
public sealed class SplitPathTests
{
    private const string Community = "tcp://et-hk.example.org:11010";

    private static (RoomCode Host, RoomCode Member) Rooms(SplitSettings split, TunnelSettings? tunnel = null)
    {
        using var host = X25519KeyPair.Generate();
        var credentials = RoomCredentials.Create(host.PublicKey, 25565, tunnel ?? new TunnelSettings(TransportMode.High, false)) with { Split = split };
        var table = new MemberTable();
        var hostRoom = credentials.ToHostRoom(table, "Host");
        var guest = table.Issue(RandomNumberGenerator.GetBytes(32), "Alice");
        var member = (credentials with { Member = new MemberKey(guest.Key.Id, guest.Key.CopyPsk()) }).ToRoom();
        return (hostRoom, member);
    }

    // ---- extra instance arguments ----

    [Fact]
    public void ExtraPathIsRelayOnlyAndServesOnlyItsLegPort()
    {
        var net = SplitNet.Create();
        var host = SplitPathArgs.Build(new SplitPathOptions(1, net, true, [Community, "tcp://relay.example.net:11010"], 15001));
        Assert.Contains("--disable-p2p", host);
        Assert.Contains("--no-listener", host);
        Assert.Contains("--no-tun", host);
        Assert.Equal("47658", host[host.IndexOf("--tcp-whitelist") + 1]);
        Assert.Equal("1", host[host.IndexOf("--udp-whitelist") + 1]);
        Assert.Equal(SplitPathArgs.HostIp(1), host[host.IndexOf("--ipv4") + 1]);
        Assert.Equal(net.Network, host[host.IndexOf("--network-name") + 1]);
        Assert.Equal(2, host.Count(a => a == "--peers"));
        Assert.DoesNotContain("--port-forward", host);
        // The secret goes through the environment, never the command line.
        Assert.DoesNotContain(net.Secret, host);
        Assert.Equal(net.Secret, SplitPathArgs.Environment(new SplitPathOptions(1, net, true, [], 15001))[EasyTierArgs.SecretVariable]);

        var guest = SplitPathArgs.Build(new SplitPathOptions(2, net, false, [Community], 15002, LocalLegPort: 40002));
        Assert.Equal("1", guest[guest.IndexOf("--tcp-whitelist") + 1]);
        Assert.Equal("tcp://127.0.0.1:40002/10.236.2.1:47659", guest[guest.IndexOf("--port-forward") + 1]);
        Assert.StartsWith("10.236.2.", guest[guest.IndexOf("--ipv4") + 1]);
        Assert.NotEqual(SplitPathArgs.HostIp(2), guest[guest.IndexOf("--ipv4") + 1]);
        // Not drawn at random: the same address every time it is built.
        Assert.Equal(guest[guest.IndexOf("--ipv4") + 1], SplitPathArgs.Build(new SplitPathOptions(2, net, false, [Community], 15002, LocalLegPort: 40002))[guest.IndexOf("--ipv4") + 1]);
        Assert.Contains("--disable-p2p", guest);
    }

    [Fact]
    public void AMemberNeedsItsLocalLegPort()
    {
        Assert.Throws<ArgumentException>(() => SplitPathArgs.Build(new SplitPathOptions(1, SplitNet.Create(), false, [Community], 15001)));
        Assert.Throws<ArgumentOutOfRangeException>(() => SplitPathArgs.Build(new SplitPathOptions(0, SplitNet.Create(), true, [Community], 15001)));
        Assert.Throws<ArgumentOutOfRangeException>(() => SplitPathArgs.Build(new SplitPathOptions(3, SplitNet.Create(), true, [Community], 15001)));
    }

    [Fact]
    public void TheMainInstanceCarriesLegZero()
    {
        var (hostRoom, memberRoom) = Rooms(SplitSettings.Create(SplitPolicy.Auto, SplitPolicy.Auto));
        // Opened when the host holds its leg ports (see AHostOpensItsLegPortsToTheRoomOnlyWhenItHoldsThem).
        var host = WithLegs(new EasyTierOptions(hostRoom, true, [Community], 15000), out _, [FreePort(), FreePort()]);
        using (host.HostSplitLegs)
        {
            Assert.Contains(SplitSettings.LegPort(0), EasyTierArgs.ServedPorts(host));
            Assert.Contains(TunnelSettings.Port, EasyTierArgs.ServedPorts(host));
        }

        var member = new EasyTierOptions(memberRoom, false, [Community], 15000) { LocalTunnelPort = 40000, LocalSplitPorts = [40010, 40011] };
        var args = EasyTierArgs.Build(member);
        Assert.Contains($"tcp://127.0.0.1:40010/{EasyTierArgs.HostIp}:47657", args);
        Assert.Contains($"tcp://127.0.0.1:40000/{EasyTierArgs.HostIp}:{TunnelSettings.Port}", args);

        // A room that doesn't split, or full-LAN mode, doesn't open the leg port.
        var (plainHost, _) = Rooms(SplitSettings.None);
        Assert.DoesNotContain(SplitSettings.LegPort(0), EasyTierArgs.ServedPorts(new EasyTierOptions(plainHost, true, [Community], 15000)));
        Assert.False(EasyTierArgs.SplitsOnThisInstance(host with { Tun = true }));
    }

    /// <summary>
    /// A member's address on an extra network has the same last part as on the room's own network, so members that don't
    /// clash there don't clash on the extra paths either.
    /// </summary>
    [Fact]
    public void AMembersExtraAddressFollowsItsRoomAddress()
    {
        var net = SplitNet.Create();
        var (_, memberRoom) = Rooms(SplitSettings.Create(SplitPolicy.Auto, SplitPolicy.Auto, 3));
        var seen = new Dictionary<int, string>();
        foreach (var machine in Enumerable.Range(0, 40).Select(i => $"machine-{i}"))
        {
            var main = EasyTierArgs.VirtualIp(new EasyTierOptions(memberRoom, false, [Community], 15000) { MachineId = machine });
            var host = EasyTierArgs.JoinerHost(machine);
            Assert.EndsWith("." + host, main);
            for (var i = 1; i <= 2; i++)
                Assert.Equal($"10.236.{i}.{host}", SplitPathArgs.MemberIp(new SplitPathOptions(i, net, false, [Community], 15001, 40001, MemberHost: host)));
            seen.TryAdd(host, machine);
        }
        Assert.True(seen.Count > 1);
    }

    // ---- operator groups and planning ----

    [Theory]
    [InlineData("tcp://a.relay.example.com:11010", "tcp://b.relay.example.com:11010", true)]
    [InlineData("tcp://relay.example.co.uk:11010", "udp://other.example.co.uk:11011", true)]
    [InlineData("tcp://relay.example.co.uk:11010", "tcp://relay.another.co.uk:11010", false)]
    [InlineData("tcp://198.18.1.5:11010", "tcp://198.18.200.9:11010", true)]
    [InlineData("tcp://203.0.113.5:11010", "tcp://198.51.100.9:11010", false)]
    [InlineData("tcp://relay.example.com:11010", "tcp://relay.example.net:11010", false)]
    public void NodesGroupByDomainOrAddressRange(string a, string b, bool same) =>
        Assert.Equal(same, RelayOperators.GroupOf(a) == RelayOperators.GroupOf(b));

    [Fact]
    public void SignedLabelsTakePrecedence()
    {
        var before = RelayOperators.Labels;
        try
        {
            RelayOperators.Labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["relay.example.com"] = "op1", ["relay.example.net"] = "op1" };
            Assert.Equal(RelayOperators.GroupOf("tcp://relay.example.com:1"), RelayOperators.GroupOf("tcp://relay.example.net:1"));
        }
        finally
        {
            RelayOperators.Labels = before;
        }
    }

    [Fact]
    public void PlanUsesOneGroupPerExtraPath()
    {
        var nodes = new[] { "tcp://a.one.example:1", "tcp://b.one.example:1", "tcp://two.example:1", "not a node" };
        var two = SplitPathPlanner.Plan(2, nodes, [1, 2, 3]);
        Assert.True(two.Ready);
        Assert.Single(two.ExtraPeers);
        var three = SplitPathPlanner.Plan(3, nodes, [1, 2, 3]);
        Assert.True(three.Ready);
        Assert.Equal(2, three.ExtraPeers.Count);
        Assert.Empty(three.ExtraPeers[0].Intersect(three.ExtraPeers[1]));
        Assert.DoesNotContain("not a node", three.ExtraPeers.SelectMany(p => p));
        // Nodes of one group stay together.
        Assert.Contains(three.ExtraPeers, p => p.Count == 2);
    }

    [Fact]
    public void ThreePathsNeedTwoGroups()
    {
        var plan = SplitPathPlanner.Plan(3, [Community], [1]);
        Assert.False(plan.Ready);
        Assert.NotNull(plan.Problem);
        Assert.True(SplitPathPlanner.Plan(2, [Community], [1]).Ready);
        Assert.False(SplitPathPlanner.Plan(2, [], [1]).Ready);
    }

    /// <summary>The same member always lands on the same groups; different members spread over them.</summary>
    [Fact]
    public void ThePlanIsFixedPerMember()
    {
        var nodes = Enumerable.Range(0, 6).Select(i => $"tcp://relay{i}.op{i}.example:1").ToArray();
        var key = RandomNumberGenerator.GetBytes(8);
        var first = SplitPathPlanner.Plan(2, nodes, key).ExtraPeers[0];
        for (var i = 0; i < 5; i++) Assert.Equal(first, SplitPathPlanner.Plan(2, [.. nodes.Reverse()], key).ExtraPeers[0]);
        var chosen = Enumerable.Range(0, 40).Select(_ => SplitPathPlanner.Plan(2, nodes, RandomNumberGenerator.GetBytes(8)).ExtraPeers[0][0]).Distinct().Count();
        Assert.True(chosen > 1);
    }

    // ---- developer switch ----

    [Theory]
    [InlineData("auto,auto", SplitPolicy.Auto, SplitPolicy.Auto, 2)]
    [InlineData("strict, off, 3", SplitPolicy.Strict, SplitPolicy.Off, 3)]
    [InlineData("AUTO,Strict,2", SplitPolicy.Auto, SplitPolicy.Strict, 2)]
    public void TheDeveloperSwitchTurnsSplittingOn(string value, SplitPolicy game, SplitPolicy chat, int paths)
    {
        var split = SplitSettings.FromSwitch(value);
        Assert.True(split.Enabled);
        Assert.Equal((game, chat, paths), (split.Game, split.Chat, split.Paths));
        Assert.Equal(paths - 1, split.Nets.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("auto")]
    [InlineData("off,off")]
    [InlineData("auto,auto,4")]
    [InlineData("auto,auto,1")]
    [InlineData("maybe,auto")]
    [InlineData("auto,auto,2,extra")]
    public void AnythingElseMeansNoSplit(string? value) => Assert.False(SplitSettings.FromSwitch(value).Enabled);

    [Fact]
    public void AnOpenSwitchLetsTheRelaysPickTheNumberOfPaths()
    {
        var open = SplitSettings.FromSwitch("auto,auto");
        Assert.True(open.PathsOpen);
        Assert.False(SplitSettings.FromSwitch("auto,auto,2").PathsOpen);
        string[] three = ["tcp://a.one.example:1", "tcp://b.one.example:1", "tcp://two.example:1", "tcp://three.example:1"];
        // One more path than independent groups, between two and three.
        Assert.Equal(2, open.FitTo([]).Paths);
        Assert.Equal(2, open.FitTo([Community]).Paths);
        Assert.Equal(3, open.FitTo(three[..3]).Paths);
        Assert.Equal(3, open.FitTo(three).Paths);
        var fitted = open.FitTo(three[..3]);
        Assert.False(fitted.PathsOpen);
        Assert.Equal((SplitPolicy.Auto, SplitPolicy.Auto, 2), (fitted.Game, fitted.Chat, fitted.Nets.Count));
        // A fixed count, and no split, stay as they are.
        var fixedTwo = SplitSettings.FromSwitch("auto,auto,2");
        Assert.Same(fixedTwo, fixedTwo.FitTo(three));
        Assert.Same(SplitSettings.None, SplitSettings.None.FitTo(three));
    }

    [Fact]
    public void IndependentGroupsCountsOneGroupPerOperator()
    {
        Assert.Equal(0, SplitPathPlanner.IndependentGroups([]));
        Assert.Equal(0, SplitPathPlanner.IndependentGroups(["not a node"]));
        Assert.Equal(2, SplitPathPlanner.IndependentGroups(["tcp://a.one.example:1", "tcp://b.one.example:1", " tcp://two.example:1 ", "tcp://two.example:1"]));
    }

    // ---- readiness ----

    [Fact]
    public void ReadyOnlyWhenTheMainPathIsDirectAndEveryRelayReachesTheHost()
    {
        var direct = new PeerInfo("amber-host", EasyTierArgs.HostIp, "p2p", 20, "udp");
        var relayed = new PeerInfo("amber-host", EasyTierArgs.HostIp, "relay(2)", 80, "tcp");
        var extra = new PeerInfo("jay-path-host", SplitPathArgs.HostIp(1), "relay(2)", 90, "tcp");
        Assert.Null(SplitPathPlanner.Check(direct, [extra]));
        Assert.NotNull(SplitPathPlanner.Check(null, [extra]));
        Assert.NotNull(SplitPathPlanner.Check(relayed, [extra]));
        Assert.NotNull(SplitPathPlanner.Check(direct, [null]));
        Assert.NotNull(SplitPathPlanner.Check(direct, [extra, null]));
    }

    // ---- the watch ----

    private static readonly PeerInfo DirectHost = new("amber-host", EasyTierArgs.HostIp, "p2p", 20, "udp");

    private static IReadOnlyList<SplitPathOptions> TwoExtraPaths()
    {
        var split = SplitSettings.Create(SplitPolicy.Auto, SplitPolicy.Auto, 3);
        return [.. Enumerable.Range(1, 2).Select(i => new SplitPathOptions(i, split.Nets[i - 1], false, [Community], 15000 + i, 40000 + i, MemberHost: 7))];
    }

    private static async Task<bool> Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(25);
        return condition();
    }

    /// <summary>
    /// A check that throws (a peer query failing, bad output, an instance going away) must not leave an earlier "ready"
    /// in place: the paths count as not ready, Auto uses one path and Strict refuses.
    /// </summary>
    [Fact]
    public async Task AFailingCheckFailsClosed()
    {
        var failing = 0;
        Task<List<PeerInfo>> Extra(SplitPathOptions o, CancellationToken _) => Volatile.Read(ref failing) != 0
            ? throw new InvalidOperationException("query broke")
            : Task.FromResult<List<PeerInfo>>([new("jay-path-host", SplitPathArgs.HostIp(o.Index), "relay(2)", 90, "tcp")]);
        var logged = new List<string>();
        using var group = SplitPathGroup.ForTest(TwoExtraPaths(), _ => Task.FromResult<List<PeerInfo>>([DirectHost]), Extra,
            TimeSpan.FromMilliseconds(20), line => { lock (logged) logged.Add(line); });
        Assert.True(await Eventually(() => group.Problem is null));

        Volatile.Write(ref failing, 1);
        Assert.True(await Eventually(() => group.Problem is not null));
        Assert.Equal(Amber.Core.Localization.L.T("core.mp.split.check_failed"), group.Problem);
        // The reason can show before the failure is logged (an unconfirmed ready also expires), so wait for the line.
        Assert.True(await Eventually(() => { lock (logged) return logged.Any(l => l.Contains(nameof(InvalidOperationException))); }));
        // It keeps checking: once the query works again the paths are ready again.
        Volatile.Write(ref failing, 0);
        Assert.True(await Eventually(() => group.Problem is null));
        Volatile.Write(ref failing, 1);
        Assert.True(await Eventually(() => group.Problem is not null));

        var (_, member) = Rooms(SplitSettings.Create(SplitPolicy.Auto, SplitPolicy.Strict));
        var singles = 0;
        Func<CancellationToken, Task<Stream>> single = _ =>
        {
            Interlocked.Increment(ref singles);
            return Task.FromResult<Stream>(new MemoryStream());
        };
        IReadOnlyList<Func<CancellationToken, Task<Stream>>> legs = [.. Enumerable.Range(0, 2).Select(_ => (Func<CancellationToken, Task<Stream>>)(_ =>
            Task.FromException<Stream>(new IOException())))];
        var game = new SplitConnector(member, TunnelService.Game, single, legs, pathProblem: () => group.Problem);
        await using (await game.OpenAsync(CancellationToken.None)) { }
        Assert.Equal(1, singles);
        var chat = new SplitConnector(member, TunnelService.Chat, single, legs, pathProblem: () => group.Problem);
        await Assert.ThrowsAsync<IOException>(() => chat.OpenAsync(CancellationToken.None));
        Assert.Equal(1, singles);
    }

    [Fact]
    public async Task AMainPeerQueryThatThrowsAlsoFailsClosed()
    {
        using var group = SplitPathGroup.ForTest(TwoExtraPaths(), _ => throw new System.Text.Json.JsonException(),
            (o, _) => Task.FromResult<List<PeerInfo>>([new("jay-path-host", SplitPathArgs.HostIp(o.Index), "relay(2)", 90, "tcp")]),
            TimeSpan.FromMilliseconds(20));
        Assert.True(await Eventually(() => group.Problem == Amber.Core.Localization.L.T("core.mp.split.check_failed")));
    }

    [Fact]
    public async Task AStoppedWatchMeansNotReady()
    {
        var group = SplitPathGroup.ForTest(TwoExtraPaths(), _ => Task.FromResult<List<PeerInfo>>([DirectHost]),
            (o, _) => Task.FromResult<List<PeerInfo>>([new("jay-path-host", SplitPathArgs.HostIp(o.Index), "relay(2)", 90, "tcp")]),
            TimeSpan.FromMilliseconds(20));
        Assert.True(await Eventually(() => group.Problem is null));
        group.Dispose();
        Assert.Equal(Amber.Core.Localization.L.T("core.mp.split.stopped"), group.Problem);
        group.Dispose();
    }

    [Fact]
    public void StartOnlySplitsWhenTheRoomAndSessionDo()
    {
        var work = Path.Combine(Path.GetTempPath(), "jay-split-" + Guid.NewGuid().ToString("N"));
        Task<List<PeerInfo>> NoPeers(CancellationToken _) => Task.FromResult<List<PeerInfo>>([]);
        var (plainHost, _) = Rooms(SplitSettings.None);
        Assert.Null(SplitPathGroup.Start("missing-core", "missing-cli", new EasyTierOptions(plainHost, true, [Community], 15000), work, NoPeers));
        var (hostRoom, memberRoom) = Rooms(SplitSettings.Create(SplitPolicy.Auto, SplitPolicy.Auto, 3));
        Assert.Null(SplitPathGroup.Start("missing-core", "missing-cli", new EasyTierOptions(hostRoom, true, [Community], 15000) { Tun = true }, work, NoPeers));
        // A member needs one local port per leg.
        Assert.Null(SplitPathGroup.Start("missing-core", "missing-cli",
            new EasyTierOptions(memberRoom, false, [Community], 15000) { LocalSplitPorts = [40010, 40011] }, work, NoPeers));
        // Three paths and one group of nodes: no instance is started and the reason is kept.
        using var noPlan = SplitPathGroup.Start("missing-core", "missing-cli",
            new EasyTierOptions(memberRoom, false, [Community], 15000) { LocalSplitPorts = [40010, 40011, 40012] }, work, NoPeers);
        Assert.NotNull(noPlan);
        Assert.Empty(noPlan.Instances);
        Assert.Equal(Amber.Core.Localization.L.T("core.mp.split.no_independent_path"), noPlan.Problem);
        Assert.False(Directory.Exists(work));
    }

    [Fact]
    public async Task AutoUsesOnePathWhilePathsArentReady()
    {
        var (_, member) = Rooms(SplitSettings.Create(SplitPolicy.Auto, SplitPolicy.Strict));
        var singles = 0;
        var legOpens = 0;
        Func<CancellationToken, Task<Stream>> single = _ =>
        {
            Interlocked.Increment(ref singles);
            return Task.FromResult<Stream>(new MemoryStream());
        };
        IReadOnlyList<Func<CancellationToken, Task<Stream>>> legs = [.. Enumerable.Range(0, 2).Select(_ => (Func<CancellationToken, Task<Stream>>)(_ =>
        {
            Interlocked.Increment(ref legOpens);
            return Task.FromException<Stream>(new IOException());
        }))];
        var game = new SplitConnector(member, TunnelService.Game, single, legs, pathProblem: () => "not direct");
        await using (await game.OpenAsync(CancellationToken.None)) { }
        Assert.Equal(1, singles);
        Assert.Equal(0, legOpens);
        Assert.Equal("not direct", game.LastReason);

        var chat = new SplitConnector(member, TunnelService.Chat, single, legs, pathProblem: () => "not direct");
        var e = await Assert.ThrowsAsync<IOException>(() => chat.OpenAsync(CancellationToken.None));
        Assert.Contains("not direct", e.Message);
        Assert.Equal(1, singles);
        Assert.Equal(0, legOpens);
    }

    // ---- review follow-ups ----

    [Fact]
    public void ExtraPathOutputIsMaskedBeforeLogging()
    {
        var net = SplitNet.Create();
        var o = new SplitPathOptions(1, net, true, [], 15001);
        foreach (var line in new[]
        {
            $"network_secret = \"{net.Secret}\"",
            $"  network_secret = '{net.Secret}'",
            $"network_secret: {net.Secret}",
            $"something else mentioning {net.Secret} in passing",
        })
        {
            var scrubbed = SplitPathArgs.Scrub(line, o);
            Assert.DoesNotContain(net.Secret, scrubbed);
            Assert.Contains("***", scrubbed);
        }
        // A field of that name is masked even when the value isn't this path's own.
        Assert.Equal("network_secret = \"***\"", SplitPathArgs.Scrub("network_secret = \"other\"", o));
        Assert.Equal("peer connected", SplitPathArgs.Scrub("peer connected", o));
    }

    [Fact]
    public void OnlyWrappedRoomsOutsideFullLanCarrySplitSettings()
    {
        var requested = SplitSettings.Create(SplitPolicy.Auto, SplitPolicy.Strict);
        Assert.Same(requested, SplitSettings.ForNewRoom(wraps: true, fullLan: false, requested));
        Assert.Same(SplitSettings.None, SplitSettings.ForNewRoom(wraps: true, fullLan: true, requested));
        Assert.Same(SplitSettings.None, SplitSettings.ForNewRoom(wraps: false, fullLan: false, requested));
        Assert.Same(SplitSettings.None, SplitSettings.ForNewRoom(wraps: false, fullLan: true, requested));
    }

    [Fact]
    public void MappedAddressesGroupLikeTheirIPv4()
    {
        Assert.Equal(RelayOperators.GroupOf("tcp://198.18.5.5:11010"), RelayOperators.GroupOf("tcp://[::ffff:198.18.0.1]:11010"));
        Assert.NotEqual(RelayOperators.GroupOf("tcp://198.19.5.5:11010"), RelayOperators.GroupOf("tcp://[::ffff:198.18.0.1]:11010"));
    }

    [Fact]
    public async Task ACheckThatNeverReturnsMeansNotReady()
    {
        var hang = 0;
        Task<List<PeerInfo>> Extra(SplitPathOptions o, CancellationToken ct) => Volatile.Read(ref hang) == 1
            ? Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => new List<PeerInfo>(), TaskScheduler.Default)
            : Task.FromResult<List<PeerInfo>>([new("jay-path-host", SplitPathArgs.HostIp(o.Index), "relay(2)", 90, "tcp")]);
        // Each round is cut off after its time limit.
        using (var group = SplitPathGroup.ForTest(TwoExtraPaths(), _ => Task.FromResult<List<PeerInfo>>([DirectHost]), Extra,
            TimeSpan.FromMilliseconds(20), minCheckTime: TimeSpan.FromMilliseconds(100)))
        {
            Assert.True(await Eventually(() => group.Problem is null));
            Volatile.Write(ref hang, 1);
            Assert.True(await Eventually(() => group.Problem == Amber.Core.Localization.L.T("core.mp.split.check_failed")));
        }
        // Even before that limit, a "ready" that hasn't been confirmed for a few rounds no longer counts.
        Volatile.Write(ref hang, 0);
        using (var group = SplitPathGroup.ForTest(TwoExtraPaths(), _ => Task.FromResult<List<PeerInfo>>([DirectHost]), Extra,
            TimeSpan.FromMilliseconds(20), minCheckTime: TimeSpan.FromMinutes(1)))
        {
            Assert.True(await Eventually(() => group.Problem is null));
            Volatile.Write(ref hang, 1);
            Assert.True(await Eventually(() => group.Problem == Amber.Core.Localization.L.T("core.mp.split.check_failed")));
        }
    }

    [Fact]
    public void AHostOpensItsLegPortsToTheRoomOnlyWhenItHoldsThem()
    {
        var (hostRoom, _) = Rooms(SplitSettings.Create(SplitPolicy.Auto, SplitPolicy.Auto));
        var options = new EasyTierOptions(hostRoom, true, [Community], 15000);
        using var squatter = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        squatter.Start();
        var taken = ((System.Net.IPEndPoint)squatter.LocalEndpoint).Port;
        var free = FreePort();

        // Another program has a leg port: Auto goes on without splitting, nothing of the leg ports is opened to the room,
        // and no extra instance starts.
        var busy = WithLegs(options, out var wasBusy, [free, taken]);
        Assert.True(wasBusy);
        Assert.Null(busy.HostSplitLegs);
        Assert.DoesNotContain(SplitSettings.LegPort(0), EasyTierArgs.ServedPorts(busy));
        var work = Path.Combine(Path.GetTempPath(), "jay-split-" + Guid.NewGuid().ToString("N"));
        Assert.Null(SplitPathGroup.Start("missing-core", "missing-cli", busy, work, _ => Task.FromResult<List<PeerInfo>>([])));
        Assert.False(Directory.Exists(work));
        // The free port wasn't kept either.
        using (var check = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, free)) check.Start();

        // Strict needs them: the room doesn't start.
        var (strictRoom, _) = Rooms(SplitSettings.Create(SplitPolicy.Strict, SplitPolicy.Auto));
        Assert.Throws<IOException>(() => WithLegs(new EasyTierOptions(strictRoom, true, [Community], 15000), out _, [free, taken]));

        // Free ports: held, opened to the room, and handed to the transport once.
        var second = FreePort();
        var held = WithLegs(options with { HostSplitLegs = null }, out wasBusy, [free, second]);
        Assert.False(wasBusy);
        Assert.NotNull(held.HostSplitLegs);
        Assert.Contains(SplitSettings.LegPort(0), EasyTierArgs.ServedPorts(held));
        var listeners = held.HostSplitLegs!.TryTake();
        Assert.NotNull(listeners);
        Assert.Null(held.HostSplitLegs.TryTake());
        foreach (var l in listeners!) l.Stop();

        // Options carried over from an earlier session never keep its ports.
        var carried = WithLegs(held with { Room = hostRoom with { Split = SplitSettings.None } }, out _);
        Assert.Null(carried.HostSplitLegs);
    }

    private static EasyTierOptions WithLegs(EasyTierOptions o, out bool busy, IReadOnlyList<int>? ports = null) =>
        EasyTierSession.WithSplitLegs(o, out busy, ports);

    private static int FreePort()
    {
        using var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        return ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
    }

    // ---- follow-ups ----

    [Theory]
    [InlineData("p2p", true)]
    [InlineData("relay(2)", true)]
    [InlineData("Relay(1)", true)]
    [InlineData("relay(3)", false)]
    [InlineData("relay(12)", false)]
    [InlineData("relay", false)]
    [InlineData("", false)]
    [InlineData("relay(2)\n", false)]
    [InlineData("relay(\u0662)", false)]
    [InlineData("relay(+2)", false)]
    public void AnExtraPathCrossesOneRelayAtMost(string cost, bool ok)
    {
        Assert.Equal(ok, SplitPathPlanner.OneRelayAtMost(cost));
        var extra = new PeerInfo("jay-path-host", SplitPathArgs.HostIp(1), cost, 90, "tcp");
        var problem = SplitPathPlanner.Check(DirectHost, [extra]);
        if (ok) Assert.Null(problem);
        else Assert.Equal(Amber.Core.Localization.L.T("core.mp.split.relay_multi_hop"), problem);
    }

    [Fact]
    public async Task ATransportStartingAgainKeepsTheSessionsLegPorts()
    {
        var (hostRoom, _) = Rooms(SplitSettings.Create(SplitPolicy.Auto, SplitPolicy.Auto));
        var held = WithLegs(new EasyTierOptions(hostRoom, true, [Community], 15000), out _, [FreePort(), FreePort()]);
        using var legs = held.HostSplitLegs!;
        var ports = legs.AllForTest.Select(l => ((System.Net.IPEndPoint)l.LocalEndpoint).Port).ToArray();
        for (var round = 0; round < 2; round++)
        {
            var lent = legs.TryBorrow();
            Assert.NotNull(lent);
            Assert.Null(legs.TryBorrow());
            Assert.Null(legs.TryTake());
            await using (new SplitTunnelServer(hostRoom, _ => null, listeners: lent, giveBack: legs.Return)) { }
            // Stopped transport: the ports are still this machine's, so nothing else can take them in between.
            foreach (var port in ports)
                Assert.Throws<System.Net.Sockets.SocketException>(() => SplitLegReservation.Bind(System.Net.IPAddress.Loopback, port));
        }
        // A refused start gives lent listeners back too.
        Assert.Throws<ArgumentException>(() => new SplitTunnelServer(hostRoom, _ => null, ports: [1], listeners: legs.TryBorrow(), giveBack: legs.Return));
        Assert.NotNull(legs.TryBorrow());
        legs.Return();
        // The session ending releases them.
        legs.Dispose();
        foreach (var port in ports) SplitLegReservation.Bind(System.Net.IPAddress.Loopback, port).Stop();
    }

    [Fact]
    public void AnExtraPathThatCantStartDoesntStopAnAutoRoom()
    {
        var (hostRoom, memberRoom) = Rooms(SplitSettings.Create(SplitPolicy.Auto, SplitPolicy.Auto));
        var work = Path.Combine(Path.GetTempPath(), "jay-split-" + Guid.NewGuid().ToString("N"));
        Task<List<PeerInfo>> NoPeers(CancellationToken _) => Task.FromResult<List<PeerInfo>>([]);
        (System.Diagnostics.Process, ProcessJob?) Broken(string core, IEnumerable<string> args, IReadOnlyDictionary<string, string> env, string dir) =>
            throw new System.ComponentModel.Win32Exception("no core");
        string[] peers = [Community, "tcp://relay.example.net:11010"];
        var logged = new List<string>();
        using var member = SplitPathGroup.Start("core", "cli",
            new EasyTierOptions(memberRoom, false, peers, 15000) { LocalSplitPorts = [40010, 40011] }, work, NoPeers, logged.Add, Broken);
        Assert.NotNull(member);
        Assert.Empty(member.Instances);
        Assert.StartsWith(Amber.Core.Localization.L.T("core.mp.split.start_failed"), member.Problem);
        Assert.Contains(logged, l => l.StartsWith(Amber.Core.Localization.L.T("core.mp.split.start_failed"), StringComparison.Ordinal));

        var held = WithLegs(new EasyTierOptions(hostRoom, true, peers, 15000), out _, [FreePort(), FreePort()]);
        using (held.HostSplitLegs)
            Assert.Null(SplitPathGroup.Start("core", "cli", held, work, NoPeers, launch: Broken));

        // Strict: the failure is the room's.
        var (_, strictMember) = Rooms(SplitSettings.Create(SplitPolicy.Strict, SplitPolicy.Auto));
        Assert.Throws<System.ComponentModel.Win32Exception>(() => SplitPathGroup.Start("core", "cli",
            new EasyTierOptions(strictMember, false, peers, 15000) { LocalSplitPorts = [40010, 40011] }, work, NoPeers, launch: Broken));
    }

    [Fact]
    public async Task AFailingCheckIsLoggedOnceUntilItRecovers()
    {
        var failing = 1;
        Task<List<PeerInfo>> Extra(SplitPathOptions o, CancellationToken _) => Volatile.Read(ref failing) != 0
            ? throw new InvalidOperationException("query broke")
            : Task.FromResult<List<PeerInfo>>([new("jay-path-host", SplitPathArgs.HostIp(o.Index), "relay(2)", 90, "tcp")]);
        var logged = new List<string>();
        int Failures() { lock (logged) return logged.Count(l => l.Contains(nameof(InvalidOperationException))); }
        using var group = SplitPathGroup.ForTest(TwoExtraPaths(), _ => Task.FromResult<List<PeerInfo>>([DirectHost]), Extra,
            TimeSpan.FromMilliseconds(10), line => { lock (logged) logged.Add(line); });
        Assert.True(await Eventually(() => Failures() == 1));
        await Task.Delay(200);
        Assert.Equal(1, Failures());
        Volatile.Write(ref failing, 0);
        Assert.True(await Eventually(() => group.Problem is null));
        Volatile.Write(ref failing, 1);
        Assert.True(await Eventually(() => Failures() == 2));
    }
}
