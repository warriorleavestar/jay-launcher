using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amber.Core.Localization;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Multiplayer.Public;

/// <summary>
/// 网络配置里的登录设置：账号由网络节点（jay-server directory --accounts）管。
/// <c>"login": { "required": true, "key": "&lt;节点登录公钥 X25519 base64&gt;", "signKey": "&lt;发凭证的 Ed25519 公钥 base64&gt;" }</c>
/// </summary>
/// <param name="Required">这个网络上的房间只让登录过的人进（房主敲门时查凭证）。</param>
/// <param name="Key">节点的登录公钥：账号密码用它端到端加密后再发，公共网络上别人看不到。</param>
/// <param name="SignKey">节点发凭证用的签名公钥：房主用它验凭证，不用联系节点。</param>
/// <remarks>
/// 可选 <c>"factors": ["totp", "mail"]</c>：网络用到的第二步验证（给 Jay 预先显示验证码框；真正要不要由节点决定）。
/// 可选 <c>"requireFactor": true</c>：声明每个账号都必须有第二步验证（节点用 jay-server directory --require-2fa 落实）。
/// </remarks>
public sealed record NetworkLogin(bool Required, byte[] Key, byte[] SignKey)
{
    public IReadOnlyList<string> Factors { get; init; } = [];
    public bool RequireFactor { get; init; }

    public static NetworkLogin? Parse(JsonObject? o)
    {
        if (o is null) return null;
        try
        {
            var key = Convert.FromBase64String(o["key"]?.GetValue<string>() ?? "");
            var sign = Convert.FromBase64String(o["signKey"]?.GetValue<string>() ?? "");
            if (key.Length != X25519KeyPair.Length || sign.Length != 32) throw new FormatException();
            var factors = (o["factors"] as JsonArray ?? []).Select(f => f?.GetValue<string>()).Take(4).ToList();
            if (factors.Any(f => !LoginFactorPrompt.IsValidName(f))) throw new FormatException();
            return new NetworkLogin(o["required"]?.GetValue<bool>() ?? false, key, sign)
            {
                Factors = [.. factors.Distinct().OfType<string>()], RequireFactor = o["requireFactor"]?.GetValue<bool>() ?? false,
            };
        }
        catch (FormatException) { throw new InvalidDataException(L.T("core.mp.netconfig.bad")); }
    }
}

/// <summary>
/// 登录凭证：节点签名的“这个账号在这个网络上，绑定这把 Jay 联机身份（指纹），什么时候过期”。
/// 绑定指纹，所以被别人看到也没用：敲门时房主核对凭证里的指纹就是握手里证明过的那把钥匙。
/// 格式：base64url(JSON) + "." + base64url(Ed25519 签名)；签的是 "jay-account-v1\n" + JSON 原始字节。
/// </summary>
public sealed record AccountToken(string User, string Fingerprint, string Network, DateTimeOffset Expires)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);
    private static readonly byte[] Domain = "jay-account-v1\n"u8.ToArray();

    public string Sign(NetworkSigner signer)
    {
        var payload = Encoding.UTF8.GetBytes(new JsonObject
        {
            ["u"] = User, ["fp"] = Fingerprint, ["net"] = Network, ["exp"] = Expires.ToUnixTimeSeconds(),
        }.ToJsonString());
        return NetworkString.Base64Url(payload) + "." + NetworkString.Base64Url(signer.Sign([.. Domain, .. payload]));
    }

    /// <summary>验凭证：签名对、网络对、没过期、绑定的就是 <paramref name="publicKey"/> 这把身份。不对返回 null。</summary>
    public static AccountToken? Verify(string? token, byte[] signKey, string network, byte[] publicKey, DateTimeOffset now)
    {
        if (token is null || token.Length > 1024) return null;
        var parts = token.Split('.');
        if (parts.Length != 2 || NetworkString.FromBase64Url(parts[0]) is not { } payload || NetworkString.FromBase64Url(parts[1]) is not { Length: 64 } sig) return null;
        if (!RoomSigner.Verify(signKey, [.. Domain, .. payload], sig)) return null;
        try
        {
            var o = JsonNode.Parse(payload) as JsonObject;
            var t = new AccountToken(o?["u"]?.GetValue<string>() ?? "", o?["fp"]?.GetValue<string>() ?? "", o?["net"]?.GetValue<string>() ?? "",
                DateTimeOffset.FromUnixTimeSeconds(o?["exp"]?.GetValue<long>() ?? 0));
            return t.Network == network && t.Expires > now && t.User.Length > 0 && t.Fingerprint == MemberIdentity.FingerprintOf(publicKey) ? t : null;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or ArgumentOutOfRangeException) { return null; }
    }
}

