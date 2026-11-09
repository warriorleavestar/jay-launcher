# 代码签名政策 / Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io/), certificate by [SignPath Foundation](https://signpath.org/).

Jay 的 Windows 版（`Jay.exe` 和 `jay-server.exe`）通过 SignPath Foundation 为开源项目提供的免费证书签名。
The Windows builds of Jay (`Jay.exe` and `jay-server.exe`) are signed with a free certificate that SignPath Foundation provides to open-source projects.

> 状态 / Status：申请中。签名生效后，从那个版本起的 Windows 发布包都会带签名，版本说明里会写明。
> Application in progress. Once signing is active, every Windows release from that version on is signed, and its release notes say so.

## 签名什么 / What is signed

- 只签本仓库源码在 GitHub Actions（GitHub 托管的运行器）上编译出来的程序：`Jay.exe`（单文件版和 AOT 版）和 `jay-server.exe`。
  Only binaries built from this repository's source by GitHub Actions on GitHub-hosted runners: `Jay.exe` (single-file and AOT builds) and `jay-server.exe`.
- 只签正式发布：推送 `v*` 标签触发构建，每次签名都由下面的批准人在 SignPath 上手动批准。日常构建和 PR 构建不签名。
  Only releases: pushing a `v*` tag starts the build, and every signing request is approved by hand by an approver listed below. Everyday and pull-request builds are not signed.
- 发布包里自带的第三方程序（EasyTier 联机组件，LGPL-3.0，来自 [EasyTier 官方发布页](https://github.com/EasyTier/EasyTier/releases)）不用本项目的证书签名，保持上游原样，SHA-256 固定在 `scripts/bundle-easytier.sh` 里。
  Bundled third-party programs (the EasyTier multiplayer component, LGPL-3.0, from the [official EasyTier releases](https://github.com/EasyTier/EasyTier/releases)) are not signed with this project's certificate. They ship exactly as upstream publishes them, pinned by SHA-256 in `scripts/bundle-easytier.sh`.
- 签名配置存档在 [`.signpath/artifact-configuration.xml`](../.signpath/artifact-configuration.xml)，构建流程在 [`.github/workflows/build.yml`](../.github/workflows/build.yml)。
  The signing configuration is archived in [`.signpath/artifact-configuration.xml`](../.signpath/artifact-configuration.xml); the build is defined in [`.github/workflows/build.yml`](../.github/workflows/build.yml).

## 团队与角色 / Team roles

所有成员访问 GitHub 和 SignPath 都开启了多重身份验证（MFA）。
All team members use multi-factor authentication for both GitHub and SignPath.

| 角色 / Role | 成员 / Members |
|---|---|
| 作者（Committers and reviewers） | [warriorleavestar](https://github.com/warriorleavestar) |
| 审查人（Reviewers） | [warriorleavestar](https://github.com/warriorleavestar) |
| 批准人（Approvers） | [warriorleavestar](https://github.com/warriorleavestar)（仓库所有者 / repository owner） |

- 作者可以直接修改源码。/ Authors may change the source without extra review.
- 非作者提交的改动（例如外部 Pull Request）必须经过审查人审查才能合并。/ Every change from someone who is not an author (for example an outside pull request) must be reviewed by a reviewer before it is merged.
- 每个签名请求都必须由批准人手动批准。/ Every signing request must be approved by an approver.

## 隐私政策 / Privacy policy

This program will not transfer any information to other networked systems unless specifically requested by the user or the person installing or operating it.

除非用户本人要求，本程序不会向其他网络系统发送任何信息。具体来说：

- 没有遥测、统计、崩溃上报，也不生成设备识别码。/ No telemetry, analytics or crash reporting, and no device identifiers.
- 只有在你执行对应操作时才会联网 / Network access happens only when you start the matching action:
  - 微软 / Xbox 账号登录（login.microsoftonline.com、Xbox Live、Minecraft 服务）/ Microsoft and Xbox account login.
  - 你添加的第三方皮肤站（authlib-injector）/ Third-party skin servers you add.
  - 下载游戏、Java、加载器和资源：Mojang 官方服务器、你选择的下载源（如 BMCLAPI）、各加载器官方地址 / Downloading the game, Java, loaders and content from Mojang, the download source you choose (such as BMCLAPI) and each loader's official site.
  - 搜索和下载模组：Modrinth、CurseForge / Searching and downloading mods from Modrinth and CurseForge.
  - 检查更新：默认关闭，只有你在设置里填了更新地址才会检查，且不会自动下载 / Update check: off by default; only runs if you enter an update URL, and never downloads by itself.
  - 联机：只连你添加的节点、房间里的其他玩家和邀请码里的 Jay 服务器；直连房间会向你设置的 STUN 服务器询问外部地址；联机组件 EasyTier 从其 GitHub 发布页下载 / Multiplayer: connects only to nodes you add, the other players in your room and the Jay server in an invite code; direct rooms ask the STUN servers you configure for your public address; EasyTier is downloaded from its GitHub releases page.
  - 远程协助：只有房主在对话框里同意后才会共享画面，可以随时停止 / Remote assistance: the screen is shared only after the host agrees in a dialog, and can be stopped at any time.
- 上面这些第三方服务各有自己的隐私政策，Jay 只把完成操作所需的数据发给它们。/ Each third-party service above has its own privacy policy; Jay sends them only what the action needs.

更详细的说明见 [README 的隐私部分](../README.zh-Hans.md#隐私)。/ See also the [Privacy section of the README](../README.md#privacy).

## 对系统的改动与卸载 / System changes and uninstalling

Jay 是绿色版，不需要安装，不写注册表，不装服务。/ Jay is portable: no installer, no registry entries, no services.

- 删除 `Jay.exe` 和设置目录（Windows：`%APPDATA%\Amber`）即可完全卸载。游戏目录是你自己选的，Jay 不会自动删除。
  To uninstall, delete `Jay.exe` and the settings folder (`%APPDATA%\Amber` on Windows). Your game folder is yours and is never deleted automatically.
- 只有在联机页选择“完整局域网”或“出口节点”模式时，才会经 Windows 的管理员确认（UAC）创建虚拟网卡（Wintun）和临时路由；路由带租期，退出联机时删除。
  Only the "full LAN" and "exit node" multiplayer modes, after you confirm a Windows UAC prompt, create a virtual network adapter (Wintun) and temporary leased routes, which are removed when you leave the room.

## 问题反馈 / Reporting

如果你认为某个签名程序有问题，请在 [Issues](https://github.com/warriorleavestar/jay-launcher/issues) 反馈。
If you think a signed binary is a problem, please open an [issue](https://github.com/warriorleavestar/jay-launcher/issues).
