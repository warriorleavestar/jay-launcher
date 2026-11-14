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
        // The source folder, found by walking up from the test output (its depth depends on the build's runtime folder).
        var dir = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(dir, "Amber.App", "ViewModels"))) dir = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(dir))!;
        dir = Path.Combine(dir, "Amber.App", "ViewModels");
        // Every partial of the room page, not just the main file: rekeys and switches live in the others.
        var files = Directory.GetFiles(dir, "MultiplayerViewModel*.cs");
        Assert.Contains(files, f => Path.GetFileName(f) == "MultiplayerViewModel.Invite.cs");
        var source = string.Join("\n", files.Select(File.ReadAllText));
        // Starting or switching a whole-LAN session waits for the system's permission prompt: never with the blocking
        // start, and always with a token, so leaving the room closes the prompt.
        Assert.DoesNotContain("EasyTierElevatedSession.Start(", source, StringComparison.Ordinal);
        var starts = System.Text.RegularExpressions.Regex.Matches(source, @"EasyTierElevatedSession\.StartAsync\(([^;]*)\);");
        Assert.NotEmpty(starts);
        foreach (System.Text.RegularExpressions.Match start in starts)
            Assert.Matches(@"[Tt]oken$", start.Groups[1].Value.Trim());
        Assert.DoesNotContain("bool SwitchNetwork(", source, StringComparison.Ordinal);
        // A live whole-LAN session changes network through its guard, without a new prompt.
        var invite = File.ReadAllText(Path.Combine(dir, "MultiplayerViewModel.Invite.cs"));
        var switchNetwork = invite[invite.IndexOf("Task<bool> SwitchNetworkAsync(", StringComparison.Ordinal)..];
        switchNetwork = switchNetwork[..switchNetwork.IndexOf("private async Task SwitchDirectRoomAsync(", StringComparison.Ordinal)];
        Assert.Contains(".RestartAsync(options, token)", switchNetwork, StringComparison.Ordinal);
        Assert.Contains("multiplayer.room.waiting_permission", source, StringComparison.Ordinal);
    }
}
