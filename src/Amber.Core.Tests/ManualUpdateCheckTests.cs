using Amber.App.ViewModels;
using Amber.Core.Launch;
using Amber.Core.Localization;

namespace Amber.Core.Tests;

/// <summary>Settings › About › Check for updates: what it says and whether it offers a page.</summary>
public sealed class ManualUpdateCheckTests
{
    private static VerifiedUpdate Release(string? page, ReleaseVerdict verdict = ReleaseVerdict.Valid) =>
        new(new UpdateInfo("99.0.0", page, null), new ReleaseCheck(verdict, "v99.0.0", 5), new Dictionary<string, string>(), new Dictionary<string, Uri>());

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
        Assert.Null(ToolsViewModel.DescribeUpdate(AutoUpdateCheck.OfficialFeed, Release("https://github.com/x", ReleaseVerdict.NotEnoughSignatures)).Page);
        var (status, page) = ToolsViewModel.DescribeUpdate(AutoUpdateCheck.OfficialFeed, null);
        Assert.Null(page);
        Assert.Equal(L.F("tools.update.latest", UpdateChecker.CurrentVersion), status);
    }
}
