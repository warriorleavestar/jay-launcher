using Amber.Core.Localization;
using Amber.Core.Multiplayer;

namespace Amber.Core.Tests;

/// <summary>
/// 加入方等房主时分清失败原因：连不上中继节点（根本没进大厅）和连上了中继却没有房主（邀请用过/过期或房主关了房间），提示不能是同一句。
/// </summary>
public sealed class LobbyJoinWaitTests
{
    private static readonly PeerInfo Self = new("me", "10.234.0.9", "Local", null, null);
    private static readonly PeerInfo Relay = new("PublicServer_hk", "", "1", 30, "tcp");
    private static readonly PeerInfo Host = new("host", LobbyArgs.HostIp, "2", 60, "tcp");
    private static readonly string[] Nodes = ["tcp://relay.example:11010"];

    private static Task Wait(Func<List<PeerInfo>> peers, IReadOnlyList<string> nodes, Action? relayFound = null, Func<bool>? reachable = null,
        TimeSpan? relayTimeout = null, TimeSpan? hostTimeout = null) =>
        LobbyJoin.WaitForHostAsync(_ => Task.FromResult(peers()), () => null, nodes, relayFound,
            relayTimeout ?? TimeSpan.FromMilliseconds(200), hostTimeout ?? TimeSpan.FromMilliseconds(600), CancellationToken.None, TimeSpan.FromMilliseconds(20), reachable);

    [Fact]
    public async Task ReturnsOnceTheHostShowsUp()
    {
        var calls = 0;
        var found = 0;
        await Wait(() => ++calls < 3 ? [Self, Relay] : [Self, Relay, Host], Nodes, () => found++);
        Assert.Equal(1, found);
    }

    [Fact]
    public async Task NoPeerAtAllMeansTheRelayIsUnreachable()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var e = await Assert.ThrowsAsync<LobbyUnreachableException>(() => Wait(() => [Self], Nodes));
        Assert.Contains("relay.example", e.Message);
        Assert.Equal(L.F("core.mp.lobby.norelay", Nodes[0]), e.Message);
        // 不等满找房主的时间。
        Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(550), watch.Elapsed.ToString());
    }

    [Fact]
    public async Task RelayButNoHostPointsAtTheInvite()
    {
        var found = 0;
        var e = await Assert.ThrowsAsync<TimeoutException>(() => Wait(() => [Self, Relay], Nodes, () => found++));
        Assert.IsNotType<LobbyUnreachableException>(e);
        Assert.Equal(L.T("core.mp.lobby.nohost"), e.Message);
        Assert.Equal(1, found);
    }

    /// <summary>EasyTier 的对端列表里没列出中继，但直接测节点能连上：按“连上了中继、没有房主”处理。</summary>
    [Fact]
    public async Task ReachableNodeCountsAsRelayEvenIfNotListed()
    {
        var e = await Assert.ThrowsAsync<TimeoutException>(() => Wait(() => [Self], Nodes, reachable: () => true));
        Assert.IsNotType<LobbyUnreachableException>(e);
        Assert.Equal(L.T("core.mp.lobby.nohost"), e.Message);
    }

    /// <summary>没设节点（局域网、直连桥）：不提前判定连不上，等满后提示要加节点。</summary>
    [Fact]
    public async Task NoNodesWaitsTheFullTimeAndSaysSo()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var e = await Assert.ThrowsAsync<LobbyUnreachableException>(() => Wait(() => [Self], []));
        Assert.Equal(L.T("core.mp.lobby.nonodes"), e.Message);
        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(550), watch.Elapsed.ToString());
    }

    [Fact]
    public async Task UserCancelIsNotATimeout()
    {
        using var cts = new CancellationTokenSource(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LobbyJoin.WaitForHostAsync(_ => Task.FromResult(new List<PeerInfo> { Self }), () => null, Nodes, null,
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), cts.Token, TimeSpan.FromMilliseconds(20)));
    }

    [Fact]
    public async Task ExitedLobbyIsReported()
    {
        await Assert.ThrowsAsync<IOException>(() => LobbyJoin.WaitForHostAsync(_ => Task.FromResult(new List<PeerInfo>()), () => "boom", Nodes, null,
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), CancellationToken.None));
    }
}
