# Amber launcher: notes for coding agents

Amber is an original cross-platform Minecraft Java launcher for Windows, macOS and Linux, built with .NET 10 and Avalonia 12 (MVVM via CommunityToolkit.Mvvm).
English comes first: write new code comments, docs, commit messages and PRs in English. UI text is localized; `src/Amber.Core/Localization/en.json` is the source and fallback, so add new strings there first and then to every other language file. Some existing comments and docs are in Simplified Chinese; leave them unless you are substantially changing that code. See CONTRIBUTING.md for the translation workflow.

## Layout

For a folder-by-folder map with entry points and "where to start" pointers, read `docs/CODEMAP.md`. `python3 tools/build-code-index.py` builds a local symbol index in `.code-index/` (not committed); `--check` fails when a source folder is missing from the map, so update the map when you add a folder.

- `src/Amber.Core`: everything that isn't UI. That covers game launch (`Launch/`), version and loader install (`Install/`, `Game/`), mods and resources (`Content/`), accounts (`Auth/`), Java detection, the downloader with mirrors (`Net/`) and EasyTier-based multiplayer (`Multiplayer/`).
- `src/Amber.App`: the Avalonia desktop app. `ViewModels/` holds one view model per page and `Views/` the XAML. `Services/LauncherService.cs` wires the Core services together. `Views/SelfTest.cs` is the end-to-end GUI self-check.
- `src/Amber.NetworkServer`: the network server framework (assembly `Jay.NetworkServer`): public network node = EasyTier seed + room directory + login + room gates + local admin API. `jay-server directory` runs on it, and third parties can build their own nodes with it (`samples/MyJayNetwork`, `docs/network-server-framework.md`).
- `src/Amber.Server`: `jay-server`, the server CLI (Jay-only MC server with gate/relays, and the public network node).
- `src/Amber.Cli`: a developer CLI over the same Core. It is used for measurements and for scripted tests (`mods`, `launch`, `prewarm`, `add`, …).
- `src/Amber.Core.Tests`: xUnit tests.
- `tools/`: helper scripts (GUI self-check runner, multiplayer test, code index builder).

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

只验证联机大厅时，设置 `AMBER_SELFTEST_SCOPE=multiplayer-guide`；用 `AMBER_DATA_DIR` 指向独立测试配置，并在该配置中指定测试游戏目录。检查没有页签（页面就是大厅或房间）、加入在最上面占满宽度、开房间和去哪玩两张卡（宽窗并排、其余上下排）、开房间的四个选择（谁能进、怎么连、保护、范围：默认值、灰掉的选项写原因且点不了、不经服务器时按钮变【生成加密直连码】、手动改过传输方式时写“已自定义”和【恢复标准】）、缺组件时按钮先写“先装联机组件”（没有单独的卡片）、去哪玩（全部 / 朋友 / 公开 / 服务器 四个筛选和各自的空状态、朋友邀请的房间排进列表）、子页面的返回行和 `play/public` `play/servers` `play/settings` 路由、万能粘贴框认出每种码（服务器邀请【加入并存下】、好友码【加朋友】、网络地址【添加网络】）、邀请 v3（写出房主的游戏、自动挑最接近的实例、没有时给【装一个再加入】）、一盏灯（贴码后琥珀移到加入）、Ctrl+V 贴码只填好不敲门、贴错框的码转到对的地方、中转在联机设置里、【和上次一样开】（写上次的世界和设置）、联机设置里的长期邀请（30 天、打码、作废）、开服第一步和步骤条、公开房间（没有公共网络时的说明、模拟的房间列表：搜索、版本筛选、只看能直接进的、排序、空状态、带锁的房间才出现密码框、/ 跳搜索）、“连接有问题？”抽屉（大厅入口、宽 560 / 紧凑铺满、Esc 和 Ctrl+Shift+D、【换中转】、折叠的【下载文件】，不点检测）、演练模式（【演练一遍】、小松鸦四步、焦点在安全的那个按钮、讲解和成绩、再来一次、紧凑铺满、Esc、全程没有网络请求）、联机小课堂和安全小课（各 5 张卡和线描插画、← →、末页接“连接有问题？”/ 演练一遍、矮窗口藏插画、Esc、看完整文章去帮助）。此范围不下载联机组件、不建立房间连接，输出真实窗口操作结果及截图。