/// <summary>房主对敲门的人的账号要求：网络要求登录时，凭证要是这个网络的节点签的、绑定敲门的人。</summary>
public sealed record AccountPolicy(string Network, byte[] SignKey)
{
    public AccountToken? Admit(string? token, byte[] guestKey, DateTimeOffset now) => AccountToken.Verify(token, SignKey, Network, guestKey, now);
}

/// <summary>
/// 账号库。内置的 <see cref="AccountStore"/> 存在节点的一个 JSON 文件里；第三方网络可以换成自己的（数据库、论坛账号、会员系统……）。
/// 实现要自己防在线猜密码（比如连续错几次就锁一会儿），账号不存在和密码错要回答一样的 <see cref="LoginResult.Wrong"/>。
/// </summary>
public interface IAccountStore
{
    LoginResult Check(string name, string password, DateTimeOffset now);
    /// <summary>第二步验证错了：算一次失败（和密码错一起计数）。</summary>
    void RecordFailure(string name, DateTimeOffset now);
    /// <summary>全部验证都过了：清掉失败计数。</summary>
    void Succeeded(string name);
    /// <summary>这个账号的 TOTP 密钥（没开两步验证是 null）。</summary>
    byte[]? TotpSecret(string name);
}

/// <summary>
/// 节点上的账号表（jay-server 用）：账号名 → PBKDF2-SHA256 的盐和哈希。密码本身不存。
/// 每个账号连续猜错 5 次锁 5 分钟，在线猜密码很慢。
/// </summary>
public sealed class AccountStore : IAccountStore
{
    public const int Iterations = 600_000;
    public const int MinPassword = 8;
    public const int MaxFails = 5;
    public static readonly TimeSpan Lockout = TimeSpan.FromMinutes(5);

    private sealed record Account(byte[] Salt, byte[] Hash, int Iterations, bool Disabled, byte[]? Totp = null);

