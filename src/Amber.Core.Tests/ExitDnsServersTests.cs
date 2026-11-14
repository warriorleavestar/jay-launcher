#pragma warning disable CA1416

using Amber.Core.Multiplayer;

namespace Amber.Core.Tests;

/// <summary>Exit mode's DNS servers come from one place, and the UI says that DNS goes through the member too.</summary>
public sealed class ExitDnsServersTests
{
    [Fact]
    public void EveryPlatformUsesTheSharedServerList()
    {
        Assert.Equal(2, ExitDnsServers.All.Length);
        Assert.Equal(ExitDnsServers.Key, string.Join(',', ExitDnsServers.All));
        // Windows: the script compares against and sets exactly the shared list.
        Assert.Contains($"'{ExitDnsServers.Key}'", ExitDnsScript.Script, StringComparison.Ordinal);
        Assert.Contains($"@('{ExitDnsServers.Primary}', '{ExitDnsServers.Secondary}')", ExitDnsScript.Script, StringComparison.Ordinal);
        Assert.DoesNotContain("{{", ExitDnsScript.Script, StringComparison.Ordinal);
        if (!OperatingSystem.IsWindows()) Assert.Equal(ExitDnsServers.All, UnixExitNetwork.DnsServers);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ja")]
    [InlineData("zh-Hans")]
    [InlineData("zh-Hant")]
    [InlineData("lzh")]
    public void ExitTooltipMentionsTheDnsSwitch(string language)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Amber.Core", "Localization", language + ".json");
        var strings = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))!;
        var tooltip = strings["multiplayer.exit.start_tooltip"];
        Assert.Contains("DNS", tooltip, StringComparison.Ordinal);
        Assert.Contains("Cloudflare", tooltip, StringComparison.Ordinal);
        Assert.Contains("Google", tooltip, StringComparison.Ordinal);
    }
}