只验证资源页首次加载和来源切换时，先设置 `AMBER_SELFTEST_SCOPE=content`；同样输出报告和截图，跳过游戏安装与启动。
只验证联机（邀请、敲门、房间、直连）时设置 `AMBER_SELFTEST_SCOPE=multiplayer`。
只验证混合整合包（通用加载器跑别的生态的模组）时设置 `AMBER_SELFTEST_SCOPE=mixed-content`：在报告目录里建临时游戏目录和手写的 NeoForge 1.21.1 实例，再放一个只描述自己的替身加载器包，检查烘焙关时搜索不变、装只有 Forge 版的模组后提示【下载加载器】/【开启烘焙】、点【开启烘焙】、烘焙就绪后按实例搜得到并带“经通用加载器”标记、有原生版的模组不带标记、混合整合包预览，截图；联网搜 Modrinth 并下载一个小模组到测试目录。`AMBER_SELFTEST_SCOPE=mixed-e2e` 是端到端的浏览器半程：`AMBER_SELFTEST_MIXED_GAME` 和 `AMBER_SELFTEST_MIXED_INSTANCE` 指向测试游戏目录里的 NeoForge 1.21.1 实例（设置里开烘焙、选真实加载器包），经发现页装一个 Fabric 模组（连 Fabric 依赖）和一个 NeoForge 模组；之后用 CLI `launch … --bake <包> --smoke` 启动到标题画面。
只验证光影检查时设置 `AMBER_SELFTEST_SCOPE=shader`：在报告目录里建一个临时游戏目录（Fabric 和原版两个手写版本），检查实例光影页和资源页在缺 Iris、已装 Iris、原版三种情况下的提示和【安装 Iris】，不联网。
只看控件库和新配色时设置 `AMBER_SELFTEST_SCOPE=gallery`：控件库每一格和现有每一页在深浅两色下截图，检查琥珀按钮每屏最多一个、图标都解析得到，以及美术风格的几条（按钮只有一种形状、状态灯是小方块、翅膀条纹只在选中的页签 / 导航指示条 / 进度条上、选中的分段是浅色调不是实心强调色、版本标签用像素数字、像素头像和像素封面都排出来），每个图标都保留 24×24 的画布、每一页按钮里的文字和图标都居中；不碰游戏目录、不联网。
只验证新外壳时设置 `AMBER_SELFTEST_SCOPE=shell`：600×420 到 1440×900 四种窗口 × 四种语言截图，检查隐私行、四个入口（游戏库、发现、联机、朋友）、设置和头像、顶栏的网络状态都看得见，侧栏只在宽窗口展开；再走快捷键、返回前进、面包屑（游戏库 › 实例，点前一段回主页）、旧的九页都能走到（启动页、日志页、工具箱走到取代它们的地方，`library/start` `library/logs` `settings/tools` 这些旧路由也是）、`settings#字段` 定位和带上下文返回；头像打开“我”页（游戏账号、Jay 账户、好友码、设备、外观五块，深浅色和语言跟设置同步，旧路由 `accounts` 落在这里）、朋友页（空状态、一个人一行、设备和安全码、送达行、三种宽度、“给孩子用”时只留说明）、顶栏网络面板（基础中转一行、加一个没审核的网络后多一行和开关、缺东西时去公开房间页问、开着房间时写“基础中转”，不建立连接）；快捷键范围（§8.5）：任何地方 Ctrl+N 进新建实例、输入框里和输入法组字时不触发、没在玩时 Ctrl+Shift+Q 不做事、F6 / Shift+F6 在侧栏 / 顶栏 / 请求条 / 内容区之间轮换、Ctrl+Alt+Shift+T 回默认外观且能撤销。不碰游戏目录、不联网。
只验证游戏库主页时设置 `AMBER_SELFTEST_SCOPE=library`：在报告目录里建一个临时游戏目录（五个手写版本），检查继续玩、需要你处理、固定排前、搜索、筛选、排序、封面和列表视图、打开、固定、空状态，三种宽度两种语言截图，不联网。
只验证模组页签时设置 `AMBER_SELFTEST_SCOPE=mods`：在报告目录里建临时游戏目录和手写的模组 jar，检查搜索、筛选、检查器（宽档一栏、其余盖在右边）、勾选和批量栏、批量禁用 / 启用 / 删除和撤销、表格快捷键、检查出更新后才有【全部更新】、原版空状态，三种宽度两种语言截图；不联网。
只验证实例详情页时设置 `AMBER_SELFTEST_SCOPE=instance`：在报告目录里建临时游戏目录，模拟开始按钮的每个状态（没账号、就绪、准备中、进入中、正在玩、退出中、崩溃后、没装完）和开始按钮下面的进度行（步骤、百分比、速度、剩余时间），正在玩时英雄区按钮里的文字和图标居中，检查“上一局”面板的就地修复（调内存、禁用模组、关光影）、概览页签、Alt+数字和 Ctrl+Tab，三种宽度两种语言截图；不启动游戏、不联网。
只验证新建实例面板时设置 `AMBER_SELFTEST_SCOPE=new-instance`：在报告目录里建临时游戏目录，检查来源栏随宽度变分段、版本类型芯片、加载器按版本置灰并写原因、名字行内检查挡住【安装】、整合包文件预览、进发现页、复制实例（带不带存档）、其他启动器的游戏文件夹、Esc 关闭，三种宽度两种语言截图；只联网取版本列表和 Fabric 版本，不安装。
只验证“最近删除”时设置 `AMBER_SELFTEST_SCOPE=recycle`：在报告目录里建临时游戏目录，把模组、世界、实例移进回收目录，检查设置里的列表（类型、来自哪个实例、剩几天）、【恢复】回原位（实例不改名）、保留天数、【永久删除】和【清空…】走锁定确认；不联网。
只验证资源包和光影页签时设置 `AMBER_SELFTEST_SCOPE=packs`：在报告目录里建临时游戏目录和 Fabric 实例，检查已启用 / 未启用两栏（按 options.txt，越靠上越优先）、勾选和 ↑↓ / Alt+↑↓ 写回 options.txt、版本旧 / 版本新标签、删除进最近删除、没装 Iris 时的横幅、选光影写 iris.properties 和【不用光影】，宽窄两种宽度两种语言截图；不联网。
只验证世界备份时设置 `AMBER_SELFTEST_SCOPE=backups`：在报告目录里建临时游戏目录和一个世界，检查 B 备份进活动中心、Enter 打开世界面板、备份按新到旧列出、【恢复…】走锁定确认（写明时间、会先备份现在的世界）、恢复后内容回到旧备份且自动备份进列表，宽窄两种截图；不联网。
只验证实例日志页签时设置 `AMBER_SELFTEST_SCOPE=logs`：在报告目录里建临时游戏目录和两局存下的日志，检查运行记录（新的在前、宽窗左栏、窄了变下拉）、崩溃诊断行、堆栈收成一行、重复警告 ×N、筛选和搜索、F8 / Shift+F8 跳错误、导出错误报告先给锁定的隐私预览、报告里没有电脑用户名，三种宽度两种语言截图；不联网。
只验证【帮我修…】时设置 `AMBER_SELFTEST_SCOPE=fix`：在报告目录里建临时游戏目录和七个占位模组文件，其中一个扮演致崩模组，检查一半一半地关模组、卡片写第几轮、换实例回来（等于重启）接着来、三轮内找到、【关掉 X】只关这一个、中途【全部恢复原样】，宽窄两种截图；不联网、不启动游戏。
只验证命令面板时设置 `AMBER_SELFTEST_SCOPE=palette`：在报告目录里建临时游戏目录和两个实例，检查 Ctrl+K 打开且光标在输入框、拼音首字母和旧页面名搜得到、`#` 只列设置并 Enter 定位到字段、最近用过排前、“开始游戏…”第二步选实例和 Esc 退一步、Tab 进到选实例、贴邀请码排第一且只填好不敲门、找不到时的提示、Esc 还焦点，宽窄两种宽度两种语言截图；不联网、不启动游戏。
只验证设置分组时设置 `AMBER_SELFTEST_SCOPE=settings-layout`：检查 9 个分组（宽窗左栏 + 说明栏、中等宽度顶部下拉、紧凑先列分组再点进去）、一次只显示一组、每个设置项都能用 `settings#字段` 定位并自动打开分组、命令面板能找到每个设置项、Ctrl+F 搜索写成“分组 › 设置”且 Enter 跳到字段、“开始游戏后”存进设置、原来工具箱里的三样（游戏默认里的内存和下载 Java、存储里的清理、关于里的检查更新），三种宽度两种语言截图；不联网。
只验证关于和隐私时设置 `AMBER_SELFTEST_SCOPE=privacy`：检查“关于和隐私”是锁定的独立页（路由 `about`）、联网清单 5 类和各自现在的状态、“改 ›”跳到对应设置、设置 › 隐私与安全显示同一张清单、“给孩子用”总开关打开六项并在联机里藏起远程控制 / 公共网络 / 经我经 TA / 免确认邀请、写上次改动时间、打开每一页没有清单外的请求、【核对 Jay 下载】（2/3 信任人签过的测试包通过，只有 1 人签、文件被改、旁边没有清单都不过），宽窄两种宽度两种语言截图；不联网（发现页的搜索请求除外，属于第 3 类）。
只验证情境提示时设置 `AMBER_SELFTEST_SCOPE=tips`：检查请求后先等 800ms 没操作才出现、矮窗口用内容区顶部一行提示条、高窗口在对应控件旁出气泡且不抢焦点、【知道了】后记住不再出、“这类提示都别出了”整类静音、命令面板开着时不出、一次一个、10 秒内关掉两个本次不再出、帮助中心【重新看所有提示】清空，截图；不联网。别的范围不弹提示。
只验证帮助时设置 `AMBER_SELFTEST_SCOPE=help`：检查 F1 在每个路由都打开本页帮助抽屉、抽屉里 Ctrl+F 搜索（拼音首字母）、文章里的 `jay:` 链接跳到设置、Esc 关闭、“打开帮助中心”和 Shift+F1、帮助中心三种宽度（左目录 / 下拉 / 先目录再文章）、命令面板 `?` 找文章、术语旁的“这是什么？”小问号（悬停一句白话、读屏名字、点开卡片、“了解更多”进文章），三种宽度两种语言截图；不联网。帮助文章在 `docs/help/{zh-Hans,zh-Hant,en,ja}/`，打包进 Amber.Core。
只验证日志停靠和密度时设置 `AMBER_SELFTEST_SCOPE=dock-density`：检查 Ctrl+` 把游戏日志停靠到内容区底部（高 240、可拖、换页面还在）、弹出成独立窗口再停靠回来、矮窗口变成抽屉且 Esc 收起；密度改紧凑时控件变矮、存进设置、锁定的安全界面不变，改回标准去掉覆盖；两种密度截图；不联网。
只验证联机房间控制台时设置 `AMBER_SELFTEST_SCOPE=room`：把联机页放进模拟的房主状态（两个人、一条聊天、几次敲门），检查顶栏（房间名、人数、邀请朋友、⋯、结束房间，不随内容滚动）、人 | 聊天 | 详情三栏（中等两栏、紧凑分段）、选人看安全码和私聊、锁定的敲门面板（默认焦点在【拒绝】、Alt+Y / Alt+N / Esc、“以后直接让 TA 进来”）、敲门等多久（2 分钟 / 10 分钟 / 一直等）、房间设置抽屉、邀请条款、Ctrl+Shift+I、先开房间再开游戏（邀请卡写在等游戏、给端口框和【查找】），三种宽度两种语言截图；不下载组件、不建立连接。
只验证敲门和收文件的几处同步时设置 `AMBER_SELFTEST_SCOPE=mp-requests`：模拟房主状态，检查敲门同时进请求条（安全类、默认拒绝、倒计时）、在请求条 / 置顶小窗 / 房间里任一处处理其余同步撤掉、置顶小窗不抢焦点（宽 360、最矮 200、关窗算拒绝）、收文件的锁定卡片和程序文件警告、聊天里只留一行、设置里的小窗开关、房间设置里的模组同步开关、加入方的“对一下模组”卡片（比对完之前不说一样、缺 / 版本不同 / 多、看看差在哪、不管了直接进并记进上一局、一样打 ✓、房主关掉同步、一键同步到新实例：进度、向房主要私有文件、原实例不动、同步后重新对过），截图；不下载组件、不建立连接。
只验证拖放导入时设置 `AMBER_SELFTEST_SCOPE=drop`：在报告目录里建临时游戏目录和 Fabric 实例，对真实窗口发拖放事件，检查按类型给落点（模组、资源包、存档、整合包）、悬停高亮、放在落点外不做事、同名不覆盖、原版实例和认不出的文件写原因、整合包打开新建实例预览，宽窄两种截图；不联网。
只验证首次启动向导时设置 `AMBER_SELFTEST_SCOPE=firstrun`（配一个全新的 `AMBER_DATA_DIR`）：检查向导盖住外壳、右上角换语言、名字为空或不合法挡住【继续】、Enter 加离线账号、第 2 步默认原版且只有一个琥珀按钮、发现的游戏【加进游戏库】、小知识手动翻页、锁定的隐私块和联网清单、Esc 不退出、【跳过】、“选别的版本”，四种窗口两种语言截图；只联网取版本列表，不安装。别的范围不会弹出向导。
只验证发现页时设置 `AMBER_SELFTEST_SCOPE=discover`：在报告目录里建临时游戏目录和一个 Fabric 实例，检查类型芯片（窄窗变下拉）、来源分段、“装到”标记（写实例的名字、默认是刚看的实例、整合包不问）、结果行【＋待装】和 Space、待装条【安装 N 个】一次装完（连前置）、详情三种宽度（右栏 / 抽屉 / 全区）、Esc、上下文模式【完成，返回】、整合包唯一的琥珀按钮，三种宽度两种语言截图；联网搜 Modrinth 并下载待装的模组。
只验证活动中心时设置 `AMBER_SELFTEST_SCOPE=activity`：在报告目录里建临时游戏目录和一个手写实例，模拟任务、请求、一局游戏和启动失败，检查活动抽屉（Ctrl+J / Esc，三档宽度，任务用统一的进度行）、请求条（倒计时、去重、安全请求在最前、处理后同步收起）、出错通知不自动消失、正在玩胶囊、实例页上启动失败卡的修复按钮和关窗保护。不联网。
生成帮助文章截图时设置 `AMBER_SELFTEST_SCOPE=help-shots`：在报告目录里建临时游戏目录（三个手写实例），1100×700 窗口按四种语言截游戏库、一起玩、“连接有问题？”抽屉、演练、设置 › 外观五张图，裁到内容区，写到 `AMBER_HELP_SHOTS_DIR`（默认报告目录下 `shots/`）；把结果复制到 `docs/help/shots/{语言}/` 即更新文章里的 `![说明](shot:名字)`。不联网。
数关键旅程时设置 `AMBER_SELFTEST_SCOPE=journeys`（官方设计 §7.9）：在报告目录里建临时游戏目录，用真实窗口走 J1–J8，数点击（点一下、回车、拖放各 1，下拉 2，打字和粘贴不算）和换页（侧栏导航、面板、抽屉、带返回的上下文子视图不算），和 §7 的目标比；600×420、1024×700、1440×900 × 四种语言，要联网的 J3、J3′、J4′ 只跑简体中文。要下载或启动游戏的最后一步只检查看得见、在窗口里、能按，不真的按。结果另写 `journeys.txt`，超出目标的截图。`AMBER_JOURNEYS_QUICK=1` 只跑最小窗口的简体中文和英文。
只看页面 × 窗口大小 × 语言的裁切矩阵时设置 `AMBER_SELFTEST_SCOPE=layout`：和 full 里的布局检查一样（含伪本地化），不装游戏、不启动，报告在 `layout/layout.txt`。
只验证安全外观锁定时设置 `AMBER_SELFTEST_SCOPE=safety-lock`：装上五个极端主题包，逐像素比对安全面板和隐私行在深浅两色下跟默认外观一致，并检查带保留词的主题包被拒、确认框默认焦点在“保留”。
只验证界面主题包（导出、导入、立即生效、坏包被拒、恢复默认、删除、format 1 自动升级：主题卡标记、说明条和【复制给作者看】、侧栏色给顶栏、开始按钮跟强调色；format 2：minJay 提示、新颜色名、种子 play、动效时长；内置的经典琥珀和高对比：列表里有、套上生效、删不掉）时设置 `AMBER_SELFTEST_SCOPE=theme`；主题包格式见 `docs/themes.md`。
只验证 Jay 账户（助记词账户）时设置 `AMBER_SELFTEST_SCOPE=jay-account`：加离线账号后提示创建、显示 12 个编号的词、核对 3 个词（答错不创建）、创建后绑定到该 MC 账号、复制 jay1 地址、从本机移除并解绑、写错一个词时提示是第几个、同一组词恢复出同一个地址；再进模拟的房主状态，检查敲门证书验得过本机联机密钥、敲门卡片写出对方的 Jay 账户和“其他设备也能进”的说明、信任后信任列表记的是账户、房间成员详情有完整地址、取消信任连同这个账户的设备一起移除、批准后请求条里的敲门也收起；再看联系人按“人 + 设备”分组（同一 Jay 账户的设备算一个人、名下写账户、能只移除一台设备）、【备份联系人】存出加密文件、【恢复联系人】先写明备份日期和会回来的人、把人带回来并记下要打招呼、改过的备份不认；认识的人新增设备时请求条里留一条安全提示（设备名、安全码、【不是 TA？移除这台设备】），设备列表标“待你确认”，移除后它再来要重新对表情；不联网。
只验证联机安全（设置页“联机安全”卡片、熔断倒计时盖住窗口、空格取消、到点重启、10 分钟内不再自动重启、跳过等待）时设置 `AMBER_SELFTEST_SCOPE=security`；重启这一步换成计数，不会真的重启。
开服端到端要开两个 Jay：一个 `AMBER_SELFTEST_SCOPE=host-server`（按“开服”卡片的按钮走完 填地址 → 准备服务端 → 启动 → 邀请 → 等进服 → 撤销 → 停服 → 再开后关窗口），一个 `AMBER_SELFTEST_SCOPE=jay-client`（离线账号 Steve，用 `AMBER_SELFTEST_VERSION` 指定的实例进服）。两边各用自己的 `AMBER_DATA_DIR`，`AMBER_SELFTEST_INVITE` 指向同一个邀请码文件。`AMBER_SELFTEST_SCOPE=host-exit` 只测“服务器开着时关 Jay 会先存盘”。

Other developer switches are environment variables:

| Variable | Effect |
|---|---|
| `AMBER_SNAPSHOT_DIR` | Render every page to PNG |
| `AMBER_STARTUP_LOG` | Write startup timing |
| `AMBER_ET_VERBOSE` | Verbose EasyTier output |
| `AMBER_ET_QUIC` | `0`: turn off EasyTier's QUIC proxy for room traffic without a TUN device (on by default; much faster single connections at high latency) |
| `AMBER_SPLIT` | Host: split new wrapped invite rooms over several paths, `game,chat[,paths]` with `off`, `auto` or `strict` (for example `auto,auto,2`; without a path count the room uses 2–3 paths, one per independent relay group plus the direct one); until the setting is in the app |
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
