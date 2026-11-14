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
}
