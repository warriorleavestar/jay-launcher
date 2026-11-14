using System.Net;
using System.Reflection;
using Amber.App.Services;
using Amber.App.ViewModels;
using Amber.Core.Multiplayer;
using Amber.Core.Localization;

namespace Amber.Core.Tests;

/// <summary>出口仅在确认后生效；失败、停止和直连所有权都不需要实际 UAC 或修改系统路由。</summary>
public sealed class ExitLifecycleRegressionTests
{
    private static readonly RoomCode Room = new("K7Q2M9XD", "4HZP8RW2N6TC", 25565);
    private static readonly DirectOptions Direct = new([], false, [], true, false, false, true);
    private static readonly EasyTierOptions Original = new(Room, false, ["udp://127.0.0.1:1"], 1, Tun: true);
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    private sealed class FakeEasyTier(EasyTierOptions options) : IEasyTierSession
    {
        public EasyTierOptions Options { get; } = options;
        public bool HasExited { get; private set; }
        public IReadOnlyList<string> RecentOutput => [];
        public Task<List<PeerInfo>> PeersAsync(CancellationToken ct = default) => Task.FromResult(new List<PeerInfo>());
        public void Dispose() => HasExited = true;
    }

    private sealed class Owner : IDisposable
    {
        public void Dispose() { }
    }

    private static DirectSession NewSession(Func<EasyTierOptions, CancellationToken, Task<IEasyTierSession>> start) =>
        new(null!, Path.Combine(Path.GetTempPath(), "amber-exit-lifecycle-" + Guid.NewGuid().ToString("N")), Room, false, Direct, new DiagnosticLog())
        { StartForTest = start };

    private static DirectPeer Ipv6Member(bool verified) =>
        new() { Remote = new IPEndPoint(IPAddress.Parse("2001:db8::7"), 40000), Path = PathKind.IPv6, Admitted = true, _verified = verified };

    [Fact]
    public async Task AMemberWhosePingAnswerHasNotArrivedKeepsTheRunningSession()
    {
        // Data already flows over the bridge, but the member's answer to a fresh ping hasn't come back: turning the exit on
        // now would leave its address out of the gate and cut the room off. The switch waits, then gives up and keeps
        // what is running.
        var member = Ipv6Member(verified: false);
        await using var session = new DirectSession(null!, Path.Combine(Path.GetTempPath(), "amber-exit-lifecycle-" + Guid.NewGuid().ToString("N")),
            Room, false, Direct, new DiagnosticLog())
        {
            StartForTest = (o, _) => Task.FromResult<IEasyTierSession>(new FakeEasyTier(o)),
            BridgePeersForTest = () => [member],
            BearerWait = TimeSpan.FromMilliseconds(300),
        };
        await session.StartEasyTierAsync(Original, default);
        var original = session.EasyTier!;
        await Assert.ThrowsAsync<IOException>(() => session.SetExitNodeAsync(EasyTierArgs.HostIp));
        Assert.Same(original, session.EasyTier);
        Assert.False(original.HasExited);
        Assert.Null(original.Options.ExitNode);
        Assert.Equal(1, session.EasyTierStarts);

        // Turning the exit off never waits.
        await session.SetExitNodeAsync(null);
    }

    [Fact]
    public async Task ALateAnswerLetsTheSwitchGoAheadWithThatMembersAddress()
    {
        var member = Ipv6Member(verified: false);
        await using var session = new DirectSession(null!, Path.Combine(Path.GetTempPath(), "amber-exit-lifecycle-" + Guid.NewGuid().ToString("N")),
            Room, false, Direct, new DiagnosticLog())
        {
            StartForTest = (o, _) => Task.FromResult<IEasyTierSession>(new FakeEasyTier(o)),
            BridgePeersForTest = () => [member],
            BearerWait = TimeSpan.FromSeconds(5),
        };
        await session.StartEasyTierAsync(Original, default);
        var switching = session.SetExitNodeAsync(EasyTierArgs.HostIp);
        await Task.Delay(250);
        Assert.False(switching.IsCompleted);
        Assert.Equal(1, session.EasyTierStarts);
        Volatile.Write(ref member._verified, true);
        await switching.WaitAsync(Deadline);
        Assert.Equal(EasyTierArgs.HostIp, session.EasyTier!.Options.ExitNode);
        Assert.Contains("2001:db8::7", session.EasyTier.Options.DirectUnderlayAddresses);
    }

