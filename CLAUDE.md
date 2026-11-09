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
