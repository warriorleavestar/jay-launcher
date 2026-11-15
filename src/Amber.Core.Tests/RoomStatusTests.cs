using System.Reflection;
using Amber.App.Services;
using Amber.App.ViewModels;
using Amber.Core.Multiplayer;

namespace Amber.Core.Tests;

public sealed class RoomStatusTests
{
    [Fact]
    public void TheRoomCountIncludesTheHostLikeTheHeader()
    {
        Assert.Equal("multiplayer.room.waiting", MultiplayerViewModel.RoomCountStatus(1).Key);
        var two = MultiplayerViewModel.RoomCountStatus(2);
        Assert.Equal("multiplayer.room.player_count", two.Key);
        Assert.Equal(2, two.Count);
        Assert.Equal(2, two.Args[0]);
    }

    [Fact]
    public void ExitChoicesShowTheLatestLatencyAndKeepTheChoice() => UiTestThread.Run(() =>
    {
        using var service = new LauncherService(new AppSettings { GameDirectory = TestDataDir.Path, PlayerName = "" });
        var vm = new MultiplayerViewModel(service);
        var host = "10.233.144.1";
        vm.SyncExitCandidates([new PeerItem(new PeerInfo("amber-host", host, "Relayed", 1000, null)), new PeerItem(new PeerInfo("amber-guest", "10.233.144.9", "Relayed", 900, null))]);
        vm.SelectedExitPeer = vm.ExitCandidates[0];
        // Same people, newer numbers (a direct path was found): the entries follow, the choice stays on the host.
        vm.SyncExitCandidates([new PeerItem(new PeerInfo("amber-host", host, "Direct", 36, null)), new PeerItem(new PeerInfo("amber-guest", "10.233.144.9", "Relayed", 880, null))]);
        Assert.Equal(36, vm.ExitCandidates[0].Peer.LatencyMs);
        Assert.Equal("Direct", vm.ExitCandidates[0].Peer.Cost);
        Assert.Equal(host, vm.SelectedExitPeer?.Peer.Ip);
        Assert.Same(vm.ExitCandidates[0], vm.SelectedExitPeer);
    });
    [Fact]
    public void ARoomStatusThePlayerClosedIsNotSetAgainUntilItChanges() => UiTestThread.Run(() =>
    {
        using var service = new LauncherService(new AppSettings { GameDirectory = TestDataDir.Path, PlayerName = "" });
        var vm = new MultiplayerViewModel(service);
        var changes = 0;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.StatusInfo)) changes++; };

        // The refresh sets the same status every two seconds: only the first is a change, so a closed bar stays closed.
        Call(vm, "SetRoomStatus", MultiplayerViewModel.RoomCountStatus(2));
        Call(vm, "SetRoomStatus", MultiplayerViewModel.RoomCountStatus(2));
        Assert.Equal(1, changes);
        // Someone joins: that is new, and shown.
        Call(vm, "SetRoomStatus", MultiplayerViewModel.RoomCountStatus(3));
        Assert.Equal(2, changes);
        Assert.Equal(3, vm.StatusInfo!.Count);
    });

    [Fact]
    public void OurOwnJoinIsNotShownAsSomeoneJoining() => UiTestThread.Run(() =>
    {
        using var service = new LauncherService(new AppSettings { GameDirectory = TestDataDir.Path, PlayerName = "" });
        var vm = new MultiplayerViewModel(service);
        var client = new ChatClient(RoomCode.Create(25565), "Me");
        try
        {
            Call(vm, "OnChatMessage", client, new ChatMessage(ChatKind.Join, client.SenderId, "Me", "", DateTimeOffset.UtcNow));
            Assert.Empty(vm.ChatLines);
            Call(vm, "OnChatMessage", client, new ChatMessage(ChatKind.Join, "someone-else", "Alex", "", DateTimeOffset.UtcNow));
            Assert.Equal("Alex", Assert.Single(vm.ChatLines).Message.Name);
        }
        finally { client.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    });

    private sealed class FakeEasyTier(EasyTierOptions options) : IEasyTierSession
    {
        public EasyTierOptions Options { get; } = options;
        public bool HasExited { get; private set; }
        public IReadOnlyList<string> RecentOutput => [];
        public Task<List<PeerInfo>> PeersAsync(CancellationToken ct = default) => Task.FromResult(new List<PeerInfo>());
        public void Dispose() => HasExited = true;
    }

    [Fact]
    public void ASwitchThatFailsBeforeAnythingChangesKeepsTheRoom() => UiTestThread.Run(() =>
    {
        using var service = new LauncherService(new AppSettings { GameDirectory = TestDataDir.Path, PlayerName = "" });
        var vm = new MultiplayerViewModel(service);
        var session = new FakeEasyTier(new EasyTierOptions(RoomCode.Create(25565), false, ["udp://127.0.0.1:1"], 1, Tun: true));
        using var lifetime = new CancellationTokenSource();
        SetField(vm, "_session", session);
        SetField(vm, "_sessionCts", lifetime);

        // Not a room address: the plan is refused before the running session is touched.
        var task = (Task<bool>)Call(vm, "RestartWithExitAsync", "192.0.2.5")!;
        Assert.False(task.GetAwaiter().GetResult());
        Assert.False(session.HasExited);
        Assert.Same(session, GetField(vm, "_session"));
        Assert.Same(lifetime, GetField(vm, "_sessionCts"));
        Assert.Equal("", vm.ExitNodeIp);
        Assert.Equal("multiplayer.exit.switch_failed_kept", vm.StatusInfo?.Key);
    });

    private static object? Call(object instance, string name, params object?[] args) =>
        instance.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(instance, args);

    private static void SetField(object instance, string name, object value) =>
        instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(instance, value);

    private static object? GetField(object instance, string name) =>
        instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance);
}
