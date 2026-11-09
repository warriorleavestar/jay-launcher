# 公共网络注册表

Jay 默认进的是内置的 Jay 公共网络。别人也可以搭自己的公共网络（比如某个服务器社区、某个地区的玩家群），
用一串 `JAYNET1-...` 的**连接串**分享出去，玩家在联机页“公共网络”里粘贴就能换过去。

连接串带签名，内容一个字节都改不了。谁都能自己签一个，但 Jay 里只有 **Jay 注册表签的** 才显示“已审核”；
没审核的网络，玩家添加时会看到提醒，确认后才加。

## 提交审核

1. 在这个文件夹里加一个 `<编号>.json`（文件名就是编号），格式见下面。
2. 开 PR。CI 会检查格式（`jay-server network check`）。
3. 维护者审核：节点能不能连、名字有没有冒充别人、有没有违规内容。
4. 合并后，CI 用注册表钥匙签名，连接串发到仓库的 [networks Release](../../releases/tag/networks) 里（`<编号>.jaynet`）。

要更新节点，改 JSON 并把 `seq` 加一，再走一遍 PR。玩家粘贴新连接串时，同一把钥匙签的更大 `seq` 会直接替换旧的。

## 格式

```json
{
  "v": 1,
  "id": "nz-friends",
  "name": "新西兰朋友服",
  "network": "jay-nz-friends",
  "secret": "nz-friends-public-v1",
  "nodes": ["tcp://node1.example.org:11010", "udp://node1.example.org:11010"],
  "seq": 1,
  "home": "https://example.org/jay",
  "config": { "url": "https://example.org/jay/network.json", "sha256": "<64 位十六进制>" }
}
```

| 字段 | 说明 |
|---|---|
| `id` | 编号：2~32 个小写字母、数字、减号。`jay` 留给内置网络。 |
| `name` | 显示的名字，最多 40 个字。 |
| `network` / `secret` | EasyTier 网络名和密钥。**公共网络的密钥是公开的**，只用来和别的网络分开，不要拿它当保密手段。 |
| `nodes` | 1~16 个节点（`tcp://` 或 `udp://`），节点用 `jay-server directory --network <连接串>` 运行。 |
| `seq` | 版本号，每次更新加一。 |
| `exp` | 可选，过期时间（Unix 秒）。 |
| `home` | 可选，网络主页（https）。 |
| `config` | 可选，网络的服务器配置文件（https 地址和 SHA-256，以后用来改界面、加登录和安全策略）。 |

公共网络只用来看房间列表和敲门，游戏、聊天、传文件都在房主的子网里端到端加密，公共网络节点看不到内容。

## 自己签（不经审核）

```bash
jay-server network keygen --out my-network.key     # 只生成一次，私钥自己留好
jay-server network sign nz-friends.json --key my-network.key
jay-server directory --network <上面打印的连接串>    # 在有公网 IP 的机器上跑节点，放行 TCP/UDP 11010
```

## 注册表钥匙（维护者）

注册表钥匙用 `jay-server network keygen` 生成。私钥放进仓库设置里 `network-registry` environment 的 secret `JAY_NETWORK_KEY`
（建议给这个 environment 打开“需要人工批准”），公钥写进 `src/Amber.Core/Multiplayer/Public/NetworkString.cs` 的 `RegistryKeys`。
换钥匙时新旧公钥并存一段时间，等旧连接串都重签以后再删旧的。
