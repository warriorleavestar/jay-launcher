using Amber.Core.Localization;
using Amber.Core;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Amber.Core.Auth;

public sealed record DeviceCodeInfo(string UserCode, string VerificationUri, string DeviceCode, int IntervalSeconds, int ExpiresInSeconds, string Message)
{
    /// <summary>带好代码的网址（消费者设备码流程通常不给，给了才用）。</summary>
    public string? VerificationUriComplete { get; init; }
}

/// <summary>微软登录失败的种类，界面按它给出能点的下一步（改版规格 3.5 的失败状态表）。</summary>
public enum MicrosoftLoginFailure
{
    Other,
    /// <summary>XErr 2148916233：还没有 Xbox 档案。</summary>
    NoXboxProfile,
    /// <summary>XErr 2148916238：未成年账号，需要家长加入家庭组。</summary>
    ChildAccount,
    /// <summary>XErr 2148916235/6/7：地区限制或需要成年验证。</summary>
    RegionOrAge,
    /// <summary>XErr 2148916227：账号被封禁。</summary>
    Banned,
    /// <summary>授权列表为空：没买 Java 版。</summary>
    NoOwnership,
    /// <summary>有授权但读档案 404：还没设玩家名。</summary>
    NoProfileName,
    /// <summary>设备码过期。</summary>
    CodeExpired,
    /// <summary>用户在网页上拒绝了授权。</summary>
    Denied,
}

/// <summary>带种类的微软登录失败。</summary>
public sealed class MicrosoftLoginException(MicrosoftLoginFailure failure, string message) : InvalidOperationException(message)
{
    public MicrosoftLoginFailure Failure { get; } = failure;
}

/// <summary>
/// 微软账号登录（设备码流程）：微软令牌 → Xbox Live → XSTS → Minecraft 令牌 → 档案。
/// Client ID 的优先级：界面里填写的 > 环境变量 AMBER_MS_CLIENT_ID > 内置的 <see cref="DefaultClientId"/>。
/// </summary>
public sealed class MicrosoftAuth(HttpClient http, string clientId)
{
    private const string Authority = "https://login.microsoftonline.com/consumers/oauth2/v2.0";
    private const string Scope = "XboxLive.signin offline_access";

    public const string ClientIdEnvironmentVariable = "AMBER_MS_CLIENT_ID";

    /// <summary>Amber 自己的公开 Azure 应用 ID（公共客户端，不是密钥）。</summary>
    public const string DefaultClientId = "9015e9e2-b197-4e1a-9659-61c96cf38ffa";

