using System.Net;
using Amber.App.ViewModels;
using Amber.Core.Auth;

namespace Amber.Core.Tests;

/// <summary>微软登录失败分类（改版规格 3.5 失败状态表）：每种失败有种类和能点的下一步。</summary>
public class MicrosoftLoginFailureTests
{
    private const string TokenUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/token";
    private const string DeviceUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode";
    private const string ProfileUrl = "https://api.minecraftservices.com/minecraft/profile";
    private const string EntitlementsUrl = "https://api.minecraftservices.com/entitlements/mcstore";

    private static FakeHttp UpToProfile() => new FakeHttp()
        .On(TokenUrl, """{"access_token":"ms","refresh_token":"rt"}""")
        .On("https://user.auth.xboxlive.com/user/authenticate", """{"Token":"xbl","DisplayClaims":{"xui":[{"uhs":"h"}]}}""")
        .On("https://xsts.auth.xboxlive.com/xsts/authorize", """{"Token":"xsts","DisplayClaims":{"xui":[{"uhs":"h","xid":"1"}]}}""")
        .On("https://api.minecraftservices.com/authentication/login_with_xbox", """{"access_token":"mc","expires_in":86400}""");

    private static Task<MicrosoftLoginException> Refresh(FakeHttp fake) =>
        Assert.ThrowsAsync<MicrosoftLoginException>(() => new MicrosoftAuth(new HttpClient(fake), "c")
            .RefreshAsync(new Account { Kind = AccountKind.Microsoft, Name = "x", RefreshToken = "old" }));

    [Theory]
    [InlineData("2148916233", MicrosoftLoginFailure.NoXboxProfile)]
    [InlineData("2148916238", MicrosoftLoginFailure.ChildAccount)]
    [InlineData("2148916235", MicrosoftLoginFailure.RegionOrAge)]
    [InlineData("2148916236", MicrosoftLoginFailure.RegionOrAge)]
    [InlineData("2148916237", MicrosoftLoginFailure.RegionOrAge)]
    [InlineData("2148916227", MicrosoftLoginFailure.Banned)]
    [InlineData("123", MicrosoftLoginFailure.Other)]
    public async Task XstsErrorCodesMapToKinds(string xerr, MicrosoftLoginFailure expected)
    {
        var fake = new FakeHttp()
            .On(TokenUrl, """{"access_token":"ms","refresh_token":"rt"}""")
            .On("https://user.auth.xboxlive.com/user/authenticate", """{"Token":"xbl","DisplayClaims":{"xui":[{"uhs":"h"}]}}""")
            .On("https://xsts.auth.xboxlive.com/xsts/authorize", $$"""{"XErr":{{xerr}}}""", HttpStatusCode.Unauthorized);
        var e = await Refresh(fake);
        Assert.Equal(expected, e.Failure);
        Assert.IsAssignableFrom<InvalidOperationException>(e);
    }

    [Fact]
    public async Task MissingProfileWithEmptyEntitlementsMeansNotPurchased()
    {
        var fake = UpToProfile().On(ProfileUrl, "", HttpStatusCode.NotFound).On(EntitlementsUrl, """{"items":[]}""");
        Assert.Equal(MicrosoftLoginFailure.NoOwnership, (await Refresh(fake)).Failure);
    }

    [Fact]
    public async Task MissingProfileWithEntitlementsMeansNoPlayerName()
    {
        var fake = UpToProfile().On(ProfileUrl, "", HttpStatusCode.NotFound)
            .On(EntitlementsUrl, """{"items":[{"name":"product_minecraft"},{"name":"game_minecraft"}]}""");
        Assert.Equal(MicrosoftLoginFailure.NoProfileName, (await Refresh(fake)).Failure);
    }

    [Fact]
    public async Task MissingProfileWhenEntitlementsFailFallsBackToCombinedMessage()
    {
        var fake = UpToProfile().On(ProfileUrl, "", HttpStatusCode.NotFound).On(EntitlementsUrl, "", HttpStatusCode.InternalServerError);
        var e = await Refresh(fake);
        Assert.Equal(MicrosoftLoginFailure.NoOwnership, e.Failure);
        Assert.Contains("minecraft.net", e.Message);
    }

    [Fact]
    public async Task ExpiredDeviceCodeAndDeclinedAreKinds()
    {
        var code = new DeviceCodeInfo("ABCD", "https://microsoft.com/link", "dc", 1, 60, "");
        var expired = new FakeHttp().On(TokenUrl, """{"error":"expired_token"}""", HttpStatusCode.BadRequest);
        var e = await Assert.ThrowsAsync<MicrosoftLoginException>(() => new MicrosoftAuth(new HttpClient(expired), "c").CompleteDeviceCodeAsync(code));
        Assert.Equal(MicrosoftLoginFailure.CodeExpired, e.Failure);

        var declined = new FakeHttp().On(TokenUrl, """{"error":"authorization_declined"}""", HttpStatusCode.BadRequest);
        e = await Assert.ThrowsAsync<MicrosoftLoginException>(() => new MicrosoftAuth(new HttpClient(declined), "c").CompleteDeviceCodeAsync(code));
        Assert.Equal(MicrosoftLoginFailure.Denied, e.Failure);
    }

    [Fact]
    public async Task VerificationUriCompleteIsReadOnlyWhenGiven()
    {
        var plain = new FakeHttp().On(DeviceUrl, """{"user_code":"A","device_code":"d","verification_uri":"https://microsoft.com/link"}""");
        Assert.Null((await new MicrosoftAuth(new HttpClient(plain), "c").BeginDeviceCodeAsync()).VerificationUriComplete);
        var full = new FakeHttp().On(DeviceUrl, """{"user_code":"A","device_code":"d","verification_uri":"https://microsoft.com/link","verification_uri_complete":"https://microsoft.com/link?otc=A"}""");
        Assert.Equal("https://microsoft.com/link?otc=A", (await new MicrosoftAuth(new HttpClient(full), "c").BeginDeviceCodeAsync()).VerificationUriComplete);
    }

    [Fact]
    public void EveryFailureOffersAButtonAndMostOfferAWayForward()
    {
        foreach (var failure in Enum.GetValues<MicrosoftLoginFailure>())
        {
            var actions = AccountsViewModel.ActionsFor(failure);
            Assert.NotEmpty(actions);
            Assert.All(actions, a => Assert.True(a.Kind != LoginActionKind.OpenUrl || a.Url?.StartsWith("https://", StringComparison.Ordinal) == true));
        }
        Assert.Contains(AccountsViewModel.ActionsFor(MicrosoftLoginFailure.ChildAccount), a => a.Kind == LoginActionKind.UseOffline);
        Assert.Contains(AccountsViewModel.ActionsFor(MicrosoftLoginFailure.NoProfileName), a => a.Url == AccountsViewModel.ProfileNameUrl);
        Assert.Contains(AccountsViewModel.ActionsFor(MicrosoftLoginFailure.NoXboxProfile), a => a.Kind == LoginActionKind.Retry);
    }
}
