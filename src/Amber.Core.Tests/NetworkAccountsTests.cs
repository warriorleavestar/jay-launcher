using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Public;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Tests;

/// <summary>公共网络的账号密码登录：节点核对密码、发绑定身份的凭证；猜错会锁；房主凭凭证放人。</summary>
public sealed class NetworkAccountsTests : IAsyncDisposable
{
    private sealed class FakeNetwork : IPublicNetwork
    {
        public readonly Dictionary<(string, int), int> Map = [];

        public async Task<Stream> ConnectAsync(string ip, int port, CancellationToken ct)
        {
            if (!Map.TryGetValue((ip, port), out var local)) throw new IOException("unreachable");
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, local, ct);
            return client.GetStream();
        }

        public Task<string?> MyIpAsync(CancellationToken ct) => Task.FromResult<string?>("10.235.0.7");
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jay-acct-" + Guid.NewGuid().ToString("N"));
    private readonly FakeNetwork _net = new();
    private readonly X25519KeyPair _host = X25519KeyPair.Generate();
    private readonly X25519KeyPair _guest = X25519KeyPair.Generate();
    private readonly List<IAsyncDisposable> _cleanup = [];

    private (AccountStore Store, LoginAuthority Authority, NetworkLogin Login) StartNode()
    {
        var store = new AccountStore(Path.Combine(_dir, "accounts.json"));
        var (key, signer) = LoginAuthority.LoadOrCreateKeys(Path.Combine(_dir, "login.key"));
        var authority = new LoginAuthority(store, key, signer, "net1");
        var server = new RoomDirectoryServer(IPAddress.Loopback, 0) { Login = authority };
        _cleanup.Add(server);
        _net.Map[(PublicNetworkProfile.DirectoryIp, PublicNetworkProfile.DirectoryPort)] = server.Port;
        return (store, authority, new NetworkLogin(true, authority.PublicKey, authority.SignKey));
    }

    [Fact]
    public async Task LoginGivesATokenBoundToTheIdentity()
    {
        var (store, _, login) = StartNode();
        store.Set("alice", "correct horse");
        var (token, parsed) = await new RoomDirectoryClient(_net).LoginAsync(_guest, login, "net1", "alice", "correct horse", default);
        Assert.Equal("alice", parsed.User);
        Assert.Equal(MemberIdentity.FingerprintOf(_guest.PublicKey), parsed.Fingerprint);
        var now = DateTimeOffset.UtcNow;
        Assert.NotNull(AccountToken.Verify(token, login.SignKey, "net1", _guest.PublicKey, now));
        // 别人拿到这张凭证也没用：绑定的是登录人的身份；换网络、过期也不认。
        Assert.Null(AccountToken.Verify(token, login.SignKey, "net1", X25519KeyPair.Generate().PublicKey, now));
        Assert.Null(AccountToken.Verify(token, login.SignKey, "net2", _guest.PublicKey, now));
        Assert.Null(AccountToken.Verify(token, login.SignKey, "net1", _guest.PublicKey, now + AccountToken.Lifetime + TimeSpan.FromMinutes(1)));
        // 签名改一个字节就不认（改解码后的字节：直接改末尾字符可能只动了补位的位，等于没改）。
        Assert.Null(AccountToken.Verify(Tamper(token), login.SignKey, "net1", _guest.PublicKey, now));
    }