    [Fact]
    public async Task AnIpv6PlayerStillJoiningIsWaitedForAndThenKeepsItsPlace()
    {
        // Another player over IPv6 is halfway through joining (heard from, not admitted yet) when the exit is turned on:
        // starting now would fix the exceptions without it and cut its join off. The switch waits until it is in.
        var joining = Ipv6Member(verified: false);
        joining.Admitted = false;
        await using var session = new DirectSession(null!, Path.Combine(Path.GetTempPath(), "amber-exit-lifecycle-" + Guid.NewGuid().ToString("N")),
            Room, false, Direct, new DiagnosticLog())
        {
            StartForTest = (o, _) => Task.FromResult<IEasyTierSession>(new FakeEasyTier(o)),
            BridgePeersForTest = () => [joining],
            BearerWait = TimeSpan.FromSeconds(5),
        };
        await session.StartEasyTierAsync(Original, default);
        var switching = session.SetExitNodeAsync(EasyTierArgs.HostIp);
        await Task.Delay(250);
        Assert.False(switching.IsCompleted);
        joining.Admitted = true;
        await Task.Delay(250);
        Assert.False(switching.IsCompleted);
        Volatile.Write(ref joining._verified, true);
        await switching.WaitAsync(Deadline);
        Assert.Contains("2001:db8::7", session.EasyTier!.Options.DirectUnderlayAddresses);
    }

    [Fact]
    public async Task AMemberOnAnOlderJayFailsAtOnceWithAMessageThatSaysToUpdate()
    {
        // An older Jay's answers never verify, so waiting and "try again" can't help: say what can.
        var older = Ipv6Member(verified: false);
        older._outdated = true;
        await using var session = new DirectSession(null!, Path.Combine(Path.GetTempPath(), "amber-exit-lifecycle-" + Guid.NewGuid().ToString("N")),
            Room, false, Direct, new DiagnosticLog())
        {
            StartForTest = (o, _) => Task.FromResult<IEasyTierSession>(new FakeEasyTier(o)),
            BridgePeersForTest = () => [older],
            BearerWait = TimeSpan.FromSeconds(30),
        };
        await session.StartEasyTierAsync(Original, default);
        var original = session.EasyTier!;
        var waited = System.Diagnostics.Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<IOException>(() => session.SetExitNodeAsync(EasyTierArgs.HostIp).WaitAsync(Deadline));
        Assert.True(waited.Elapsed < TimeSpan.FromSeconds(5));
        Assert.Equal(L.T("core.mp.session.bearer_outdated"), error.Message);
        Assert.Same(original, session.EasyTier);

        // Another member still on its way keeps the plain wait going; once it's verified, the older one decides.
        var current = Ipv6Member(verified: false);
        Assert.Equal(DirectSession.BearerWaitResult.Unverified,
            await DirectSession.BearersVerifiedAsync(() => [current], TimeSpan.FromMilliseconds(200), default));
        Volatile.Write(ref current._verified, true);
        Assert.Equal(DirectSession.BearerWaitResult.Outdated,
            await DirectSession.BearersVerifiedAsync(() => [current, older], TimeSpan.FromMilliseconds(200), default));
        Assert.Equal(DirectSession.BearerWaitResult.Ready,
            await DirectSession.BearersVerifiedAsync(() => [current], TimeSpan.FromMilliseconds(200), default));
    }

