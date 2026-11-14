using Amber.App.Services;
using Amber.App.ViewModels;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Public;
using System.Reflection;

namespace Amber.Core.Tests;

/// <summary>The public network's EasyTier ending on its own is noticed: the session says so once and the page disconnects.</summary>
public sealed class PublicNetworkExitTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jay-public-exit-" + Guid.NewGuid().ToString("N"));

    public PublicNetworkExitTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>A stand-in for easytier-core: a shell script that ignores its arguments.</summary>
    private string FakeCore(string name, string body)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private PublicNetworkSession Start(string core) =>
        PublicNetworkSession.Start(core, core, new PublicNetworkOptions(PublicNetworkProfile.Jay([]), EasyTierArgs.FreeTcpPort(), EasyTierArgs.FreeTcpPort()),
            Path.Combine(_root, "work-" + Guid.NewGuid().ToString("N")[..8]));

    [Fact]
    public void AComponentThatEndsOnItsOwnRaisesExitedOnce()
    {
        if (OperatingSystem.IsWindows()) return;
        using var session = Start(FakeCore("quits", "echo bye\nexit 3"));
        var raised = 0;
        using var once = new ManualResetEventSlim();
        session.Exited += () =>
        {
            Interlocked.Increment(ref raised);
            once.Set();
        };
        // It may have ended before the handler above was attached: then HasExited says so.
        Assert.True(once.Wait(TimeSpan.FromSeconds(15)) || session.HasExited);
        Thread.Sleep(200);
        Assert.True(raised <= 1);
        Assert.True(session.HasExited);
    }

    [Fact]
    public void DisposingTheSessionIsNotAnExit()
    {
        if (OperatingSystem.IsWindows()) return;
        var session = Start(FakeCore("stays", "sleep 30"));
        var raised = 0;
        session.Exited += () => Interlocked.Increment(ref raised);
        Assert.False(session.HasExited);
        session.Dispose();
        Thread.Sleep(300);
        Assert.Equal(0, raised);
    }

    [Fact]
    public void ThePageDisconnectsWhenThePublicNetworkEnds()
    {
        if (OperatingSystem.IsWindows()) return;
        UiTestThread.Run(() =>
        {
            using var service = new LauncherService(new AppSettings { GameDirectory = TestDataDir.Path, PlayerName = "" });
            var vm = new MultiplayerViewModel(service);
            var session = Start(FakeCore("ended", "exit 1"));
            typeof(MultiplayerViewModel).GetField("_public", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(vm, session);
            vm.IsPublicConnected = true;

            vm.OnPublicExited(session);
            Assert.False(vm.IsPublicConnected);
            Assert.Equal(Amber.Core.Localization.StatusKind.Warning, vm.StatusInfo?.Kind);
            // A session that is no longer the current one changes nothing.
            vm.IsPublicConnected = true;
            vm.OnPublicExited(session);
            Assert.True(vm.IsPublicConnected);
        });
    }
}