    private static string Tamper(string token)
    {
        var parts = token.Split('.');
        var b64 = parts[1].Replace('-', '+').Replace('_', '/');
        var sig = Convert.FromBase64String(b64 + new string('=', (4 - b64.Length % 4) % 4));
        sig[0] ^= 0x01;
        return parts[0] + "." + Convert.ToBase64String(sig).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    [Fact]
    public async Task WrongPasswordAndDisabledAccountsAreRefused()
    {
        var (store, _, login) = StartNode();
        store.Set("bob", "password1");
        var client = new RoomDirectoryClient(_net);
        var wrong = await Assert.ThrowsAsync<InvalidDataException>(() => client.LoginAsync(_guest, login, "net1", "bob", "nope-nope", default));
        Assert.Equal(Localization.L.T("core.mp.account.wrong"), wrong.Message);
        // 不存在的账号和密码错一样的回答。
        var missing = await Assert.ThrowsAsync<InvalidDataException>(() => client.LoginAsync(_guest, login, "net1", "nobody", "password1", default));
        Assert.Equal(wrong.Message, missing.Message);
        store.SetDisabled("bob", true);
        var disabled = await Assert.ThrowsAsync<InvalidDataException>(() => client.LoginAsync(_guest, login, "net1", "bob", "password1", default));
        Assert.Equal(Localization.L.T("core.mp.account.disabled"), disabled.Message);
    }

    [Fact]
    public void TooManyWrongGuessesLockTheAccount()
    {
        var store = new AccountStore(Path.Combine(_dir, "a.json"));
        store.Set("carol", "password1");
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < AccountStore.MaxFails; i++) Assert.Equal(LoginResult.Wrong, store.Check("carol", "x", now));
        Assert.Equal(LoginResult.Locked, store.Check("carol", "password1", now));
        Assert.Equal(LoginResult.Ok, store.Check("carol", "password1", now + AccountStore.Lockout + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void AFullFailureTableNeverUnlocksALockedAccount()
    {
        var store = new AccountStore(Path.Combine(_dir, "a.json"));
        store.Set("carol", "password1");
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < AccountStore.MaxFails; i++) store.RecordFailure("carol", now);
        Assert.Equal(LoginResult.Locked, store.Check("carol", "password1", now));
        // Junk names, more than the table holds: carol stays locked and the table stays bounded.
        for (var i = 0; i < AccountStore.MaxTrackedNames + 50; i++) store.RecordFailure("junk" + i, now + TimeSpan.FromMilliseconds(i));
        Assert.Equal(LoginResult.Locked, store.Check("carol", "password1", now + TimeSpan.FromMinutes(1)));
        Assert.True(store.TrackedNames <= AccountStore.MaxTrackedNames);
    }

    [Fact]
    public void ExpiredFailuresAreDroppedBeforeAnythingElse()
    {
        var store = new AccountStore(Path.Combine(_dir, "a.json"));
        var start = DateTimeOffset.UtcNow;
        for (var i = 0; i < AccountStore.MaxTrackedNames; i++) store.RecordFailure("old" + i, start);
        var later = start + AccountStore.Lockout + TimeSpan.FromSeconds(1);
        for (var i = 0; i < 3; i++) store.RecordFailure("dave", later);
        store.RecordFailure("erin", later);
        // The expired names made room; the live counts survive.
        Assert.Equal(2, store.TrackedNames);
        for (var i = 0; i < 2; i++) store.RecordFailure("dave", later);
        Assert.Equal(LoginResult.Locked, store.Check("dave", "whatever1", later));
    }

    [Fact]
    public void AccountsArePersistedWithoutThePassword()
    {
        var path = Path.Combine(_dir, "a.json");
        var store = new AccountStore(path);
        store.Set("dave", "password1");
        Assert.Throws<ArgumentException>(() => store.Set("eve", "short"));
        Assert.Throws<ArgumentException>(() => store.Set("   ", "password1"));
        Assert.DoesNotContain("password1", File.ReadAllText(path));
        var again = new AccountStore(path);
        Assert.Equal([("dave", false)], again.List());
        Assert.Equal(LoginResult.Ok, again.Check("dave", "password1", DateTimeOffset.UtcNow));
        Assert.True(again.Remove("dave"));
        Assert.Empty(new AccountStore(path).List());
    }

    [Fact]
    public void LoginRequestsAreOnlyReadableByTheNodeAndExpire()
    {
        var store = new AccountStore(Path.Combine(_dir, "a.json"));
        store.Set("frank", "password1");
        var (key, signer) = LoginAuthority.LoadOrCreateKeys(Path.Combine(_dir, "login.key"));
        var now = DateTimeOffset.UtcNow;
        var authority = new LoginAuthority(store, key, signer, "net1", () => now);
        var login = new NetworkLogin(true, key.PublicKey, signer.PublicKey);
        var request = NetworkLoginClient.Request(_guest, login, "frank", "password1", now);
        Assert.DoesNotContain("password1", request.ToJsonString());
        // 旧请求被重放：时间差太大就不认。
        var late = new LoginAuthority(store, key, signer, "net1", () => now + LoginAuthority.Skew + TimeSpan.FromSeconds(1));
        Assert.Equal("time", late.Handle(request)["error"]?.GetValue<string>());
        // 钥匙存了文件，下次读回来是同一把。
        var (key2, signer2) = LoginAuthority.LoadOrCreateKeys(Path.Combine(_dir, "login.key"));
        Assert.Equal(key.PublicKey, key2.PublicKey);
        Assert.Equal(signer.PublicKey, signer2.PublicKey);
        Assert.True(authority.Handle(request)["ok"]?.GetValue<bool>());
    }

    [Fact]
    public void LoginSectionIsParsedFromTheNetworkConfig()
    {
        var key = Convert.ToBase64String(X25519KeyPair.Generate().PublicKey);
        var sign = Convert.ToBase64String(new byte[32]);
        var config = NetworkConfig.Parse(System.Text.Encoding.UTF8.GetBytes(
            $$"""{ "v": 1, "network": "x1", "login": { "required": true, "key": "{{key}}", "signKey": "{{sign}}" } }"""), "x1");
        Assert.True(config.Login!.Required);
        Assert.Empty(config.Login.Factors);
        var with2fa = NetworkConfig.Parse(System.Text.Encoding.UTF8.GetBytes(
            $$"""{ "v": 1, "network": "x1", "login": { "key": "{{key}}", "signKey": "{{sign}}", "factors": ["totp", "mail"], "requireFactor": true } }"""), "x1");
        Assert.Equal(["totp", "mail"], with2fa.Login!.Factors);
        Assert.True(with2fa.Login.RequireFactor);
        Assert.Throws<InvalidDataException>(() => NetworkConfig.Parse(System.Text.Encoding.UTF8.GetBytes(
            $$"""{ "v": 1, "network": "x1", "login": { "key": "{{key}}", "signKey": "{{sign}}", "factors": ["Not OK"] } }"""), "x1"));
        Assert.Throws<InvalidDataException>(() => NetworkConfig.Parse("""{ "v": 1, "network": "x1", "login": { "key": "AAAA" } }"""u8, "x1"));
        Assert.Null(NetworkConfig.Parse("""{ "v": 1, "network": "x1" }"""u8, "x1").Login);
    }

    [Fact]
    public async Task HostOnlyLetsLoggedInPeopleIn()
    {
        var (store, _, login) = StartNode();
        store.Set("gina", "password1");
        var seen = new List<KnockRequest>();
        var book = new InviteBook();
        var credentials = RoomCredentials.Create(_host.PublicKey, 25565, TunnelSettings.Default);
        var server = new KnockServer(book, _host, () => credentials, _ => false, (r, _) =>
        {
            seen.Add(r);
            return Task.FromResult(true);
        }, IPAddress.Loopback, 0) { AccountPolicy = () => new AccountPolicy("net1", login.SignKey) };
        _cleanup.Add(server);
        _net.Map[("10.235.0.7", KnockProtocol.Port)] = server.Port;
        var invite = book.CreateStanding(false, TunnelSettings.Default).Invite;

        Assert.Equal("account", (await PublicJoin.KnockAsync(_net, "10.235.0.7", invite, _guest, "G", "0.8.0", null, default)).Reason);
        // 别人的凭证不能拿来用。
        var other = X25519KeyPair.Generate();
        var (otherToken, _) = await new RoomDirectoryClient(_net).LoginAsync(other, login, "net1", "gina", "password1", default);
        Assert.Equal("account", (await PublicJoin.KnockAsync(_net, "10.235.0.7", invite, _guest, "G", "0.8.0", null, default, account: otherToken)).Reason);
        var (token, _) = await new RoomDirectoryClient(_net).LoginAsync(_guest, login, "net1", "gina", "password1", default);
        Assert.True((await PublicJoin.KnockAsync(_net, "10.235.0.7", invite, _guest, "G", "0.8.0", null, default, account: token)).Approved);
        Assert.Equal("gina", Assert.Single(seen).Account);
    }

    [Fact]
    public async Task TokenIsNotSentWhenTheHostDoesNotAsk()
    {
        var seen = new List<KnockRequest>();
        var book = new InviteBook();
        var credentials = RoomCredentials.Create(_host.PublicKey, 25565, TunnelSettings.Default);
        var server = new KnockServer(book, _host, () => credentials, _ => false, (r, _) =>
        {
            seen.Add(r);
            return Task.FromResult(true);
        }, IPAddress.Loopback, 0);
        _cleanup.Add(server);
        _net.Map[("10.235.0.7", KnockProtocol.Port)] = server.Port;
        var invite = book.CreateStanding(false, TunnelSettings.Default).Invite;
        Assert.True((await PublicJoin.KnockAsync(_net, "10.235.0.7", invite, _guest, "G", "0.8.0", null, default, account: "x.y")).Approved);
        Assert.Null(Assert.Single(seen).Account);
    }

    [Theory]
    [InlineData(59L, "287082")]
    [InlineData(1111111109L, "081804")]
    [InlineData(1234567890L, "005924")]
    [InlineData(2000000000L, "279037")]
    public void TotpMatchesTheRfcVectors(long seconds, string code) =>
        Assert.Equal(code, TotpFactor.Code("12345678901234567890"u8.ToArray(), DateTimeOffset.FromUnixTimeSeconds(seconds)));

    [Fact]
    public void TotpUriIsReadableByAuthenticatorApps()
    {
        Assert.Equal("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", TotpFactor.Base32("12345678901234567890"u8.ToArray()));
        Assert.StartsWith("otpauth://totp/net1%3Aalice?secret=", TotpFactor.Uri("net1", "alice", TotpFactor.NewSecret()));
    }

    private sealed class PinFactor(string pin) : ILoginFactor
    {
        public string Name => "pin";
        public string Prompt => "会员 PIN";
        public bool AppliesTo(string user) => user != "free";
        public bool Verify(string user, string response, DateTimeOffset now) => response == pin;
    }

    private sealed class Clock
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
    }

    private (AccountStore Store, NetworkLogin Login, Clock Clock) StartNodeWith(Func<AccountStore, ILoginFactor[]> factors, bool require = false)
    {
        var store = new AccountStore(Path.Combine(_dir, "accounts.json"));
        var (key, signer) = LoginAuthority.LoadOrCreateKeys(Path.Combine(_dir, "login.key"));
        var clock = new Clock();
        var authority = new LoginAuthority(store, key, signer, "net1", () => clock.Now) { Factors = factors(store), RequireSecondFactor = require };
        var server = new RoomDirectoryServer(IPAddress.Loopback, 0) { Login = authority };
        _cleanup.Add(server);
        _net.Map[(PublicNetworkProfile.DirectoryIp, PublicNetworkProfile.DirectoryPort)] = server.Port;
        return (store, new NetworkLogin(true, key.PublicKey, signer.PublicKey), clock);
    }

    [Fact]
    public async Task TotpIsAskedForAfterThePasswordAndCannotBeReused()
    {
        var (store, login, clock) = StartNodeWith(s => [new TotpFactor(s)]);
        store.Set("hana", "password1");
        var client = new RoomDirectoryClient(_net);
        // 没开两步验证的账号直接登录。
        await client.LoginAsync(_guest, login, "net1", "hana", "password1", default);
        var secret = TotpFactor.NewSecret();
        store.SetTotp("hana", secret);
        var need = await Assert.ThrowsAsync<SecondFactorRequiredException>(() => client.LoginAsync(_guest, login, "net1", "hana", "password1", default));
        Assert.False(need.Wrong);
        Assert.Equal("totp", Assert.Single(need.Need).Name);
        // 密码错时不提两步验证，免得看出密码对没对。
        await Assert.ThrowsAsync<InvalidDataException>(() => client.LoginAsync(_guest, login, "net1", "hana", "wrong-pass", default));
        var bad = await Assert.ThrowsAsync<SecondFactorRequiredException>(() =>
            client.LoginAsync(_guest, login, "net1", "hana", "password1", default, new Dictionary<string, string> { ["totp"] = "000000" == TotpFactor.Code(secret, clock.Now) ? "111111" : "000000" }));
        Assert.True(bad.Wrong);
        var code = TotpFactor.Code(secret, clock.Now);
        var (_, parsed) = await client.LoginAsync(_guest, login, "net1", "hana", "password1", default, new Dictionary<string, string> { ["totp"] = code });
        Assert.Equal("hana", parsed.User);
        var reused = await Assert.ThrowsAsync<SecondFactorRequiredException>(() =>
            client.LoginAsync(_guest, login, "net1", "hana", "password1", default, new Dictionary<string, string> { ["totp"] = code }));
        Assert.True(reused.Wrong);
    }

    [Fact]
    public async Task WrongCodesCountTowardsTheLockout()
    {
        var (store, login, _) = StartNodeWith(s => [new PinFactor("4321")]);
        store.Set("ivan", "password1");
        var client = new RoomDirectoryClient(_net);
        var codes = new Dictionary<string, string> { ["pin"] = "0000" };
        for (var i = 0; i < AccountStore.MaxFails; i++)
            await Assert.ThrowsAsync<SecondFactorRequiredException>(() => client.LoginAsync(_guest, login, "net1", "ivan", "password1", default, codes));
        var locked = await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.LoginAsync(_guest, login, "net1", "ivan", "password1", default, new Dictionary<string, string> { ["pin"] = "4321" }));
        Assert.Equal(Localization.L.T("core.mp.account.locked"), locked.Message);
    }