    [Fact]
    public async Task WhileTheExitRunsTheBridgeTakesIpv6OnlyFromThePlannedAddresses()
    {
        // From the start of the switch no IPv6 peer the bridge doesn't have yet gets in, then only the planned ones,
        // until the exit is turned off. A switch that fails leaves no limit behind.
        var member = Ipv6Member(verified: true);
        var holds = new List<IReadOnlySet<IPAddress>?>();
        await using var session = new DirectSession(null!, Path.Combine(Path.GetTempPath(), "amber-exit-lifecycle-" + Guid.NewGuid().ToString("N")),
            Room, false, Direct, new DiagnosticLog())
        {
            StartForTest = (o, _) => Task.FromResult<IEasyTierSession>(new FakeEasyTier(o)),
            BridgePeersForTest = () => [member],
            BearerWait = TimeSpan.FromMilliseconds(300),
            Ipv6HoldForTest = h => { lock (holds) holds.Add(h); },
        };
        await session.StartEasyTierAsync(Original, default);
        await session.SetExitNodeAsync(EasyTierArgs.HostIp).WaitAsync(Deadline);
        var only = IPAddress.Parse("2001:db8::7");
        Assert.Equal(3, holds.Count);
        Assert.All(holds, h => Assert.Equal([only], h!));

        await session.SetExitNodeAsync(null).WaitAsync(Deadline);
        Assert.Null(holds[^1]);

        holds.Clear();
        Volatile.Write(ref member._verified, false);
        await Assert.ThrowsAsync<IOException>(() => session.SetExitNodeAsync(EasyTierArgs.HostIp));
        Assert.Equal([only], holds[0]!);
        Assert.Null(holds[^1]);
    }

    [Fact]
    public async Task APeerThatSlipsInWhileTheExitStartsIsReported()
    {
        var member = Ipv6Member(verified: true);
        var late = new DirectPeer { Remote = new IPEndPoint(IPAddress.Parse("2001:db8::9"), 40000), Path = PathKind.IPv6, Admitted = true, _verified = true };
        var peers = new List<DirectPeer> { member };
        var log = new DiagnosticLog();
        await using var session = new DirectSession(null!, Path.Combine(Path.GetTempPath(), "amber-exit-lifecycle-" + Guid.NewGuid().ToString("N")),
            Room, false, Direct, log)
        {
            StartForTest = (o, _) =>
            {
                if (o.ExitNode is not null) lock (peers) peers.Add(late);
                return Task.FromResult<IEasyTierSession>(new FakeEasyTier(o));
            },
            BridgePeersForTest = () => { lock (peers) return [.. peers]; },
        };
        await session.StartEasyTierAsync(Original, default);
        await session.SetExitNodeAsync(EasyTierArgs.HostIp).WaitAsync(Deadline);
        Assert.DoesNotContain("2001:db8::9", session.EasyTier!.Options.DirectUnderlayAddresses);
        Assert.Contains(log.Snapshot(), e => e.Text.Contains(L.T("core.mp.session.exit_peer_rejoin"), StringComparison.Ordinal));
        Assert.Single(DirectSession.PeersOutsideThePlan([member, late], session.EasyTier.Options.DirectUnderlayAddresses));
    }

