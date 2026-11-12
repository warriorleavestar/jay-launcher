using System.Globalization;
using System.Text;
using Amber.Core.Launch;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Gate;
using Amber.Core.Multiplayer.Public;
using Amber.Core.Multiplayer.Transport;
using Amber.Core.Net;
using Amber.NetworkServer;
using Amber.NetworkServer.Hardware;

namespace Amber.Server;

/// <summary>
/// Jay 服务器版（jay-server）：在 VPS 或局域网里的一台电脑上开一个只能用 Jay 进的 MC 服务器。
/// 同一个程序既跑网关，也管 MC 服务端；管理命令（发邀请、撤销设备）可以在服务运行时另开一个窗口执行。
/// </summary>
public static class Program
{
    private const string Usage = """
        Jay 服务器版

        第一次用：
          jay-server init --host <玩家连的地址> [--port 47670]
              地址可以是 VPS 的公网 IP / 域名，也可以是局域网 IP（比如 192.168.1.20）
          jay-server setup [--version latest|1.21.1] [--memory 2048] [--accept-eula]
              下载官方服务端和合适的 Java；需要同意 Minecraft EULA（https://aka.ms/MinecraftEULA）
          jay-server run [--no-mc]
              启动网关和 MC 服务端。在窗口里直接输入 MC 控制台命令；以 jay 开头的是下面的管理命令

        管理（服务运行时也可以另开窗口执行）：
          jay-server invite <用户名> [--ttl 24h] [--uses 1]   生成邀请码，发给这个玩家
          jay-server list                                     名单和设备
          jay-server invites                                  还没用掉的邀请
          jay-server cancel-invite <编号>
          jay-server revoke <用户名> [<设备码前几位>]          撤销设备（不写设备码就撤销全部）
          jay-server disable <用户名> / enable <用户名>
          jay-server remove <用户名>
          jay-server online-mode on|off                        正版验证（默认 off：离线账号，由网关核对用户名）

        隐私+（玩家经两个不同节点各走一半，源站地址不外传）：
          jay-server relay --origin <网关地址:端口> [--listen 47671] [--code JAYR1-...]
              在节点电脑上运行（VPS 或局域网电脑都行）：只把连接原样转给网关，不存玩家的任何密钥
              带上节点码时还会给网关发心跳（隐私++ 要用）
          jay-server relays add <节点地址:端口> [--source <网关看到的节点 IP>]
              加节点并打印它的节点码。节点在局域网里、或者和网关在同一台机器上时，--source 填网关这边看到的它的 IP
          jay-server relays code <节点地址:端口>              重发节点码（旧码作废；节点重装后也用这个）
          jay-server relays remove <节点地址:端口> / relays list
          jay-server relays revoke <节点地址:端口>             吊销不可信的节点：马上断开所有人，重新握手换新密钥
          jay-server privacy basic|plus|plusplus               plus 至少要两个节点；plusplus 还要节点发心跳，不够就熔断
          jay-server status                                    节点心跳、熔断状态、在线人数（服务运行时）

        入口端口轮换（像验证器 App 的动态码：入口端口每隔一段时间按种子换一个，扫描器找不到，泄露的旧地址自动过期）：
          jay-server rotate on [--window 10m] [--range 20000-45000] [--grace 3d] [--new-seed]
              普通模式轮换网关端口，隐私+/++ 轮换节点端口（节点要带节点码运行）。
              grace 期间固定端口照开，老玩家进一次服就会拿到轮换信息；之后固定端口关闭
          jay-server rotate off / rotate status

        没有域名的服务器（邀请码里不写 IP）：
          jay-server records on [--network <连接串或 .jaynet 文件>]   在公共网络的目录里放一份签名的地址记录（默认 Jay 公共网络），
              之后的邀请码只写记录编号，玩家入服时到目录里查地址；不用口令。服务运行时每 12 小时续一次
          jay-server records off / records status                    关掉后，没有域名的服务器回到口令加密的邀请码

        公共网络节点（Jay 公共网络 / 自己的公共网络的种子节点 + 房间目录）：
          jay-server directory [--listen 11010] [--seeds tcp://别的种子:11010,...] [--easytier <放 easytier-core 的文件夹> [--easytier-sha256 <easytier-core 的 SHA-256>]]
              在有公网 IP 的机器上运行，要放行 TCP 和 UDP 的 11010 端口。房间目录只在公共网络里可达（10.235.0.1:47668），不对公网开放。
              目录只存房主签过名的房间信息（名字、人数、敲门地址），不经手任何密钥；90 秒没续期的房间自动消失。
              --config <网络配置.json>        节点也按配置里允许的房间方式挡一道（防改过的客户端）
              --blocked-words <文件>          屏蔽词，一行一个：公开房间的名字、房主名里有就不收
              --rate-limit 3,6,600            每人同时几个房间、每人每分钟新发布几次、整个目录每分钟几个新房间（默认 3,6,600；--no-rate-limit 关掉）
              --admin-port 47669 / --no-admin 本机管理接口（只听 127.0.0.1，口令在数据目录的 admin.token；/health 不用口令）
              --seed-only                     只当种子节点帮忙转发（分地区多开几台），不跑目录
              --no-volunteers                 不收志愿节点（玩家自愿帮忙转发的电脑；默认收，网络配置 policy.volunteers 为 false 时也不收）
              --signed-volunteers             只收用 Jay 账户签名登记的志愿节点（每个节点都知道是谁在运行，可以按人封禁）
              --directory-index 2             备用目录节点（2–4，主目录是 1）：Jay 把房间发给每个在线的目录，一台挂了别的照常。
                                              备用节点要和主节点用同样的 --config、--blocked-words，开登录时复制 login.key 和 accounts.json
          jay-server node status|rooms|bans|nodes  看运行中节点的计数、公开房间、封禁名单、志愿中转
          jay-server node bind [--check | --install <证书>]  把节点绑定到这台机器的硬件（TPM、硬盘、主板、machine-id），全程离线：
              不带参数：生成 hardware.request.json（只有加盐哈希）→ 在离线电脑上 network hw-cert 签名 → 拷回来 --install。
              绑定后 directory.key、login.key 只以加密形式存在；硬件对不上时节点删除数据目录里的 Jay 文件，运行环境被动过（调试器、注入、
              文件可被别人改、要求的安全启动关了、TPM 不解封）时拒绝运行。--check 只检查不删除。directory --wipe-dry-run 只列出会删什么。
          jay-server node panel               打印网页管理面板的链接（只能在本机打开；远程服务器用 ssh -L 转发端口）
          jay-server node ban host|room|account|room-host|node|operator <房主指纹|房间编号|账号|房间编号|志愿节点地址|志愿节点运营者的 Jay 账户> [--for 7d] [--reason 原因]
              封禁房主（换房间也进不了列表）、房间、网络账号（也不能再登录），或者某个房间的房主（指纹 + 他登录的账号）。
              马上把已经在目录里的房间拿掉；--for 30m/24h/7d 是临时封禁，到点自动解除。node unban host|room|account|room-host|node|operator <...>
          jay-server node remove <房间编号>     只删掉这一个房间（房主续期时会重新审核）

        自己的公共网络（连接串 JAYNET2-...）：
          jay-server network init <编号> --name <名字> --host <公网地址> [--listen 11010] [--out <文件夹>]
              一步生成签名钥匙、网络 JSON 和网络配置模板，并打印后面几步
          jay-server network config-hash <配置.json> --network <网络.json> [--url https://...]
              检查配置文件、算 SHA-256；给了 --url 就写进网络 JSON（版本号加一），然后重新 sign
          jay-server network keygen --out <钥匙文件> [--no-passphrase]  生成签名钥匙（Ed25519 + ML-DSA-65，默认用口令加密），打印公钥
          jay-server network sign <网络.json> --key <钥匙文件>  网络主人签名并打印连接串
          jay-server network cosign <连接串或文件> --key <信任钥匙文件> [--prev <上一版>] [--out <文件>]
              再加一个签名（Jay 信任人离线审核用，够数才算“已审核”）：先逐项显示内容，手打网络编号最后 6 位才签
          jay-server network check <网络.json> ...             只检查格式（注册表的 PR 检查用）
          jay-server network show <连接串或文件>              显示连接串里的内容和签名
          jay-server network dir-key [<网络.json>] --dir <节点数据目录>  打印目录公钥（没有就生成 directory.key），给了网络 JSON 就写进 dirKeys
          jay-server network root-key --out <钥匙文件> [--paper]  生成一把信任 / 根钥匙（每个信任人在自己的离线电脑上各生成一把），
              用口令加密；--paper 在屏幕上显示手抄的两行
          jay-server network key-public --key <钥匙文件>     打印钥匙的公钥、编号和指纹
          jay-server network paper-check --key <钥匙文件>    照着纸输入两行，核对抄得对不对
          jay-server network paper-restore --out <钥匙文件>  从纸上的两行恢复钥匙文件
          jay-server network dir-cert --root <根钥匙文件> --network <EasyTier 网络名> --dir-key <目录公钥> --out <证书文件> [--days 365]
              在离线电脑上用根钥匙给节点的目录公钥（dir-key 打印的）签证书，再把证书拷到节点数据目录，命名为 directory.cert
          jay-server network dir-cert --cosign <证书文件> --root <根钥匙文件>
              另一个信任人在自己的离线电脑上给同一张证书加签名（网络要求几个就签几个）
              （根钥匙和节点在同一台电脑时也可以用 --dir <节点数据目录> 直接写进去，但根钥匙不该放在服务器上）
          jay-server network hw-cert --root <根钥匙文件> --request <hardware.request.json> --out <hardware.cert> [--need N] [--secure-boot] [--no-tpm] [--network <网络名>]
              在离线电脑上给节点的硬件签证书（默认：只允许一项硬件标识变化，有 TPM 就必须对上 TPM）。先逐项显示内容和核对码，手打目录节点公钥最后 6 位才签
          jay-server network hw-cert --cosign <hardware.cert> --root <根钥匙文件>
              另一位信任人在自己的离线电脑上加签（Jay 公共网络要三人中两人；自己的网络看 dirRootsNeed），同样先显示再手打确认
          jay-server node bind --install <证书> [--network <连接串或文件>]  只认签名够数的证书（不加 --network 就是 Jay 信任人）
          jay-server directory --network <连接串或文件> ...     用自己的公共网络当节点（不加就是 Jay 公共网络）
          网络 JSON 的格式见 networks/README.md。

        公共网络账号（网络节点管账号，Jay 里登录；房主可以只让登录过的人进）：
          jay-server directory --accounts [--require-2fa] [--factor <名字>:<提示>:<程序>] ...
              开账号登录。第一次会生成登录钥匙（数据目录里的 login.key，不要外传），并打印要写进网络配置的 login 段。
              --require-2fa：每个账号都必须开两步验证。
              --factor：接自己的验证方式（邮件码、短信、会员系统……）：程序从标准输入读 {"user","response","network"}，退出码 0 = 通过。可以写多个，用逗号隔开。
          jay-server account add|passwd <账号>      加账号 / 改密码（密码在终端输入；脚本里可以用环境变量 JAY_ACCOUNT_PASSWORD）
          jay-server account remove|disable|enable <账号> / account list
          jay-server account 2fa on <账号> [--issuer <网络名>]   开两步验证：打印密钥和 otpauth 地址，用验证器 App 添加
          jay-server account 2fa off <账号>提交到 Jay 仓库的 networks/ 审核合并后，CI 会用 Jay 注册表钥匙签名，Jay 里显示“已审核”。

        发版签名（维护者在离线电脑上用，见 docs/security/release-signing.md）：
          jay-server release sums <文件...> [--out SHA256SUMS.txt]  算发布包的 SHA-256 清单
          jay-server release prepare --tag <v0.9.0> --seq <序号> --commit <40 位提交号> [--sums SHA256SUMS.txt] [--out SHA256SUMS.txt.sigs]
              写出还没人签的发布签名文件（序号每次发布加 1）
          jay-server release cosign --key <发版钥匙文件> [--sums SHA256SUMS.txt] [--sigs SHA256SUMS.txt.sigs]
              信任人在离线电脑上看清要签的版本、提交号和文件清单，手打版本号后签；3 人里要 2 人
          jay-server release trust-change --key <发版钥匙文件> --to <新名单文件> --need <几人> --seq <序号> [--from <旧名单文件>] [--chain trust-chain.json]
              换信任人：旧名单里够数的人签了，旧版 Jay 才认新名单（名单文件每行一把完整公钥）
          jay-server release verify <安装包> [--tag <v0.9.0>]   核对安装包、清单、签名和 trust-chain.json（放在安装包旁边）
          EasyTier 加固版（Yumao-Feather/jay-easytier）的发布也要 2/3 信任人签：prepare 和 verify 加 --component easytier，--tag 用它自己的（例如 jay-v2.6.4-1）

        通用选项：--dir <数据目录>（默认是程序旁边的 jay-server-data，也可以用环境变量 JAY_SERVER_DIR）
        """;

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            var options = Options.Parse(args);
            if (options.WantsHelp) return Help();
            var root = options.Get("dir") ?? Environment.GetEnvironmentVariable("JAY_SERVER_DIR") ?? Path.Combine(AppContext.BaseDirectory, "jay-server-data");
            root = Path.GetFullPath(root);
            return options.Command switch
            {
                "init" => Init(root, options),
                "setup" => await SetupAsync(root, options),
                "run" => await RunAsync(root, options),
                "relay" => await RelayAsync(options),
                "directory" => await DirectoryAsync(root, options),
                "network" => NetworkCommand(options, Console.Out, root),
                "release" => ReleaseCommand(options, Console.Out),
                "account" => AccountCommand(root, options, Console.Out, ReadPassword),
                "node" => await NodeCommand(root, options, Console.Out),
                null => Help(),
                _ => Admin(root, options, Console.Out),
            };
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or IOException or InvalidDataException or UnauthorizedAccessException or FormatException)
        {
            Console.Error.WriteLine("出错：" + e.Message);
            return 1;
        }
    }

    private static int Help()
    {
        Console.WriteLine(Usage);
        return 0;
    }

    private static GateStore Gate(string root)
    {
        var dir = Path.Combine(root, "gate");
        if (!GateStore.IsInitialized(dir)) throw new InvalidOperationException("还没初始化，先运行 jay-server init --host <地址>");
        return new GateStore(dir);
    }

    private static int Init(string root, Options options)
    {
        var host = options.Get("host") ?? throw new ArgumentException("要用 --host 指定玩家连的地址（公网 IP、域名或局域网 IP）");
        var port = options.GetInt("port", 47670);
        var listen = options.GetInt("listen-port", port);
        var store = GateStore.Initialize(Path.Combine(root, "gate"), host, port, listen);
        using var key = store.LoadServerKey();
        Console.WriteLine($"数据目录：{root}");
        Console.WriteLine($"网关地址：{host}:{port}（监听 {listen}）");
        Console.WriteLine($"服务器身份：{MemberIdentity.FingerprintOf(key.PublicKey)}");
        Console.WriteLine("下一步：jay-server setup --accept-eula");
        return 0;
    }

    private static async Task<int> SetupAsync(string root, Options options)
    {
        Gate(root);
        var host = new McServerHost(root);
        var memory = options.GetInt("memory", 2048);
        if (memory is < 512 or > 65536) throw new ArgumentException("--memory 要在 512–65536（MB）之间");
        if (!options.Has("accept-eula") && !host.EulaAccepted)
        {
            Console.Write("运行 MC 服务端需要同意 Minecraft EULA（https://aka.ms/MinecraftEULA）。同意请输入 yes：");
            if (!string.Equals(Console.ReadLine()?.Trim(), "yes", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("没有同意 EULA，已取消。");
                return 1;
            }
        }
        using var http = Downloader.CreateHttpClient();
        var downloader = new Downloader(http, SourceChain.Default());
        var settings = await host.SetupAsync(downloader, options.Get("version") ?? "latest", memory, Console.WriteLine);
        host.AcceptEula();
        Console.WriteLine($"MC {settings.Version} 准备好了（Java：{settings.JavaPath}，内存 {settings.MemoryMb} MB，只监听 127.0.0.1:{settings.Port}）。");
        Console.WriteLine("下一步：jay-server run，然后 jay-server invite <用户名>");
        return 0;
    }

    private static async Task<int> RunAsync(string root, Options options)
    {
        var log = (string line) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [Jay] {line}");
        await using var runner = JayServerRunner.Start(root, !options.Has("no-mc"), log, Console.WriteLine);

        // 不 Dispose：退出时 ProcessExit 还可能来取消它。
        var stop = new CancellationTokenSource();
        void Stop() { try { stop.Cancel(); } catch (ObjectDisposedException) { } }
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; Stop(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Stop();
        using var sigterm = OperatingSystem.IsWindows() ? null
            : System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; Stop(); });
        // MC 服务端自己退出了：网关一起停。
        _ = runner.Exited.ContinueWith(_ => Stop(), TaskScheduler.Default);

        // 控制台：普通输入交给 MC；jay 开头的是管理命令。没有可交互的控制台（作为服务运行）时只等结束信号。
        var input = Task.Run(async () =>
        {
            if (Console.IsInputRedirected && !options.Has("console")) return;
            while (!stop.IsCancellationRequested)
            {
                var line = await Console.In.ReadLineAsync(stop.Token);
                if (line is null) return;
                line = line.Trim();
                if (line.Length == 0) continue;
                if (line == "stop" || line == "jay stop") { Stop(); return; }
                if (line.StartsWith("jay ", StringComparison.Ordinal))
                {
                    try { Admin(root, Options.Parse(Split(line[4..])), Console.Out); }
                    // Any failure here (a corrupt store, a bad key file, ...) must not end this loop: it is also what forwards
                    // console input to the Minecraft server.
                    catch (Exception e) when (e is not OperationCanceledException) { Console.WriteLine("出错：" + e.Message); }
                }
                else runner.Command(line);
            }
        });

        try { await Task.Delay(Timeout.Infinite, stop.Token); }
        catch (OperationCanceledException) { }

        log("正在关闭……");
        await runner.StopAsync(TimeSpan.FromSeconds(60));
        return 0;
    }

    /// <summary>节点：不需要初始化网关数据目录，只要知道源站地址。</summary>
    private static async Task<int> RelayAsync(Options options)
    {
        var origin = GateEndpoint.TryParse(options.Get("origin")) ?? throw new ArgumentException("要用 --origin <网关地址:端口> 指定源站");
        var listen = options.GetInt("listen", 47671);
        var log = (string line) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [Jay 节点] {line}");
        GateRelayCode? code = null;
        X25519KeyPair? key = null;
        if (options.Get("code") is { } text)
        {
            code = GateRelayCode.TryParse(text) ?? throw new ArgumentException("节点码不对（应该是 JAYR1- 开头的一串）");
            var dir = options.Get("dir") ?? Environment.GetEnvironmentVariable("JAY_RELAY_DIR") ?? Path.Combine(AppContext.BaseDirectory, "jay-relay-data");
            key = GateRelay.LoadOrCreateKey(Path.Combine(Path.GetFullPath(dir), "relay.key"));
        }
        using var _ = key;
        await using var relay = new GateRelay(options.Get("bind") ?? "::", listen, origin.Host, origin.Port, log: log, code: code, key: key);
        relay.Start();
        log(code is null
            ? $"节点在端口 {relay.Port} 上运行，转给 {origin}（没带节点码：不发心跳，隐私++ 下不算健康节点）"
            : $"节点在端口 {relay.Port} 上运行，转给 {origin}，节点身份 {MemberIdentity.FingerprintOf(key!.PublicKey)}，每 5 秒发一次心跳");
        var stop = new CancellationTokenSource();
        void Stop() { try { stop.Cancel(); } catch (ObjectDisposedException) { } }
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; Stop(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Stop();
        using var sigterm = OperatingSystem.IsWindows() ? null
            : System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; Stop(); });
        try { await Task.Delay(Timeout.Infinite, stop.Token); }
        catch (OperationCanceledException) { }
        log("正在关闭……");
        return 0;
    }

    /// <summary>
    /// A self-supplied easytier-core is not covered by Jay's pinned package hashes. Always log its SHA-256 so the
    /// operator can compare it with the build they meant to use; with --easytier-sha256, refuse anything else.
    /// </summary>
    internal static void CheckEasyTierBinary(string corePath, string? expectedSha256, Action<string> log)
    {
        string actual;
        using (var stream = File.OpenRead(corePath)) actual = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream));
        if (expectedSha256 is null)
        {
            log($"用的是自己指定的 EasyTier（{corePath}），没有校验。SHA-256：{actual}。确认无误后可以加 --easytier-sha256 {actual} 固定它。");
            return;
        }
        if (!string.Equals(actual, expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{corePath} 的 SHA-256 是 {actual}，和 --easytier-sha256 给的不一样，不启动。");
        log($"EasyTier 校验通过（SHA-256 {actual}）");
    }

    /// <summary>
    /// Release signing for trustees (privacy plan items 2 and 3). Every trustee signs on their own offline machine with
    /// their own release key (separate from the key that reviews networks); a release needs 2 of the 3. Nothing here can run in CI.
    /// </summary>
    internal static int ReleaseCommand(Options options, TextWriter output, Func<IReadOnlyList<string>, string, string, bool>? confirm = null)
    {
        confirm ??= TrustCommands.ConfirmOnConsole;
        var sub = options.Positional.FirstOrDefault() ?? throw new ArgumentException("用法：jay-server release sums|prepare|cosign|trust-change|verify");
        var rest = options.Positional.Skip(1).ToList();
        switch (sub)
        {
            case "sums":
            {
                if (rest.Count == 0) throw new ArgumentException("要给出发布包文件");
                foreach (var f in rest) if (!File.Exists(f)) throw new IOException($"找不到 {f}");
                var text = ReleaseSignature.BuildSums(rest);
                var outPath = options.Get("out");
                if (outPath is null) output.Write(text);
                else { File.WriteAllText(outPath, text); output.WriteLine($"已写入 {outPath}（{rest.Count} 个文件）"); }
                return 0;
            }
            case "prepare":
            {
                // --component easytier: a build of Jay's EasyTier fork (its own tag, such as jay-v2.6.4-1), not a Jay release.
                var component = options.Get("component");
                if (component is not null && !ReleaseStatement.Components.Contains(component))
                    throw new ArgumentException($"不认识的 --component：{component}（可用：{string.Join("、", ReleaseStatement.Components)}）");
                var rawTag = options.Get("tag") ?? throw new ArgumentException("要用 --tag 指定版本（例如 v0.9.0）");
                var tag = component is null ? ReleaseSignature.NormalizeTag(rawTag) : rawTag.Trim();
                var seq = long.Parse(options.Get("seq") ?? throw new ArgumentException("要用 --seq 指定发布序号（比上一版大 1）"), System.Globalization.CultureInfo.InvariantCulture);
                var commit = (options.Get("commit") ?? throw new ArgumentException("要用 --commit 指定完整的 40 位提交号")).ToLowerInvariant();
                var sumsPath = options.Get("sums") ?? ReleaseSignature.SumsFileName;
                var sums = File.ReadAllBytes(sumsPath);
                if (ReleaseSignature.ParseSums(Encoding.UTF8.GetString(sums)).Count == 0) throw new InvalidDataException($"{sumsPath} 里没有可用的 SHA-256 行");
                var trust = options.Get("trust") is { } t ? ReadTrustList(t, options.Get("need")).Hash : TrustList.Jay.Hash;
                var statement = new ReleaseStatement(tag, seq, commit, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(sums)), trust,
                    (options.Get("log") ?? ReleaseStatement.NoLog).ToLowerInvariant(), component);
                if (!statement.IsValid) throw new ArgumentException("--tag、--seq、--commit 或 --log 的格式不对");
                var outPath = options.Get("out") ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(sumsPath)) ?? ".", ReleaseSignature.SignatureFileName);
                if (File.Exists(outPath)) throw new IOException($"{outPath} 已经存在，不覆盖");
                File.WriteAllText(outPath, ReleaseSignature.Create(statement));
                output.WriteLine($"已写入 {outPath}（还没有人签）。拿到离线电脑上，至少 2 位信任人各自用 release cosign 签。");
                return 0;
            }
            case "cosign":
            {
                var keyPath = options.Get("key") ?? throw new ArgumentException("要用 --key 指定你的发版钥匙文件（jay-release.key）");
                var sumsPath = options.Get("sums") ?? ReleaseSignature.SumsFileName;
                var sigsPath = options.Get("sigs") ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(sumsPath)) ?? ".", ReleaseSignature.SignatureFileName);
                var manifest = File.ReadAllText(sigsPath);
                var sums = File.ReadAllBytes(sumsPath);
                var lines = TrustCommands.DescribeRelease(manifest, sums, options.Get("trust") is { } t ? ReadTrustList(t, options.Get("need")) : TrustList.Jay);
                var tag = ReleaseManifest.Parse(manifest)!.Statement.Tag;
                if (!confirm(lines, "版本号", tag)) return 3;
                var signed = ReleaseSignature.Cosign(manifest, TrustCommands.ForUse(TrustCommands.LoadKey(keyPath), release: true));
                var outPath = options.Get("out") ?? sigsPath;
                File.WriteAllText(outPath, signed);
                output.WriteLine($"已签 {tag}，写入 {outPath}。签够 2 位后，把它和 SHA256SUMS.txt 一起上传到同一个 Release。");
                return 0;
            }
            case "trust-change":
            {
                // From the list this Jay was built with (or --from), to the list in --to: one public key per line.
                var keyPath = options.Get("key") ?? throw new ArgumentException("要用 --key 指定你的发版钥匙文件（jay-release.key）");
                var from = options.Get("from") is { } f ? ReadTrustList(f, options.Get("from-need")) : TrustList.Jay;
                var to = ReadTrustList(options.Get("to") ?? throw new ArgumentException("要用 --to 指定新名单（每行一把完整公钥）"), options.Get("need"));
                var seq = long.Parse(options.Get("seq") ?? throw new ArgumentException("要用 --seq 指定这次换名单的序号（不小于上一版发布序号）"), System.Globalization.CultureInfo.InvariantCulture);
                var chainPath = options.Get("chain") ?? ReleaseSignature.ChainFileName;
                var chain = File.Exists(chainPath) ? File.ReadAllText(chainPath) : null;
                if (!confirm(TrustCommands.DescribeTrustChange(from, to, seq), "新名单编号", TrustCommands.CodeOf(to.Hash))) return 3;
                File.WriteAllText(options.Get("out") ?? chainPath, TrustChain.Cosign(chain, from, to.Keys, to.Need, seq, TrustCommands.ForUse(TrustCommands.LoadKey(keyPath), release: true)));
                output.WriteLine($"已签换名单，写入 {options.Get("out") ?? chainPath}。旧名单里签够 {from.Need} 位后，把它放进之后每个 Release。");
                return 0;
            }
            case "verify":
            {
                var package = rest.FirstOrDefault() ?? throw new ArgumentException("要给出安装包文件");
                var own = options.Get("trust") is { } t ? ReadTrustList(t, options.Get("need")) : null;
                var check = ReleaseSignature.VerifyPackageAsync(package, options.Get("tag"), options.Get("sums"), options.Get("sigs"), options.Get("chain"), own,
                        component: options.Get("component"))
                    .GetAwaiter().GetResult();
                output.WriteLine(DescribeVerdict(check, Path.GetFileName(package)));
                return check.IsValid ? 0 : 2;
            }
            default:
                throw new ArgumentException($"不认识的 release 子命令：{sub}");
        }
    }

    internal static string DescribeVerdict(ReleaseCheck check, string package) => check.Verdict switch
    {
        ReleaseVerdict.Valid => $"通过：{package} 是 {check.Tag}（第 {check.Seq} 版）的正式发布包，够数的 Jay 信任人签过。",
        ReleaseVerdict.Unsigned => "没有签名：安装包旁边找不到 SHA256SUMS.txt 和 SHA256SUMS.txt.sigs。",
        ReleaseVerdict.NoTrustedKey => "这个版本的程序里还没有信任人公钥，没法核对。",
        ReleaseVerdict.Malformed => "签名文件格式不对。",
        ReleaseVerdict.SumsMismatch => "签名文件和 SHA256SUMS.txt 对不上（清单被改过）。不要安装。",
        ReleaseVerdict.Rollback => $"{check.Tag} 比已经装过的版本旧。不要安装。",
        ReleaseVerdict.BrokenChain => "这个版本写的信任人名单，从程序自带的名单接不上（trust-chain.json 缺了或签得不够）。不要安装。",
        ReleaseVerdict.NotEnoughSignatures => "签的信任人不够（要 3 人里 2 人，两种签名都要对）。不要安装。",
        ReleaseVerdict.WrongTag => $"签名属于 {check.Tag}，不是你要的版本。不要安装。",
        ReleaseVerdict.NotListed => "清单里没有这个文件名。",
        ReleaseVerdict.HashMismatch => "安装包的 SHA-256 和清单不一样（文件被改过或没下载完整）。不要安装。",
        ReleaseVerdict.WrongComponent => "签名是给别的程序的（Jay 发布和 EasyTier 加固版的签名不能互换；核对 EasyTier 包要加 --component easytier）。不要安装。",
        _ => check.Verdict.ToString(),
    };

    /// <summary>A trustee list file: one full public key (base64) per line; # starts a comment.</summary>
    private static TrustList ReadTrustList(string path, string? need)
    {
        var keys = File.ReadAllLines(path).Select(l => l.Split('#')[0].Trim()).Where(l => l.Length > 0).Select(Convert.FromBase64String).ToList();
        if (keys.Count == 0 || keys.Any(k => k.Length != TrustSigner.PublicKeySize)) throw new InvalidDataException($"{path} 里要每行一把完整公钥（{TrustSigner.PublicKeySize} 字节）");
        var n = need is null ? keys.Count / 2 + 1 : int.Parse(need, System.Globalization.CultureInfo.InvariantCulture);
        if (n < 1 || n > keys.Count) throw new ArgumentException("--need 要在 1 和公钥数之间");
        return new TrustList(keys, n);
    }

    /// <summary>公共网络连接串：生成钥匙、签名、检查、显示。</summary>
    internal static int NetworkCommand(Options options, TextWriter output, string? root = null)
    {
        var sub = options.Positional.FirstOrDefault() ?? throw new ArgumentException("用法：jay-server network init|keygen|sign|cosign|config-hash|check|show|root-key|key-public|paper-check|paper-restore|dir-key|dir-cert");
        var rest = options.Positional.Skip(1).ToList();
        switch (sub)
        {
            case "keygen":
            {
                var path = options.Get("out") ?? throw new ArgumentException("要用 --out 指定钥匙文件");
                if (File.Exists(path)) throw new IOException($"{path} 已经存在，不覆盖（换钥匙请换个文件名）");
                var passphrase = TrustCommands.NewPassphrase(options);
                var signer = TrustSigner.Generate(out var seed);
                TrustCommands.WriteKey(path, seed, passphrase);
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(seed);
                output.WriteLine($"私钥已存到 {path}（{(passphrase is null ? "没有口令" : "已用口令加密")}；不要提交、不要发给别人）");
                foreach (var line in TrustCommands.PublicLines(signer)) output.WriteLine(line);
                return 0;
            }
            case "sign":
            {
                var file = rest.FirstOrDefault() ?? throw new ArgumentException("要给网络 JSON 文件");
                var keyFile = options.Get("key") ?? throw new ArgumentException("要用 --key 指定网络主人的钥匙文件");
                var info = ReadNetwork(file);
                output.WriteLine(NetworkString.Create(info, TrustCommands.LoadKey(keyFile)));
                return 0;
            }
            case "cosign":
            {
                var text = rest.FirstOrDefault() ?? throw new ArgumentException("要给连接串或文件");
                var keyFile = options.Get("key") ?? throw new ArgumentException("要用 --key 指定自己的信任钥匙文件");
                var current = File.Exists(text) ? File.ReadAllText(text) : text;
                var previous = options.Get("prev") is { } prev ? File.Exists(prev) ? File.ReadAllText(prev) : prev : null;
                var info = NetworkString.Parse(current, now: DateTimeOffset.UtcNow).Info;
                if (!TrustCommands.ConfirmOnConsole(TrustCommands.DescribeNetwork(current, previous), "网络编号", TrustCommands.CodeOf(info.Id))) return 3;
                var signed = NetworkString.Cosign(current, TrustCommands.ForUse(TrustCommands.LoadKey(keyFile), release: false));
                if (options.Get("out") is { } outPath) { File.WriteAllText(outPath, signed + "\n"); Console.Error.WriteLine($"已写 {outPath}"); }
                else output.WriteLine(signed);
                var parsed = NetworkString.Parse(signed);
                Console.Error.WriteLine($"签名数 {parsed.Signers.Count}，Jay 信任人 {JayTrust.Quorum.Count(parsed.Signers)}/{JayTrust.Need}{(parsed.Reviewed ? "，已审核" : "")}");
                return 0;
            }
            case "check":
            {
                if (rest.Count == 0) throw new ArgumentException("要给网络 JSON 文件");
                var bad = 0;
                var ids = new HashSet<string>();
                foreach (var file in rest)
                {
                    try
                    {
                        var info = ReadNetwork(file);
                        if (Path.GetFileNameWithoutExtension(file) != info.Id) throw new InvalidDataException($"文件名要和编号一样（{info.Id}.json）");
                        if (!ids.Add(info.Id)) throw new InvalidDataException($"编号 {info.Id} 重复");
                        output.WriteLine($"{file}：没问题（{info.Name}，{info.Nodes.Count} 个节点）");
                    }
                    catch (Exception e) when (e is InvalidDataException or FormatException or System.Text.Json.JsonException or InvalidOperationException)
                    {
                        output.WriteLine($"{file}：{e.Message}");
                        bad++;
                    }
                }
                return bad == 0 ? 0 : 1;
            }
            case "init":
            {
                // 一步准备好自己的公共网络：签名钥匙 + 网络 JSON + 网络配置模板。
                var id = rest.FirstOrDefault() ?? throw new ArgumentException("用法：jay-server network init <编号> --name <名字> --host <公网地址> [--listen 11010] [--out <文件夹>]");
                var name = options.Get("name") ?? throw new ArgumentException("要用 --name 给网络起个名字");
                var host = options.Get("host") ?? throw new ArgumentException("要用 --host 给节点的域名（网络连接串里不能写公网 IP）");
                var port = options.GetInt("listen", 11010);
                var dir = Path.GetFullPath(options.Get("out") ?? id);
                var nodes = new List<string> { $"tcp://{host}:{port}", $"udp://{host}:{port}" };
                var r = NetworkScaffold.Init(dir, id, name, nodes);
                output.WriteLine($"已生成（在 {dir}）：");
                output.WriteLine($"  {Path.GetFileName(r.KeyPath)}：签名私钥，只自己留着，不要提交、不要发给别人");
                output.WriteLine($"  {Path.GetFileName(r.NetworkPath)}：网络信息（名字、EasyTier 网络名和密钥、节点）");
                output.WriteLine($"  {Path.GetFileName(r.ConfigPath)}：网络配置（公告、链接、安全策略），按需修改");
                output.WriteLine($"签名钥匙编号：{Convert.ToBase64String(r.KeyId)}");
                output.WriteLine("下一步：");
                output.WriteLine($"  1. 改好 config.json，传到一个 https 地址（比如 GitHub Pages），然后：");
                output.WriteLine($"     jay-server network config-hash {r.ConfigPath} --network {r.NetworkPath} --url https://<地址>/config.json");
                output.WriteLine($"  2. jay-server network sign {r.NetworkPath} --key {r.KeyPath} > {id}.jaynet");
                output.WriteLine($"  3. jay-server directory --network {id}.jaynet --config {r.ConfigPath} --listen {port}");
                output.WriteLine("  不要配置文件就跳过第 1 步。");
                return 0;
            }
            case "config-hash":
            {
                // 检查配置文件 Jay 能不能读，算 SHA-256；给了 --network 和 --url 就顺手写进网络 JSON（版本号加一）。
                var file = rest.FirstOrDefault() ?? throw new ArgumentException("要给配置文件");
                var networkFile = options.Get("network");
                var networkId = options.Get("id") ?? (networkFile is not null ? ReadNetwork(networkFile).Id
                    : throw new ArgumentException("要用 --network <网络.json> 或 --id <编号> 说明是哪个网络的配置"));
                var (config, sha) = NetworkScaffold.Hash(file, networkId);
                output.WriteLine($"配置文件没问题：允许的房间方式 {string.Join("、", config.Policy.AllowedModes.Order())}，" +
                    $"{(config.Login is { } l ? l.Required ? "要求登录" : "可选登录" : "不用登录")}，{config.Links.Count} 个链接，" +
                    $"{(config.Policy.MinVersion is { } min ? $"最低 Jay {min}（节点挡更旧的）" : "不限 Jay 版本")}");
                output.WriteLine($"SHA-256：{sha}");
                if (options.Get("url") is { } url)
                {
                    if (networkFile is null) throw new ArgumentException("要写进网络 JSON 时要给 --network <网络.json>");
                    var info = NetworkScaffold.AttachConfig(networkFile, url, sha);
                    output.WriteLine($"已写进 {networkFile}（版本号 {info.Seq}）。重新签名：jay-server network sign {networkFile} --key <钥匙文件>");
                }
                else output.WriteLine($"写进网络 JSON：\"config\": {{ \"url\": \"https://<地址>\", \"sha256\": \"{sha}\" }}");
                output.WriteLine("注意：改了配置文件就要重新算哈希、重新签连接串，玩家粘贴新连接串后才生效。");
                return 0;
            }
            case "root-key":
            {
                // A trustee's or directory root's key: Ed25519 + ML-DSA-65 from one seed, sealed with a passphrase.
                var path = options.Get("out") ?? throw new ArgumentException("要用 --out 指定钥匙文件");
                if (File.Exists(path)) throw new IOException($"{path} 已经存在，不覆盖（换钥匙请换个文件名）");
                var passphrase = TrustCommands.NewPassphrase(options);
                var signer = TrustSigner.Generate(out var seed);
                TrustCommands.WriteKey(path, seed, passphrase);
                if (options.Has("paper"))
                {
                    // Shown once, on this offline screen only, for copying by hand. Never written to a file.
                    Console.Error.WriteLine("手抄备份（这是私钥，只抄在纸上，不要拍照）：");
                    foreach (var line in PaperKey.ToLines(seed)) Console.Error.WriteLine("    " + line);
                }
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(seed);
                output.WriteLine($"私钥已存到 {path}（{(passphrase is null ? "没有口令" : "已用口令加密")}）：离线保管（U 盘、纸），不要放在服务器、CI、网盘或仓库里");
                foreach (var line in TrustCommands.PublicLines(signer)) output.WriteLine(line);
                return 0;
            }
            case "key-public":
            {
                var path = options.Get("key") ?? throw new ArgumentException("要用 --key 指定钥匙文件");
                foreach (var line in TrustCommands.PublicLines(TrustCommands.LoadKey(path))) output.WriteLine(line);
                return 0;
            }
            case "paper-check":
            case "paper-restore":
            {
                // Check a hand-copied backup against the key file, or rebuild the key file from paper.
                Console.Error.WriteLine("照着纸输入两行（空格、横杠、大小写都不要紧）：");
                byte[] fromPaper;
                try { fromPaper = PaperKey.FromLines(TrustCommands.AskSecret("第 1 行："), TrustCommands.AskSecret("第 2 行：")); }
                catch (InvalidDataException e) { output.WriteLine($"✗ {e.Message}"); return 2; }
                try
                {
                    var signer = TrustSigner.FromSeed(Convert.ToBase64String(fromPaper));
                    if (sub == "paper-check")
                    {
                        var keyPath = options.Get("key") ?? throw new ArgumentException("要用 --key 指定要核对的钥匙文件");
                        var stored = TrustKeyFile.ReadSeed(File.ReadAllText(keyPath), () => TrustCommands.AskSecret($"{Path.GetFileName(keyPath)} 的口令："));
                        var same = System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(stored, fromPaper);
                        System.Security.Cryptography.CryptographicOperations.ZeroMemory(stored);
                        output.WriteLine(same ? $"✓ 抄对了（指纹 {TrustCommands.Fingerprint(signer.Id)}）" : "✗ 两行校验都对，但和钥匙文件不是同一把：可能抄成了另一把钥匙");
                        return same ? 0 : 2;
                    }
                    var outPath = options.Get("out") ?? throw new ArgumentException("要用 --out 指定恢复出的钥匙文件");
                    if (File.Exists(outPath)) throw new IOException($"{outPath} 已经存在，不覆盖");
                    TrustCommands.WriteKey(outPath, fromPaper, TrustCommands.NewPassphrase(options));
                    output.WriteLine($"✓ 已恢复到 {outPath}");
                    foreach (var line in TrustCommands.PublicLines(signer)) output.WriteLine(line);
                    return 0;
                }
                finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(fromPaper); }
            }
            case "dir-cert":
            {
                var rootFile = options.Get("root") ?? throw new ArgumentException("要用 --root 指定根钥匙文件");
                if (options.Get("cosign") is { } existing)
                {
                    var bytes = File.ReadAllBytes(existing);
                    var before = DirectoryCertificate.Parse(bytes) ?? throw new InvalidDataException("不是有效的目录证书（格式或签名不对）");
                    if (!TrustCommands.ConfirmOnConsole(TrustCommands.DescribeCertificate(bytes), "目录节点公钥",
                        TrustCommands.CodeOf(Convert.ToBase64String(before.DirectoryKey).TrimEnd('=')))) return 3;
                    var cosigned = DirectoryCertificate.Cosign(bytes, TrustCommands.ForUse(TrustCommands.LoadKey(rootFile), release: false));
                    File.WriteAllBytes(existing, cosigned);
                    var c = DirectoryCertificate.Parse(cosigned)!;
                    output.WriteLine($"已在 {existing} 上加签名（现在 {c.Signatures.Count} 个，Jay 信任人 {JayTrust.Quorum.Count(c.Signers)}/{JayTrust.Need}）。");
                    return 0;
                }
                var networkName = options.Get("network") ?? throw new ArgumentException("要用 --network 指定 EasyTier 网络名（Jay 公共网络是 " + PublicNetworkProfile.JayNetworkName + "）");
                var days = int.Parse(options.Get("days") ?? "365", System.Globalization.CultureInfo.InvariantCulture);
                if (days is < 1 or > 3650) throw new ArgumentException("--days 要在 1 到 3650 之间");
                // On the offline machine: only the node's public key (printed by dir-key) is needed, never its private key.
                byte[] directoryKey;
                string target;
                if (options.Get("dir-key") is { } given)
                {
                    directoryKey = Convert.FromBase64String(given.Trim());
                    if (directoryKey.Length != DirectoryChannel.KeyLength) throw new ArgumentException("--dir-key 不是目录公钥（32 字节 base64）");
                    target = options.Get("out") ?? throw new ArgumentException("用 --dir-key 时要用 --out 指定证书文件");
                }
                else
                {
                    var dataDir = root ?? throw new ArgumentException("要用 --dir-key <目录公钥> --out <文件>，或 --dir <节点数据目录>");
                    directoryKey = DirectoryPublicKey(dataDir);
                    target = Path.Combine(dataDir, "directory.cert");
                }
                var expires = DateTimeOffset.UtcNow.AddDays(days);
                var review = TrustCommands.DescribeCertificateContent(directoryKey, networkName, expires);
                review.Add("已经签过的：还没有人（你是第一个）");
                if (!TrustCommands.ConfirmOnConsole(review, "目录节点公钥", TrustCommands.CodeOf(Convert.ToBase64String(directoryKey).TrimEnd('=')))) return 3;
                var cert = DirectoryCertificate.Issue(TrustCommands.ForUse(TrustCommands.LoadKey(rootFile), release: false), directoryKey, networkName, expires);
                File.WriteAllBytes(target, cert);
                output.WriteLine($"已写 {target}（到 {expires:yyyy-MM-dd} 有效）。证书不是秘密，可以随节点数据目录一起备份。");
                return 0;
            }
            case "dir-key":
            {
                // This node's directory key (made on first use, same file `jay-server directory` uses), optionally pinned in the network JSON.
                var dataDir = root ?? throw new ArgumentException("要用 --dir 指定节点的数据目录");
                var publicKey = DirectoryPublicKey(dataDir);
                output.WriteLine($"目录公钥 / directory key：{Convert.ToBase64String(publicKey)}");
                if (rest.FirstOrDefault() is { } networkFile)
                {
                    var (info, added) = NetworkScaffold.AttachDirectoryKey(networkFile, publicKey);
                    output.WriteLine(added
                        ? $"已写进 {networkFile} 的 dirKeys（版本号 {info.Seq}）。重新签名：jay-server network sign {networkFile} --key <钥匙文件>"
                        : $"{networkFile} 的 dirKeys 里已经有这把钥匙");
                }
                output.WriteLine("把 directory.key 备份好：换了钥匙的目录在已经粘贴连接串的 Jay 看来就是冒充的。");
                return 0;
            }
            case "hw-cert":
            {
                // Offline: the network's roots sign a node's hardware.request.json one after another
                // (docs/security/node-hardware-binding.md). Each signer sees every field and confirms by hand first.
                var rootFile = options.Get("root") ?? throw new ArgumentException("要用 --root 指定自己的根钥匙文件");
                string target;
                HardwareCertificate shown;
                Func<byte[]> sign;
                if (options.Get("cosign") is { } existing)
                {
                    target = existing;
                    var bytes = File.ReadAllBytes(existing);
                    shown = HardwareCertificate.Parse(bytes) ?? throw new InvalidDataException(existing + " 不是有效的硬件证书（有签名对不上，或格式不认识）");
                    sign = () => HardwareCertificate.Cosign(bytes, TrustCommands.LoadKey(rootFile));
                }
                else
                {
                    var requestFile = options.Get("request") ?? rest.FirstOrDefault() ?? throw new ArgumentException("要用 --request 指定节点生成的 hardware.request.json");
                    target = options.Get("out") ?? throw new ArgumentException("要用 --out 指定证书文件（交给下一位信任人 --cosign，够数后拷回节点 node bind --install）");
                    if (File.Exists(target)) throw new IOException($"{target} 已经存在，不覆盖");
                    var request = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(requestFile)) as System.Text.Json.Nodes.JsonObject
                        ?? throw new InvalidDataException(requestFile + " 不是硬件绑定请求");
                    int? need = options.Get("need") is { } n ? int.Parse(n, System.Globalization.CultureInfo.InvariantCulture) : null;
                    bool? tpm = options.Has("no-tpm") ? false : null;
                    shown = HardwareCertificate.Preview(request, options.Get("network"), need, tpm, options.Has("secure-boot"));
                    sign = () => HardwareCertificate.Issue(TrustCommands.LoadKey(rootFile), request, options.Get("network"), need, tpm, options.Has("secure-boot"));
                }
                if (!ConfirmHardwareCertificate(shown, output)) return 3;
                var cert = sign();
                File.WriteAllBytes(target, cert);
                var parsed = HardwareCertificate.Parse(cert)!;
                output.WriteLine($"已写 {target}，现在有 {parsed.Signatures.Count} 个签名，Jay 信任人 {JayTrust.Quorum.Count(parsed.Signers)}/{JayTrust.Need}（自己的网络看 dirRootsNeed）。");
                output.WriteLine("签名够数后把证书拷回节点：jay-server node bind --install <证书>。硬件对不上时节点会删除自己数据目录里的 Jay 文件。");
                return 0;
            }
            case "show":
            {
                var text = rest.FirstOrDefault() ?? throw new ArgumentException("要给连接串或文件");
                var signed = NetworkString.Parse(File.Exists(text) ? File.ReadAllText(text) : text);
                output.WriteLine(signed.Info.ToJson().ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                output.WriteLine($"网络主人的钥匙编号：{Convert.ToBase64String(signed.SignerKey)}（指纹 {TrustCommands.Fingerprint(signed.SignerKey)}）");
                foreach (var k in signed.Signers.Skip(1)) output.WriteLine($"加签：{TrustCommands.Fingerprint(k)}{(JayTrust.Quorum.Count([k]) == 1 ? "（Jay 信任人）" : "")}");
                output.WriteLine($"Jay 信任人签了 {JayTrust.Quorum.Count(signed.Signers)}/{JayTrust.Need}{(signed.Reviewed ? "，Jay 审核过" : "，未经 Jay 审核")}");
                return 0;
            }
            default:
                throw new ArgumentException("用法：jay-server network init|keygen|sign|cosign|config-hash|check|show|root-key|key-public|paper-check|paper-restore|dir-key|dir-cert");
        }
    }

    private static NetworkInfo ReadNetwork(string file)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file)) as System.Text.Json.Nodes.JsonObject
            ?? throw new InvalidDataException("要是一个 JSON 对象");
        var info = NetworkInfo.FromJson(node);
        if (NetworkString.Validate(info) is { } problem) throw new InvalidDataException(problem);
        return info;
    }

    /// <summary>
    /// 公共网络的种子节点 + 房间目录：用网络服务端框架（Jay.NetworkServer）跑一个 EasyTier（固定地址 10.235.0.1，同时监听公网端口当节点）
    /// + 本机 127.0.0.1:47668 的目录服务 + 本机 127.0.0.1:47669 的管理接口。
    /// </summary>
    private static async Task<int> DirectoryAsync(string root, Options options)
    {
        var log = (string line) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [Jay 公共节点] {line}");
        var builder = DirectoryBuilder(root, options, Console.Out, log);
        JayNetworkServer built;
        try { built = builder.Build(); }
        catch (HardwareMismatchException e)
        {
            log(e.DryRun ? $"硬件检查没通过（只演练，没删除）：{e.Reason}" : $"硬件检查没通过，已删除这个节点的 Jay 文件（{e.Wiped.Count} 项）：{e.Reason}");
            return 77;
        }
        catch (NodeIntegrityException e)
        {
            log("拒绝运行：" + string.Join("；", e.Problems));
            return JayNetworkServer.NodeIntegrityExitCode;
        }
        await using var server = built;
        await server.StartAsync();
        server.EasyTierOutput += line => { if (options.Has("verbose")) log(line); };
        var listen = options.GetInt("listen", 11010);
        log(server.SeedOnly
            ? $"公共网络 {server.Profile.NetworkName} 的种子节点（不跑目录）在端口 {listen}（TCP/UDP）上运行"
            : $"公共网络 {server.Profile.NetworkName} 的节点在端口 {listen}（TCP/UDP）上运行，房间目录在 {server.DirectoryIp}:{PublicNetworkProfile.DirectoryPort}");
        if (server.AdminPort is { } admin) log($"管理接口在 127.0.0.1:{admin}（健康检查 /health；jay-server node status 看状态）");
        log($"在 Jay 的联机设置里把公共节点填成 tcp://<这台机器的公网地址>:{listen}");

        var stop = new CancellationTokenSource();
        void Stop() { try { stop.Cancel(); } catch (ObjectDisposedException) { } }
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; Stop(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Stop();
        using var sigterm = OperatingSystem.IsWindows() ? null
            : System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; Stop(); });
        var code = await server.RunAsync(stop.Token);
        log("正在关闭……");
        return code;
    }

    /// <summary>按 directory 命令的选项配好网络服务端（不启动）。测试直接调。</summary>
    internal static JayNetworkServerBuilder DirectoryBuilder(string root, Options options, TextWriter output, Action<string> log)
    {
        var listen = options.GetInt("listen", 11010);
        if (listen is < 1 or > 65535) throw new ArgumentException("--listen 要是 1–65535 的端口");
        var seeds = (options.Get("seeds") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var builder = new JayNetworkServerBuilder().UseDataDir(root).UseListenPort(listen).UseSeeds(seeds).UseLog(log);
        if (options.Has("wipe-dry-run")) builder.WipeDryRun();

        if (options.Get("easytier") is { } dir)
        {
            var exe = OperatingSystem.IsWindows() ? ".exe" : "";
            var core = Path.Combine(Path.GetFullPath(dir), "easytier-core" + exe);
            var cli = Path.Combine(Path.GetFullPath(dir), "easytier-cli" + exe);
            if (!File.Exists(core) || !File.Exists(cli)) throw new IOException($"{dir} 里没有 easytier-core 和 easytier-cli");
            CheckEasyTierBinary(core, options.Get("easytier-sha256"), log);
            builder.UseEasyTier(core, cli);
        }

        var networkId = PublicNetworkProfile.Jay().Id;
        if (options.Get("network") is { } networkText)
        {
            // 自己的公共网络：用连接串里的网络名和密钥（节点就是这台机器，种子另外给）。
            var signed = NetworkString.Parse(File.Exists(networkText) ? File.ReadAllText(networkText) : networkText);
            builder.UseNetwork(signed);
            networkId = signed.Info.Id;
            log($"用公共网络“{signed.Info.Name}”（{signed.Info.Id}，{(signed.Reviewed ? "Jay 审核过" : "未经 Jay 审核")}）");
        }
        if (options.Has("seed-only"))
        {
            if (options.Has("accounts") || options.Get("config") is not null || options.Get("blocked-words") is not null)
                throw new ArgumentException("--seed-only 的节点不跑目录，不能同时用 --accounts、--config、--blocked-words");
            builder.SeedOnly();
        }
        if (options.Get("directory-index") is not null)
        {
            if (options.Has("seed-only")) throw new ArgumentException("--seed-only 的节点不跑目录，不能用 --directory-index");
            builder.UseDirectoryIndex(options.GetInt("directory-index", 1));
        }
        if (options.Get("config") is { } configPath)
        {
            // 节点这边也按网络配置的 policy.modes 挡一道（防改过的客户端）。
            var (config, sha) = NetworkScaffold.Hash(configPath, networkId);
            builder.UseConfig(config);
            log($"网络配置 {Path.GetFileName(configPath)}（SHA-256 {sha[..12]}…）：允许的房间方式 {string.Join("、", config.Policy.AllowedModes.Order())}");
        }
        if (options.Get("blocked-words") is { } words)
        {
            var filter = NameFilterGate.FromFile(words);
            builder.AddGate(filter);
            log($"屏蔽词 {filter.Count} 个");
        }
        // The directory only speaks the authenticated channel now; the old flag is accepted so existing scripts keep running.
        if (options.Has("secure-directory-only")) log("--secure-directory-only is the default now and can be removed");
        if (options.Has("no-rate-limit")) builder.WithoutRateLimit();
        else if (options.Get("rate-limit") is { } rate)
        {
            var parts = rate.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length != 3 || !parts.All(p => int.TryParse(p, out var n) && n > 0))
                throw new ArgumentException("--rate-limit 格式是 <每人同时几个房间>,<每人每分钟新发布几次>,<整个目录每分钟几个新房间>，比如 3,6,600");
            var n = parts.Select(int.Parse).ToArray();
            builder.UseRateLimit(n[0], n[1], n[2]);
        }
        if (options.Has("no-volunteers")) builder.WithoutVolunteers();
        if (options.Has("signed-volunteers")) builder.RequireSignedVolunteers();
        if (options.Has("no-admin")) builder.WithoutAdmin();
        else builder.UseAdmin(options.GetInt("admin-port", JayNetworkServer.DefaultAdminPort));
        if (!options.Has("seed-only") && LoginFor(root, options, networkId, output) is { } login) builder.UseLogin(login);
        return builder;
    }

    /// <summary>jay-server node：经本机管理接口看运行中的网络节点、封禁、删房间。</summary>
    /// <summary>Where signing confirmations are read from; tests replace it. Signing needs a terminal, never piped input.</summary>
    internal static TextReader ConfirmInput = Console.In;
    internal static Func<bool> ConfirmIsTerminal = () => !Console.IsInputRedirected;

    /// <summary>
    /// Shows every field of a hardware certificate and the signed bytes' digest to compare with the other signers, then asks
    /// for the last 6 characters of the node's directory key (same review as dir-cert). No flag skips this.
    /// </summary>
    internal static bool ConfirmHardwareCertificate(HardwareCertificate cert, TextWriter output)
    {
        var lines = new List<string>
        {
            $"目录节点公钥：{Convert.ToBase64String(cert.DirectoryKey)}",
            $"网络：{cert.Network ?? "（没写）"}",
            $"硬件标识：{string.Join(", ", cert.Factors.Keys.Order(StringComparer.Ordinal))}",
            $"至少对上：{cert.Need} / {cert.Factors.Count}",
            $"必须对上（must）：{(cert.Must.Count == 0 ? "-" : string.Join(", ", cert.Must))}",
            $"钥匙封存进 TPM：{(cert.RequireTpm ? "是" : "否")}",
            $"要求开着安全启动（secureBoot）：{(cert.RequireSecureBoot ? "是" : "否")}",
            "硬件对不上时节点会删除自己数据目录里的 Jay 文件",
        };
        var signers = cert.Signers.ToList();
        if (signers.Count == 0) lines.Add("已经签过的：还没有人（你是第一个）");
        for (var i = 0; i < signers.Count; i++)
            lines.Add($"已签 {i + 1}：{TrustCommands.Fingerprint(signers[i])}{(JayTrust.Quorum.Count([signers[i]]) == 1 ? "（Jay 信任人）" : "")}");
        var digest = Convert.ToHexString(cert.Digest.AsSpan(0, 8));
        lines.Add($"被签内容的 SHA-256（和另一位信任人对一下）：{string.Join(' ', Enumerable.Range(0, 4).Select(i => digest.Substring(i * 4, 4)))}");
        return TrustCommands.Confirm(lines, "目录节点公钥", TrustCommands.CodeOf(Convert.ToBase64String(cert.DirectoryKey).TrimEnd('=')),
            output, ConfirmInput, ConfirmIsTerminal());
    }

    /// <summary>The root quorum a node's hardware certificate must meet: the given network's dirRoots, or the Jay trustees.</summary>
    private static TrustQuorum NodeTrust(Options options)
    {
        if (options.Get("network") is not { } text) return JayTrust.Quorum;
        var profile = NetworkString.Parse(File.Exists(text) ? File.ReadAllText(text) : text).ToProfile();
        return new TrustQuorum(profile.DirectoryRoots, profile.DirectoryRootsNeed);
    }

    /// <summary>The node's directory public key: from hardware.cert once the node is bound (the key itself is sealed), else directory.key.</summary>
    private static byte[] DirectoryPublicKey(string dataDir)
    {
        if (HardwareBinding.IsBound(dataDir))
        {
            var certPath = Path.Combine(dataDir, HardwareBinding.CertificateFile);
            return (File.Exists(certPath) ? HardwareCertificate.Parse(File.ReadAllBytes(certPath)) : null)?.DirectoryKey
                ?? throw new InvalidDataException("这个节点绑定过硬件，但 hardware.cert 缺失或损坏");
        }
        using var key = DirectoryChannel.LoadOrCreateIdentity(Path.Combine(dataDir, "directory.key"));
        return key.PublicKey;
    }

    /// <summary>
    /// jay-server node bind: bind this node to its hardware (docs/security/node-hardware-binding.md). All offline.
    /// No option: write hardware.request.json. --install &lt;cert&gt;: check and seal the keys. --check: report only.
    /// </summary>
    internal static int NodeBind(string root, Options options, TextWriter output, IHardwareSource hardware, ITpmSealer tpm)
    {
        if (options.Has("check"))
        {
            var factors = hardware.Read();
            output.WriteLine($"能读到的硬件标识：{(factors.Count == 0 ? "没有" : string.Join("、", factors.Select(f => f.Kind)))}；TPM：{(tpm.Available ? (tpm.BindsBootState ? "有（绑定安全启动状态）" : "有") : "没有")}；安全启动：{RuntimeIntegrity.SecureBootEnabled() switch { true => "开", false => "关", null => "不知道" }}");
            var problems = RuntimeIntegrity.Instance.Problems(root, false);
            foreach (var p in problems) output.WriteLine("运行环境问题：" + p);
            if (!HardwareBinding.IsBound(root)) { output.WriteLine("这个节点还没有绑定硬件。"); return 0; }
            var cert = HardwareCertificate.Parse(File.Exists(Path.Combine(root, HardwareBinding.CertificateFile)) ? File.ReadAllBytes(Path.Combine(root, HardwareBinding.CertificateFile)) : []);
            if (cert is null) { output.WriteLine("hardware.cert 缺失或签名不对：节点启动时会删除自己的文件。"); return 2; }
            var match = cert.Match(factors);
            output.WriteLine(match.Ok ? $"硬件对得上（{match.Reason}）。" : $"硬件对不上：{match.Reason}。现在启动节点会删除它的文件；换硬件前先重新绑定。");
            return match.Ok && problems.Count == 0 ? 0 : 2;
        }
        if (options.Get("install") is { } certFile)
        {
            var sealedFiles = HardwareBinding.Install(root, File.ReadAllBytes(certFile), hardware, tpm, NodeTrust(options));
            output.WriteLine($"已绑定硬件。{string.Join("、", sealedFiles)} 现在只以加密形式存在（{string.Join("、", sealedFiles.Select(f => f + HardwareBinding.SealedSuffix))}），只有这台机器能打开。");
            output.WriteLine("硬件对不上时节点会删除数据目录里的 Jay 文件；换硬件或迁移虚拟机之前先跑 jay-server node bind --check。");
            return 0;
        }
        var request = HardwareBinding.CreateRequest(root, hardware, tpm);
        var ids = (System.Text.Json.Nodes.JsonObject)request["f"]!;
        output.WriteLine($"已写 {Path.Combine(root, HardwareBinding.RequestFile)}：硬件标识 {ids.Count} 项（{string.Join("、", ids.Select(p => p.Key))}），只含加盐后的哈希，没有原始编号。");
        output.WriteLine(tpm.Available ? "有 TPM：钥匙会封存进 TPM" + (tpm.BindsBootState ? "，并绑定安全启动状态（PCR 7）。" : "。") : "没有 TPM：钥匙只靠硬件标识加密，强度较弱。");
        output.WriteLine("先把 directory.key 和 login.key 离线备份好（装上证书后它们只以加密形式存在）。");
        output.WriteLine("然后把请求文件拷到放根钥匙的离线电脑：jay-server network hw-cert --root <根钥匙文件> --request hardware.request.json --out hardware.cert");
        output.WriteLine("再把 hardware.cert 拷回来：jay-server node bind --install hardware.cert");
        return 0;
    }

    internal static async Task<int> NodeCommand(string root, Options options, TextWriter output, HttpClient? http = null)
    {
        var sub = options.Positional.FirstOrDefault() ?? "status";
        var rest = options.Positional.Skip(1).ToList();
        if (sub == "bind") return NodeBind(root, options, output, SystemHardwareSource.Instance, TpmSealers.ForThisMachine());
        using var owned = http is null ? new HttpClient { Timeout = TimeSpan.FromSeconds(10) } : null;
        var client = AdminClient.ForDataDir(http ?? owned!, root, options.GetInt("admin-port", JayNetworkServer.DefaultAdminPort));
        var pretty = new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        string Need(int i, string what) => rest.ElementAtOrDefault(i) ?? throw new ArgumentException("要给" + what);
        if (sub == "panel")
        {
            // The web panel: the token rides in the URL fragment, which the browser keeps to itself.
            var port = options.GetInt("admin-port", JayNetworkServer.DefaultAdminPort);
            var token = File.ReadAllText(Path.Combine(root, "admin.token")).Trim();
            output.WriteLine("在这台机器的浏览器里打开（链接里带着管理口令，不要发给别人）：");
            output.WriteLine(AdminPanel.Url(port, token));
            output.WriteLine($"节点在远程服务器上时，先在自己电脑上运行 ssh -L {port}:127.0.0.1:{port} <用户>@<服务器>，再在自己电脑上打开上面的链接。");
            return 0;
        }
        System.Text.Json.Nodes.JsonObject reply;
        try
        {
            reply = sub switch
            {
                "status" => await client.CallAsync("GET", "/stats"),
                "rooms" => await client.CallAsync("GET", "/rooms"),
                "bans" => await client.CallAsync("GET", "/bans"),
                "nodes" => await client.CallAsync("GET", "/nodes"),
                "ban" => await client.CallAsync("POST", "/ban", new()
                {
                    ["kind"] = Need(0, "host、room、account 或 room-host"), ["value"] = Need(1, "房主指纹、房间编号或账号"), ["reason"] = options.Get("reason"),
                    ["for"] = options.Get("for") is { } span ? BanSeconds(span) : null,
                }),
                "unban" => await client.CallAsync("POST", "/unban", new() { ["kind"] = Need(0, "host、room 或 account"), ["value"] = Need(1, "房主指纹、房间编号或账号") }),
                "remove" => await client.CallAsync("POST", "/remove", new() { ["id"] = Need(0, "房间编号") }),
                _ => throw new ArgumentException("用法：jay-server node status|rooms|bans|nodes|panel|ban|unban|remove"),
            };
        }
        catch (HttpRequestException e)
        {
            throw new IOException("连不上节点的管理接口（节点在运行吗？--admin-port 对吗？）：" + e.Message);
        }
        if (sub == "rooms" && reply["ok"]?.GetValue<bool>() == true)
        {
            output.WriteLine($"目录里 {reply["total"]} 个房间，其中 {reply["hidden"]} 个不公开（不显示内容）。公开的：");
            foreach (var r in reply["rooms"] as System.Text.Json.Nodes.JsonArray ?? [])
                output.WriteLine($"  {r!["id"]}  {r["name"]}（房主 {r["host"]}，指纹 {r["hostFingerprint"]}，{r["mode"]}，{r["players"]} 人）");
            return 0;
        }
        output.WriteLine(reply.ToJsonString(pretty));
        if (sub == "ban" && reply["disputedNotBanned"] is System.Text.Json.Nodes.JsonArray { Count: > 0 } spared)
            output.WriteLine($"这些地址还有别人认领，只移除、没封：{string.Join("、", spared)}（认领的也可能是同一个人的另一个账户，看看 node nodes）");
        return reply["ok"]?.GetValue<bool>() == true ? 0 : 1;
    }

    /// <summary>directory --accounts：账号表 + 登录钥匙 + 第二步验证。没开就是 null。</summary>
    internal static LoginAuthority? LoginFor(string root, Options options, string network, TextWriter output)
    {
        if (!options.Has("accounts") && !options.Has("require-2fa") && options.Get("factor") is null) return null;
        var (key, signer) = LoginAuthority.LoadOrCreateKeys(Path.Combine(root, "login.key"));
        var accounts = new AccountStore(Path.Combine(root, "accounts.json"));
        var factors = new List<ILoginFactor> { new TotpFactor(accounts) };
        foreach (var spec in (options.Get("factor") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = spec.Split(':', 3);
            if (parts.Length != 3 || !LoginFactorPrompt.IsValidName(parts[0]) || parts[1].Length == 0 || parts[2].Length == 0)
                throw new ArgumentException($"--factor 格式是 <名字>:<提示>:<程序>，名字只用小写字母、数字、横线：{spec}");
            factors.Add(new CommandFactor(parts[0], parts[1], parts[2], [], network));
        }
        var require = options.Has("require-2fa");
        var section = new System.Text.Json.Nodes.JsonObject
        {
            ["required"] = true,
            ["key"] = Convert.ToBase64String(key.PublicKey),
            ["signKey"] = Convert.ToBase64String(signer.PublicKey),
            ["factors"] = new System.Text.Json.Nodes.JsonArray([.. factors.Select(f => (System.Text.Json.Nodes.JsonNode)f.Name)]),
            ["requireFactor"] = require,
        };
        output.WriteLine($"账号登录已开启（{accounts.List().Count} 个账号）。把这段写进网络配置文件的 \"login\"（required 改成 false 就是登录可选）：");
        output.WriteLine(section.ToJsonString());
        return new LoginAuthority(accounts, key, signer, network) { Factors = factors, RequireSecondFactor = require };
    }

    /// <summary>公共网络账号：加、改密码、删、停用、两步验证。</summary>
    internal static int AccountCommand(string root, Options options, TextWriter output, Func<string, string> readPassword)
    {
        var accounts = new AccountStore(Path.Combine(root, "accounts.json"));
        string Name(int i) => options.Positional.ElementAtOrDefault(i) ?? throw new ArgumentException("要给账号名");
        string Existing(int i) => accounts.List().Any(a => a.Name == Name(i)) ? Name(i) : throw new ArgumentException($"没有账号 {Name(i)}");
        switch (options.Positional.FirstOrDefault())
        {
            case "add" or "passwd":
            {
                var name = AccountStore.CleanName(Name(1)) ?? throw new ArgumentException("账号名要 1–32 个字");
                var exists = accounts.List().Any(a => a.Name == name);
                if (options.Positional[0] == "add" && exists) throw new ArgumentException($"账号 {name} 已经有了（改密码用 account passwd）");
                if (options.Positional[0] == "passwd" && !exists) throw new ArgumentException($"没有账号 {name}");
                var password = Environment.GetEnvironmentVariable("JAY_ACCOUNT_PASSWORD") is { Length: > 0 } env ? env : readPassword($"{name} 的密码（至少 {AccountStore.MinPassword} 位）：");
                if (Environment.GetEnvironmentVariable("JAY_ACCOUNT_PASSWORD") is not { Length: > 0 } && readPassword("再输一次：") != password)
                    throw new ArgumentException("两次密码不一样");
                accounts.Set(name, password);
                output.WriteLine(exists ? $"已改 {name} 的密码" : $"已加账号 {name}");
                return 0;
            }
            case "remove":
                output.WriteLine(accounts.Remove(Existing(1)) ? $"已删除 {Name(1)}" : "没有这个账号");
                return 0;
            case "disable" or "enable":
                accounts.SetDisabled(Existing(1), options.Positional[0] == "disable");
                output.WriteLine(options.Positional[0] == "disable" ? $"已停用 {Name(1)}（已发的凭证最多 7 天后失效）" : $"已启用 {Name(1)}");
                return 0;
            case "list":
                foreach (var (name, disabled) in accounts.List())
                    output.WriteLine($"{name}{(disabled ? "（停用）" : "")}{(accounts.TotpSecret(name) is not null ? "（两步验证）" : "")}");
                return 0;
            case "2fa":
            {
                var on = options.Positional.ElementAtOrDefault(1) switch
                {
                    "on" => true,
                    "off" => false,
                    _ => throw new ArgumentException("用法：jay-server account 2fa on|off <账号>"),
                };
                var name = Existing(2);
                if (!on)
                {
                    accounts.SetTotp(name, null);
                    output.WriteLine($"已关掉 {name} 的两步验证");
                    return 0;
                }
                var secret = TotpFactor.NewSecret();
                accounts.SetTotp(name, secret);
                output.WriteLine($"已给 {name} 开两步验证。在验证器 App 里扫码或手动输入（只显示这一次，只发给账号本人）：");
                output.WriteLine($"密钥：{TotpFactor.Base32(secret)}");
                output.WriteLine(TotpFactor.Uri(options.Get("issuer") ?? "Jay", name, secret));
                return 0;
            }
            default:
                throw new ArgumentException("用法：jay-server account add|passwd|remove|disable|enable|list|2fa");
        }
    }

    private static string ReadPassword(string prompt)
    {
        Console.Write(prompt);
        if (Console.IsInputRedirected) return Console.ReadLine() ?? "";
        var sb = new StringBuilder();
        while (true)
        {
            var k = Console.ReadKey(true);
            if (k.Key == ConsoleKey.Enter) break;
            if (k.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; }
            else if (!char.IsControl(k.KeyChar)) sb.Append(k.KeyChar);
        }
        Console.WriteLine();
        return sb.ToString();
    }

    internal static int Admin(string root, Options options, TextWriter output)
    {
        var store = Gate(root);
        string Arg(int i, string what) => options.Positional.ElementAtOrDefault(i) ?? throw new ArgumentException($"缺少{what}");
        switch (options.Command)
        {
            case "invite":
            {
                var name = Arg(0, "用户名");
                var ttl = ParseDuration(options.Get("ttl") ?? "24h");
                using var key = store.LoadServerKey();
                var invite = store.CreateInvite(name, ttl, options.GetInt("uses", 1), key.PublicKey);
                output.WriteLine($"给 {name} 的邀请码（{FormatDuration(ttl)}内有效，可用 {options.GetInt("uses", 1)} 次）：");
                output.WriteLine();
                var (code, passphrase) = invite.ToCode();
                output.WriteLine(code);
                output.WriteLine();
                if (passphrase is not null)
                {
                    // An IP-only server: the code is sealed so it shows no address; the passphrase opens it.
                    output.WriteLine("这台服务器没有域名，邀请码已加密，不含 IP。打开它的口令：");
                    output.WriteLine();
                    output.WriteLine("  " + passphrase);
                    output.WriteLine();
                    output.WriteLine("口令要换个方式告诉对方（打电话、当面），别和邀请码发在一起。");
                }
                output.WriteLine("让对方在 Jay 启动器里选“加入 Jay 服务器”，粘贴这段码。别发到公开的地方。");
                return 0;
            }
            case "list":
            {
                var state = store.Read();
                if (state.Members.Count == 0) output.WriteLine("名单是空的。用 jay-server invite <用户名> 邀请第一个人。");
                foreach (var m in state.Members.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
                {
                    output.WriteLine($"{m.Name}{(m.Disabled ? "（已停用）" : "")}");
                    foreach (var d in m.Devices)
                        output.WriteLine($"    设备 {d.Fingerprint}  加入于 {d.Added.ToLocalTime():yyyy-MM-dd HH:mm}  最近在线 {(d.LastSeen is { } s ? s.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "从未")}");
                    if (m.Devices.Count == 0) output.WriteLine("    （没有设备）");
                }
                return 0;
            }
            case "invites":
            {
                var invites = store.Read().Invites;
                if (invites.Count == 0) output.WriteLine("没有待用的邀请。");
                foreach (var i in invites)
                    output.WriteLine($"{i.Id}  给 {i.Name}  剩 {i.UsesLeft} 次  到期 {i.Expires.ToLocalTime():yyyy-MM-dd HH:mm}");
                return 0;
            }
            case "cancel-invite":
                output.WriteLine(store.CancelInvite(Arg(0, "邀请编号")) ? "已作废。" : "没有这个邀请。");
                return 0;
            case "revoke":
            {
                var n = store.RevokeDevices(Arg(0, "用户名"), options.Positional.ElementAtOrDefault(1));
                output.WriteLine(n == 0 ? "没找到要撤销的设备。" : $"撤销了 {n} 台设备，它们之后进不来了（网关在运行时，几秒内会断开它们在线的连接）。");
                return 0;
            }
            case "disable":
            case "enable":
                output.WriteLine(store.SetDisabled(Arg(0, "用户名"), options.Command == "disable") ? "好了。" : "名单里没有这个人。");
                return 0;
            case "remove":
                output.WriteLine(store.RemoveMember(Arg(0, "用户名")) ? "已从名单移除。" : "名单里没有这个人。");
                return 0;
            case "online-mode":
            {
                var on = Arg(0, "on 或 off") switch { "on" => true, "off" => false, _ => throw new ArgumentException("只能是 on 或 off") };
                var host = new McServerHost(root);
                host.WriteProperties(host.LoadSettings().Port, on);
                output.WriteLine(on ? "已开启正版验证（重启 MC 服务端后生效）。" : "已关闭正版验证：离线账号，由网关核对用户名（重启后生效）。");
                return 0;
            }
            case "privacy":
            {
                var level = Arg(0, "basic、plus 或 plusplus") switch
                {
                    "basic" => PrivacyLevel.Basic,
                    "plus" => PrivacyLevel.Plus,
                    "plusplus" or "plus+" or "++" => PrivacyLevel.PlusPlus,
                    _ => throw new ArgumentException("只能是 basic、plus 或 plusplus"),
                };
                store.SetPrivacy(level);
                output.WriteLine(level switch
                {
                    PrivacyLevel.Plus => "已切到隐私+：玩家经两个节点进服，网关只收节点转来的连接（新邀请码里写的是节点地址）。别忘了用防火墙只放行节点访问网关端口。",
                    PrivacyLevel.PlusPlus => "已切到隐私++：在隐私+ 之上，节点要持续发心跳。发心跳的节点少于两个时熔断：断开所有人、暂停进服（服务端只存盘不停），节点恢复后自动可用。",
                    _ => "已切回普通模式：玩家直连网关。",
                });
                return 0;
            }
            case "relays":
            {
                switch (Arg(0, "add、code、remove、revoke 或 list"))
                {
                    case "add":
                    {
                        var address = GateEndpoint.TryParse(Arg(1, "节点地址")) ?? throw new ArgumentException("节点地址要写成 主机:端口");
                        store.AddRelay(address, options.Get("source"));
                        output.WriteLine($"已加节点 {address}。");
                        WriteRelayCode(store, address.ToString(), output);
                        break;
                    }
                    case "code":
                    {
                        var address = GateEndpoint.TryParse(Arg(1, "节点地址")) ?? throw new ArgumentException("节点地址要写成 主机:端口");
                        WriteRelayCode(store, address.ToString(), output);
                        output.WriteLine("旧的节点码已经作废，节点要换上新码重新运行。");
                        break;
                    }
                    case "revoke":
                        output.WriteLine(store.RevokeRelay(Arg(1, "节点地址"))
                            ? "已吊销：运行中的网关会马上断开所有人，大家重新握手换新密钥，之后不会再经过这个节点。记得在防火墙里也去掉它。"
                            : "没有这个节点。");
                        break;
                    case "remove":
                        output.WriteLine(store.RemoveRelay(Arg(1, "节点地址")) ? "已删除。" : "没有这个节点。");
                        break;
                    case "list":
                    {
                        var state = store.Read();
                        output.WriteLine($"隐私等级：{PrivacyName(state.Privacy)}");
                        if (state.Relays.Count == 0) output.WriteLine("还没有节点。");
                        foreach (var r in state.Relays)
                            output.WriteLine($"  {r.Address}{(r.Source is null ? "" : $"  （来源 IP {r.Source}）")}"
                                + (r.HeartbeatPsk is null ? "  没有节点码" : r.PublicKey is null ? "  节点码已发，节点还没连上" : $"  节点身份 {MemberIdentity.FingerprintOf(Convert.FromBase64String(r.PublicKey))}"));
                        break;
                    }
                    default:
                        throw new ArgumentException("只能是 add、code、remove、revoke 或 list");
                }
                return 0;
            }
            case "records":
            {
                switch (Arg(0, "on、off 或 status"))
                {
                    case "on":
                    {
                        var network = options.Get("network") ?? Amber.Core.Multiplayer.Public.PublicNetworkProfile.Jay().Id;
                        // The built-in network ID can also be the name of the executable in the current directory.
                        if (network != Amber.Core.Multiplayer.Public.PublicNetworkProfile.Jay().Id && File.Exists(network))
                            network = File.ReadAllText(network).Trim();
                        if (GateStore.RecordNetworkId(network) is not { } networkId) throw new ArgumentException("--network 要写 jay、一个 JAYNET2- 连接串或 .jaynet 文件");
                        store.EnableRecords(network);
                        output.WriteLine($"已开启地址记录：公共网络 {networkId}。之后给没有域名的服务器发的邀请码只写记录编号，不写 IP，也不用口令。");
                        output.WriteLine("记录在服务运行时发布并每 12 小时续一次（jay-server run 会连上这个公共网络）。服务没开时，玩家查不到地址。");
                        return 0;
                    }
                    case "off":
                        store.DisableRecords();
                        output.WriteLine("已关闭地址记录。没有域名的服务器之后发口令加密的邀请码。");
                        return 0;
                    case "status":
                    {
                        var state = store.Read();
                        output.WriteLine(state.RecordNetwork is { } n ? $"地址记录：开，公共网络 {GateStore.RecordNetworkId(n)}" : "地址记录：关");
                        if (store.Locator(state) is not null)
                            foreach (var e in store.RecordEntries(store.LoadConfig(), state))
                                output.WriteLine($"  记录里的入口：{e.Host}:{e.Port}{(e.Rotation is null ? "" : "（轮换）")}");
                        return 0;
                    }
                    default:
                        throw new ArgumentException("只能是 on、off 或 status");
                }
            }
            case "rotate":
            {
                switch (Arg(0, "on、off 或 status"))
                {
                    case "on":
                    {
                        var range = (options.Get("range") ?? $"{GateRotation.Default.MinPort}-{GateRotation.Default.MaxPort}").Split('-');
                        if (range.Length != 2 || !int.TryParse(range[0], out var min) || !int.TryParse(range[1], out var max)) throw new ArgumentException("--range 写成 20000-45000");
                        var window = ParseDuration(options.Get("window") ?? "10m");
                        var grace = ParseDuration(options.Get("grace") ?? "3d");
                        store.EnableRotation(new GateRotation((int)window.TotalSeconds, min, max), grace, options.Has("new-seed"));
                        output.WriteLine($"已开启入口端口轮换：每 {FormatDuration(window)}换一次，端口在 {min}–{max} 之间。");
                        output.WriteLine("防火墙 / 安全组要放行这个端口范围（网关在普通模式下，节点在隐私+/++ 下）。之后发的邀请码里带着轮换信息。");
                        if (store.Read().Rotation?.FixedUntil is { } until)
                            output.WriteLine($"固定端口开放到 {until.ToLocalTime():yyyy-MM-dd HH:mm}，这之前进过服的玩家会自动改用轮换端口；之后没进过服的老玩家要重新发邀请。");
                        return 0;
                    }
                    case "off":
                        store.DisableRotation();
                        output.WriteLine("已关闭轮换，回到固定端口。");
                        return 0;
                    case "status":
                    {
                        var rotation = store.Rotation();
                        if (rotation is not { } r)
                        {
                            output.WriteLine("没开轮换。");
                            return 0;
                        }
                        var config = store.LoadConfig();
                        var now = DateTimeOffset.UtcNow;
                        output.WriteLine($"轮换中：每 {FormatDuration(TimeSpan.FromSeconds(r.Rule.WindowSeconds))}换一次，端口 {r.Rule.MinPort}–{r.Rule.MaxPort}");
                        output.WriteLine($"  网关直连：现在 {r.Rule.PortAt(GateRotation.EntrySeed(r.Master, GateRotation.DirectEntry), now)}（固定端口 {config.ListenPort}）");
                        foreach (var relay in store.Read().Relays)
                            output.WriteLine($"  节点 {relay.Address}：现在 {r.Rule.PortAt(GateRotation.EntrySeed(r.Master, relay.Address), now)}");
                        return 0;
                    }
                    default:
                        throw new ArgumentException("只能是 on、off 或 status");
                }
            }
            case "status":
            {
                var state = store.Read();
                output.WriteLine($"隐私等级：{PrivacyName(state.Privacy)}");
                var health = store.ReadHealth();
                if (health is null || DateTimeOffset.UtcNow - health.Updated > TimeSpan.FromSeconds(30))
                {
                    output.WriteLine("网关没在运行（或者 30 秒内没更新状态）。");
                    return 0;
                }
                output.WriteLine($"在线 {health.Online} 人；{(health.BreakerOpen ? "熔断中（暂停进服）" : "正常")}；纪元 {health.Epoch}");
                foreach (var r in health.Relays)
                {
                    var seen = r.LastHeartbeat is { } t ? $"{(DateTimeOffset.UtcNow - t).TotalSeconds:0} 秒前" : "从未";
                    output.WriteLine($"  {r.Address}  心跳通道{(r.Connected ? "已连" : "未连")}  最近心跳 {seen}");
                }
                return 0;
            }
            default:
                output.WriteLine($"不认识的命令：{options.Command}");
                output.WriteLine(Usage);
                return 1;
        }
    }

    private static string PrivacyName(PrivacyLevel level) => level switch
    {
        PrivacyLevel.PlusPlus => "隐私++",
        PrivacyLevel.Plus => "隐私+",
        _ => "普通",
    };

    private static void WriteRelayCode(GateStore store, string address, TextWriter output)
    {
        using var key = store.LoadServerKey();
        var code = store.IssueRelayCode(address, key.PublicKey);
        output.WriteLine("节点码（隐私++ 的心跳要用，别外传）：");
        output.WriteLine();
        output.WriteLine(code.Encode());
        output.WriteLine();
        output.WriteLine($"在节点电脑上运行：jay-server relay --origin <网关地址>:<网关端口> --listen {GateEndpoint.TryParse(address)?.Port} --code <上面这段>");
    }

    private static long BanSeconds(string text)
    {
        var span = ParseDuration(text);
        return span > TimeSpan.Zero ? (long)span.TotalSeconds : throw new ArgumentException("--for 要大于 0，比如 30m、24h、7d");
    }

    internal static TimeSpan ParseDuration(string text)
    {
        if (text == "0") return TimeSpan.Zero;
        if (text.Length < 2) throw new FormatException("时长写成 30m、24h 或 7d");
        var number = double.Parse(text[..^1], CultureInfo.InvariantCulture);
        return char.ToLowerInvariant(text[^1]) switch
        {
            'm' => TimeSpan.FromMinutes(number),
            'h' => TimeSpan.FromHours(number),
            'd' => TimeSpan.FromDays(number),
            _ => throw new FormatException("时长写成 30m、24h 或 7d"),
        };
    }

    private static string FormatDuration(TimeSpan t) =>
        t.TotalDays >= 1 && t.TotalDays % 1 == 0 ? $"{t.TotalDays:0} 天" : t.TotalHours >= 1 ? $"{t.TotalHours:0.#} 小时" : $"{t.TotalMinutes:0} 分钟";

    private static string[] Split(string line) => line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>最简单的参数解析：第一个词是命令，--name value 是选项，--flag 是开关，其余是位置参数。</summary>
internal sealed class Options
{
    private static readonly HashSet<string> Flags = ["accept-eula", "no-mc", "console", "new-seed", "verbose", "accounts", "require-2fa", "seed-only", "no-admin", "no-rate-limit", "secure-directory-only", "no-volunteers", "signed-volunteers", "no-passphrase", "paper", "wipe-dry-run", "check", "secure-boot", "no-tpm"];
    private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);
    public string? Command { get; private set; }
    public List<string> Positional { get; } = [];

    /// <summary>help、-h、--help、/?（放在哪里都行）：显示用法，不执行命令。</summary>
    public bool WantsHelp { get; private set; }

    public static Options Parse(IReadOnlyList<string> args)
    {
        var o = new Options();
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a is "--help" or "-h" or "/?" || (a == "help" && o.Command is null))
            {
                o.WantsHelp = true;
                continue;
            }
            if (a.StartsWith("--", StringComparison.Ordinal))
            {
                var name = a[2..];
                if (Flags.Contains(name)) o._values[name] = null;
                else if (i + 1 < args.Count) o._values[name] = args[++i];
                else throw new ArgumentException($"--{name} 后面要跟一个值");
            }
            else if (o.Command is null) o.Command = a;
            else o.Positional.Add(a);
        }
        return o;
    }

    public bool Has(string name) => _values.ContainsKey(name);
    public string? Get(string name) => _values.GetValueOrDefault(name);
    public int GetInt(string name, int fallback) =>
        Get(name) is { } v ? int.TryParse(v, out var n) ? n : throw new ArgumentException($"--{name} 要是整数") : fallback;
}