    private readonly string _path;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Account> _accounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int Fails, DateTimeOffset Until)> _fails = new(StringComparer.Ordinal);

    /// <summary>Last-write time of the file as last read or written here, to notice edits from another process.</summary>
    private DateTime _loadedStamp;

    public AccountStore(string path)
    {
        _path = path;
        if (File.Exists(path)) Load();
    }

    private void Load()
    {
        var stamp = File.GetLastWriteTimeUtc(_path);
        var o = JsonNode.Parse(File.ReadAllText(_path)) as JsonObject ?? throw new InvalidDataException(_path);
        var accounts = new Dictionary<string, Account>(StringComparer.Ordinal);
        foreach (var (name, node) in o["users"] as JsonObject ?? [])
            if (node is JsonObject a)
                accounts[name] = new Account(Convert.FromBase64String(a["salt"]?.GetValue<string>() ?? throw new InvalidDataException(_path)),
                    Convert.FromBase64String(a["hash"]?.GetValue<string>() ?? throw new InvalidDataException(_path)),
                    a["iter"]?.GetValue<int>() ?? Iterations, a["disabled"]?.GetValue<bool>() ?? false,
                    a["totp"]?.GetValue<string>() is { } totp ? Convert.FromBase64String(totp) : null);
        _accounts.Clear();
        foreach (var (name, account) in accounts) _accounts[name] = account;
        _loadedStamp = stamp;
    }

    /// <summary>
    /// Picks up changes made by <c>jay-server account</c> while the node runs (disable, remove, new password, 2FA), so they
    /// take effect at the next login instead of at the next restart. Call under <see cref="_gate"/>. A file that can't be
    /// read right now (being replaced, or broken) keeps the last good list.
    /// </summary>
    private void Refresh()
    {
        try
        {
            if (File.Exists(_path) && File.GetLastWriteTimeUtc(_path) != _loadedStamp) Load();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or JsonException
            or FormatException or InvalidOperationException) { }
    }

    public IReadOnlyList<(string Name, bool Disabled)> List()
    {
        lock (_gate)
        {
            Refresh();
            return [.. _accounts.OrderBy(a => a.Key, StringComparer.Ordinal).Select(a => (a.Key, a.Value.Disabled))];
        }
    }

    /// <summary>账号名：1~32 个字，去掉控制字符和首尾空格，大小写有区别。</summary>
    public static string? CleanName(string? name)
    {
        var clean = KnockProtocol.SanitizeName(name).Normalize(NormalizationForm.FormKC);
        return clean.Length is > 0 and <= 32 ? clean : null;
    }

    /// <summary>加账号或改密码。</summary>
    public void Set(string name, string password)
    {
        var clean = CleanName(name) ?? throw new ArgumentException(L.T("core.mp.account.bad_name"));
        if (password.Length < MinPassword) throw new ArgumentException(L.F("core.mp.account.short", MinPassword));
        var salt = RandomNumberGenerator.GetBytes(16);
        lock (_gate)
        {
            Refresh();
            _accounts.TryGetValue(clean, out var old);
            _accounts[clean] = new Account(salt, Hash(password, salt, Iterations), Iterations, old?.Disabled ?? false, old?.Totp);
            Save();
        }
    }

    public bool Remove(string name)
    {
        lock (_gate)
        {
            Refresh();
            if (!_accounts.Remove(name)) return false;
            Save();
            return true;
        }
    }

    public bool SetDisabled(string name, bool disabled)
    {
        lock (_gate)
        {
            Refresh();
            if (!_accounts.TryGetValue(name, out var a)) return false;
            _accounts[name] = a with { Disabled = disabled };
            Save();
            return true;
        }
    }

    /// <summary>给账号开 / 关两步验证（TOTP 密钥，null = 关掉）。</summary>
    public bool SetTotp(string name, byte[]? secret)
    {
        lock (_gate)
        {
            Refresh();
            if (!_accounts.TryGetValue(name, out var a)) return false;
            _accounts[name] = a with { Totp = secret };
            Save();
            return true;
        }
    }

    /// <summary>账号的 TOTP 密钥（没开两步验证是 null）。</summary>
    public byte[]? TotpSecret(string name)
    {
        lock (_gate)
        {
            Refresh();
            return _accounts.TryGetValue(name, out var a) ? a.Totp : null;
        }
    }

    /// <summary>记一次失败（比如两步验证码错了），和密码猜错一起算锁定次数。</summary>
    public void RecordFailure(string name, DateTimeOffset now)
    {
        lock (_gate)
        {
            var known = _fails.TryGetValue(name, out var f);
            if (!known && _fails.Count >= MaxTrackedNames) TrimFailures(now);
            var fails = known && f.Until > now ? f.Fails + 1 : 1;
            _fails[name] = (fails, now + Lockout);
        }
    }

    /// <summary>How many names the failure table holds before it is trimmed (see <see cref="TrimFailures"/>).</summary>
    internal const int MaxTrackedNames = 10_000;

    /// <summary>Names in the failure table (tests).</summary>
    internal int TrackedNames
    {
        get { lock (_gate) return _fails.Count; }
    }

    /// <summary>
    /// Makes room in a full failure table. Entries whose window has passed go first; if that isn't enough, names that
    /// aren't locked go, the ones closest to expiring first. A locked name is never dropped, so filling the table with
    /// junk names can't unlock anybody. Called under the gate.
    /// </summary>
    private void TrimFailures(DateTimeOffset now)
    {
        foreach (var name in _fails.Where(p => p.Value.Until <= now).Select(p => p.Key).ToList()) _fails.Remove(name);
        if (_fails.Count < MaxTrackedNames) return;
        var excess = _fails.Count - MaxTrackedNames + 1;
        foreach (var name in _fails.Where(p => p.Value.Fails < MaxFails).OrderBy(p => p.Value.Until).Take(excess).Select(p => p.Key).ToList())
            _fails.Remove(name);
    }

    /// <summary>整个登录都通过了：清掉失败次数。</summary>
    public void Succeeded(string name)
    {
        lock (_gate) _fails.Remove(name);
    }

    /// <summary>核对账号密码。账号不存在也照样算一遍哈希，看不出账号在不在。</summary>
    public LoginResult Check(string name, string password, DateTimeOffset now)
    {
        var clean = CleanName(name) ?? "";
        Account? account;
        lock (_gate)
        {
            if (_fails.TryGetValue(clean, out var f) && f.Fails >= MaxFails && f.Until > now) return LoginResult.Locked;
            Refresh();
            _accounts.TryGetValue(clean, out account);
        }
        var salt = account?.Salt ?? new byte[16];
        var ok = account is not null && CryptographicOperations.FixedTimeEquals(Hash(password, salt, account.Iterations), account.Hash);
        if (account is null) Hash(password, salt, Iterations);
        lock (_gate)
        {
            // 密码对了也先不清失败次数：两步验证也过了才算登录成功（见 Succeeded），不然有密码的人能一直试验证码。
            if (ok) return account!.Disabled ? LoginResult.Disabled : LoginResult.Ok;
        }
        RecordFailure(clean, now);
        return LoginResult.Wrong;
    }

    private static byte[] Hash(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password.Normalize(NormalizationForm.FormKC)), salt, iterations, HashAlgorithmName.SHA256, 32);

    private void Save()
    {
        var users = new JsonObject();
        foreach (var (name, a) in _accounts)
            users[name] = new JsonObject
            {
                ["salt"] = Convert.ToBase64String(a.Salt), ["hash"] = Convert.ToBase64String(a.Hash), ["iter"] = a.Iterations, ["disabled"] = a.Disabled,
                ["totp"] = a.Totp is { } totp ? Convert.ToBase64String(totp) : null,
            };
        PrivateFile.WriteAllText(_path, new JsonObject { ["v"] = 1, ["users"] = users }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        _loadedStamp = File.GetLastWriteTimeUtc(_path);
    }
}