    [Fact]
    public async Task ThirdPartyFactorsPlugIn()
    {
        var (store, login, _) = StartNodeWith(s => [new PinFactor("4321")]);
        store.Set("free", "password1");
        store.Set("member", "password1");
        var client = new RoomDirectoryClient(_net);
        await client.LoginAsync(_guest, login, "net1", "free", "password1", default);
        var need = await Assert.ThrowsAsync<SecondFactorRequiredException>(() => client.LoginAsync(_guest, login, "net1", "member", "password1", default));
        Assert.Equal(new LoginFactorPrompt("pin", "会员 PIN"), Assert.Single(need.Need));
        await client.LoginAsync(_guest, login, "net1", "member", "password1", default, new Dictionary<string, string> { ["pin"] = "4321" });
    }

    [Fact]
    public async Task NetworkCanRequireASecondFactorForEveryone()
    {
        var (store, login, _) = StartNodeWith(s => [new TotpFactor(s)], require: true);
        store.Set("jo", "password1");
        var e = await Assert.ThrowsAsync<InvalidDataException>(() => new RoomDirectoryClient(_net).LoginAsync(_guest, login, "net1", "jo", "password1", default));
        Assert.Equal(Localization.L.T("core.mp.factor.setup"), e.Message);
    }

    [Fact]
    public void CommandFactorRunsTheMaintainersProgram()
    {
        if (OperatingSystem.IsWindows()) return;
        var script = Path.Combine(_dir, "check.sh");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(script, "#!/bin/sh\ngrep -q '\"response\":\"ok-123\"'\n");
        var factor = new CommandFactor("mail", "邮件里的验证码", "/bin/sh", [script], "net1");
        Assert.True(factor.Verify("a", "ok-123", DateTimeOffset.UtcNow));
        Assert.False(factor.Verify("a", "nope", DateTimeOffset.UtcNow));
        Assert.False(new CommandFactor("x", "x", "/does/not/exist", [], "net1").Verify("a", "ok", DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentException>(() => new CommandFactor("Bad Name", "x", "/bin/true", [], "net1"));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var d in _cleanup) await d.DisposeAsync();
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