    public async Task<DeviceCodeInfo> BeginDeviceCodeAsync(CancellationToken ct = default)
    {
        using var response = await http.PostAsync($"{Authority}/devicecode", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["scope"] = Scope,
        }), ct);
        var json = await ReadJsonAsync(response, ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(L.F("core.auth.microsoft.devicecodefailed", Describe(json)));
        return new DeviceCodeInfo(
            json["user_code"]!.GetValue<string>(),
            json["verification_uri"]!.GetValue<string>(),
            json["device_code"]!.GetValue<string>(),
            json["interval"]?.GetValue<int>() ?? 5,
            json["expires_in"]?.GetValue<int>() ?? 900,
            json["message"]?.GetValue<string>() ?? "")
        {
            VerificationUriComplete = json["verification_uri_complete"]?.GetValue<string>(),
        };
    }

    /// <summary>轮询直到用户在浏览器里完成授权，然后走完整条换票流程。</summary>
    public async Task<Account> CompleteDeviceCodeAsync(DeviceCodeInfo code, IProgress<string>? status = null, CancellationToken ct = default)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(code.IntervalSeconds, 1));
        var deadline = DateTimeOffset.UtcNow.AddSeconds(code.ExpiresInSeconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(interval, ct);
            using var response = await http.PostAsync($"{Authority}/token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                ["client_id"] = clientId,
                ["device_code"] = code.DeviceCode,
            }), ct);
            var json = await ReadJsonAsync(response, ct);
            if (response.IsSuccessStatusCode)
                return await FinishAsync(json, new Account { Kind = AccountKind.Microsoft }, status, ct);

            switch (json["error"]?.GetValue<string>())
            {
                case "authorization_pending":
                    continue;
                case "slow_down":
                    interval += TimeSpan.FromSeconds(5);
                    continue;
                case "authorization_declined":
                    throw new MicrosoftLoginException(MicrosoftLoginFailure.Denied, L.T("core.auth.microsoft.denied"));
                case "expired_token":
                    throw new MicrosoftLoginException(MicrosoftLoginFailure.CodeExpired, L.T("core.auth.microsoft.devicecodeexpired"));
                default:
                    throw new InvalidOperationException(L.F("core.auth.microsoft.loginfailed", Describe(json)));
            }
        }
        throw new MicrosoftLoginException(MicrosoftLoginFailure.CodeExpired, L.T("core.auth.microsoft.devicecodeexpired"));
    }

    /// <summary>令牌快过期时用刷新令牌续期。刷新令牌失效时抛 <see cref="LoginRequiredException"/>。</summary>
    public async Task RefreshAsync(Account account, CancellationToken ct = default)
    {
        if (account.AccessTokenExpires is { } expires && expires > DateTimeOffset.UtcNow.AddMinutes(10) && account.AccessToken is not null) return;
        if (string.IsNullOrEmpty(account.RefreshToken)) throw new LoginRequiredException(L.F("core.auth.account.expired", account.Name));

        using var response = await http.PostAsync($"{Authority}/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = clientId,
            ["refresh_token"] = account.RefreshToken,
            ["scope"] = Scope,
        }), ct);
        var json = await ReadJsonAsync(response, ct);
        if (!response.IsSuccessStatusCode)
            throw new LoginRequiredException(L.F("core.auth.account.expireddetail", account.Name, Describe(json)));
        await FinishAsync(json, account, null, ct);
    }

    private async Task<Account> FinishAsync(JsonObject msToken, Account account, IProgress<string>? status, CancellationToken ct)
    {
        var accessToken = msToken["access_token"]!.GetValue<string>();
        account.RefreshToken = msToken["refresh_token"]?.GetValue<string>() ?? account.RefreshToken;

        status?.Report(L.T("core.auth.microsoft.xboxlogin"));
        var xbl = await PostJsonAsync("https://user.auth.xboxlive.com/user/authenticate", new JsonObject
        {
            ["Properties"] = new JsonObject
            {
                ["AuthMethod"] = "RPS",
                ["SiteName"] = "user.auth.xboxlive.com",
                ["RpsTicket"] = "d=" + accessToken,
            },
            ["RelyingParty"] = "http://auth.xboxlive.com",
            ["TokenType"] = "JWT",
        }, ct);
        var xblToken = xbl["Token"]!.GetValue<string>();
        var userHash = xbl["DisplayClaims"]!["xui"]![0]!["uhs"]!.GetValue<string>();

        status?.Report(L.T("core.auth.microsoft.xstsauthorization"));
        var xsts = await PostJsonAsync("https://xsts.auth.xboxlive.com/xsts/authorize", new JsonObject
        {
            ["Properties"] = new JsonObject
            {
                ["SandboxId"] = "RETAIL",
                ["UserTokens"] = new JsonArray(xblToken),
            },
            ["RelyingParty"] = "rp://api.minecraftservices.com/",
            ["TokenType"] = "JWT",
        }, ct);
        var xstsToken = xsts["Token"]!.GetValue<string>();
        account.Xuid = xsts["DisplayClaims"]?["xui"]?[0]?["xid"]?.GetValue<string>() ?? account.Xuid;

        status?.Report(L.T("core.auth.microsoft.minecraftlogin"));
        var mc = await PostJsonAsync("https://api.minecraftservices.com/authentication/login_with_xbox", new JsonObject
        {
            ["identityToken"] = $"XBL3.0 x={userHash};{xstsToken}",
        }, ct);
        account.AccessToken = mc["access_token"]!.GetValue<string>();
        account.AccessTokenExpires = DateTimeOffset.UtcNow.AddSeconds(mc["expires_in"]?.GetValue<int>() ?? 86400);

        status?.Report(L.T("core.auth.microsoft.readprofile"));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.minecraftservices.com/minecraft/profile");
        request.Headers.Authorization = new("Bearer", account.AccessToken);
        using var profileResponse = await http.SendAsync(request, ct);
        if (profileResponse.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw await MissingProfileAsync(account.AccessToken, ct);
        var profile = await ReadJsonAsync(profileResponse, ct);
        if (!profileResponse.IsSuccessStatusCode) throw new InvalidOperationException(L.F("core.auth.microsoft.profilefailed", Describe(profile)));

        account.Name = profile["name"]!.GetValue<string>();
        account.Uuid = Guid.Parse(profile["id"]!.GetValue<string>());
        if (profile["skins"] is JsonArray skins && skins.FirstOrDefault(s => s?["state"]?.GetValue<string>() == "ACTIVE") is { } active)
        {
            account.SkinUrl = active["url"]?.GetValue<string>();
            account.SlimSkin = string.Equals(active["variant"]?.GetValue<string>(), "SLIM", StringComparison.OrdinalIgnoreCase);
        }
        return account;
    }

    /// <summary>读档案 404：看授权列表分清“没买 Java 版”和“买了但没设玩家名”；授权列表读不到时给合在一起的说法。</summary>
    private async Task<MicrosoftLoginException> MissingProfileAsync(string accessToken, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.minecraftservices.com/entitlements/mcstore");
            request.Headers.Authorization = new("Bearer", accessToken);
            using var response = await http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
            {
                var json = await ReadJsonAsync(response, ct);
                return json["items"] is JsonArray { Count: > 0 }
                    ? new MicrosoftLoginException(MicrosoftLoginFailure.NoProfileName, L.T("core.auth.microsoft.noprofilename"))
                    : new MicrosoftLoginException(MicrosoftLoginFailure.NoOwnership, L.T("core.auth.microsoft.noownership"));
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // 授权列表读不到：退回合在一起的说法。
        }
        return new MicrosoftLoginException(MicrosoftLoginFailure.NoOwnership, L.T("core.auth.microsoft.missingownership"));
    }

    private async Task<JsonObject> PostJsonAsync(string url, JsonObject body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = AmberJson.Body(body) };
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await http.SendAsync(request, ct);
        var json = await ReadJsonAsync(response, ct);
        if (response.IsSuccessStatusCode) return json;
        if (json["XErr"] is { } code) throw XboxError(code.ToString());
        throw new InvalidOperationException(L.F("core.auth.microsoft.requestfailed", url, (int)response.StatusCode, Describe(json)));
    }

    internal static MicrosoftLoginException XboxError(string code) => code switch
    {
        "2148916227" => new(MicrosoftLoginFailure.Banned, L.T("core.auth.xbox.banned")),
        "2148916233" => new(MicrosoftLoginFailure.NoXboxProfile, L.T("core.auth.xbox.missingprofile")),
        "2148916235" => new(MicrosoftLoginFailure.RegionOrAge, L.T("core.auth.xbox.regionunavailable")),
        "2148916236" or "2148916237" => new(MicrosoftLoginFailure.RegionOrAge, L.T("core.auth.xbox.adultverification")),
        "2148916238" => new(MicrosoftLoginFailure.ChildAccount, L.T("core.auth.xbox.familyrequired")),
        _ => new(MicrosoftLoginFailure.Other, L.F("core.auth.xbox.loginfailed", code)),
    };

    private static async Task<JsonObject> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(text)) return [];
        try
        {
            return JsonNode.Parse(text) as JsonObject ?? [];
        }
        catch (JsonException)
        {
            return new JsonObject { ["error_description"] = text.Length > 200 ? text[..200] : text };
        }
    }

    private static string Describe(JsonObject json) =>
        json["error_description"]?.GetValue<string>() ?? json["errorMessage"]?.GetValue<string>() ?? json["error"]?.ToString() ?? json.ToJsonString();
}
