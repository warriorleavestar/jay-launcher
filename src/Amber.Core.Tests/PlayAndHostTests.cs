using Amber.App.Services;
using Amber.App.ViewModels;
using Amber.Core.Localization;
using Avalonia.Threading;

namespace Amber.Core.Tests;

/// <summary>[▶ Play and open a room] (§6.14, J5a): start the focus instance, wait for Open to LAN, then open the room.</summary>
public sealed class PlayAndHostTests
{
    [Fact]
    public void StartsTheGameThenHostsWhenTheWorldIsOpenedToLan() => UiTestThread.Run(() =>
    {
        using var service = new LauncherService(new AppSettings { GameDirectory = TestDataDir.Path, PlayerName = "" });
        var vm = new MultiplayerViewModel(service);
        vm.Instances.Clear();
        vm.Instances.Add("fabric-1.20.1");
        Assert.False(vm.StartsGameToHost);
        Assert.Equal(L.T("multiplayer.lobby.host_button"), vm.HostButtonText);

        var started = new List<string>();
        vm.StartInstance = started.Add;
        vm.IsComponentReady = true;
        Assert.True(vm.StartsGameToHost);
        Assert.Equal(L.T("multiplayer.lobby.play_and_host"), vm.HostButtonText);
        Assert.Contains("fabric-1.20.1", vm.PlayAndHostHint);

        vm.HostFromLobbyCommand.Execute(null);
        Assert.Equal(["fabric-1.20.1"], started);
        Assert.True(vm.IsWaitingForLan);
        Assert.False(vm.ShowHostButton);
        Assert.Contains("fabric-1.20.1", vm.WaitingForLanText);

        vm.GameOutputForTest("fabric-1.20.1", "[Server thread/INFO]: Started serving on 51234");
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.IsWaitingForLan);
        Assert.Equal(51234, vm.SelectedLanGame?.Game.Port);
        Assert.True(vm.ShowHostButton);
        Assert.Equal(L.T("multiplayer.lobby.host_button"), vm.HostButtonText);
        Assert.Single(started);

        // Opening the room checks the game port with a real status ping. Let that check finish here: on Windows a
        // refused loopback connect takes about two seconds, and a check still running would hold one of the shared
        // server query slots while ServerStatusTests and ServerQueryLifecycleTests count them.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (vm.StatusInfo?.Key != "multiplayer.lan.port_unavailable" && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(10);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.Equal("multiplayer.lan.port_unavailable", vm.StatusInfo?.Key);
    });

    [Fact]
    public void NeedsTheComponentAndCanBeCancelled() => UiTestThread.Run(() =>
    {
        using var service = new LauncherService(new AppSettings { GameDirectory = TestDataDir.Path, PlayerName = "" });
        var vm = new MultiplayerViewModel(service);
        vm.Instances.Clear();
        vm.Instances.Add("1.21.4");
        var started = new List<string>();
        vm.StartInstance = started.Add;

        vm.IsComponentReady = false;
        vm.HostFromLobbyCommand.Execute(null);
        Assert.Empty(started);
        Assert.False(vm.IsWaitingForLan);

        vm.IsComponentReady = true;
        vm.HostFromLobbyCommand.Execute(null);
        Assert.True(vm.IsWaitingForLan);
        vm.CancelWaitForLanCommand.Execute(null);
        Assert.False(vm.IsWaitingForLan);

        // A LAN world after cancelling is picked up but doesn't open a room by itself.
        vm.GameOutputForTest("1.21.4", "Started serving on 40000");
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.IsIdle);
    });
}