public enum LoginResult { Ok, Wrong, Locked, Disabled }

/// <summary>
/// 节点的登录服务：收到加密的账号密码（用 <see cref="PrivateSeal"/> 封给节点的登录公钥，发方是登录人的 Jay 联机身份），
/// 核对后发一张绑定这把身份的凭证，再封回去。公共网络上的其他人看不到账号密码，也拿不走能用的凭证。
/// </summary>
public sealed class LoginAuthority(IAccountStore accounts, X25519KeyPair key, NetworkSigner signer, string network, Func<DateTimeOffset>? clock = null)
{
    /// <summary>登录请求里的时间和节点时间差多少以内算有效（防重放）。</summary>
    public static readonly TimeSpan Skew = TimeSpan.FromMinutes(5);

    public byte[] PublicKey => key.PublicKey;
    public byte[] SignKey => signer.PublicKey;
    public string Network => network;

    /// <summary>密码之后的验证（两步验证、第三方自定义验证），按顺序都要过。</summary>
    public IReadOnlyList<ILoginFactor> Factors { get; init; } = [];

    /// <summary>每个账号都必须至少有一种第二步验证；没设置的账号不让登录。</summary>
    public bool RequireSecondFactor { get; init; }

    /// <summary>Accounts the operator banned: their logins are refused like a disabled account (set by the node's ban list).</summary>
    public Func<string, bool>? Blocked { get; set; }

