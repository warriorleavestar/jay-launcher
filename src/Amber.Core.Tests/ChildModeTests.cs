using Amber.Core.Safety;

namespace Amber.Core.Tests;

public class ChildModeTests
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 2, 20, 14, 0, TimeSpan.Zero);

    [Fact]
    public void OffMeansNoEffects()
    {
        var s = new ChildModeSettings();
        Assert.Equal(ChildModeEffects.None, s.Effects);
        Assert.True(s.Effects.AcceptsFileFrom(isFriend: false));
        Assert.True(s.Effects.MayAutoAdmit(autoApprove: true));
    }

    [Fact]
    public void MainSwitchTurnsEveryItemOnAndRecordsTime()
    {
        var s = new ChildModeSettings { NoRemoteControl = false, QuietNotifications = false };
        s.SetEnabled(true, T0);
        Assert.All(Enum.GetValues<ChildModeItem>(), i => Assert.True(s.Get(i)));
        Assert.Equal(T0, s.ChangedAt);
        var e = s.Effects;
        Assert.True(e.HideRemoteControl && e.InviteOnly && e.HideNetworkExit && e.FriendsFilesOnly && e.AlwaysAskKnock && e.QuietNotifications);
    }

    [Theory]
    [InlineData(ChildModeItem.NoRemoteControl)]
    [InlineData(ChildModeItem.NoPublicNetworks)]
    [InlineData(ChildModeItem.NoNetworkExit)]
    [InlineData(ChildModeItem.FriendsFilesOnly)]
    [InlineData(ChildModeItem.AlwaysAskKnock)]
    [InlineData(ChildModeItem.QuietNotifications)]
    public void EachItemCanBeTurnedOffAlone(ChildModeItem item)
    {
        var s = new ChildModeSettings();
        s.SetEnabled(true, T0);
        s.Set(item, false, T0.AddHours(1));
        Assert.Equal(T0.AddHours(1), s.ChangedAt);
        foreach (var other in Enum.GetValues<ChildModeItem>()) Assert.Equal(other != item, s.Get(other));
        var e = s.Effects;
        Assert.Equal(item != ChildModeItem.NoRemoteControl, e.HideRemoteControl);
        Assert.Equal(item != ChildModeItem.NoPublicNetworks, e.InviteOnly);
        Assert.Equal(item != ChildModeItem.NoNetworkExit, e.HideNetworkExit);
        Assert.Equal(item != ChildModeItem.FriendsFilesOnly, e.FriendsFilesOnly);
        Assert.Equal(item != ChildModeItem.AlwaysAskKnock, e.AlwaysAskKnock);
        Assert.Equal(item != ChildModeItem.QuietNotifications, e.QuietNotifications);
    }

    [Fact]
    public void FilesAndKnocksFollowTheRules()
    {
        var s = new ChildModeSettings();
        s.SetEnabled(true, T0);
        Assert.False(s.Effects.AcceptsFileFrom(isFriend: false));
        Assert.True(s.Effects.AcceptsFileFrom(isFriend: true));
        Assert.False(s.Effects.MayAutoAdmit(autoApprove: true));
        s.Set(ChildModeItem.AlwaysAskKnock, false, T0);
        Assert.True(s.Effects.MayAutoAdmit(autoApprove: true));
        Assert.False(s.Effects.MayAutoAdmit(autoApprove: false));
    }

    [Fact]
    public void SameValueKeepsTheTime()
    {
        var s = new ChildModeSettings();
        s.SetEnabled(true, T0);
        s.Set(ChildModeItem.NoRemoteControl, true, T0.AddDays(1));
        s.SetEnabled(true, T0.AddDays(2));
        Assert.Equal(T0, s.ChangedAt);
    }

    [Fact]
    public void OffKeepsItemsButStopsEffects()
    {
        var s = new ChildModeSettings();
        s.SetEnabled(true, T0);
        s.Set(ChildModeItem.NoNetworkExit, false, T0);
        s.SetEnabled(false, T0.AddMinutes(5));
        Assert.Equal(ChildModeEffects.None, s.Effects);
        Assert.Equal(T0.AddMinutes(5), s.ChangedAt);
    }
}
