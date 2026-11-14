# 隐私政策 / Privacy policy

## 联网 / Network access

This program will not transfer any information to other networked systems unless specifically requested by the user or the person installing or operating it.

除非用户本人要求，本程序不会向其他网络系统发送任何信息。具体来说：

- 没有遥测、统计、崩溃上报，也不生成设备识别码。/ No telemetry, analytics or crash reporting, and no device identifiers.
- 只有在你执行对应操作时才会联网 / Network access happens only when you start the matching action:
  - 微软 / Xbox 账号登录（login.microsoftonline.com、Xbox Live、Minecraft 服务）/ Microsoft and Xbox account login.
  - 你添加的第三方皮肤站（authlib-injector）/ Third-party skin servers you add.
  - 下载游戏、Java、加载器和资源：Mojang 官方服务器、你选择的下载源（如 BMCLAPI）、各加载器官方地址 / Downloading the game, Java, loaders and content from Mojang, the download source you choose (such as BMCLAPI) and each loader's official site.
  - 少数文件没有别的来源：很老的 Forge（1.5.2 及更早）缺的库从 web.archive.org 下载；Mojang 不提供 Java 的平台（ARM Linux）从 api.adoptium.net 下载 Java；打开烘焙（通用加载器）后从 github.com/warriorleavestar/jay-loader 的发布页下载加载器包 / A few files have no other source: missing libraries of very old Forge (1.5.2 and earlier) come from web.archive.org; on platforms Mojang has no Java for (ARM Linux) Java comes from api.adoptium.net; with baking (the universal loader) turned on, the loader package comes from the releases page of github.com/warriorleavestar/jay-loader.
  - 搜索和下载模组：Modrinth、CurseForge / Searching and downloading mods from Modrinth and CurseForge.
  - 检查更新：新装的 Jay 默认每天一次（从旧版升级的保持关闭，自己打开才查）向 Jay 官方发布页（GitHub）查最新版本号，只提示 3 位信任人里有 2 位签过的新版本，从不自动下载或安装；可以在 设置 › 关于 关掉，或在 设置 › 下载与服务 换成你自己的更新地址 / Update check: a new install checks once a day (an upgraded Jay keeps it off until you turn it on); Jay asks the official release page (GitHub) for the latest version and tells you only about a new version that 2 of the 3 trustees signed; it never downloads or installs anything by itself. Turn it off in Settings › About, or use your own update address in Settings › Downloads and services.
  - 联机：只连联机页里列出的中转节点（内置的可以关掉，也可以加自己的）、房间里的其他玩家和邀请码里的 Jay 服务器；直连房间会向你设置的 STUN 服务器询问外部地址；联机组件 EasyTier 用 Jay 的加固版，发布包里自带；自带的那份不在时才从 github.com/Yumao-Feather/jay-easytier 的发布页下载 / Multiplayer: connects only to the relay nodes listed on the Multiplayer page (built-in ones can be turned off, and you can add your own), the other players in your room and the Jay server in an invite code; direct rooms ask the STUN servers you configure for your public address; EasyTier, in Jay's hardened build, comes with the release package and is downloaded from the releases page of github.com/Yumao-Feather/jay-easytier only when that copy is missing.
  - GitHub 下载镜像：从 GitHub 下载联机组件和加载器包时，GitHub 连不上就改走第三方下载镜像 ghfast.top、gh-proxy.com、ghproxy.net（系统时区是中国时先试镜像）。镜像只搬运同一个文件，Jay 只接受大小和 SHA-256 与固定值一致的文件 / GitHub download mirrors: when Jay downloads the multiplayer component or the loader package from GitHub and GitHub can't be reached, it fetches the same file through the third-party download mirrors ghfast.top, gh-proxy.com and ghproxy.net (mirrors are tried first when the system time zone is China). A mirror only passes the file along; Jay accepts it only if its size and SHA-256 match the pinned values.
  - 远程协助：只有被控制的一方每次在对话框里同意后才会共享画面，可以随时停止 / Remote assistance: a screen is shared only after its owner agrees in a dialog each time, and can be stopped at any time.
- 上面这些第三方服务各有自己的隐私政策，Jay 只把完成操作所需的数据发给它们。/ Each third-party service above has its own privacy policy; Jay sends them only what the action needs.

更详细的说明见 [README 的隐私部分](../README.zh-Hans.md#隐私)。/ See also the [Privacy section of the README](../README.md#privacy).

## 对系统的改动与卸载 / System changes and uninstalling

Jay 是绿色版，不需要安装，不写注册表，不装服务。/ Jay is portable: no installer, no registry entries, no services.

- 删除 `Jay.exe` 和设置目录（Windows：`%APPDATA%\Amber`）即可完全卸载。游戏目录是你自己选的，Jay 不会自动删除。
  To uninstall, delete `Jay.exe` and the settings folder (`%APPDATA%\Amber` on Windows). Your game folder is yours and is never deleted automatically.
- 只有在联机里把范围开到“整个局域网”或选择“经 TA 上网”时，才会经管理员确认（Windows 的 UAC，macOS / Linux 的管理员密码）创建虚拟网卡和临时路由；路由带租期，退出联机时删除。
  Only the "whole LAN" multiplayer scope and "going online through a member", after you confirm an administrator prompt (UAC on Windows, your password on macOS and Linux), create a virtual network adapter and temporary leased routes, which are removed when you leave the room.
- 经 TA 上网期间，Jay 还会临时把 DNS 换成经隧道的 1.1.1.1 / 8.8.8.8（Windows 只改虚拟网卡的设置，macOS / Linux 改系统的 DNS），并加上只放行隧道和联机连接本身的出站过滤（Windows 用只在本次会话里存在的 WFP 规则，Linux 用 nftables，macOS 用 pf）；在 Windows 上选“共享给手机”时，会把已经打开的移动热点的共享上游临时换成虚拟网卡。这些改动在会话结束时恢复。
  While you go online through a member, Jay also sets DNS to 1.1.1.1 / 8.8.8.8 through the tunnel for the session (on the virtual adapter only on Windows, system-wide on macOS and Linux) and adds an outbound filter that lets only the tunnel and the multiplayer connection itself through (WFP rules that live only for the session on Windows, nftables on Linux, pf on macOS). On Windows, "Share with phone" temporarily points your already-running Mobile hotspot's shared connection at the virtual adapter. These changes are undone when the session ends.
- 设置目录下的 `diagnostics/` 里有一份只存本机的退出记录（这次和上次意外退出那次）：联机走到哪一步（只记状态名，不记邀请码、地址或名字）、EasyTier 的启动和退出、系统信号和异常堆栈（家目录写成 `~`）。它不会被发送，只有你自己附到问题反馈里时别人才看得到。
  The settings folder holds a local-only exit record in `diagnostics/` (this run, and the last run that ended unexpectedly): multiplayer steps by status name only (no invite codes, addresses or names), EasyTier starts and exits, system signals and exception stacks (with your home folder written as `~`). It is never sent; others see it only if you attach it to a report yourself.
- macOS 的设置目录是 `~/Library/Application Support/Amber`，Linux 通常是 `~/.config/Amber`。/ The settings folder is `~/Library/Application Support/Amber` on macOS and usually `~/.config/Amber` on Linux.

## 问题反馈 / Reporting

发现 Jay 有和这里不一致的联网或改动，请在 [Issues](https://github.com/warriorleavestar/jay-launcher/issues) 反馈。
If Jay goes online or changes your system in a way this page doesn't describe, please open an [issue](https://github.com/warriorleavestar/jay-launcher/issues).
