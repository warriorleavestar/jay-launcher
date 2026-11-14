# 界面用词表 / UI glossary

Use these terms when writing or translating UI text. The tables live in `src/Amber.Core/Localization/`; `en.json` is the source. See [CONTRIBUTING.md](../CONTRIBUTING.md#ui-translations).

写新文案、改翻译时按这张表用词。翻译表在 `src/Amber.Core/Localization/`，英文 `en.json` 是原文。

## 核心概念

| 概念 | 简体中文 | 繁體中文 | English | 日本語 | 不要这样写 |
|---|---|---|---|---|---|
| 一个已安装、可配置的游戏文件夹（自己的模组、存档、设置） | 实例 | 實例 | instance | インスタンス | 不要用“版本 / version / バージョン”指它，例如“删除版本”应写“删除实例” |
| Minecraft 本体的版本号 | 游戏版本（紧凑处写“MC 1.21.1”） | 遊戲版本 | game version | ゲームバージョン | 不要只写“版本”而让人分不清是游戏还是实例 |
| Forge / NeoForge / Fabric / Quilt | 加载器 | 模組載入器 | mod loader | MODローダー | |
| 预先配好的一套模组和配置 | 整合包 | 模組包 | modpack | MODパック | |
| 模组 | 模组 | 模組 | mod | MOD | 日文里 MOD 和后面的字之间不加空格（MODが、MODファイル） |
| 资源包 / 光影包 / 数据包 | 资源包 / 光影 / 数据包 | 資源包 / 光影 / 資料包 | resource pack / shader pack / data pack | リソースパック / シェーダーパック / データパック | |
| 存档 | 世界 | 世界 | world | ワールド | |

## Multiplayer

One thing has one name, on every page and in every language. The table is the whole vocabulary; `LocalizationTests.RetiredNamesDoNotComeBack` fails the build when a retired name returns.

| Concept | 简体中文 | 繁體中文 | English | 日本語 | Retired names (do not use) |
|---|---|---|---|---|---|
| The code a host sends so someone can knock (`JAY2-`, `JAYS2-` for a server) | 邀请 / 服务器邀请 | 邀請 / 伺服器邀請 | invite / server invite | 招待 / サーバー招待 | 邀请码, invite code, 招待コード, 房间码 for new codes |
| Asking to be let in | 敲门 | 敲門 | knock | ノック | |
| The short code both sides compare | 安全码 | 安全碼 | safety code | 安全コード | 指纹, fingerprint |
| Comparing four emoji when adding a friend | 对表情 | 對表情 | compare emoji | 絵文字の確認 | |
| A person you added and checked | 朋友 | 朋友 | friend | フレンド | 联系人, contact, 連絡先 |
| The code that lets someone ask to add you (`JAYC1-`) | 好友码 | 好友碼 | friend code | フレンドコード | 联系人码, contact code |
| The code that points at one computer and finds its room (`JAYF2-`, older `JAYF1-`) | 设备码 | 裝置碼 | device code | デバイスコード | 好友码 / friend code for this code, 设备认证, device verification |
| The host checking that a connection really comes from that computer | 核对设备 | 核對裝置 | device check | デバイス確認 | 设备验证 |
| The key pair of this installation | 这台设备的身份 | 這台裝置的身分 | this device's identity | このデバイスの身元 | 联机身份, multiplayer identity |
| A server that helps two sides meet and forwards when they cannot connect directly | 中转 | 中轉 | relay | 中継 | 中转服务器, 联机节点, 节点, relay server, node, ノード |
| A room list and knock service run by a community (`JAYNET2-` adds one) | 公共网络，its code is a 网络地址 | 公共網路 / 網路位址 | public network / network address | 公開ネットワーク / ネットワークアドレス | 连接串, network string, 网络服务器 |
| The address a public network is first reached at (advanced) | 种子地址 | 種子位址 | seed address | シードアドレス | |
| Servers a friend runs with `jay-server`, and the one you host | 服务器（我加入的 / 我开的） | 伺服器 | servers (Joined / Hosted by me) | サーバー（参加済み / 自分のサーバー） | Jay 服务器, Jay server |
| The extra encryption layer around room traffic | 保护 | 保護 | protection | 保護 | 数据封装, 封装, tunneling, 伪装, disguise (say "looks like an MC login") |
| How much of the network a room joins | 范围：只联这个游戏 / 整个局域网 | 範圍 | scope: this game only / whole LAN | 範囲：このゲームだけ / LAN 全体 | 完整局域网模式, full LAN mode |
| Sending all traffic out through a room member | 经 TA 上网 / 让房间里的人经我上网 | 經 TA 上網 | going online through a member | メンバー経由のインターネット接続 | 出口, exit, exit node |
| Sending only the game's login and server traffic through a member | 转发游戏（只转游戏） | 轉發遊戲 | game forwarding (game only) | ゲームの転送 | |
| Seeing and controlling someone's computer to help them | 远程协助 | 遠端協助 | remote help | リモートサポート | 远程控制, remote control |
| Dropping everyone when an impostor is found | 紧急断开 | 緊急斷開 | emergency disconnect | 緊急切断 | 熔断, circuit breaker |
| The room key inside an old room code | 房间密钥 | 房間金鑰 | room key | ルームキー | 密码 / password |

Rules that go with the table:

- There is no "lobby" (大厅) in the UI. Say what happens: "waiting for knocks", "looking for the host".
- Explanations on a main screen are two lines at most. Longer text goes behind "Learn more" or into a help article.
- State facts, not promises. Say who can see what ("a relay can see your IP address, not the content"); do not write "safe to share", "rest assured" or 放心.
- Status is a mark (a tag, a dot, a chip), not a sentence.
- The old `AMB-` room code, the sealed direct codes (`JAYDS1-` / `JAYDR1-`: 加密直连码, 回复码) and the word "seed" keep their names because they only appear in their own flows.

Names in code are older than the UI words and stay as they are. `PastedCodeKind.FriendCode` and the `multiplayer.device.*` keys are the device code (`JAYF2-`, older `JAYF1-`); `PastedCodeKind.ContactCode` and `multiplayer.contacts.*` are the friend code (`JAYC1-`) and the friends list; `multiplayer.nodes.*` is relays; `multiplayer.exit.*` is going online through a member; `multiplayer.breaker.*` is the emergency disconnect.

## 账号

| 概念 | 简体中文 | 繁體中文 | English | 日本語 |
|---|---|---|---|---|
| 不登录、只填名字的账号 | 离线 | 離線 | offline | オフライン |
| 正版账号 | 微软 | 微軟 | Microsoft | Microsoft |
| authlib-injector 皮肤站账号 | 外置 | 外部 | external | 外部 |

## 写法

- 按钮用动词开头，短：“打开文件夹”“删除实例”。英文用句首大写（Open folder），不用每个词大写。
- 日文完整句子用です/ます；按钮和标签用名词或动词原形。
- 数量会变的文字在英文表里加 `.one` 变体（`L.P`），中文、日文不需要。
- 大小写成“1.5 MB”（保留 1 位小数），时长不到 1 小时写“12:34”，更长写“1 小时 5 分钟”（`Fmt`）。
- 标点：中文用全角，英文用半角；不要把标点写在 C# 代码里拼接，放进翻译文字。
- “实例”第一次出现的地方加提示：“一个实例 = 一套独立的游戏，有自己的模组和存档”。
