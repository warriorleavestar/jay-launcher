# Jay 网络服务端（官方版）

> **实验功能**：已通过单元测试和本机真 EasyTier 的端到端测试（目录节点 + 地区种子节点）；备用目录只有单元测试；还没在两台不同网络的电脑上实测。

Jay 的公共网络（看房间列表、敲门、账号登录）要有人跑**网络节点**。网络服务端就是 `jay-server directory`：

| 它做的事 | 说明 |
|---|---|
| 种子节点 | 一个 EasyTier，对公网监听 TCP/UDP 11010，Jay 连上它进公共网络 |
| 房间目录 | 在公共网络里固定地址 10.235.0.1:47668，只存房主签过名的房间信息，不经手任何钥匙 |
| 账号登录 | 可选（`--accounts`），账号密码加密后才发给节点，节点只存哈希，见 [public-network.md](public-network.md#网络账号账号密码--两步验证) |
| 房间审核 | 封禁名单、限流、屏蔽词、按网络配置挡不允许的房间方式（防改过的客户端） |
| 管理接口 | 只听本机 127.0.0.1:47669：健康检查、计数、公开房间、封禁 |

游戏、聊天、传文件、远程控制都在房主自己的子网里端到端加密，网络节点看不到内容。

它建立在 [网络服务端框架](network-server-framework.md) 上。想接自己的账号库、审核规则、验证方式，用框架写自己的节点程序。

## 一、Jay 公共网络的节点

```bash
jay-server directory --listen 11010
```

第一次运行会下载固定版本的 EasyTier（校验 SHA-256）。在云服务商的安全组里放行 **TCP 和 UDP 11010**。玩家在 Jay 的「联机 → Jay 公共网络 → 公共节点」里填 `tcp://<公网地址>:11010`。

## 二、自己的公共网络（四步）

```bash
# 1. 生成签名钥匙、网络信息、网络配置模板
# --host 写节点的域名：网络连接串会广泛传播，不能写公网 IP（Jay 会拒绝）
jay-server network init nz-friends --name 新西兰朋友服 --host node.example.org

# 2.（可选）改 nz-friends/config.json：公告、链接、允许的房间方式、传文件、最低版本、登录……
#    传到一个 https 地址（比如 GitHub Pages），然后算哈希并写进网络信息（版本号自动加一）：
jay-server network config-hash nz-friends/config.json --network nz-friends/nz-friends.json --url https://example.org/jay/config.json

# 3. 签名，得到连接串 JAYNET2-...，发给玩家（联机 → 公共网络 → 粘贴 → 添加网络）
jay-server network sign nz-friends/nz-friends.json --key nz-friends/network.key > nz-friends.jaynet

# 4. 跑节点
jay-server directory --network nz-friends.jaynet --config nz-friends/config.json
```

- `network.key` 是签名私钥，只自己留着。丢了就只能换一把钥匙重新发连接串，玩家要先删掉旧的再加。
- 改了配置文件就要重新 `config-hash` 和 `sign`，玩家粘贴新连接串后才生效。
- Directory identity (recommended): before step 3, run `jay-server network dir-key nz-friends/nz-friends.json --dir <node data dir>` on each directory node. It adds the node's key to `"dirKeys"`, and Jay then refuses to talk to anyone else who takes the directory address. Back up `directory.key` in the data dir. Details: [security/directory-identity.md](security/directory-identity.md).
- 想让 Jay 显示“已审核”：把 `nz-friends.json` 提交到仓库的 [networks/](../networks/README.md)。

## 三、开账号登录

```bash
jay-server directory --network nz-friends.jaynet --config nz-friends/config.json --accounts [--require-2fa]
jay-server account add alice
jay-server account 2fa on alice --issuer nz-friends
```

第一次带 `--accounts` 运行会打印 `login` 段，写进 `config.json`，再走一遍第二步。

## 四、管理

```bash
jay-server node status                 # 健康状态和计数（发布、续期、被拒、登录成功 / 失败）
jay-server node rooms                  # 公开房间（不公开的只显示数量，不显示内容）
jay-server node ban host <房主指纹> --reason 刷广告   # 封房主：他换房间也进不了列表，已有的马上拿掉
jay-server node ban room <房间编号>
jay-server node ban account <账号> --for 7d            # 封网络账号：他的房间马上拿掉，也不能再登录
jay-server node ban room-host <房间编号> --for 24h     # 封这个房间的房主：指纹 + 他发布时登录的账号
jay-server node unban host <房主指纹>
jay-server node bans
jay-server node remove <房间编号>       # 只删这一次，房主续期时重新审核
```

**网页管理面板**：`jay-server node panel` 打印一个链接（`http://127.0.0.1:47669/#token=...`），在节点那台机器的浏览器里打开就能看状态和计数、公开房间、志愿中转、封禁，并且能删房间、封房主（指纹 + 账号）、封禁和解封，手机宽度也能用。节点在远程服务器上时，先在自己电脑上运行 `ssh -L 47669:127.0.0.1:47669 <用户>@<服务器>`，再在自己电脑上打开这个链接。

- 面板只听本机，用的还是 `admin.token`。口令放在链接的 `#` 后面，浏览器不会把它发给任何服务器；页面拿到后马上从地址栏去掉，只存在这个标签页里，关掉就没了。
- 请求里的 Host 必须是 127.0.0.1、localhost 或 [::1]，别的网站发来的请求（Origin 不是本机）一律拒绝，防 DNS 重绑定和跨站请求。页面没有任何外部脚本、字体、图片（CSP `default-src 'none'`），房间名、房主名只当文字显示。

- `node` 命令经本机管理接口操作运行中的节点，口令在数据目录的 `admin.token`（只有运行节点的用户能读）。要和 `directory` 用同一个 `--dir`（或 `JAY_SERVER_DIR`）。
- 健康检查：`curl http://127.0.0.1:47669/health`，不用口令，给监控和 Docker 用。
- Prometheus 监控：`GET /metrics` 给出 Prometheus 文本格式的计数（房间数、公开数、新房间 / 续期 / 离开 / 被拒的累计数、登录成功失败、封禁条数、志愿中转数、运行时长）。只有总数，没有房间名、房主、地址、指纹或账号。要口令；在节点那台机器上的 Prometheus 这样配：

  ```yaml
  scrape_configs:
    - job_name: jay-node
      static_configs:
        - targets: ["127.0.0.1:47669"]
      authorization:
        credentials_file: /etc/prometheus/jay-admin.token   # 数据目录里 admin.token 的副本，只给 Prometheus 的用户读
  ```

  `admin.token` 本身只有运行节点的用户能读，所以复制一份（`sudo install -m 600 -o prometheus /var/lib/jay-network/admin.token /etc/prometheus/jay-admin.token`），别放宽原文件的权限。target 必须写 `127.0.0.1` 或 `localhost`（管理接口只认本机的 Host）。Prometheus 在别的机器上时，用 SSH 隧道把 47669 转过去，target 仍写 `127.0.0.1:<本地端口>`。节点自己从不往外发数据，是你的 Prometheus 来取。
- 封禁名单存在数据目录的 `bans.json`。`--for 30m` / `24h` / `7d` 是临时封禁，到点自动解除；不加就是永久。房主被拒时能看到原因和解封时间。
- 节点每 5 秒检查一次 `bans.json` 有没有被改过，改了就重新读。几台目录节点想共用一份封禁名单，可以用 rsync、共享盘之类把这个文件同步过去，不用在每台上各执行一次。
- 封账号只在开了账号登录的节点上有用：Jay 发布房间时会带上登录凭证，节点核对过签名才记下账号。
- Account bans match the exact account name. Lists saved by older versions (`"v": 1` in `bans.json`) matched the name in any case; at start-up the node ties each such ban to the one account with that name and saves the list as `"v": 2`. A ban that matches no account, or several accounts that differ only in case, keeps matching any case: `node bans` marks it `"anyCase": true` and the log names it. Ban the exact account name and unban the old entry.

## 要求的 Jay 版本

在网络配置文件的 `policy` 里写最低版本（`network init` 生成的模板默认就是 `jay-server` 自己的版本）：

```json
"policy": { "modes": ["public", "password", "hidden", "direct"], "minVersion": "0.8.0" }
```

- Jay 读到配置后，自己低于这个版本就不连这个网络，提示升级。
- 节点用 `--config` 加载同一份配置时，也按它挡：每个目录请求都带着 Jay 的版本号，太旧（或者没带版本号的旧版 Jay）就回 `version`，Jay 显示“这个网络要求 Jay x.y.z 或更新的版本”。防的是改过的客户端或没更新配置的旧客户端。
- 改了版本要求，和改别的配置一样，重新 `config-hash` 和 `sign`。
- 配置里 `login.required` 为 `true` 时，节点也要求发布房间的人先登录（没登录回 `login`）。

审核相关的选项：

| 选项 | 作用 |
|---|---|
| `--config <配置.json>` | 按配置里的 `policy.modes` 挡不允许的房间方式（Jay 本身也挡，这里防改过的客户端） |
| `--blocked-words <文件>` | 一行一个词（`#` 开头是注释）：公开房间的名字、房主名里有就不收 |
| `--rate-limit 3,6,600` | 每人同时几个房间、每人每分钟新发布几次、整个目录每分钟几个新房间（默认就是这个）。`--no-rate-limit` 关掉 |
| `--admin-port 47669` / `--no-admin` | 管理接口端口，或者不开 |

房主被拒时，Jay 里会显示节点给的原因（比如“房间名或房主名里有这个网络不允许的词”）。

## 五、多开节点：备用目录和地区种子

**备用目录**：最多 4 台跑目录，一台挂了房间列表、查找、发布照常。

```bash
# 主目录（编号 1，10.235.0.1）
jay-server directory --network nz-friends.jaynet --config nz-friends/config.json
# 备用目录（编号 2–4，10.235.255.254 / .253 / .252），连到主节点
jay-server directory --network nz-friends.jaynet --config nz-friends/config.json --directory-index 2 --seeds tcp://主节点:11010
```

- Jay 把房间发给**每个**在线的目录，看列表时合并所有目录的结果，查找按编号顺序问。备用目录之间不用同步、不用互相信任：房间信息是房主签过名的。
- 备用目录要和主目录用同样的 `--config`、`--blocked-words`。封禁要在每台上各执行一次 `node ban`，或者把 `bans.json` 同步过去（节点会自动重读）。
- 开账号登录时，把主目录数据目录里的 `login.key` 和 `accounts.json` 复制过去（登录凭证的签名钥匙要一样）。
- 旧版 Jay（这个版本之前）只认主目录。

**地区种子**：别的地区的机器只帮忙转发、离玩家近一点，不跑目录：

```bash
jay-server directory --network nz-friends.jaynet --listen 11010 --seed-only --seeds tcp://主节点:11010
```

把所有节点都写进网络信息的 `nodes`，Jay 会都连上。

## 六、长期运行

**Linux（systemd）**：在解压好的 `JayServer-linux-*` 文件夹里：

```bash
sudo bash install-network-node.sh
# 自己的网络：
sudo NETWORK=nz-friends.jaynet CONFIG=nz-friends/config.json ACCOUNTS=1 bash install-network-node.sh
# 地区节点：
sudo NETWORK=nz-friends.jaynet SEED_ONLY=1 SEEDS=tcp://主节点:11010 bash install-network-node.sh
# 备用目录：
sudo NETWORK=nz-friends.jaynet CONFIG=nz-friends/config.json SEEDS=tcp://主节点:11010 EXTRA="--directory-index 2" bash install-network-node.sh
```

脚本建 `jay` 用户、数据放 `/var/lib/jay-network`、注册 `jay-network` 服务、在 ufw / firewalld 上放行端口。之后：

```bash
journalctl -u jay-network -f
sudo -u jay JAY_SERVER_DIR=/var/lib/jay-network /opt/jay-server/jay-server node status
```

**Docker**（在仓库根目录）：

```bash
mkdir -p server/docker/jay-network-data && sudo chown 10010 server/docker/jay-network-data
docker compose -f server/docker/docker-compose.yml up -d --build
docker compose -f server/docker/docker-compose.yml exec jay-network /opt/jay-server/jay-server node status
```

镜像自带健康检查（容器里 curl `/health`）。

**Windows**：解压后在文件夹里打开终端运行 `jay-server directory ...`；防火墙询问时允许。想开机自动运行，用“任务计划程序”建一个开机任务。

## 把节点绑定到硬件（防止被整盘拷走冒充）

在节点上 `jay-server node bind`（生成只含加盐哈希的 hardware.request.json）→ 拷到放根钥匙的离线电脑 `jay-server network hw-cert --root <根钥匙> --request hardware.request.json --out hardware.cert` → 第二位信任人在自己的离线电脑上 `jay-server network hw-cert --cosign hardware.cert --root <自己的钥匙>`（Jay 公共网络要三人中两人，自己的网络看 dirRootsNeed；每次签名前逐项显示内容和核对码，要手打目录公钥最后 6 个字符）→ 拷回节点 `jay-server node bind --install hardware.cert`。之后 directory.key、login.key 只以加密形式存在（有 TPM 就封存进 TPM，Linux 上还绑定安全启动状态）。硬件对不上时节点删除数据目录里的 Jay 文件；运行环境被动过（调试器、注入、文件可被别人改、TPM 不解封）时拒绝运行。不需要 root：Linux 上把服务账号加进 tss 组即可（安装脚本会自动加）；手动绑定时也用服务账号运行：`sudo -u jay JAY_SERVER_DIR=/var/lib/jay-network /opt/jay-server/jay-server node bind`。换硬件、迁移虚拟机前先 `jay-server node bind --check`。细节和限制见 [security/node-hardware-binding.md](security/node-hardware-binding.md)。

## 节点能看到什么

运营一个节点，你（以及能进这台机器的人）看得到下面这些。这张表要和 Jay 的“关于和隐私”页对得上，新功能改了它就要改这里。

| 看得到 | 什么时候 | 以后 |
| --- | --- | --- |
| 连到这台节点的电脑的公网 IP 和端口（EasyTier 这一层） | 节点当种子或中转时 | 不变；终极方案的三跳转发才解决 |
| 每个成员在公共网络里的地址（10.235.x.x）和它发目录请求的时刻 | 一直 | 不变 |
| 公开房间的名字、房主名、人数、游戏版本、房主地址（就是房间列表） | 一直 | 不变 |
| 隐藏房间的房间信息（名字、房主名、房主地址、房主公钥），以及它的封好条目放在哪个索引下（房主发布时带着整份签名的房间信息） | 一直 | 不变 |
| 快照的读者（网络里的其他人）只看到隐藏房间等长的封好条目，看不出地址、是哪个房间、是哪个邀请 | 一直 | 不变 |
| 谁在什么时候按邀请找房间 | 看不到：每个 Jay 每 60 秒取一次整份目录（`snap`，只有变化），在本机查 | 不变 |
| 谁在什么时候按指纹码找房间 | 一直 | 第 7、14 项后好友码只带账户哈希 |
| 开了指纹查找的房主的指纹密钥（见 [public-network.md](public-network.md#设备指纹)） | 房主打开时 | 第 7、14 项后好友码只带账户哈希 |
| 密封信件的箱号、大小、存取时刻（看不到内容和寄信人） | 一直 | 第 13 项后箱号由双方共享钥匙算出，读写次数固定 |
| 志愿中转的公网地址和端口（也会发给网络里要中转的人） | 有人登记时 | 不变；界面写明了 |
| 开了账号登录时：账号名、密码哈希、登录成败次数、哪个账号发布了哪个公开房间 | 开登录时 | 不变 |

看不到：游戏流量、聊天、私聊、文件、房间钥匙、邀请码本身、密码房间的密码。这些在房间自己的加密子网里，节点只转发密文。

管理接口、网页面板和 `/metrics` 只给数字和公开房间；日志不记连上来的 IP，也不记谁看了列表、谁敲了谁的门。节点不往外发任何数据，也没有远程开关。

## 七、志愿中转（玩家自愿帮忙转发）

玩家可以在 Jay 的「联机设置 → 公共网络 → 志愿中转」里打开，把自己的电脑当这个网络的中转：填公网地址（IP 或域名）、端口（默认 11010，路由器上要转发 TCP 和 UDP）、限速。

- 填地址旁边的【检测】用 EasyTier 的 STUN 结果填上这台电脑的公网地址；【测试端口】请目录从外网连一次这个地址和端口（这台电脑还没在这个端口当中转时，Jay 临时监听一下），告诉你连不连得上，连不上时写明常见原因（路由器没转发、防火墙、运营商共用地址）。两个按钮都要先连上公共网络。测试只回答通不通，不会把你列出来；同一个地址 10 秒内只测一次，整个目录每分钟最多测 30 次。
- 志愿者的 Jay 对公网监听这个端口，只帮这个网络转发（`--relay-network-whitelist`），按限速收流量（`--instance-recv-bps-limit`），每 3 分钟向目录登记一次。
- 目录先自己连一次这个地址，连得上才列出来，之后每小时重新确认；10 分钟没登记就拿掉。只收公网地址（局域网、本机、运营商内网、文档用的地址都不收），域名解析出内网地址也不收，防止有人拿目录去探别人家里的网络。
- 每个 Jay 连上公共网络后向目录要几个志愿中转，另外连上其中最多 3 个，并记住最近 16 个；下次官方节点连不上时，先试记住的这些。
- 经过志愿中转的只有公共网络的流量（房间列表、敲门、目录请求），和经过任何节点一样；游戏、聊天、文件都在房间自己的加密子网里。志愿者的公网地址会给向目录要中转的人，界面上写明了。
- 网络运营者：配置文件里 `"policy": { "volunteers": false }` 就不收（Jay 也不显示这个开关），或者节点加 `--no-volunteers`。`jay-server node nodes` 看列出的志愿中转，`jay-server node ban node tcp://<地址>:<端口>` 封掉。志愿者在志愿中转设置里打开【用我的 Jay 账户签名】（默认关，以前就开着志愿中转的升级后也是关的；打开时才显示签名会让运营者知道什么）、MC 账号又绑定了 Jay 账户时，登记会带设备签名；只收签名的网络会提示志愿者打开这个开关。`node nodes` 也会写出是哪个 Jay 账户在运行；`jay-server node ban operator jay1...` 把这个人的中转一起拿掉，这些地址也一起封（同样的时间，再封一次时跟着改；有别的没被封的账户签名认领过、或者有人不签名来登记被拒过的地址只拿掉不封，可能是被抢先登记的，但也可能是这个人自己的另一个账户，所以命令会分别列出一起封掉的和只拿掉的地址、日志里也有，运营者要看一眼：封错了用 `node unban node` 解封，该封的单独封）；解封这个人时这些地址也一起解封。封运营者总是挡得住这个账户的签名登记；不签名的登记只有开了 `--signed-volunteers` 才挡得住；新开的账户挡不住。签名登记会带上设备证书，运营者也会看到设备在账户里的名字。节点加 `--signed-volunteers` 就只收签名登记的志愿中转。Jay 要中转时同一个账户的只先给一个，用来分散负载；一个人可以有好几个账户，所以这不是安全保证。一个地址登记后到过期前不能换运营者；有人用别的账户签名来认领时（不签名的只会被拒并计数，不算认领；认领名额满了时最久没再认领的让位，一直在续期的最早认领者保留；认领也限速；`node nodes` 还写出认领总次数和不同账户数），`node nodes` 的 `operators` 里会写 `disputed` 和最近还在认领的几个账户，运营者可以自己判断要不要封。一直收到“以别的身份登记过”的志愿者，可能是地址被人抢先登记了，可以找运营者处理。

## 以后

- 志愿中转的真机测试（需要一台有公网 IP 或能做端口转发的电脑）。
