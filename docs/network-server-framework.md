# Jay 网络服务端框架（第三方公网服务端）

想搭自己的 Jay 公共网络，又想按自己的规矩来（接自己的账号库、自己的审核、自己的验证方式、自己的网页），有两条路：

1. **用框架写**（.NET）：引用 `src/Amber.NetworkServer`（程序集 `Jay.NetworkServer`），几十行就是一个完整节点。官方的 `jay-server directory` 就是用它跑的。
2. **自己实现协议**（任何语言）：按下面的[协议](#协议)写一个目录服务，前面放一个 EasyTier。

不管哪条路，玩家都用同一个 Jay，粘贴你的 `JAYNET2-...` 连接串就能进你的网络（怎么生成连接串见 [network-server.md](network-server.md#二自己的公共网络四步)）。

## 最小的节点

```csharp
using Amber.Core.Multiplayer.Public;
using Amber.NetworkServer;

await using var server = new JayNetworkServerBuilder()
    .UseDataDir("my-network-data")
    .UseNetwork(NetworkString.Parse(File.ReadAllText("my.jaynet")))
    .UseListenPort(11010)
    .Build();
await server.RunAsync(CancellationToken.None);
```

完整的例子在 [samples/MyJayNetwork](../samples/MyJayNetwork/Program.cs)：地区标签审核、内存账号库、当日邀请码验证、事件日志、自定义请求，全都用上了。

## 扩展点

| 接口 | 用来做什么 | 怎么挂 |
|---|---|---|
| `IRoomGate` | 房间审核：每次发布 / 续期都问一遍，返回 `RoomVerdict.Allow` 或 `RoomVerdict.Reject(code, 原因)`。原因会显示在房主的 Jay 里。续期被拒的房间马上从目录里拿掉 | `AddGate` |
| `IAccountStore` | 换账号库（数据库、论坛、会员系统）。账号不存在和密码错要回答一样的 `Wrong`，自己防在线猜密码 | `UseAccounts(store)` |
| `ILoginFactor` | 密码之后的验证（短信、邮件码、人工审核……）。内置 TOTP 总在第一个 | `AddLoginFactor` |
| `IDirectoryObserver` | 事件：房间上线 / 续期 / 离开（撤下、过期、被删）、被拒、登录成功 / 失败。在锁外调用，出错不影响目录 | `AddObserver` |
| `IDirectoryExtension` | 自定义请求：op 以 `x-` 开头（比如 `x-motd`），给你自己的网页、机器人、插件用。Jay 本身不发这些请求 | `AddExtension` |

内置的规则（`jay-server directory` 的选项就是开关它们）：

| 类 | 作用 |
|---|---|
| `BanList` | 按房主指纹、房间编号或网络账号封禁，可以设到期时间，存在 `bans.json`（文件被别人改了会自动重读）。封账号也挡登录。默认开着，`WithoutBanList()` 关掉 |
| `RateLimitGate` | 每人同时几个房间、每人每分钟新发布几次、整个目录每分钟几个新房间。默认 3 / 6 / 600 |
| `NameFilterGate` | 屏蔽词（`UseBlockedWords`） |
| `PolicyGate` | 按网络配置的 `policy.modes` 挡（`UseConfig`） |

审核规则按顺序跑：封禁 → 网络策略 → 限流 → 你加的。规则在目录的锁里调用，要快，不要在里面做网络请求，要查外部系统就自己缓存。

其他常用的：

- `UseSeeds([...])`、`SeedOnly()`：多个地区节点（只转发，不跑目录）。
- `UseDirectoryIndex(2..4)`：备用目录节点（见 [network-server.md](network-server.md#五多开节点备用目录和地区种子)）。
- `UseEasyTier(core, cli)`：用已有的 EasyTier；不调就第一次运行时下载固定版本。
- `WithoutEasyTier(endpoint)`：不启动 EasyTier，目录直接监听这个地址（测试，或者你自己管 EasyTier）。
- `UseAdmin(port)` / `WithoutAdmin()`：本机管理接口（见下）。`AdminClient` 是它的客户端，可以拿来做你自己的面板。
- `NetworkScaffold`：生成签名钥匙、网络信息、配置模板，算配置哈希（`jay-server network init` / `config-hash` 用的就是它）。
- `server.LoginSection`：开了账号登录时，要写进网络配置 `login` 的那一段。
- `server.Ban(kind, value, reason, duration)`、`server.BanRoomHost(roomId, ...)`：封禁并马上拿掉相关房间。
- `WithoutVolunteers()` / `UseVolunteerProbe(...)`：不收志愿中转，或者换掉“列出前先连一次”的检查。`server.Volunteers` 是列表（`VolunteerRegistry`）。
- `UseConfig(config)` 时，`policy.minVersion` 会挡旧版 Jay，`login.required` 会要求发布房间的人先登录（`RoomDirectoryServer.MinClientVersion` / `RequireAccountToPublish`，也可以自己设）。
- `RoomAnnouncement.Account`：房主登录过时是他的账号名（凭证核对过），审核规则可以按账号判断。

## 管理接口

只听 `127.0.0.1`（默认 47669），每个请求一次连接。除了 `/health` 都要 `Authorization: Bearer <数据目录里 admin.token 的内容>`。

`GET /`、`/panel.js`、`/panel.css` 是网页管理面板（静态文件，不用口令，本身不含数据；`AdminPanel.Url(port, token)` 生成直接登录的链接）。所有请求的 Host 都必须是 127.0.0.1 / localhost / [::1]（任意端口），带 Origin 时也必须是本机，否则回 403。

| 请求 | 回复 |
|---|---|
| `GET /health` | `{ok, network, role: directory\|seed, easytier: running\|stopped\|external, rooms, listed, login, uptime}` |
| `GET /stats` | `{ok, health, stats: {published, renewed, withdrawn, expired, removed, rejected: {code: n}, loginOk, loginFail}}` |
| `GET /metrics` | Prometheus 文本（`text/plain; version=0.0.4`）：`jay_node_up`、`jay_node_info{network,role,easytier,login}`、`jay_node_uptime_seconds`、`jay_directory_rooms`、`jay_directory_rooms_listed`、`jay_volunteer_relays`、`jay_bans`、`jay_rendezvous_records`、`jay_signed_records`、`jay_directory_rooms_published_total`、`jay_directory_rooms_renewed_total`、`jay_directory_rooms_ended_total{reason}`、`jay_directory_rooms_rejected_total{reason}`、`jay_logins_total{result}`。只有总数，要口令 |
| `GET /rooms` | `{ok, total, hidden, rooms: [{id, name, host, hostFingerprint, mode, players, game, account, updated}]}`（只有公开房间） |
| `GET /nodes` | `{ok, enabled, nodes: ["tcp://...", ...], operators: [{url, operator, disputed, claimedBy, unsignedAttempts, lastUnsigned, claims, claimants}]}`：列出的志愿中转；`operator` 是签名登记的志愿者的 Jay 账户（`jay1...`），没签名是 null；`disputed` 为真表示有别的 Jay 账户签名认领过这个地址（对签名地址的不签名登记只会被拒，不算认领），`claimedBy` 是最近还在认领的最多 4 个账户（满了时最久没再认领的让位，但一直在续期的最早认领者保留，所以一大批新账户也挤不掉真正的运行者）；`claims` / `claimants` 是签名认领的总次数和不同账户数（账户数最多数到 256），很多账户说明有人在刷；`unsignedAttempts` / `lastUnsigned` 是这个签名地址被拒的不签名登记次数和最近时间（可能是从没签名、被人抢先登记的志愿者）；`operator` 不变 |
| `GET /bans` | `{ok, bans: [{kind, value, reason, at, until}]}`（`until` 为 null 是永久） |
| `POST /ban` `{kind: host\|room\|account\|node\|operator, value, reason?, for?}` | `{ok, removed}`（`operator` 另有 `bannedNodes: [地址, ...]` 和 `disputedNotBanned: [地址, ...]`）；`for` 是秒数，`node` 的值是志愿中转地址，`operator` 的值是志愿者的 Jay 账户（他登记的所有中转一起拿掉，这些地址也按 `node` 封同样的时间，原因写 `with operator <账户>`；有没被封的别的账户签名认领过、或有被拒的不签名登记的地址只拿掉不封，列在 `disputedNotBanned` 里；一起封掉的列在 `bannedNodes` 里，两者都写进日志：认领的也可能是同一个人的另一个账户，要运营者自己看；再封一次这个账户时，已有的这些地址封禁也改成新的结束时间；`/unban` 这个账户时一起解封）。封运营者总是挡得住这个账户的签名登记；没开 `RequireSignedVolunteers` 时不签名的登记挡不住，换一个新账户也挡不住 |
| `POST /ban` `{kind: room-host, value: <房间编号>, reason?, for?}` | `{ok, removed, host, account}`：封这个房间房主的指纹和账号；房间不在回 404 |
| `POST /unban` `{kind, value}` | `{ok}` 或 404 |
| `POST /remove` `{id}` | `{ok}` 或 404 |

## 协议

自己用别的语言实现时，按这一节来。

### 网络

- 节点跑一个 EasyTier（2.6.x），网络名和密钥用连接串里的 `network` / `secret`，对公网监听 `tcp://0.0.0.0:11010` 和 `udp://0.0.0.0:11010`。
- 跑目录的那台：`--ipv4 10.235.0.1/16 --no-tun --tcp-whitelist 47668`，目录服务监听本机 `127.0.0.1:47668`，EasyTier 把公共网络里到 10.235.0.1:47668 的连接交过来。参数全貌见 `PublicNetworkArgs.Build`。
- 备用目录用 10.235.255.254、.253、.252（`PublicNetworkProfile.DirectoryIpOf`）。Jay 的写请求（announce、withdraw）发给每个在线的目录；`snap` 也问每个目录，本机分别留一份，公开列表合并（同一个房间取 `seq` 最大的），按邀请找房间在每份里找；`find` / `login` 按编号顺序问，回 `op` 或连不上就问下一个。
- Jay 连进来时用 `--dhcp --no-tun --disable-p2p --tcp-whitelist 47660`：成员之间只开敲门端口，不打洞，不暴露公网地址。

### 目录请求

一条 TCP 连接一个请求，全程走加密的目录通道（`DirectoryChannel`，Noise XXhfspsk0，见 `docs/security/directory-identity.md`）：握手第三条消息里是请求 JSON（UTF-8，最长 16 KB）。回复是一帧加密的 JSON，然后关连接（各版本都读得懂，旧版 Jay 也能看到 `version` 错误）。只有 `snap` 的回复不同：先发一帧加密的 4 字节长度（大端，第一个字节是 0，JSON 不会以它开头），再按每帧最多 16 KB 发加密的 JSON，用空格补到 64 KB 的整数倍，最长 16 MB。10 秒内要回；`snap` 每一帧 10 秒内要发出去，整份按不低于 256 KB/s 算时限（10 秒加上大小除以这个速度，`RoomDirectoryProtocol.SnapshotDeadline`；Jay 读到长度后也按这个等）。不超过 256 KB 的回复（平常的增量）有自己的 32 个名额；更大的同时最多 8 个，开始发送另有一个桶（一次最多 32 个，每秒补 4 个），还有一个字节桶（一次最多 32 MB，每秒补 4 MB）；没有名额回 `busy`；名额按请求本身（`epoch`/`have` 对不上就要整份）先占好，再整理、生成回复。增量比整份的四分之一还大（又超过 256 KB）时直接给整份（每个版本只生成一次）；缓存的增量合计不超过 512 KB。目录看得到每个读者自己的地址时，每个来源地址每分钟最多 6 次（`rate`）；看不到时就只靠上面的名额和桶。没有明文协议了：以 `{` 开头的旧请求直接断开。
回复都有 `ok`。失败时 `{ok: false, error: "<code>", reason?: "<给房主看的话，最多 120 字>"}`。

每个请求都带 `jay: "<Jay 的版本号>"`（比如 `"0.8.0"`）。网络配置有 `policy.minVersion` 时，版本更低或者没带这个字段就回 `{ok: false, error: "version", min: "<最低版本>"}`，不处理请求。

**升级顺序**：这一版目录去掉了 `list` 和 `lookup` 两个操作，改用 `snap`。先升级目录、Jay 还没升级时，旧版 Jay 看公开房间列表会收到操作错误，按邀请找房间也找不到（会改走邀请自己的大厅）；只靠隐藏条目登记的旧版房主发布时回 `lookup`，房间登记不上。所以随这一版发布 Jay 的同时把网络配置的 `policy.minVersion` 提到这一版，让旧版直接看到“Jay 太旧”的提示，而不是上面这些含糊的失败。

| op | 请求 | 回复 |
|---|---|---|
| `announce` | `{room: <房间>, listed: bool, hidden: [{k: "<64 位十六进制>", b: "<284 字节，base64>", p: "<64 位十六进制>", s: "<128 位十六进制>"}, ...最多 4 个], fp?: "<64 位十六进制>", account?: "<登录凭证>"}`。`hidden` 是房主给房间的大厅密钥封好的条目（`HiddenEntry`，大厅密钥在多久就发布多久，公开房间也发），目录原样放进快照，长度不对回 `bad`。`p` 是从邀请算出的条目公钥，`k` 必须是它的哈希；`s` 是这把钥匙对条目、房间编号和 `seq` 的签名（`HiddenEntry.VerifyPublish`），不对回 `lookup`。同一个索引下已经有 8 个别的房间时只丢这一条，房间照常登记，回复带 `warn: "lookup"`（只有隐藏条目的房间回 `lookup`）。房间的 `seq` 是毫秒时间，和目录的时钟差超过 10 分钟时不收 `hidden`：房间还要上公开列表（或有 `fp`）就照常登记，回复带 `warn: "clock"`；别的都没有时回 `clock`。`account` 是这个节点登录服务签的凭证，必须绑定房间的 `key`；网络要求登录时没有有效凭证回 `login` | `{ok, warn?}`（`warn` 是 `"lookup"` 或 `"clock"`，见左边） |
| `withdraw` | `{id, seq, sig}`：`sig` 是房间签名钥匙对 `"jay-withdraw-v1\n<id>\n<seq>"` 的 Ed25519 签名（十六进制） | `{ok}` |
| `snap` | `{epoch?, have?}`：本机那份快照来自哪次启动、到第几版；第一次不带 | `{ok, epoch, ver, full, from?, rooms: [<房间>, ...], hidden: [{k, t, b}, ...], gone: ["r:<id>" 或 "h:<k>:<t>", ...]}`。`t` 是随机的槽位标记：同一个索引 `k` 下最多 8 个房间各有一条。公开房间只改了 `seq`、签名和时间的续期不算变化。整份快照有上限，再发布会超过时回 `full`。`epoch` 对得上、`have` 还在变更记录里时只给之后变了的（`full` 为假，`from` = `have`），否则给全部。`rooms` 是 `listed` 为真且不是隐藏房间的。每个请求者都发一样的请求，目录看不出谁在找哪个房间 |
| `find` | `{code: "JAYF1-..."}` | `{ok, room?}`：用登记的指纹密钥算标签，见 `FingerprintCode.Matches` |
| `login` | 见 `NetworkLoginClient.Request` / `LoginAuthority.Handle` | 加密的凭证或 `factor` 提示 |
| `volunteer` | `{url: "tcp://<公网地址>:<端口>", operator?: {cert, at, sig}}`：登记或续期志愿中转（每 3 分钟）。`operator` 是志愿者的 Jay 账户给这台设备签的证书（`DeviceCertificate`）、Unix 秒和设备混合签名（`"jay-volunteer-v3\n" ‖ 网络标记 ‖ "\n" ‖ url ‖ "\n" ‖ at`，64 位大端；网络标记是 `VolunteerOperator.NetworkTag`：网络编号 + ":" + SHA-256("jay-volunteer-net-v1\n" ‖ EasyTier 网络名 ‖ "\n" ‖ 网络密钥) 的前 16 字节十六进制，编号撞了的两个网络也互相用不了对方的签名），见 `VolunteerOperator` | `{ok, listed}`：第一次先探测，探测通过后的续期 `listed` 为真。新地址每分钟不签名的最多收 60 个、签名的另外 60 个，每个 Jay 账户 8 个，每个来源地址 10 个（经本机转发进来的不分来源）；续期不算，同一账户回到刚才列出过（探测通过过）的地址也不算；探测没通过的地址 10 秒内谁登记都不收；对已有地址的签名认领也按账户每分钟 8 次、按来源每分钟 10 次算；多了回 `rate`（不签名和按来源的上限在验签之前就查）。不收时回 `off`，被封回 `banned`，地址不是公网回 `bad`；签名或证书不对、不是这个网络的、时间比目录慢 3 分钟以上或快 1 分钟以上、或网络要求签名（`RequireSignedVolunteers`）而没带时回 `operator`；同一账户给同一地址的签名时间没有比上次接受的新时回 `stale`（Jay 每次至少比上次晚 1 秒）；地址已由别的运营者登记（别的账户、没签名换成签名、或签过名换成不签）、还没过期时回 `owner`，运营者不变，管理接口的 `/nodes` 记下这次认领（签名的记账户，不签名的只计数） |
| `nodes` | `{}` | `{ok, nodes: ["tcp://...", ...最多 8 个，随机]}`：探测过、还在续期的志愿中转，同一个运营者的先只给一个（目录按签名登记的账户分散，只用来分散负载，不是安全保证：一个人可以有好几个 Jay 账户）。谁运行哪个中转只留在目录里，不发给成员 |
| `probe` | `{url: "tcp://<公网地址>:<端口>"}`：想当志愿中转的人先测端口 | `{ok, reachable}`：目录从外网连一次，不列出。不收志愿中转时回 `off`，被封回 `banned`，地址不是公网回 `bad`，测得太频繁回 `rate` |
| `post` | `{box: "<32 位十六进制>", blob: "<base64，最长 4 KB>", drop: "<64 位十六进制>"}`：往一个邮箱放一封封好的信（见 [封信](security/zero-leak-codes.md#sealed-mail-through-the-directories)）。`drop` 是删除令牌的哈希 | `{ok}`。同样的信再放一次也回 `ok`；每个邮箱最多 32 封、整个目录最多 8000 封，满了回 `full`；信放 48 小时 |
| `fetch` | `{boxes: ["<32 位十六进制>", ...最多 3 个], from?: <序号>}` | `{ok, items: [{box, blob}, ...], next?}`：一次回复里的信合计约 12 KB，还有没给的就带 `next`，下次从它接着取 |
| `drop` | `{box, token: "<32 位十六进制>"}`：收信人提前删信，目录删掉 `drop` 等于这个令牌哈希的信 | `{ok}`（没有也回 `ok`） |
| `rv.put` | `{id: "<64 位十六进制>", blob: "<base64，最长 2 KB>", ttl?: <秒，最多 600>}`：放一条一次性记录（见 [一次性记录](security/zero-leak-codes.md#one-shot-rendezvous-records)） | `{ok}`。同一个 `id` 还在等着时回 `exists`（不覆盖）；目录最多 4096 条，满了回 `full`；每个来源地址 10 分钟最多 30 次，多了回 `rate` |
| `rv.take` | `{id}` | `{ok, blob}`，同一步里删掉这条；没有回 `not_found` |
| `rec.put` | `{record: {lookup, signer, seq, expires, blob, sig}}`：登记一条签过名的记录（见 [签名记录](security/zero-leak-codes.md#long-lived-signed-records)，格式以 `SignedRecord` 为准）。目录核对签名，`expires` 已过或比现在晚 7 天又 10 分钟以上回 `bad` | `{ok}`。`lookup` 归第一个签名者，直到记录过期：别人签的回 `owner`，`seq` 不比原来大回 `stale`；目录最多 20000 条（`full`），每个来源地址每小时最多 60 次（`rate`） |
| `rec.get` | `{lookup: "<64 位十六进制>"}` | `{ok, record}`；没有回 `not_found` |
| `x-*` | 你自己的 | 你自己的 |

缺了 `post` / `fetch` / `drop`，好友之间的信和寄出的邀请都送不到；缺了 `rec.put` / `rec.get`，第 3 版 Jay 服务器邀请（`JAYS2-`）和中转的记录码（`JAYNR1-`）都找不到地址。`rv.put` / `rv.take` 是给只带随机令牌的码用的一次性记录。

Jay 认识的错误码：`owner` / `lookup` / `stale`（编号或查找键被别人占着、旧版本）、`full`（满了）、`gone`、`rate`（限流）、`busy`（快照名额满了，稍后再试）、`clock`（房主电脑时间不准）、`version`（Jay 太旧，带 `min`）、`login`（要先登录），其他码有 `reason` 就显示 `reason`。

### 房间（room）

```json
{ "id": "<签名公钥 SHA-256 的前 16 字节，十六进制>", "name": "...", "host": "...", "mode": 0,
  "key": "<房主联机公钥，十六进制>", "ip": "10.235.x.y", "game": "1.21.1", "ver": "", "players": 2,
  "approval": true, "tm": 0, "td": false, "invite": "JAY2-...", "lobby": "...", "salt": "...",
  "sk": "<房间签名公钥（Ed25519），十六进制>", "seq": 1762646400000, "sig": "<签名，十六进制>" }
```

- `mode`：0 隐藏、1 公开、2 密码、3 直连。
- 签名：`sig` = Ed25519(`sk` 的私钥, `"jay-room-v1\n"` + 去掉 `sig` 和 `updated` 后按上面字段顺序的 JSON)。具体以 `RoomListing.SignedBytes` 为准。
- **目录必须**：核对 `id == SHA-256(sk)[..16]` 和签名；同一个 `id` 只收 `seq` 更大的版本；撤下过的 `id` 不收不大于撤下 `seq` 的版本；同一个隐藏索引 `k` 最多收 8 个房间的条目，满了只丢这一条并回 `warn: "lookup"`（只有隐藏条目的房间回 `lookup`）；指纹密钥先到先得；90 秒没续期就删掉（Jay 每 30 秒续期一次）。
- 房间信息不含任何房间钥匙。房主的真实性由加入方敲门时核对 `key` 保证，目录不用信任。

## 安全边界

- 你的节点看得到的东西见 [network-server.md](network-server.md#节点能看到什么)。框架不会把这些发到别处；你加的扩展会不会，由你负责，请在网络主页上写清楚。
- 节点不能替房主放人进房间，也不能读房间里的流量：子网钥匙在敲门后经加密通道直接由房主发给加入方。
- 网络配置只能比 Jay 的默认更严（见 [public-network.md](public-network.md#网络配置文件)），节点改不了玩家这边的安全设置。
