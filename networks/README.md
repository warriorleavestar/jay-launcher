# 公共网络注册表

Jay 默认进的是内置的 Jay 公共网络。别人也可以搭自己的公共网络（比如某个服务器社区、某个地区的玩家群），
用一串 `JAYNET2-...` 的**连接串**分享出去，玩家在联机页“公共网络”里粘贴就能换过去。

连接串带签名，内容一个字节都改不了。谁都能自己签一个，但 Jay 里只有 **Jay 信任钥匙签够数的** 才显示“已审核”
（目标是 3 位信任人里 2 位；现在只有一位信任人的钥匙和一份离线冷备份，两把都要签）；
没审核的网络，玩家添加时会看到提醒，确认后才加。

## 提交审核

1. 在这个文件夹里加一个 `<编号>.json`（文件名就是编号），格式见下面。
2. 开 PR。CI 会检查格式（`jay-server network check`）。
3. 维护者审核：节点能不能连、名字有没有冒充别人、有没有违规内容。
4. 合并后，网络主人用自己的钥匙签名（`network sign`），把连接串交给 Jay 信任人。信任人各自在自己的离线电脑上加签
   （`network cosign`），够 2 个以后连接串就是“已审核”的，再由维护者上传到仓库的 [networks Release](../../releases/tag/networks)
   （`<编号>.jaynet`）。CI 只检查格式，不签名，也拿不到任何私钥。

要更新节点，改 JSON 并把 `seq` 加一，再走一遍 PR。玩家粘贴新连接串时，同一个网络主人签的更大 `seq` 会直接替换旧的。

## 格式

```json
{
  "v": 2,
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
| `config` | 可选，网络配置文件（https 地址和 SHA-256）：公告、链接、安全策略、界面主题。格式见 [docs/public-network.md](../docs/public-network.md#网络配置文件)。 |

公共网络只用来看房间列表和敲门，游戏、聊天、传文件都在房主的子网里端到端加密，公共网络节点看不到内容。

## 自己签（不经审核）

```bash
jay-server network keygen --out my-network.key     # 只生成一次，私钥自己留好
jay-server network sign nz-friends.json --key my-network.key
jay-server directory --network <上面打印的连接串>    # 在有公网 IP 的机器上跑节点，放行 TCP/UDP 11010
```

## Jay 信任人（维护者）

“已审核”不由一把钥匙决定。目标是 3 个信任人、任意 2 人签；现在只有一位信任人，他的钥匙和一份离线冷备份两把都要签，
所以眼下防的是一把钥匙被偷，防不了这一个人。每把钥匙在自己的离线电脑上用 `jay-server network root-key --out <文件> --paper`
生成，私钥只留在持有人手里（U 盘 + 手抄），不放进仓库、CI、服务器、网盘或聊天软件。

- 每把钥匙是一个 32 字节种子，推出一把 Ed25519 和一把 ML-DSA-65（抗量子，FIPS 204）。签名两种都要对才算这个人签了。
- 钥匙文件用口令加密（Argon2id + ChaCha20-Poly1305）；`--paper` 在屏幕上显示手抄的两行，`network paper-check` 核对抄得对不对，
  `network paper-restore` 从纸上恢复。
- 完整公钥写进 `src/Amber.Core/Multiplayer/Public/TrustQuorum.cs` 的 `JayTrust.Keys`（现在两把），`JayTrust.Need` 是要几把签（2）。

审核一个网络（每个信任人各自从仓库里的 `networks/<编号>.json` 核对内容，不只看别人转来的文件）：

```bash
jay-server network cosign <网络主人签好的连接串或文件> --key <自己的信任钥匙> [--prev <上一版>] --out 加签后.jaynet
# 先逐项显示内容和被签内容的 SHA-256（两位信任人对一下这一行），手打网络编号最后 6 位才签；不在终端里运行就不签
# 把输出交给下一个信任人，他在自己的离线电脑上再跑一次 cosign
jay-server network show 加签后.jaynet   # 显示 “Jay 信任人签了 2/2，Jay 审核过”
```

一个签名约 5.3 KB，主人加两个信任人的连接串约 23 KB，所以审核过的网络用 `.jaynet` 文件或网络列表传，不适合手打。

同样的规则也用在内置 Jay 公共网络的目录证书上（`network dir-cert`，第二把钥匙用 `--cosign`）。现在是 2 把信任钥匙要 2 把（`JayTrust.Need`），
所以丢了一把就签不了，只能发一个新版 Jay，把新公钥换进 `JayTrust.Keys`（换钥匙的声明由旧名单签够数，见隐私方案第 3 项）。
