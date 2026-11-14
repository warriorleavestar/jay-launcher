using Amber.App.Services;
using Amber.App.ViewModels;
using Avalonia.Threading;

namespace Amber.Core.Tests;

/// <summary>Picking another network while one is connecting: the new connect starts only after the old one has stopped.</summary>
public sealed class PublicConnectSwitchTests
{
    [Fact]
    public void ANewConnectWaitsForTheCancelledOneToStop() => UiTestThread.Run(() =>
    {
        using var service = new LauncherService(new AppSettings
        {
            GameDirectory = TestDataDir.Path,
            PlayerName = "",
            PublicNetworkNoticeAccepted = true,
            PublicNetworkNodes = ["tcp://127.0.0.1:1"],
        });
        var vm = new MultiplayerViewModel(service) { IsComponentReady = true };
        Assert.True(vm.HasPublicNodes);

        var running = 0;
        var maxRunning = 0;
        var attempts = 0;
        var firstStopping = new TaskCompletionSource();
        vm.TestPublicAttempt = async ct =>
        {
            var n = ++attempts;
            maxRunning = Math.Max(maxRunning, ++running);
            try
            {
                if (n == 1)
                {
                    // Connecting until cancelled, then slow to shut down.
                    try { await Task.Delay(Timeout.Infinite, ct); }
                    finally { await firstStopping.Task; }
                }
                else await Task.Delay(Timeout.Infinite, ct);
            }
            finally { running--; }
        };

        var first = vm.ConnectPublicCommand.ExecuteAsync(null);
        Pump(() => attempts == 1);
        Assert.True(vm.IsPublicConnecting);

        // The top bar picks another network: the first connect is cancelled, the second must wait for it.
        vm.DisconnectPublicCommand.Execute(null);
        var second = vm.ConnectPublicWhenFreeAsync();
        Pump(() => false, TimeSpan.FromMilliseconds(200));
        Assert.Equal(1, attempts);
        Assert.False(second.IsCompleted);

        firstStopping.SetResult();
        Pump(() => attempts == 2);
        Assert.Equal(2, attempts);
        Assert.Equal(1, maxRunning);
        Assert.True(vm.IsPublicConnecting);
        Assert.True(first.IsCompleted);

        vm.DisconnectPublicCommand.Execute(null);
        Pump(() => !vm.IsPublicConnecting);
        Assert.False(vm.IsPublicConnecting);
    });

    private static void Pump(Func<bool> done, TimeSpan? limit = null)
    {
        var until = DateTime.UtcNow + (limit ?? TimeSpan.FromSeconds(10));
        while (!done() && DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
    }
}