    [Fact]
    public async Task AJoinThatDoesNotFinishKeepsTheRunningSessionButAnAbandonedOneDoesNotHoldTheSwitch()
    {
        var joining = Ipv6Member(verified: false);
        joining.Admitted = false;
        await using var session = new DirectSession(null!, Path.Combine(Path.GetTempPath(), "amber-exit-lifecycle-" + Guid.NewGuid().ToString("N")),
            Room, false, Direct, new DiagnosticLog())
        {
            StartForTest = (o, _) => Task.FromResult<IEasyTierSession>(new FakeEasyTier(o)),
            BridgePeersForTest = () => [joining],
            BearerWait = TimeSpan.FromMilliseconds(300),
        };
        await session.StartEasyTierAsync(Original, default);
        var original = session.EasyTier!;
        await Assert.ThrowsAsync<IOException>(() => session.SetExitNodeAsync(EasyTierArgs.HostIp));
        Assert.Same(original, session.EasyTier);
        Assert.Equal(1, session.EasyTierStarts);

        // Not heard from for a while: it gave up, and doesn't hold the switch (nor get an exception).
        joining.LastSeen = DateTime.UtcNow - DirectSession.JoiningWindow - TimeSpan.FromSeconds(1);
        await session.SetExitNodeAsync(EasyTierArgs.HostIp).WaitAsync(Deadline);
        Assert.DoesNotContain("2001:db8::7", session.EasyTier!.Options.DirectUnderlayAddresses);
    }

    [Fact]
    public async Task ExitIsPublishedOnlyAfterReadinessAcknowledgement()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = NewSession(async (o, ct) =>
        {
            if (o.ExitNode is not null)
            {
                entered.TrySetResult();
                await ready.Task.WaitAsync(ct);
            }
            return new FakeEasyTier(o);
        });
        await session.StartEasyTierAsync(Original, default);
        var original = session.EasyTier!;
        var switching = session.SetExitNodeAsync(EasyTierArgs.HostIp);
        await entered.Task.WaitAsync(Deadline);
        Assert.False(switching.IsCompleted);
        Assert.Same(original, session.EasyTier);
        Assert.Null(session.EasyTier!.Options.ExitNode);
        Assert.False(original.HasExited);
        Assert.Equal(1, session.EasyTierStarts);

