# Amber launcher: notes for coding agents

Amber is an original cross-platform Minecraft Java launcher for Windows, macOS and Linux, built with .NET 10 and Avalonia 12 (MVVM via CommunityToolkit.Mvvm).
English comes first: write new code comments, docs, commit messages and PRs in English. UI text is localized; `src/Amber.Core/Localization/en.json` is the source and fallback, so add new strings there first and then to every other language file. Some existing comments and docs are in Simplified Chinese; leave them unless you are substantially changing that code. See CONTRIBUTING.md for the translation workflow.

## Layout

- `src/Amber.Core`: everything that isn't UI. That covers game launch (`Launch/`), version and loader install (`Install/`, `Game/`), mods and resources (`Content/`), accounts (`Auth/`), Java detection, the downloader with mirrors (`Net/`) and EasyTier-based multiplayer (`Multiplayer/`).
- `src/Amber.App`: the Avalonia desktop app. `ViewModels/` holds one view model per page and `Views/` the XAML. `Services/LauncherService.cs` wires the Core services together. `Views/SelfTest.cs` is the end-to-end GUI self-check.
- `src/Amber.Cli`: a developer CLI over the same Core. It is used for measurements and for scripted tests (`mods`, `launch`, `prewarm`, `add`, …).
- `src/Amber.Core.Tests`: xUnit tests.
- `tools/`: helper scripts (GUI self-check runner, multiplayer test).

## Build and test

```bash
cd src
dotnet build Amber.slnx -c Release
dotnet test Amber.Core.Tests -c Release
```

Release build, a single self-contained exe:

```bash
dotnet publish Amber.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishReadyToRun=true -p:DebugType=none -o ../publish
```

Use `osx-arm64`, `osx-x64`, `linux-x64` or `linux-arm64` for the other release targets. The CI workflow publishes every supported RID on its native runner.

GUI self-check: it drives the real window through install, launch, mods, multiplayer and import, then writes `report.txt` to the report directory you pass.

```bash
powershell -NoProfile -ExecutionPolicy Bypass -File tools/run-selftest.ps1 -Report <dir>
```

只验证联机页引导时，设置 `AMBER_SELFTEST_SCOPE=multiplayer-guide`；用 `AMBER_DATA_DIR` 指向独立测试配置，并在该配置中指定测试游戏目录。此范围不下载联机组件、不建立房间连接，输出真实窗口操作结果及四种语言的小中大布局截图。

