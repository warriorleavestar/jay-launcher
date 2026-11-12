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
