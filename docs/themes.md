# Jay 界面主题包（.jaytheme）

主题包可以换掉 Jay 的配色、字体、字号、间距、圆角、窗口背景图、组件的美术风格，以及窗口和页面的排版、层级。在 **设置 → 界面主题包 → 导入主题…** 里导入。

主题包里**只有数据**：JSON、字体、图片。不能带代码、脚本、XAML，也不能从网上加载任何东西。所以别人做的主题、联机网络配置文件带来的主题，都不能在你电脑上运行程序。Windows、macOS、Linux 上效果一样。

> 现在支持配色和字体（tokens.json）、组件样式（styles.json）、窗口外壳（layout/shell.json）和除设置页以外各页面的布局（layout/pages/*.json）。

## 文件结构

`.jaytheme` 就是一个 zip，把下面这些文件直接放在 zip 根目录：

```
theme.json        必需：清单
tokens.json       可选：配色、字体、字号、间距、圆角、背景图
styles.json       可选：组件样式（按钮、卡片、导航、输入框……的美术风格）
layout/shell.json 可选：窗口外壳（导航放哪、多宽、只显示图标、页面顺序）
layout/pages/     可选：各页面的布局，如 layout/pages/home.json
fonts/            可选：.ttf / .otf
assets/           可选：.png / .jpg（每边不超过 4096 像素）
README.txt / *.md 可选：说明
```

其他类型的文件会让整个包被拒绝。限制：整个包不超过 20 MB、最多 256 个文件、单个文件不超过 16 MB、JSON 不超过 512 KB。

最快的起点：在设置里点 **导出当前主题…**，解开导出的文件改 tokens.json，再打包。仓库里有三个示例：[`themes/examples/`](../themes/examples/)，其中 `topbar` 演示了排版。

## theme.json

```json
{
  "format": 1,
  "id": "pixel-night",
  "name": "像素夜 Pixel Night",
  "author": "你的名字",
  "version": "1.0.0"
}
```

`id` 只能用小写字母、数字、`.`、`_`、`-`，2 到 64 个字符。导入同 id 的主题会替换旧版本。

## tokens.json

JSON 里可以写 `//` 注释和结尾逗号。每一部分都可选，没写的用 Jay 默认值。

```jsonc
{
  // both（默认）/ dark / light。只有一种时，选了这个主题深浅色设置会被忽略。
  "variants": "dark",

  // 种子色：给一两个颜色，Jay 推出整套配色。background 只用在明暗一致的那一套。
  "seed": { "accent": "#5BD75B", "background": "#0E1224" },

  // 逐个指定颜色，盖住种子色推出来的值。dark / light / both（两套都用）。
  // 颜色写 #RRGGBB 或 #AARRGGBB（透明度在前）。
  "colors": {
    "dark":  { "Accent": "#5BD75B", "SidebarBg": "#080A16" },
    "light": { "Accent": "#2E9E2E" },
    "both":  { "Danger": "#E5484D" }
  },

  "sizes":  { "FontBody": 15, "FontTitle": 22, "Space4": 16 },
  "radius": { "RadiusSm": 0, "RadiusMd": 0, "RadiusLg": 0, "RadiusPill": 0 },

  // 字体：包里的文件（fonts/…）或系统字体名。可以按语言指定：default / zh-Hans / zh-Hant / ja / en。
  "fonts": {
    "ui": { "default": "fonts/MyFont.ttf", "ja": "Noto Sans JP" },
    "mono": "Cascadia Mono"
  },

  "background": { "image": "assets/bg.png", "opacity": 0.5 }
}
```

### 颜色名

| 名字 | 用在哪 |
|---|---|
| PageBg / Surface / SurfaceRaised | 页面底色 / 卡片 / 卡片上的浮起层 |
| BorderSubtle | 边框、分隔线 |
| SidebarBg, SidebarText, SidebarTextMuted, NavSelected, NavHover | 侧边栏 |
| TextPrimary / TextSecondary / TextTertiary | 正文 / 说明 / 次要说明 |
| Accent, AccentHover, AccentPressed, OnAccent, AccentText, AccentTint | 强调色、按下悬停、强调色上的文字、强调色文字、强调色浅底 |
| Success, SuccessText, SuccessTint | 成功 |
| Warning, WarningText, WarningTint | 警告 |
| Danger, DangerText, DangerTint, OnDanger | 危险操作 |
| DisabledFill, DisabledText | 禁用状态 |
| Overlay / Sunken / InputBg / Scrim | 菜单和对话框 / 日志和代码的凹陷底 / 输入框底 / 对话框后的遮罩。没写时 Overlay 跟着 Surface，Sunken 和 InputBg 跟着 PageBg |
| ControlBorder, ControlHover, ControlPressed, ControlSelected, Focus | 输入框和次按钮的边、悬停、按下、选中底、焦点环 |
| Play, PlayHover, PlayPressed, OnPlay, PlayBorder, PlayText, PlayGlow, PlayTint | “开始游戏”这一类琥珀按钮（Accent 管普通主按钮） |
| Badge, OnBadge, BadgeMuted, OnBadgeMuted | 角标 |
| IlloLine, IlloFill | 线描插画 |
| LoaderVanilla, LoaderFabric, LoaderForge, LoaderNeoForge, LoaderQuilt, LoaderOptiFine | 加载器标签的文字色 |
| LogDebug, LogInfo, LogWarn, LogError, LogFatal, LogJay | 日志各级别的文字色 |

Jay 默认配色是“夜 / 晨”两套（墨蓝底、松鸦蓝强调色、琥珀只给开始游戏），完整色表和对比度见官方设计 §4.2。

### 数字

| 名字 | 默认 | 允许范围 |
|---|---|---|
| FontCaption / FontBodySmall / FontBody | 12 / 13 / 14 | 最小 11 |
| FontSubtitle / FontTitle / FontHeadline / FontDisplay | 16 / 20 / 24 / 32 | |
| Space0 … Space8 | 2 4 8 12 16 24 32 48 64 | |
| RadiusXs / RadiusSm / RadiusMd / RadiusLg / RadiusXl / RadiusPill | 4 / 6 / 8 / 12 / 16 / 999 | ≥ 0 |

超出范围的值会被拉回范围内并提示。

## styles.json：组件样式

按“组件 → 属性”写，状态写成 `":hover": { … }` 这样的子对象。没写的组件和属性保持 Jay 默认样子。

```jsonc
{
  "button":         { "borderWidth": 2, "border": "#000000", "radius": 0, "shadow": "3 3 0 0 #1F6B2A",
                      ":pressed": { "shadow": "none" } },
  "button.primary": { "background": "linear(90, #E0538A, #F08AB0)", "foreground": "#FFFFFF", "radius": 999,
                      "padding": [18, 6], ":hover": { "background": "token:AccentHover" } },
  "card":           { "radius": 18, "borderWidth": 0, "shadow": "0 6 20 0 #1FE0538A" },
  "sidebar":        { "background": "image:assets/sidebar.png" },
  "nav.item":       { "radius": 12, ":selected": { "background": "token:AccentTint" } },
  "input":          { "radius": 12, ":focus": { "border": "token:Accent" } },
  "heading":        { "foreground": "token:Accent", "fontWeight": "bold" }
}
```

| 组件 | 状态 | 能设的属性 |
|---|---|---|
| button、button.primary / secondary / ghost / danger | :hover :pressed（button 还有 :disabled） | background foreground border borderWidth radius padding fontWeight shadow |
| card | | background border borderWidth radius padding shadow |
| pill（类型/状态小标签） | | background foreground border borderWidth radius padding |
| callout（提示条） | | background border borderWidth radius padding |
| sidebar（侧边栏） | | background border borderWidth shadow |
| nav.item（侧边栏导航项） | :hover :selected | background foreground radius padding fontWeight |
| input（输入框） | :hover :focus | background foreground border borderWidth radius padding |
| combobox（下拉框） | :hover | background foreground border borderWidth radius padding |
| heading（页面标题） | | foreground fontWeight |
| window（窗口底色） | | background |

值的写法：

- **颜色**：`#RRGGBB`、`#AARRGGBB`，或 `token:名字` 引用上面的颜色 token（跟着深浅色变）。
- **渐变**：`linear(角度, 颜色, 颜色[, 颜色, 颜色])`，2 到 4 个颜色。角度和 CSS 一样：0 从下到上，90 从左到右。
- **图片**（只有 background 能用）：`image:assets/文件.png`，铺满并居中裁剪。
- **阴影**：`x y 模糊 扩展 颜色`，最多两层用逗号分开，可以加 `inset`；数值绝对值不超过 32；`none` 去掉阴影。
- radius 0–64，borderWidth 0–8，padding 写一个数或 `[水平, 垂直]`，fontWeight 是 normal / medium / semibold / bold。

组件样式只作用在 Jay 主窗口里。确认框、远程控制同意框、“正在被远程控制”提示条这些安全窗口永远用 Jay 自带的配色和样式。

## layout/shell.json：窗口外壳

侧栏上只有三个一级入口：游戏库、发现、联机。设置、账号和隐私行在侧栏底部，主题藏不掉。

```jsonc
{
  "nav": "left",             // 导航放在 left（默认）/ right / top / bottom
  "navStyle": "auto",        // auto（默认：窗口 ≥ 1340 时展开，否则图标栏）/ expanded（总是展开）/ rail（总是图标栏）
  "sidebarWidth": 240,       // 展开时的宽度，200–320
  "railWidth": 76,           // 图标栏的宽度，64–96
  "showBrand": true,         // 显示 Jay 的标志
  "showShelf": true,         // 侧栏的“常玩”（后续版本）
  "contentMaxWidth": 1280,   // 页面内容最宽多少（大屏上居中），640–4000
  "pages": ["library", "multiplayer", "discover"],  // 入口顺序；没写到的按原顺序接在后面
  "hidden": ["discover"]     // 只能藏 discover、multiplayer（入口还在，从别的地方还能跳过去）
}
```

用户在 设置 › 侧栏 里选了“总是展开”或“总是只显示图标”时，以用户的为准。“总是展开”只在展开后内容区还有 640 宽时生效。

旧版（format 1）的写法自动升级：
- `navWidth` 小于 120 变成 `navStyle: "rail"`、`railWidth` 取 64–96；其余变成 `sidebarWidth`（夹到 200–320）。
- `navStyle`：`full` → `expanded`，`icons` → `rail`。
- `pages` / `hidden` 里的旧页面名：`home` `instances` `log` → `library`；`downloads` `content` → `discover`；`multiplayer` 不变；`accounts` `tools` `settings` 不再是导航项，忽略。`hidden` 里一个入口下的旧页面全藏了才藏这个入口（只藏 `content` 不会把整个发现藏掉）。`home`、`settings`、`library` 不能隐藏。

## layout/pages/*.json：页面布局

页面被分成几个**区块**，每个区块是 Jay 自带的一整块功能（按钮、绑定、提示都在里面）。主题不能改区块里面，但可以决定区块放在哪、套什么容器、怎么分栏，还能加装饰性的文字和图片。

启动页（`home.json`）的区块：

| 区块 | 内容 | 必需 |
|---|---|---|
| `home.title` | 页面标题 | 否 |
| `home.pending` | “游戏已在运行，还要再开一个吗”提示 | 是 |
| `home.onboarding` | 新手卡（没有账号或实例时） | 是 |
| `home.instance` | 版本、账号、内存 | 是 |
| `home.crash` | 崩溃原因分析 | 是 |
| `home.launch` | 启动按钮和状态 | 是 |

其他页面的区块（标题以外都必需）：

| 页面文件 | 区块 |
|---|---|
| `accounts.json` | `accounts.title` `accounts.list` `accounts.add` `accounts.status` |
| `instances.json` | `instances.header` (the hero) `instances.lastrun` `instances.details` `instances.status` |
| `downloads.json` | `downloads.title` `downloads.search` `downloads.filters` `downloads.body` `downloads.status` |
| `content.json` | `content.header` `content.filters` `content.body` `content.status` |
| `multiplayer.json` | `multiplayer.title` `multiplayer.tabs` `multiplayer.room` (the room header and knock panel) `multiplayer.body` `multiplayer.status` |
| `tools.json` | `tools.title` `tools.body` `tools.status` |
| `log.json` | `log.header` `log.jump` `log.filters` `log.list` |

设置页的排版固定，不能改（“恢复默认主题”在那里）。

必需的区块没写进去，Jay 会把它补在最外层容器的最后并在设置页提示，所以功能不会因为主题丢掉。每个区块只能出现一次。

文件可以只写一棵树，也可以按页面宽度分档写：`compact`（窄于 640）、`medium`、`wide`（1100 起）、`default`。某一档没写时用相邻的档或 `default`。

```jsonc
{
  "wide": {
    "type": "grid", "columns": ["*", "360"], "rows": ["auto", "*"], "spacing": 16,
    "children": [
      { "type": "region", "name": "home.title", "columnSpan": 2 },
      { "type": "scroll", "row": 1, "children": ["home.pending", "home.onboarding", "home.instance", "home.crash"] },
      { "type": "region", "name": "home.launch", "row": 1, "column": 1, "valign": "start" }
    ]
  },
  "default": {
    "type": "dock",
    "children": [
      { "type": "region", "name": "home.launch", "dock": "top" },
      { "type": "scroll", "children": ["home.pending", "home.onboarding", "home.instance", "home.crash"] }
    ]
  }
}
```

节点类型：

- 容器：`stack`（`"orientation": "horizontal"` 横排）、`grid`（`columns` / `rows`，如 `["auto", "*", "2*", "240"]`）、`dock`、`wrap`、`panel`（叠在一起）、`scroll`、`card`（套上卡片样式）。
- 叶子：`region`（`"name"` 写区块名；直接写字符串 `"home.launch"` 是简写）、`spacer`、`text`（`"text"` 不超过 200 字，`"style"`：h1 / h2 / hint / caption / body）、`image`（`"source": "assets/xx.png"`）。
- 通用属性：`width` `height` `minWidth` `maxWidth` `minHeight` `maxHeight` `margin` `padding`（一个数，或 `[左右, 上下]`，或 `[左, 上, 右, 下]`）、`align` / `valign`（start / center / end / stretch）、`spacing`。
- 放在父容器里的位置：`row` `column` `rowSpan` `columnSpan`（grid）、`dock`（left / right / top / bottom）。
- `hideBelow`：页面窄于这个宽度时不显示（只能用在装饰和容器上，区块不能这样藏起来）。

限制：一棵树最多 2000 个节点、嵌套 12 层。

## 联机网络带主题

联机网络的主人可以在网络配置文件里带一个主题，加入的人在联机页里看到它：

```jsonc
"ui": { "url": "https://你的网站/net.jaytheme", "sha256": "64 位十六进制", "size": 123456, "scope": "network" }
// 或者只带配色（不超过 64 KB，不能引用字体文件和图片）：
"ui": { "inline": { "seed": { "accent": "#CC2233" } }, "scope": "network" }
```

- 网址只能是 https，由网络主人自己托管。Jay 只下载 `size` 字节，大小和 sha256 都对上才用，然后照常按导入规则检查；对不上整个丢掉，联机不受影响。下载过的按 sha256 缓存。
- `scope: "network"`：只在联机页里用，离开联机页换回你自己的主题；不改导航。
- `scope: "global"`：Jay 先问你，同意了才用在整个 Jay 上（只对这一次的主题有效）。
- 网络主题不写进设置、不出现在主题列表里。设置里可以关掉“使用联机网络带来的主题”；用 `--default-theme` 启动时也不用。

## 边改边看

- **开发模式**：`Jay --theme-dev <主题目录>`（或设环境变量 `AMBER_THEME_DEV=<主题目录>`）直接用没打包的目录启动。目录里的文件一保存，Jay 在半秒内重新套用；写错了就保留上一次能用的样子，原因写在设置页和标准错误里。开发模式不安装主题、不改设置。
- **检查**：`Amber.Cli theme-check <目录或 .jaytheme>` 按导入时的规则读一遍，列出各层和所有提示。没有提示返回 0，有提示返回 2，读不了返回 1。
- **打包**：`Amber.Cli theme-pack <目录> <输出.jaytheme>` 打包并检查，跳过 `.git`、`.DS_Store` 这类隐藏文件；不符合规则的不会留下输出文件。

## 自动修正

- **对比度**：正文、说明、按钮文字、侧边栏文字和背景的对比度低于 4.5:1（禁用文字 3:1）时，Jay 自动把文字颜色往黑或白推到看得清，并在设置页提示。组件样式里同时写了文字色和背景色（包括渐变的每个颜色、深浅两套 token）也一样检查。
- **坏掉的部分**：颜色写错、文件缺失、字体文件不是字体、图片太大，这些只丢掉那一项，用默认值代替，主题的其余部分照常用。设置页会列出问题。

## 主题让 Jay 打不开怎么办

- 连续两次用某个主题启动都没成功，第三次 Jay 自动用默认主题启动，设置页会说明原因。在设置里重新选一次这个主题就会再试。
- 也可以用参数 `--default-theme` 启动（或设环境变量 `AMBER_DEFAULT_THEME=1`），这次不套用主题包。
- 设置页的 **↺ 恢复默认主题** 永远在同一个位置，主题改不了它。

## 安全边界

这些是故意不让主题做的：

- 带任何可执行的东西（代码、脚本、XAML、插件）。
- 从网上加载图片或字体（会被用来追踪谁打开了 Jay）。
- 把字号调到 11 以下，或把关键文字调到看不清。
- 改确认框、远程控制同意框和共享提示条的样式（这些窗口锁定用 Jay 自带样式）。
- 改区块里面的文字和按钮，或把必需的区块藏起来；隐藏启动页、设置页和“恢复默认主题”。