只验证资源页首次加载和来源切换时，先设置 `AMBER_SELFTEST_SCOPE=content`；同样输出报告和截图，跳过游戏安装与启动。
只验证联机（邀请、敲门、房间、直连）时设置 `AMBER_SELFTEST_SCOPE=multiplayer`。
只看控件库和新配色时设置 `AMBER_SELFTEST_SCOPE=gallery`：控件库每一格和现有每一页在深浅两色下截图，检查琥珀按钮每屏最多一个、图标都解析得到；不碰游戏目录、不联网。
只验证新外壳时设置 `AMBER_SELFTEST_SCOPE=shell`：600×420 到 1440×900 四种窗口 × 四种语言截图，检查隐私行、三个入口、设置和账号都看得见，侧栏只在宽窗口展开；再走快捷键、返回前进、旧的九页都能走到、`settings#字段` 定位和带上下文返回。不碰游戏目录、不联网。
只验证游戏库主页时设置 `AMBER_SELFTEST_SCOPE=library`：在报告目录里建一个临时游戏目录（五个手写版本），检查继续玩、需要你处理、固定排前、搜索、筛选、排序、封面和列表视图、打开、固定、空状态，三种宽度两种语言截图，不联网。
只验证模组页签时设置 `AMBER_SELFTEST_SCOPE=mods`：在报告目录里建临时游戏目录和手写的模组 jar，检查搜索、筛选、检查器（宽档一栏、其余盖在右边）、勾选和批量栏、批量禁用 / 启用 / 删除和撤销、表格快捷键、检查出更新后才有【全部更新】、原版空状态，三种宽度两种语言截图；不联网。
只验证实例详情页时设置 `AMBER_SELFTEST_SCOPE=instance`：在报告目录里建临时游戏目录，模拟开始按钮的每个状态（没账号、就绪、准备中、进入中、正在玩、退出中、崩溃后、没装完），检查“上一局”面板的就地修复（调内存、禁用模组、关光影）、概览页签、Alt+数字和 Ctrl+Tab，三种宽度两种语言截图；不启动游戏、不联网。
只验证新建实例面板时设置 `AMBER_SELFTEST_SCOPE=new-instance`：在报告目录里建临时游戏目录，检查来源栏随宽度变分段、版本类型芯片、加载器按版本置灰并写原因、名字行内检查挡住【安装】、整合包文件预览、进发现页、复制实例（带不带存档）、其他启动器的游戏文件夹、Esc 关闭，三种宽度两种语言截图；只联网取版本列表和 Fabric 版本，不安装。
只验证“最近删除”时设置 `AMBER_SELFTEST_SCOPE=recycle`：在报告目录里建临时游戏目录，把模组、世界、实例移进回收目录，检查设置里的列表（类型、来自哪个实例、剩几天）、【恢复】回原位（实例不改名）、保留天数、【永久删除】和【清空…】走锁定确认；不联网。
只验证资源包和光影页签时设置 `AMBER_SELFTEST_SCOPE=packs`：在报告目录里建临时游戏目录和 Fabric 实例，检查已启用 / 未启用两栏（按 options.txt，越靠上越优先）、勾选和 ↑↓ / Alt+↑↓ 写回 options.txt、版本旧 / 版本新标签、删除进最近删除、没装 Iris 时的横幅、选光影写 iris.properties 和【不用光影】，宽窄两种宽度两种语言截图；不联网。
只验证世界备份时设置 `AMBER_SELFTEST_SCOPE=backups`：在报告目录里建临时游戏目录和一个世界，检查 B 备份进活动中心、Enter 打开世界面板、备份按新到旧列出、【恢复…】走锁定确认（写明时间、会先备份现在的世界）、恢复后内容回到旧备份且自动备份进列表，宽窄两种截图；不联网。
只验证实例日志页签时设置 `AMBER_SELFTEST_SCOPE=logs`：在报告目录里建临时游戏目录和两局存下的日志，检查运行记录（新的在前、宽窗左栏、窄了变下拉）、崩溃诊断行、堆栈收成一行、重复警告 ×N、筛选和搜索、F8 / Shift+F8 跳错误、导出错误报告先给锁定的隐私预览、报告里没有电脑用户名，三种宽度两种语言截图；不联网。
只验证拖放导入时设置 `AMBER_SELFTEST_SCOPE=drop`：在报告目录里建临时游戏目录和 Fabric 实例，对真实窗口发拖放事件，检查按类型给落点（模组、资源包、存档、整合包）、悬停高亮、放在落点外不做事、同名不覆盖、原版实例和认不出的文件写原因、整合包打开新建实例预览，宽窄两种截图；不联网。
只验证首次启动向导时设置 `AMBER_SELFTEST_SCOPE=firstrun`（配一个全新的 `AMBER_DATA_DIR`）：检查向导盖住外壳、右上角换语言、名字为空或不合法挡住【继续】、Enter 加离线账号、第 2 步默认原版且只有一个琥珀按钮、发现的游戏【加进游戏库】、小知识手动翻页、锁定的隐私块和联网清单、Esc 不退出、【跳过】、“选别的版本”，四种窗口两种语言截图；只联网取版本列表，不安装。别的范围不会弹出向导。
只验证发现页时设置 `AMBER_SELFTEST_SCOPE=discover`：在报告目录里建临时游戏目录和一个 Fabric 实例，检查类型芯片（窄窗变下拉）、来源分段、结果行【＋待装】和 Space、待装条【安装 N 个】一次装完（连前置）、详情三种宽度（右栏 / 抽屉 / 全区）、Esc、上下文模式【完成，返回】、整合包唯一的琥珀按钮，三种宽度两种语言截图；联网搜 Modrinth 并下载待装的模组。
只验证活动中心时设置 `AMBER_SELFTEST_SCOPE=activity`：模拟任务、请求、一局游戏和启动失败，检查活动抽屉（Ctrl+J / Esc，三档宽度）、请求条（倒计时、去重、安全请求在最前、处理后同步收起）、出错通知不自动消失、正在玩胶囊、启动失败卡的修复按钮和关窗保护。不碰游戏目录、不联网。
只验证安全外观锁定时设置 `AMBER_SELFTEST_SCOPE=safety-lock`：装上五个极端主题包，逐像素比对安全面板和隐私行在深浅两色下跟默认外观一致，并检查带保留词的主题包被拒、确认框默认焦点在“保留”。
只验证界面主题包（导出、导入、立即生效、坏包被拒、恢复默认、删除）时设置 `AMBER_SELFTEST_SCOPE=theme`；主题包格式见 `docs/themes.md`。
只验证联机安全（设置页“联机安全”卡片、熔断倒计时盖住窗口、空格取消、到点重启、10 分钟内不再自动重启、跳过等待）时设置 `AMBER_SELFTEST_SCOPE=security`；重启这一步换成计数，不会真的重启。
开服端到端要开两个 Jay：一个 `AMBER_SELFTEST_SCOPE=host-server`（按“开服”卡片的按钮走完 填地址 → 准备服务端 → 启动 → 邀请 → 等进服 → 撤销 → 停服 → 再开后关窗口），一个 `AMBER_SELFTEST_SCOPE=jay-client`（离线账号 Steve，用 `AMBER_SELFTEST_VERSION` 指定的实例进服）。两边各用自己的 `AMBER_DATA_DIR`，`AMBER_SELFTEST_INVITE` 指向同一个邀请码文件。`AMBER_SELFTEST_SCOPE=host-exit` 只测“服务器开着时关 Jay 会先存盘”。

Other developer switches are environment variables:

| Variable | Effect |
|---|---|
| `AMBER_SNAPSHOT_DIR` | Render every page to PNG |
| `AMBER_STARTUP_LOG` | Write startup timing |
| `AMBER_ET_VERBOSE` | Verbose EasyTier output |
| `AMBER_THEME_DEV` | Use an unpacked theme folder and reload it on every save (same as `--theme-dev <dir>`) |

## Rules

- Write original code only. Don't copy or port code from other launchers (HMCL, PCL and the like) or from decompiled binaries. Public protocol and format documentation is fine.
- No telemetry, analytics or remote kill switches. Network calls are limited to:
  - user-initiated downloads
  - Microsoft login
  - the Modrinth and CurseForge APIs
  - the update check
  - multiplayer
- Tests and experiments must use a separate test game directory. Never write to a user's real `%APPDATA%\.minecraft`. Old Forge (≤1.5.2) writes to the APPDATA home, which is why `GameLauncher` redirects it.
- Use established cryptographic primitives and libraries. Don't invent new ones.
- Keep personal data out of committed files: no emails, usernames, tokens, or machine-specific absolute paths. Machine-specific notes go in `CLAUDE.local.md`, which is gitignored.
- Keep `Amber.Core` free of UI dependencies. Platform-specific code goes behind `OperatingSystem.IsWindows()` or into a Windows-only type.
- New behaviour gets unit tests, and a self-check step when it is UI-visible.
