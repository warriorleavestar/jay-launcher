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
}