        ready.SetResult();
        await switching.WaitAsync(Deadline);
        Assert.True(original.HasExited);
        Assert.Equal(EasyTierArgs.HostIp, session.EasyTier!.Options.ExitNode);
        Assert.Equal(2, session.EasyTierStarts);
    }

    [Fact]
    public async Task FailedReadinessCannotLeaveRequestedExitPublished()
    {
        await using var session = NewSession((o, _) => o.ExitNode is null
            ? Task.FromResult<IEasyTierSession>(new FakeEasyTier(o))
            : Task.FromException<IEasyTierSession>(new IOException("route acknowledgement failed")));
        await session.StartEasyTierAsync(Original, default);
        var original = session.EasyTier!;
        await Assert.ThrowsAsync<IOException>(() => session.SetExitNodeAsync(EasyTierArgs.HostIp));
        Assert.Null(session.EasyTier);
        Assert.True(original.HasExited);
        Assert.Equal(1, session.EasyTierStarts);
    }

    [Fact]
    public async Task RejectedMixedUnderlayPlanPreservesWorkingSession()
    {
        await using var session = NewSession((o, _) => Task.FromResult<IEasyTierSession>(new FakeEasyTier(o)));
        await session.StartEasyTierAsync(Original with { Peers = ["udp://127.0.0.1:1", "udp://192.0.2.20:11010"] }, default);
        var original = session.EasyTier!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.SetExitNodeAsync(EasyTierArgs.HostIp));
        Assert.Same(original, session.EasyTier);
        Assert.False(original.HasExited);
        Assert.Null(original.Options.ExitNode);
        Assert.Equal(1, session.EasyTierStarts);
    }

    [Fact]
    public async Task StopQueuedBehindEnableUsesTheAcknowledgedConfiguration()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = NewSession(async (o, ct) =>
        {
            if (o.ExitNode is not null)
            {
                entered.TrySetResult();
                await ready.Task.WaitAsync(ct);
            }
            return new FakeEasyTier(o);
        });
        await session.StartEasyTierAsync(Original, default);
        var enable = session.SetExitNodeAsync(EasyTierArgs.HostIp);
        await entered.Task.WaitAsync(Deadline);
        var stop = session.SetExitNodeAsync(null);
        Assert.False(stop.IsCompleted);
        ready.SetResult();
        await Task.WhenAll(enable, stop).WaitAsync(Deadline);
        Assert.Null(session.EasyTier!.Options.ExitNode);
        Assert.False(session.EasyTier.HasExited);
        Assert.Equal(3, session.EasyTierStarts);
    }

    [Fact]
    public async Task DisposedDirectOwnerStopsALateExitAndDoesNotReportSuccess()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeEasyTier? late = null;
        await using var session = NewSession(async (o, _) =>
        {
            if (o.ExitNode is not null)
            {
                entered.TrySetResult();
                await ready.Task; // 模拟底层无法及时响应取消。
                return late = new FakeEasyTier(o);
            }
            return new FakeEasyTier(o);
        });
        await session.StartEasyTierAsync(Original, default);
        var enable = session.SetExitNodeAsync(EasyTierArgs.HostIp);
        await entered.Task.WaitAsync(Deadline);
        await session.DisposeAsync();
        ready.SetResult();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => enable.WaitAsync(Deadline));
        Assert.True(late!.HasExited);
    }

    [Fact]
    public void UnderlayIncludesChangedPeerAddressesAndNormalizesMappedIpv4()
    {
        var result = DirectSession.CollectDirectUnderlayAddresses(
            [IPAddress.Parse("198.51.100.20"), IPAddress.Parse("2001:db8::1"), IPAddress.Parse("fe80::2")],
            [IPAddress.Parse("::ffff:198.51.100.20"), IPAddress.Parse("203.0.113.30"), IPAddress.Parse("2001:db8::9"), IPAddress.Parse("fe80::3")]);
        // IPv6 only from peers actually connected: a listed candidate that never answered (2001:db8::1) gets no exception.
        // A connected link-local peer is kept so the plan refuses it with a reason.
        Assert.Equal(["198.51.100.20", "2001:db8::9", "203.0.113.30", "fe80::3"], result);
    }

    [Fact]
    public void ViewModelKeepsDirectOwnerAndSynchronizesExitOptions() => UiTestThread.Run(() =>
    {
        // 界面上的“经别人上网”只在 Windows 上开放（Mac/Linux 的完整局域网模式不带出口）。
        if (!PlatformFeatures.FullTrafficExit) return;
        using var service = new LauncherService(new AppSettings { GameDirectory = TestDataDir.Path, PlayerName = "" });
        var session = NewSession((o, _) => Task.FromResult<IEasyTierSession>(new FakeEasyTier(o)));
        using var lifetime = new CancellationTokenSource();
        var owner = new Owner();
        try
        {
            session.StartEasyTierAsync(Original, default).GetAwaiter().GetResult();
            var vm = new MultiplayerViewModel(service) { SelectedExitPeer = new PeerItem(new PeerInfo("amber-host", EasyTierArgs.HostIp, "p2p", 1, "udp")) };
            SetField(vm, "_direct", session);
            SetField(vm, "_session", session.EasyTier);
            SetField(vm, "_sessionCts", lifetime);
            service.ActiveMultiplayer = owner;

            vm.UseExitNodeCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Assert.Same(owner, service.ActiveMultiplayer);
            Assert.Same(session.EasyTier, GetField(vm, "_session"));
            Assert.Same(session.EasyTier!.Options, GetField(vm, "_currentOptions"));
            Assert.Equal(EasyTierArgs.HostIp, vm.ExitNodeIp);

            vm.StopExitNodeCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Assert.Same(owner, service.ActiveMultiplayer);
            Assert.Same(session.EasyTier, GetField(vm, "_session"));
            Assert.Null(session.EasyTier!.Options.ExitNode);
            Assert.Equal("", vm.ExitNodeIp);
        }
        finally { session.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    });

    private static void SetField(object instance, string name, object value) =>
        instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(instance, value);

    private static object? GetField(object instance, string name) =>
        instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance);
}
