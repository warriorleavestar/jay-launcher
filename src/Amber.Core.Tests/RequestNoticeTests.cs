using Amber.Core.Activity;
using Amber.Core.Localization;
using Amber.Core.Safety;

namespace Amber.Core.Tests;

/// <summary>The notice shown outside Jay's window for a request, with and without "no names or content in notifications".</summary>
public class RequestNoticeTests
{
    private static RequestSpec Knock() => new("knock", "Alex wants to join", "Safety code 12345 67890",
        [new("deny", "Deny", IsDefault: true), new("approve", "Let them in")], IsSafety: true);

    [Fact]
    public void NormallyTheNoticeIsTheRequest()
    {
        var spec = Knock();
        var notice = RequestNotice.For(spec, quiet: false);
        Assert.False(notice.Quiet);
        Assert.Equal(spec.Title, notice.Title);
        Assert.Equal(spec.Body, notice.Body);
        Assert.Equal(spec.Choices, notice.Choices);
    }

    [Fact]
    public void QuietNoticeHasNoNameOrContentAndOnlyTheSafeAnswer()
    {
        var notice = RequestNotice.For(Knock(), quiet: true);
        Assert.True(notice.Quiet);
        Assert.Equal(L.T("activity.quiet.knock"), notice.Title);
        Assert.Equal(L.T("activity.quiet.body"), notice.Body);
        Assert.DoesNotContain("Alex", notice.Title + notice.Body);
        Assert.DoesNotContain("12345", notice.Title + notice.Body);
        var only = Assert.Single(notice.Choices);
        Assert.Equal("deny", only.Id);
    }

    [Theory]
    [InlineData("file", "activity.quiet.file")]
    [InlineData("call", "activity.quiet.call")]
    [InlineData("contact-account", "activity.quiet.other")]
    public void QuietTitleSaysOnlyTheKind(string kind, string key)
    {
        var spec = new RequestSpec(kind, "Bo: holiday.png", "from Bo", [new("ok", "OK"), new("no", "No", IsDefault: true)]);
        var notice = RequestNotice.For(spec, quiet: true);
        Assert.Equal(L.T(key), notice.Title);
        Assert.Equal("no", Assert.Single(notice.Choices).Id);
    }

    [Fact]
    public void ChildModeItemDrivesQuiet()
    {
        var settings = new ChildModeSettings();
        Assert.False(settings.Effects.QuietNotifications);
        settings.SetEnabled(true, DateTimeOffset.UnixEpoch);
        Assert.True(settings.Effects.QuietNotifications);
        settings.Set(ChildModeItem.QuietNotifications, false, DateTimeOffset.UnixEpoch);
        Assert.False(settings.Effects.QuietNotifications);
    }
}
