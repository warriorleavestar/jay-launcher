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
        vm.IsComponentReady = true;
        Assert.False(vm.StartsGameToHost);
        Assert.Equal(L.T("multiplayer.lobby.host_button"), vm.HostButtonText);

        var started = new List<string>();
        vm.StartInstance = started.Add;
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

    /// <summary>Only the game started for the wait opens the room, and the wait ends when that game's session does.</summary>
    [Fact]
    public void OnlyTheStartedGameOpensTheRoomAndItsEndStopsTheWait() => UiTestThread.Run(() =>
    {
        using var service = new LauncherService(new AppSettings { GameDirectory = TestDataDir.Path, PlayerName = "" });
        var vm = new MultiplayerViewModel(service);
        vm.Instances.Clear();
        vm.Instances.Add("fabric-1.20.1");
        vm.IsComponentReady = true;
        vm.StartInstance = _ => { };
        vm.HostFromLobbyCommand.Execute(null);
        Assert.True(vm.IsWaitingForLan);

        // Another instance opening its world to LAN is listed, but opens no room and does not end the wait.
        vm.GameOutputForTest("vanilla-1.21", "[Server thread/INFO]: Started serving on 51235");
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.IsWaitingForLan);
        Assert.True(vm.IsIdle);
        Assert.Equal(51235, vm.SelectedLanGame?.Game.Port);

        // A session of another instance ending changes nothing; the waited game's session ending ends the wait.
        var other = new Amber.Core.Launch.LaunchSession("vanilla-1.21", DateTimeOffset.Now);
        vm.WatchGameSession(other);
        other.Cancel(DateTimeOffset.Now);
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.IsWaitingForLan);
        var game = new Amber.Core.Launch.LaunchSession("fabric-1.20.1", DateTimeOffset.Now);
        vm.WatchGameSession(game);
        game.ProcessUp(DateTimeOffset.Now);
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.IsWaitingForLan);
        game.ProcessEnded(DateTimeOffset.Now, 0, crashed: false);
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.IsWaitingForLan);

        // A later "Open to LAN" from that instance no longer opens a room by itself.
        vm.GameOutputForTest("fabric-1.20.1", "[Server thread/INFO]: Started serving on 51236");
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.IsIdle);
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

        // Without the component the button is the install step: it installs and stops there.
        var installs = 0;
        vm.TestInstallComponent = () =>
        {
            installs++;
            vm.IsComponentReady = true;
            return Task.CompletedTask;
        };
        vm.IsComponentReady = false;
        Assert.Equal(vm.ComponentStepText, vm.HostButtonText);
        vm.HostFromLobbyCommand.Execute(null);
        Assert.Equal(1, installs);
        Assert.Empty(started);
        Assert.False(vm.IsWaitingForLan);

        // The same button then starts the game.
        Assert.Equal(L.T("multiplayer.lobby.play_and_host"), vm.HostButtonText);
        vm.HostFromLobbyCommand.Execute(null);
        Assert.Equal(1, installs);
        Assert.True(vm.IsWaitingForLan);
        vm.CancelWaitForLanCommand.Execute(null);
        Assert.False(vm.IsWaitingForLan);

        // A LAN world after cancelling is picked up but doesn't open a room by itself.
        vm.GameOutputForTest("1.21.4", "Started serving on 40000");
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.IsIdle);
    });
}
