# Jay 网络服务端（官方版）

> **实验功能**：已通过单元测试和本机真 EasyTier 的端到端测试（目录节点 + 地区种子节点），还没在两台不同网络的电脑上实测。

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
jay-server network init nz-friends --name 新西兰朋友服 --host 203.0.113.5

# 2.（可选）改 nz-friends/config.json：公告、链接、允许的房间方式、传文件、最低版本、登录……
#    传到一个 https 地址（比如 GitHub Pages），然后算哈希并写进网络信息（版本号自动加一）：
jay-server network config-hash nz-friends/config.json --network nz-friends/nz-friends.json --url https://example.org/jay/config.json

# 3. 签名，得到连接串 JAYNET1-...，发给玩家（联机 → 公共网络 → 粘贴 → 添加网络）
jay-server network sign nz-friends/nz-friends.json --key nz-friends/network.key > nz-friends.jaynet

# 4. 跑节点
jay-server directory --network nz-friends.jaynet --config nz-friends/config.json
```

- `network.key` 是签名私钥，只自己留着。丢了就只能换一把钥匙重新发连接串，玩家要先删掉旧的再加。
- 改了配置文件就要重新 `config-hash` 和 `sign`，玩家粘贴新连接串后才生效。
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
jay-server node unban host <房主指纹>
jay-server node bans
jay-server node remove <房间编号>       # 只删这一次，房主续期时重新审核
```

- `node` 命令经本机管理接口操作运行中的节点，口令在数据目录的 `admin.token`（只有运行节点的用户能读）。要和 `directory` 用同一个 `--dir`（或 `JAY_SERVER_DIR`）。
- 健康检查：`curl http://127.0.0.1:47669/health`，不用口令，给监控和 Docker 用。
- 封禁名单存在数据目录的 `bans.json`。

审核相关的选项：

| 选项 | 作用 |
|---|---|
| `--config <配置.json>` | 按配置里的 `policy.modes` 挡不允许的房间方式（Jay 本身也挡，这里防改过的客户端） |
| `--blocked-words <文件>` | 一行一个词（`#` 开头是注释）：公开房间的名字、房主名里有就不收 |
| `--rate-limit 3,6,600` | 每人同时几个房间、每人每分钟新发布几次、整个目录每分钟几个新房间（默认就是这个）。`--no-rate-limit` 关掉 |
| `--admin-port 47669` / `--no-admin` | 管理接口端口，或者不开 |

房主被拒时，Jay 里会显示节点给的原因（比如“房间名或房主名里有这个网络不允许的词”）。

## 五、分地区多开节点

一个网络只能有**一台**跑目录（它占着 10.235.0.1）。别的地区的机器只当种子节点，帮忙转发、离玩家近一点：

```bash
# 主节点（比如在香港）
jay-server directory --network nz-friends.jaynet --listen 11010
# 地区节点（比如在新西兰），连到主节点
jay-server directory --network nz-friends.jaynet --listen 11010 --seed-only --seeds tcp://主节点:11010
```

把所有节点都写进网络信息的 `nodes`，Jay 会都连上。目录在多个节点之间同步（志愿节点）还没做，见“以后”。

## 六、长期运行

**Linux（systemd）**：在解压好的 `JayServer-linux-*` 文件夹里：

```bash
sudo bash install-network-node.sh
# 自己的网络：
sudo NETWORK=nz-friends.jaynet CONFIG=nz-friends/config.json ACCOUNTS=1 bash install-network-node.sh
# 地区节点：
sudo NETWORK=nz-friends.jaynet SEED_ONLY=1 SEEDS=tcp://主节点:11010 bash install-network-node.sh
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

## 节点能看到什么

- 公开房间的名字、房主名、人数、游戏版本、房主在公共网络里的地址（就是房间列表的内容）。
- 隐藏房间的查找键（邀请码大厅名的哈希）和房主地址，看不到邀请码本身。
- 打开了指纹查找的房主的指纹密钥（见 [public-network.md](public-network.md#设备指纹)）。
- 开了账号登录时：账号名、密码哈希、登录成功和失败的次数。
- 看不到：游戏流量、聊天、文件、房间钥匙、邀请码、密码房间的密码。

管理接口和日志只给数字和公开房间，不记录谁看了列表、谁敲了谁的门。

## 以后

- 目录在多个节点之间同步（房间信息本来就是签过名的，可以直接转发，协议不用改）。
- 志愿节点（玩家自愿把自己的电脑当种子节点）。
