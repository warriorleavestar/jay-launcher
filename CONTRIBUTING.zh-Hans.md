# 参与 Jay 的开发

[English](CONTRIBUTING.md) | 简体中文

感谢参与。本页说明代码、界面翻译和文档翻译的做法。英文版 [CONTRIBUTING.md](CONTRIBUTING.md) 为准，两者不一致时以英文版为准。

## 语言约定

Jay 面向全世界的玩家，所以**英文优先**：

- issue、PR、提交说明、新写的代码注释和新文档用英文。
- 界面文字以 `en.json` 为原文和回退语言，新加的界面文字先写进 `en.json`。
- 也欢迎用其他语言提 issue 和 PR，需要时维护者或其他贡献者会帮忙翻译。
- 现有的部分代码注释和文档还是简体中文，不需要专门去改写；大改某一段时顺手换成英文即可。欢迎把现有文档翻译成英文（见[翻译文档](#翻译文档)）。

## 基本规则

- 只写原创代码。不要复制或移植其他启动器、反编译程序的代码；公开的协议和格式文档可以参考。
- 不加遥测、统计或远程开关。
- 不要拿真实的 `%APPDATA%\.minecraft` 测试，用单独的测试游戏目录。
- 用成熟的加密算法和库，不要自己发明。
- 新功能要有单元测试；界面上看得到的，还要加 GUI 自检步骤。

## 构建和测试

```bash
cd src
dotnet build Amber.slnx -c Release
dotnet test Amber.Core.Tests -c Release
```

GUI 自检（操作真实窗口，写出 `report.txt`）：

```bash
powershell -NoProfile -ExecutionPolicy Bypass -File tools/run-selftest.ps1 -Report <dir>
```

只做布局检查（每个页面 × 每种语言 × 6 种窗口大小，输出截图和被切掉的控件列表 `layout.txt`）：

```bash
AMBER_LAYOUTCHECK=<dir> src/Amber.App/bin/Release/net10.0/Jay.exe
```

## 界面翻译

### 界面用哪种语言

默认跟随系统语言：系统语言（或它的上级，比如 `zh-HK` → 繁体中文）有翻译就用，没有就用英文。用户在设置里选了语言时以设置为准。某条文字没翻译时显示英文，英文也没有时显示键名。

### 文件

界面文字在 `src/Amber.Core/Localization/`，用词按[用词表](docs/i18n-glossary.md)（比如已安装的游戏文件夹叫“实例”，不叫“版本”）。

| 文件 | 语言 |
|---|---|
| `en.json` | 英文（美国），原文和回退语言，所有键都在这里 |
| `zh-Hans.json` | 简体中文 |
| `zh-Hant.json` | 繁体中文（台湾用词） |
| `ja.json` | 日文 |

每个文件是一个平铺的 JSON 对象 `"键": "文字"`，键按字母排序。

- XAML 里写 `{l:T some.key}`（需要 `xmlns:l="using:Amber.App.Localization"`）；C# 里写 `L.T("some.key")`，带 `{0}`、`{1}` 占位符的用 `L.F("some.key", a, b)`，随数量变化的用 `L.P("some.key", count, …)`。
- 单复数：英文可以在 `some.key` 旁边加 `some.key.one`（数量为 1 时用）；没有单复数的语言只需要 `some.key`。
- 不要在 `Amber.App` 里直接写界面文字；`NoHardCodedChineseInUi` 测试会抓中文和日文，其他语言靠审查。
- 每种语言都要保留所有占位符（`{0}`、`{1:F0}`、`{0:yyyyMMdd-HHmm}`）；`TranslationsAreCompleteAndKeepPlaceholders` 会检查这一点，以及每种语言是否有 `en.json` 的全部键。
- 用各语言的 Minecraft 官方译名（World / ワールド / 世界、资源包、数据包、光影包、Mod / MOD / 模組、Server / サーバー / 伺服器、Open to LAN / LANに公開 / 在區域網路上開放）。
- 键名说明文字出现的位置（`页面.区域.用途`）。两处英文相同、但在其他语言里要用不同说法时，分成两个键。
- 改完文字跑一次上面的布局检查：长译文要换行或省略，不能被切掉。`--pseudo-loc`（或 `AMBER_PSEUDO_LOC=1`）会把每段文字加长 40%，方便找出空间不够的地方。

### 新增一种语言

1. 把 `en.json` 复制成 `<代码>.json`（BCP 47 代码，如 `ko`、`de`、`pt-BR`），翻译所有的值，键和占位符不要动。
2. 在 `src/Amber.Core/Localization/L.cs` 的 `L.Languages` 里加 `("<代码>", "<这种语言自己的名字>")`，并在 `Cultures` 里加上格式化数字和日期用的区域。跟随系统会沿区域的上级链自动匹配；只有旧式区域名才需要在 `Match` 里加特例。
3. 在 `src/Amber.Core.Tests/LocalizationTests.cs` 的 `TranslationsAreCompleteAndKeepPlaceholders` 里加上这个代码，并在用词表里加一列。
4. 把代码加进 `ThemePack.FontLanguages`，让主题包可以给这种语言指定字体。
5. 跑测试和布局检查，在 PR 里附几张截图。

可以先提交部分翻译：没翻的文字会显示英文。在 PR 里注明是部分翻译。

## 翻译文档

每种语言一个文件，放在一起：

- 英文版用原名：`README.md`、`CONTRIBUTING.md`、`docs/themes.md`。
- 译文在 `.md` 前加语言代码：`README.zh-Hans.md`、`CONTRIBUTING.ja.md`、`docs/themes.zh-Hans.md`。
- 每个版本开头放一行语言切换，比如 `[English](README.md) | 简体中文`，加新语言时把其他版本的切换行也补上。
- 改文档时尽量同步改译文，改不了的在 PR 里说明哪些译文已经过时。

`docs/`、`server/`、`node/`、`networks/` 下的很多文档目前只有简体中文，特别欢迎补英文版：英文写进原名文件，中文移到 `<名字>.zh-Hans.md`。

## Pull request

改动保持专注，写清楚测试了什么；界面改动附上改前改后的截图。
