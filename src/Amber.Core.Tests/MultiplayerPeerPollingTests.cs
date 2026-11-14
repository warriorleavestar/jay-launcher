using Amber.App.ViewModels;

namespace Amber.Core.Tests;

/// <summary>The room page reads the member list (one easytier-cli process each time) less often once nothing changes.</summary>
public sealed class MultiplayerPeerPollingTests
{
    [Fact]
    public void ReadsEveryRefreshWhileThingsChangeAndEveryThirdOnceSteady()
    {
        // No list yet, or the list changed recently: read it.
        Assert.True(MultiplayerViewModel.ShouldQueryPeers(false, TimeSpan.FromMinutes(5), 0));
        Assert.True(MultiplayerViewModel.ShouldQueryPeers(true, TimeSpan.FromSeconds(10), 0));
        // Steady for 30 s: two refreshes reuse it, the third reads again.
        Assert.False(MultiplayerViewModel.ShouldQueryPeers(true, TimeSpan.FromSeconds(31), 0));
        Assert.False(MultiplayerViewModel.ShouldQueryPeers(true, TimeSpan.FromSeconds(33), 1));
        Assert.True(MultiplayerViewModel.ShouldQueryPeers(true, TimeSpan.FromSeconds(35), 2));
    }

    [Fact]
    public void TheWholeLanPermissionPromptIsNotWaitedForOnTheUiThread()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Amber.App", "ViewModels");
        var source = File.ReadAllText(Path.Combine(dir, "MultiplayerViewModel.cs"));
        // Starting or switching a whole-LAN session waits for the system's permission prompt: it runs off the UI thread.
        foreach (var line in source.Split('\n').Where(l => l.Contains("EasyTierElevatedSession.Start(", StringComparison.Ordinal)))
            Assert.Contains("Task.Run(", line, StringComparison.Ordinal);
        Assert.Contains("multiplayer.room.waiting_permission", source, StringComparison.Ordinal);
    }
}