    /// <summary>处理一个 login 请求（目录服务调）。</summary>
    public JsonObject Handle(JsonObject request)
    {
        var now = clock?.Invoke() ?? DateTimeOffset.UtcNow;
        var from = Convert.FromBase64String(request["from"]?.GetValue<string>() ?? "");
        var sealedData = Convert.FromBase64String(request["b"]?.GetValue<string>() ?? "");
        if (from.Length != X25519KeyPair.Length || PrivateSeal.Open(key, from, sealedData) is not { } plain) return Error("bad");
        var body = JsonNode.Parse(plain) as JsonObject ?? throw new FormatException();
        var ts = DateTimeOffset.FromUnixTimeMilliseconds(body["ts"]?.GetValue<long>() ?? 0);
        if ((now - ts).Duration() > Skew) return Error("time");
        var user = body["user"]?.GetValue<string>() ?? "";
        var result = accounts.Check(user, body["pass"]?.GetValue<string>() ?? "", now);
        if (result != LoginResult.Ok) return Error(result switch { LoginResult.Locked => "locked", LoginResult.Disabled => "disabled", _ => "wrong" });
        if (AccountStore.CleanName(user) is not { } name) return Error("wrong");
        if (Blocked?.Invoke(name) == true) return Error("disabled");
        var factors = Factors.Where(f => f.AppliesTo(name)).ToList();
        if (RequireSecondFactor && factors.Count == 0) return Error("factor_setup");
        if (factors.Count > 0)
        {
            var answers = body["factors"] as JsonObject;
            string? Answer(ILoginFactor f) => answers?[f.Name]?.GetValue<string>()?.Trim() is { Length: > 0 and <= LoginFactorPrompt.MaxResponse } a ? a : null;
            var missing = factors.Any(f => Answer(f) is null);
            var wrong = !missing && factors.Any(f => !f.Verify(name, Answer(f)!, now));
            if (missing || wrong)
            {
                if (wrong) accounts.RecordFailure(name, now);
                // 要哪几步、上次填得对不对，也封起来发回去：旁人看不出这次密码是不是对的。
                var need = new JsonArray([.. factors.Select(f => (JsonNode)new JsonObject { ["name"] = f.Name, ["prompt"] = f.Prompt })]);
                var payload = Encoding.UTF8.GetBytes(new JsonObject { ["need"] = need, ["wrong"] = wrong, ["ts"] = body["ts"]!.GetValue<long>() }.ToJsonString());
                return new JsonObject { ["ok"] = false, ["error"] = "factor", ["b"] = Convert.ToBase64String(PrivateSeal.Seal(key, from, payload)) };
            }
        }
        accounts.Succeeded(name);
        var token = new AccountToken(name, MemberIdentity.FingerprintOf(from), network, now + AccountToken.Lifetime).Sign(signer);
        var reply = Encoding.UTF8.GetBytes(new JsonObject { ["token"] = token, ["ts"] = body["ts"]!.GetValue<long>() }.ToJsonString());
        return new JsonObject { ["ok"] = true, ["b"] = Convert.ToBase64String(PrivateSeal.Seal(key, from, reply)) };
    }

    private static JsonObject Error(string code) => new() { ["ok"] = false, ["error"] = code };

    /// <summary>Reads the contents of a login.key file (also used for hardware-sealed copies). Null when malformed.</summary>
    public static (X25519KeyPair Key, NetworkSigner Signer)? ParseKeys(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length != 2) return null;
        try { return (X25519KeyPair.FromPrivateKey(Convert.FromBase64String(lines[0])), NetworkSigner.FromSeed(lines[1])); }
        catch (Exception e) when (e is FormatException or InvalidDataException or ArgumentException) { return null; }
    }

    /// <summary>
    /// 节点的登录钥匙：一个文件里存 X25519 私钥和 Ed25519 种子（各 32 字节，base64 两行），没有就生成。
    /// 公钥要写进网络配置的 login.key / login.signKey。
    /// </summary>
    public static (X25519KeyPair Key, NetworkSigner Signer) LoadOrCreateKeys(string path)
    {
        if (File.Exists(path))
        {
            return ParseKeys(File.ReadAllText(path)) ?? throw new InvalidDataException(path);
        }
        var key = X25519KeyPair.Generate();
        var signer = NetworkSigner.Generate(out var seed);
        PrivateFile.WriteAllText(path, Convert.ToBase64String(key.ExportPrivateKey()) + "\n" + Convert.ToBase64String(seed) + "\n");
        return (key, signer);
    }
}

