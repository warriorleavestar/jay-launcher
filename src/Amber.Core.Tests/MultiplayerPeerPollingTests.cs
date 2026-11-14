using Amber.App.ViewModels;

namespace Amber.Core.Tests;

/// <summary>The room page's EasyTier use: the permission prompt off the UI thread, and how often the member list is read.</summary>
public sealed class MultiplayerPeerPollingTests
{
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
