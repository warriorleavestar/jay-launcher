using System.Text.Json.Nodes;
using Amber.Core.Multiplayer.Public;
using Amber.NetworkServer;

// 一个第三方公共网络节点的完整例子：
//   dotnet run -- --network my.jaynet --config config.json
// 用到了框架的每一种扩展：房间审核、账号库、第二步验证、事件、自定义请求。

var args2 = new Queue<string>(args);
string? network = null, config = null, dir = "my-network-data";
while (args2.TryDequeue(out var a))
{
    switch (a)
    {
        case "--network": network = args2.Dequeue(); break;
        case "--config": config = args2.Dequeue(); break;
        case "--dir": dir = args2.Dequeue(); break;
    }
}

var builder = new JayNetworkServerBuilder()
    .UseDataDir(dir)
    .UseLog(line => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {line}"))
    // 每人最多 2 个房间，每人每分钟最多新开 4 次，整个网络每分钟最多 300 个新房间。
    .UseRateLimit(2, 4, 300)
    .UseBlockedWords(["广告", "代练"])
    // 自己的规矩：公开房间名必须带地区标签，比如 [NZ] 生存服。
    .AddGate(new RegionTagGate("[NZ]", "[AU]"))
    // 换成自己的账号库（这里是演示用的内存账号表；实际可以接数据库或论坛）。
    .UseAccounts(new DemoAccounts(), requireSecondFactor: false)
    .AddLoginFactor(new InviteCodeFactor())
    .AddObserver(new ConsoleObserver())
    // 自定义请求：自己的网页 / 机器人可以连目录拿“今日公告”和房间数。
    .AddExtension(new MotdExtension());

var networkId = "jay";
if (network is not null)
{
    var signed = NetworkString.Parse(File.Exists(network) ? File.ReadAllText(network) : network);
    builder.UseNetwork(signed);
    networkId = signed.Info.Id;
}
// 节点也按网络配置里允许的房间方式挡一道。
if (config is not null) builder.UseConfig(NetworkConfig.Parse(File.ReadAllBytes(config), networkId));

await using var server = builder.Build();
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
await server.StartAsync(stop.Token);
if (server.LoginSection is { } login) Console.WriteLine("写进网络配置的 login 段：" + login.ToJsonString());
return await server.RunAsync(stop.Token);

/// <summary>公开房间的名字要以这些地区标签开头。隐藏房间不进列表，不管。</summary>
sealed class RegionTagGate(params string[] tags) : IRoomGate
{
    public RoomVerdict Check(RoomAnnouncement a) =>
        !a.Listed || a.Listing.Mode == RoomVisibility.Hidden || tags.Any(t => a.Listing.Name.StartsWith(t, StringComparison.OrdinalIgnoreCase))
            ? RoomVerdict.Allow
            : RoomVerdict.Reject("region", "公开房间名要以地区标签开头，比如 " + tags[0]);
}

/// <summary>演示用的账号库。真正的实现要存密码哈希（比如 PBKDF2 / Argon2），并且连续猜错要锁。</summary>
sealed class DemoAccounts : IAccountStore
{
    private readonly Dictionary<string, string> _users = new() { ["demo"] = "demo-password" };
    private readonly Dictionary<string, (int Fails, DateTimeOffset Until)> _fails = [];

    public LoginResult Check(string name, string password, DateTimeOffset now)
    {
        lock (_fails)
        {
            if (_fails.TryGetValue(name, out var f) && f.Until > now) return LoginResult.Locked;
            if (_users.TryGetValue(name, out var p) && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.UTF8.GetBytes(p), System.Text.Encoding.UTF8.GetBytes(password)))
                return LoginResult.Ok;
            RecordFailure(name, now);
            return LoginResult.Wrong;
        }
    }

    public void RecordFailure(string name, DateTimeOffset now)
    {
        lock (_fails)
        {
            var fails = _fails.GetValueOrDefault(name).Fails + 1;
            _fails[name] = (fails, fails >= 5 ? now + TimeSpan.FromMinutes(5) : default);
        }
    }

    public void Succeeded(string name) { lock (_fails) _fails.Remove(name); }

    public byte[]? TotpSecret(string name) => null;
}

/// <summary>第二步：管理员在群里发的当日邀请码。</summary>
sealed class InviteCodeFactor : ILoginFactor
{
    public string Name => "daily-code";
    public string Prompt => "今天群公告里的邀请码";
    public bool AppliesTo(string user) => true;
    public bool Verify(string user, string response, DateTimeOffset now) => response == $"JAY{now:MMdd}";
}

sealed class ConsoleObserver : IDirectoryObserver
{
    public void OnAnnounced(RoomListing listing, bool listed, bool isNew)
    {
        if (isNew) Console.WriteLine($"新房间：{listing.Name}（{(listed ? "公开" : "不公开")}）");
    }

    public void OnRejected(RoomListing listing, RoomVerdict verdict) => Console.WriteLine($"没收：{listing.Name}（{verdict.Code}）");
}

sealed class MotdExtension : IDirectoryExtension
{
    public string Op => "x-motd";
    public JsonObject Handle(JsonObject request, DirectoryView directory) =>
        new() { ["text"] = "周末有建筑比赛！", ["rooms"] = directory.Listed };
}
