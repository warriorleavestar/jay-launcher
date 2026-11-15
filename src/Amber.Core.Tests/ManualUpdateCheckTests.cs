using Amber.App.ViewModels;
using Amber.Core.Launch;
using Amber.Core.Localization;

namespace Amber.Core.Tests;

/// <summary>Settings › About › Check for updates: what it says and whether it offers a page.</summary>
public sealed class ManualUpdateCheckTests
{
    private static VerifiedUpdate Release(string? page, ReleaseVerdict verdict = ReleaseVerdict.Valid) =>
        new(new UpdateInfo("99.0.0", page, null), new ReleaseCheck(verdict, "v99.0.0", 5), new Dictionary<string, string>(), new Dictionary<string, Uri>());

    [Theory]
    [InlineData(true, true, null, "about.net.update.auto")]
    [InlineData(true, false, null, "about.net.update.manual")]
    [InlineData(true, true, "https://mirror.example.test/latest", "about.net.update.auto")]
    [InlineData(false, true, null, "about.net.update.never")]
    [InlineData(false, true, "  ", "about.net.update.never")]
    [InlineData(false, true, "https://mirror.example.test/latest", "about.net.update.manual")]
    [InlineData(false, false, "https://mirror.example.test/latest", "about.net.update.manual")]
    public void TheNetworkListSaysWhatTheUpdateCheckReallyDoes(bool supported, bool autoSwitch, string? feed, string key) =>
        Assert.Equal(key, NetworkList.UpdateStatusKey(supported, autoSwitch, feed));

    [Fact]
    public void APhoneWithoutAnOwnAddressAsksNobody()
    {
        Assert.Null(AutoUpdateCheck.ManualFeed(null, supported: false));
        Assert.Equal("https://mirror.example.test/latest", AutoUpdateCheck.ManualFeed("https://mirror.example.test/latest", supported: false));
        Assert.Equal(AutoUpdateCheck.OfficialFeed, AutoUpdateCheck.ManualFeed(" ", supported: true));
    }

    [Fact]
    public void AnOwnFeedWhosePageMovedHostGetsNoButtonAndSaysWhy()
    {
        var (status, page) = ToolsViewModel.DescribeUpdate("https://mirror.example.test/jay/latest.json", Release("https://other.example.test/jay/v99.0.0"));
        Assert.Null(page);
        Assert.StartsWith(L.F("tools.update.available", "99.0.0", UpdateChecker.CurrentVersion), status);
        Assert.EndsWith(L.T("tools.update.no_page"), status);
    }

    [Fact]
    public void AnOwnFeedsPageOnItsHostIsOffered()
    {
        var (status, page) = ToolsViewModel.DescribeUpdate("https://mirror.example.test/jay/latest.json", Release("https://mirror.example.test/jay/v99.0.0"));
        Assert.Equal("https://mirror.example.test/jay/v99.0.0", page);
        Assert.DoesNotContain(L.T("tools.update.no_page"), status);
    }

    [Fact]
    public void TheOfficialFeedOffersThePageOfTheSignedTag()
    {
        var (_, page) = ToolsViewModel.DescribeUpdate(AutoUpdateCheck.OfficialFeed, Release("https://phishing.example.test/jay"));
        Assert.Equal(AutoUpdateCheck.OfficialReleasePages + "v99.0.0", page);
    }

    [Fact]
    public void AnUnverifiedReleaseOrNoneHasNoButton()
    {
        var unverified = ToolsViewModel.DescribeUpdate(AutoUpdateCheck.OfficialFeed, Release("https://github.com/x", ReleaseVerdict.NotEnoughSignatures));
        Assert.Null(unverified.Page);
        Assert.StartsWith(L.F("tools.update.unverified", "99.0.0", ""), unverified.Status);
        var (status, page) = ToolsViewModel.DescribeUpdate(AutoUpdateCheck.OfficialFeed, null);
        Assert.Null(page);
        Assert.Equal(L.F("tools.update.latest", UpdateChecker.CurrentVersion), status);
    }
}
