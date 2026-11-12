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
        var host = new EasyTierOptions(hostRoom, true, [Community], 15000);
        Assert.Contains(SplitSettings.LegPort(0), EasyTierArgs.ServedPorts(host));
        Assert.Contains(TunnelSettings.Port, EasyTierArgs.ServedPorts(host));

        var member = new EasyTierOptions(memberRoom, false, [Community], 15000) { LocalTunnelPort = 40000, LocalSplitPorts = [40010, 40011] };
        var args = EasyTierArgs.Build(member);
        Assert.Contains($"tcp://127.0.0.1:40010/{EasyTierArgs.HostIp}:47657", args);
        Assert.Contains($"tcp://127.0.0.1:40000/{EasyTierArgs.HostIp}:{TunnelSettings.Port}", args);

        // A room that doesn't split, or full-LAN mode, doesn't open the leg port.
        var (plainHost, _) = Rooms(SplitSettings.None);
        Assert.DoesNotContain(SplitSettings.LegPort(0), EasyTierArgs.ServedPorts(new EasyTierOptions(plainHost, true, [Community], 15000)));
        Assert.False(EasyTierArgs.SplitsOnThisInstance(host with { Tun = true }));
    }

    // ---- operator groups and planning ----

    [Theory]
    [InlineData("tcp://a.relay.example.com:11010", "tcp://b.relay.example.com:11010", true)]
    [InlineData("tcp://relay.example.co.uk:11010", "udp://other.example.co.uk:11011", true)]
    [InlineData("tcp://relay.example.co.uk:11010", "tcp://relay.another.co.uk:11010", false)]
    [InlineData("tcp://203.0.113.5:11010", "tcp://203.0.200.9:11010", true)]
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
}