/// <summary>Jay 这边的登录：把账号密码封好发给节点，拿回绑定自己身份的凭证。</summary>
public static class NetworkLoginClient
{
    public static JsonObject Request(X25519KeyPair me, NetworkLogin login, string user, string password, DateTimeOffset now,
        IReadOnlyDictionary<string, string>? factors = null)
    {
        var body = new JsonObject { ["user"] = user, ["pass"] = password, ["ts"] = now.ToUnixTimeMilliseconds() };
        if (factors is { Count: > 0 })
        {
            var answers = new JsonObject();
            foreach (var (name, answer) in factors) answers[name] = answer;
            body["factors"] = answers;
        }
        var plain = Encoding.UTF8.GetBytes(body.ToJsonString());
        return new JsonObject { ["op"] = "login", ["from"] = Convert.ToBase64String(me.PublicKey), ["b"] = Convert.ToBase64String(PrivateSeal.Seal(me, login.Key, plain)) };
    }

    /// <summary>解开节点的回复，验凭证（签名、网络、绑定的是自己）。失败抛 <see cref="InvalidDataException"/>，要两步验证抛 <see cref="SecondFactorRequiredException"/>，消息给用户看。</summary>
    public static (string Token, AccountToken Parsed) Read(JsonObject reply, X25519KeyPair me, NetworkLogin login, string network, DateTimeOffset now)
    {
        if (reply["ok"]?.GetValue<bool>() != true && reply["error"]?.GetValue<string>() == "factor"
            && PrivateSeal.Open(me, login.Key, Convert.FromBase64String(reply["b"]?.GetValue<string>() ?? "")) is { } sealedNeed
            && JsonNode.Parse(sealedNeed) is JsonObject needed)
        {
            var need = (needed["need"] as JsonArray ?? []).OfType<JsonObject>()
                .Select(f => LoginFactorPrompt.From(f["name"]?.GetValue<string>(), f["prompt"]?.GetValue<string>())).OfType<LoginFactorPrompt>()
                .DistinctBy(p => p.Name, StringComparer.Ordinal).Take(4).ToList();
            if (need.Count == 0) throw new InvalidDataException(L.T("core.mp.account.failed"));
            var wrong = needed["wrong"]?.GetValue<bool>() == true;
            throw new SecondFactorRequiredException(need, wrong, L.T(wrong ? "core.mp.factor.wrong" : "core.mp.factor.needed"));
        }
        if (reply["ok"]?.GetValue<bool>() != true)
            throw new InvalidDataException(L.T((reply["error"]?.GetValue<string>()) switch
            {
                "wrong" => "core.mp.account.wrong",
                "locked" => "core.mp.account.locked",
                "disabled" => "core.mp.account.disabled",
                "factor_setup" => "core.mp.factor.setup",
                "time" => "core.mp.account.time",
                "busy" => "core.mp.account.busy",
                "op" => "core.mp.account.unsupported",
                _ => "core.mp.account.failed",
            }));
        var plain = PrivateSeal.Open(me, login.Key, Convert.FromBase64String(reply["b"]?.GetValue<string>() ?? "")) ?? throw new InvalidDataException(L.T("core.mp.account.failed"));
        var token = (JsonNode.Parse(plain) as JsonObject)?["token"]?.GetValue<string>();
        var parsed = AccountToken.Verify(token, login.SignKey, network, me.PublicKey, now) ?? throw new InvalidDataException(L.T("core.mp.account.failed"));
        return (token!, parsed);
    }
}
