using System.Diagnostics;
using System.Net;
using System.Reflection;
using Amber.App.Services;
using Amber.App.ViewModels;
using Amber.Core.Multiplayer;
using Avalonia.Threading;
using Xunit;

namespace Amber.Core.Tests;

/// <summary>Multiplayer page state that belongs to one room or one session and must not leak into the next.</summary>
public sealed class MultiplayerSessionStateTests
{
    private static (LauncherService Service, MultiplayerViewModel Vm) Create()
    {
        var service = new LauncherService(new AppSettings { GameDirectory = TestDataDir.Path, PlayerName = "" });
        return (service, new MultiplayerViewModel(service));
    }

    private static void SetField(object instance, string name, object? value) =>
        instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(instance, value);

    private static void PumpFor(TimeSpan time)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < time)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// Stopping a direct room resets the direct state right away, not after the old session finished closing: a room
    /// opened while the old one was still closing keeps its own state.
    /// </summary>
    [Fact]
    public void StoppingADirectRoomDoesNotClobberTheNextRoom() => UiTestThread.Run(() =>
    {
        var (service, vm) = Create();
        using var s = service;
        var dir = Path.Combine(Path.GetTempPath(), "jay-stop-direct-" + Guid.NewGuid().ToString("N"));
        var direct = new DirectSession(null!, dir, null, false, new DirectOptions([], false, [], false, false, false, true), new DiagnosticLog());
        // A port mapping that takes a while to remove keeps the old session's dispose running.
        var removing = new TaskCompletionSource();
        SetField(direct, "_gathered", new DirectGatherer.Gathered([], NatKind.Unknown, "",
            new PortMappingResult("test", new IPEndPoint(IPAddress.Loopback, 1), () => removing.Task)));
        SetField(vm, "_direct", direct);
        vm.IsDirect = true;
        vm.DirectHint = "old room";

        vm.StopCommand.Execute(null);
        Assert.False(vm.IsDirect);
        Assert.Equal("", vm.DirectHint);

        // The next room starts before the old session is closed.
        vm.IsDirect = true;
        vm.DirectHint = "new room";
        removing.SetResult();
        PumpFor(TimeSpan.FromMilliseconds(300));
        Assert.True(vm.IsDirect);
        Assert.Equal("new room", vm.DirectHint);
    });

    /// <summary>A public room listing names the Minecraft version (what the room list filters by), not the host's instance name.</summary>
    [Fact]
    public void APublicListingNamesTheGameVersionNotTheInstance() => UiTestThread.Run(() =>
    {
        var root = Path.Combine(Path.GetTempPath(), "jay-listing-game-" + Guid.NewGuid().ToString("N"));
        try
        {
            void Version(string id, string json)
            {
                Directory.CreateDirectory(Path.Combine(root, "versions", id));
                File.WriteAllText(Path.Combine(root, "versions", id, id + ".json"), json);
            }
            Version("1.21.1", """{"id":"1.21.1"}""");
            Version("My world", """{"id":"My world","inheritsFrom":"1.21.1","libraries":[{"name":"net.fabricmc:fabric-loader:0.16.5"}]}""");
            using var service = new LauncherService(new AppSettings { GameDirectory = root, PlayerName = "" });
            var vm = new MultiplayerViewModel(service) { TargetInstance = "My world" };
            Assert.Equal("1.21.1", vm.BuildListing().Game);
            // An instance that can't be read: no version rather than its name.
            vm.TargetInstance = "Gone";
            Assert.Equal("", vm.BuildListing().Game);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    });

    /// <summary>Refreshing the room list rebuilds the visible list once, not once per room.</summary>
    [Fact]
    public void RefillingThePublicRoomsRebuildsTheListOnce() => UiTestThread.Run(() =>
    {
        var (service, vm) = Create();
        using var s = service;
        var rooms = Enumerable.Range(0, 50).Select(i => new Amber.Core.Multiplayer.Public.RoomListing
        {
            Id = "r" + i, Name = "Room " + i, Host = "Host " + i, Mode = Amber.Core.Multiplayer.Public.RoomVisibility.Public,
            HostKey = new byte[32], Game = i % 2 == 0 ? "1.21.1" : "1.20.1", Players = i,
        }).ToList();
        var changes = 0;
        vm.VisibleRooms.CollectionChanged += (_, _) => changes++;
        vm.ReplacePublicRooms(rooms);
        Assert.Equal(50, vm.VisibleRooms.Count);
        Assert.Equal(["", "1.20.1", "1.21.1"], vm.RoomGameChoices.Select(c => c.Game));
        // One rebuild (clear + one add per room), maybe a second from the game choice being set again; never one per room.
        Assert.True(changes <= 2 * (rooms.Count + 1), $"{changes} changes for {rooms.Count} rooms");
    });
}
