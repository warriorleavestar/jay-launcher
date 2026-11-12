using Amber.Core.Launch;

namespace Amber.Core.Tests;

/// <summary>The automatic update check: which feed, when it is due, and not repeating a dismissed version.</summary>
public sealed class AutoUpdateCheckTests
{
    [Fact]
    public void UsesTheOfficialFeedUnlessTheUserSetOne()
    {
        Assert.Equal(AutoUpdateCheck.OfficialFeed, AutoUpdateCheck.FeedFor(null));
        Assert.Equal(AutoUpdateCheck.OfficialFeed, AutoUpdateCheck.FeedFor("  "));
        Assert.Equal("https://mirror.example.test/latest", AutoUpdateCheck.FeedFor(" https://mirror.example.test/latest "));
        Assert.StartsWith("https://", AutoUpdateCheck.OfficialFeed);
    }

    private static VerifiedUpdate Signed(string? page, string tag = "v1.2.3", ReleaseVerdict verdict = ReleaseVerdict.Valid) =>
        new(new UpdateInfo(tag.TrimStart('v'), page, null), new ReleaseCheck(verdict, tag, 5), new Dictionary<string, string>(), new Dictionary<string, Uri>());

    [Fact]
    public void TheOfficialPageComesFromTheVerifiedTagNotTheFeed()
    {
        Assert.Equal(AutoUpdateCheck.OfficialReleasePages + "v1.2.3",
            AutoUpdateCheck.ReleasePage(AutoUpdateCheck.OfficialFeed, Signed("https://phishing.example.test/jay")));
        Assert.Equal(AutoUpdateCheck.OfficialReleasePages + "v1.2.3", AutoUpdateCheck.ReleasePage(AutoUpdateCheck.OfficialFeed, Signed(null)));
        Assert.Null(AutoUpdateCheck.ReleasePage(AutoUpdateCheck.OfficialFeed, Signed(null, verdict: ReleaseVerdict.NotEnoughSignatures)));
    }

    [Theory]
    [InlineData("https://api.github.com/repos/warriorleavestar/jay-launcher/releases/latest", true)]
    [InlineData("https://API.github.com/repos/warriorleavestar/jay-launcher/releases/latest/", true)]
    [InlineData(" https://api.github.com/repos/warriorleavestar/jay-launcher/releases/latest ", true)]
    [InlineData("http://api.github.com/repos/warriorleavestar/jay-launcher/releases/latest", false)]
    [InlineData("https://api.github.com/repos/someone/jay-launcher/releases/latest", false)]
    [InlineData("https://api.github.com/repos/warriorleavestar/jay-launcher/releases/latest?x=1", false)]
    [InlineData("abc", false)]
    public void RecognisesTheOfficialFeedTypedByHand(string feed, bool official) => Assert.Equal(official, AutoUpdateCheck.IsOfficial(feed));

    [Fact]
    public void AnOwnFeedsPageMustBeHttpsOnTheFeedsHost()
    {
        const string feed = "https://mirror.example.test/jay/latest.json";
        Assert.Equal("https://mirror.example.test/jay/v1.2.3", AutoUpdateCheck.ReleasePage(feed, Signed("https://mirror.example.test/jay/v1.2.3")));
        Assert.Null(AutoUpdateCheck.ReleasePage(feed, Signed("https://other.example.test/jay/v1.2.3")));
        Assert.Null(AutoUpdateCheck.ReleasePage(feed, Signed("http://mirror.example.test/jay/v1.2.3")));
        Assert.Null(AutoUpdateCheck.ReleasePage(feed, Signed("https://mirror.example.test.evil.test/jay")));
        Assert.Null(AutoUpdateCheck.ReleasePage(feed, Signed(null)));
    }

    [Fact]
    public void ChecksOnceADay()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.True(AutoUpdateCheck.IsDue(null, now));
        Assert.False(AutoUpdateCheck.IsDue(now.AddHours(-23), now));
        Assert.True(AutoUpdateCheck.IsDue(now.AddHours(-24), now));
        Assert.True(AutoUpdateCheck.IsDue(now.AddDays(3), now)); // a clock that went backwards doesn't block checks forever
    }

    [Fact]
    public void ADismissedVersionIsNotShownAgain()
    {
        Assert.True(AutoUpdateCheck.ShouldNotify("1.0.0", null));
        Assert.False(AutoUpdateCheck.ShouldNotify("1.0.0", "1.0.0"));
        Assert.True(AutoUpdateCheck.ShouldNotify("1.0.1", "1.0.0"));
    }
}
