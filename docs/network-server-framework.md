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
| `GET /nodes` | `{ok, enabled, nodes: ["tcp://...", ...], operators: [{url, operator, disputed, claimedBy, unsignedAttempts, lastUnsigned}]}`：列出的志愿中转；`operator` 是签名登记的志愿者的 Jay 账户（`jay1...`），没签名是 null；`disputed` 为真表示有别的 Jay 账户签名认领过这个地址（对签名地址的不签名登记只会被拒，不算认领），`claimedBy` 是最近还在认领的最多 4 个账户（满了时最久没再认领的让位，所以一直续期的真正运行者挤不掉）；`unsignedAttempts` / `lastUnsigned` 是这个签名地址被拒的不签名登记次数和最近时间（可能是从没签名、被人抢先登记的志愿者）；`operator` 不变 |
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
- 备用目录用 10.235.255.254、.253、.252（`PublicNetworkProfile.DirectoryIpOf`）。Jay 的写请求（announce、withdraw）发给每个在线的目录，`list` 合并结果（同一个房间取 `seq` 最大的），`lookup` / `find` / `login` 按编号顺序问，回 `op` 或连不上就问下一个。
- Jay 连进来时用 `--dhcp --no-tun --disable-p2p --tcp-whitelist 47660`：成员之间只开敲门端口，不打洞，不暴露公网地址。

### 目录请求

一条 TCP 连接一个请求，全程走加密的目录通道（`DirectoryChannel`，Noise XXhfspsk0，见 `docs/security/directory-identity.md`）：握手第三条消息里是请求 JSON（UTF-8，最长 16 KB），你回一条加密的 JSON，然后关连接。10 秒内要回。没有明文协议了：以 `{` 开头的旧请求直接断开。
回复都有 `ok`。失败时 `{ok: false, error: "<code>", reason?: "<给房主看的话，最多 120 字>"}`。

每个请求都带 `jay: "<Jay 的版本号>"`（比如 `"0.8.0"`）。网络配置有 `policy.minVersion` 时，版本更低或者没带这个字段就回 `{ok: false, error: "version", min: "<最低版本>"}`，不处理请求。

| op | 请求 | 回复 |
|---|---|---|
| `announce` | `{room: <房间>, listed: bool, lookup: ["<64 位十六进制>", ...最多 4 个], fp?: "<64 位十六进制>", account?: "<登录凭证>"}`。`account` 是这个节点登录服务签的凭证，必须绑定房间的 `key`；网络要求登录时没有有效凭证回 `login` | `{ok}` |
| `withdraw` | `{id, seq, sig}`：`sig` 是房间签名钥匙对 `"jay-withdraw-v1\n<id>\n<seq>"` 的 Ed25519 签名（十六进制） | `{ok}` |
| `list` | `{}` | `{ok, rooms: [<房间>, ...]}`：`listed` 为真且不是隐藏房间的，最多 300 个 |
| `lookup` | `{key}`：邀请码大厅名的查找键 | `{ok, ip?, key?, id?}` |
| `find` | `{code: "JAYF1-..."}` | `{ok, room?}`：用登记的指纹密钥算标签，见 `FingerprintCode.Matches` |
| `login` | 见 `NetworkLoginClient.Request` / `LoginAuthority.Handle` | 加密的凭证或 `factor` 提示 |
| `volunteer` | `{url: "tcp://<公网地址>:<端口>", operator?: {cert, at, sig}}`：登记或续期志愿中转（每 3 分钟）。`operator` 是志愿者的 Jay 账户给这台设备签的证书（`DeviceCertificate`）、Unix 秒和设备混合签名（`"jay-volunteer-v2\n" ‖ 网络编号 ‖ "\n" ‖ url ‖ "\n" ‖ at`，64 位大端；网络编号是 `PublicNetworkProfile.Id`），见 `VolunteerOperator` | `{ok, listed}`：第一次先探测，探测通过后的续期 `listed` 为真。新地址每分钟不签名的最多收 60 个、签名的另外 60 个，每个 Jay 账户 8 个，每个来源地址 10 个（经本机转发进来的不分来源）；续期和同一账户回到刚签过的地址不算；多了回 `rate`（不签名和按来源的上限在验签之前就查）。不收时回 `off`，被封回 `banned`，地址不是公网回 `bad`；签名或证书不对、不是这个网络的、时间比目录慢 3 分钟以上或快 1 分钟以上、或网络要求签名（`RequireSignedVolunteers`）而没带时回 `operator`；同一账户给同一地址的签名时间没有比上次接受的新时回 `stale`（Jay 每次至少比上次晚 1 秒）；地址已由别的运营者登记（别的账户、没签名换成签名、或签过名换成不签）、还没过期时回 `owner`，运营者不变，管理接口的 `/nodes` 记下这次认领（签名的记账户，不签名的只计数） |
| `nodes` | `{}` | `{ok, nodes: ["tcp://...", ...最多 8 个，随机]}`：探测过、还在续期的志愿中转，同一个运营者的先只给一个（目录按签名登记的账户分散，只用来分散负载，不是安全保证：一个人可以有好几个 Jay 账户）。谁运行哪个中转只留在目录里，不发给成员 |
| `probe` | `{url: "tcp://<公网地址>:<端口>"}`：想当志愿中转的人先测端口 | `{ok, reachable}`：目录从外网连一次，不列出。不收志愿中转时回 `off`，被封回 `banned`，地址不是公网回 `bad`，测得太频繁回 `rate` |
| `x-*` | 你自己的 | 你自己的 |

Jay 认识的错误码：`owner` / `lookup` / `stale`（编号或查找键被别人占着、旧版本）、`full`（满了）、`gone`、`rate`（限流）、`version`（Jay 太旧，带 `min`）、`login`（要先登录），其他码有 `reason` 就显示 `reason`。

### 房间（room）

```json
{ "id": "<签名公钥 SHA-256 的前 16 字节，十六进制>", "name": "...", "host": "...", "mode": 0,
  "key": "<房主联机公钥，十六进制>", "ip": "10.235.x.y", "game": "1.21.1", "ver": "", "players": 2,
  "approval": true, "tm": 0, "td": false, "invite": "JAY2-...", "lobby": "...", "salt": "...",
  "sk": "<房间签名公钥（Ed25519），十六进制>", "seq": 1762646400000, "sig": "<签名，十六进制>" }
```

- `mode`：0 隐藏、1 公开、2 密码、3 直连。
- 签名：`sig` = Ed25519(`sk` 的私钥, `"jay-room-v1\n"` + 去掉 `sig` 和 `updated` 后按上面字段顺序的 JSON)。具体以 `RoomListing.SignedBytes` 为准。
- **目录必须**：核对 `id == SHA-256(sk)[..16]` 和签名；同一个 `id` 只收 `seq` 更大的版本；撤下过的 `id` 不收不大于撤下 `seq` 的版本；查找键和指纹密钥先到先得，别的房间占着就回 `lookup`；90 秒没续期就删掉（Jay 每 30 秒续期一次）。
- 房间信息不含任何房间钥匙。房主的真实性由加入方敲门时核对 `key` 保证，目录不用信任。

## 安全边界

- 你的节点看得到的东西见 [network-server.md](network-server.md#节点能看到什么)。框架不会把这些发到别处；你加的扩展会不会，由你负责，请在网络主页上写清楚。
- 节点不能替房主放人进房间，也不能读房间里的流量：子网钥匙在敲门后经加密通道直接由房主发给加入方。
- 网络配置只能比 Jay 的默认更严（见 [public-network.md](public-network.md#网络配置文件)），节点改不了玩家这边的安全设置。
