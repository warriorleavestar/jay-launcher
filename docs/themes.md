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
layout/pages/     可选：各页面的布局，如 layout/pages/downloads.json
fonts/            可选：.ttf / .otf
assets/           可选：.png / .jpg（每边不超过 4096 像素）
README.txt / *.md 可选：说明
```

其他类型的文件会让整个包被拒绝。限制：整个包不超过 20 MB、最多 256 个文件、单个文件不超过 16 MB、JSON 不超过 512 KB。

最快的起点：在设置里点 **导出当前主题…**，解开导出的文件改 tokens.json，再打包。仓库里有三个示例：[`themes/examples/`](../themes/examples/)，其中 `topbar` 演示了排版。

## theme.json

```json
{
  "format": 2,
  "id": "pixel-night",
  "name": "像素夜 Pixel Night",
  "author": "你的名字",
  "version": "1.0.0",
  "minJay": "0.8.0"
}
```

`id` 只能用小写字母、数字、`.`、`_`、`-`，2 到 64 个字符。导入同 id 的主题会替换旧版本。

### 格式版本（format）

- 新主题写 `"format": 2`。没写 `format` 的包按 format 1 读。
- `minJay`（可选）：主题需要的最低 Jay 版本。比当前 Jay 新时照常读，设置页提示“这个主题需要更新的 Jay”。
- 比 Jay 认识的更新的 format：能认出的部分照常用，并提示（不拒绝）。
- **format 1 自动升级**：读的时候在内存里换成 format 2，原文件不改。主题卡上写“已自动升级”，设置页的说明条写升级了什么、自动调了几处颜色，【复制给作者看】把完整说明复制成一段文字。具体对应：
  - 只设计了一个强调色的 format 1 主题，开始按钮也用这个强调色（`Play*` = `Accent*`）；
  - `FontDisplay`（format 1 是 26 号的大标题）→ `FontHeadline`，`FontDisplay` 按比例放大；`RadiusSm` 同时给 `RadiusXs`；`RadiusXl` = `RadiusLg` + 4（方角主题保持方角）；
  - 组件 `callout` → `banner`，`pill` → `chip`、`badge`、`tag`，`nav.item` 也套到图标栏的 `rail.item`，`sidebar` 的背景同时给 `topbar`；
  - shell.json 的旧字段见下面“窗口外壳”一节。

## tokens.json

JSON 里可以写 `//` 注释和结尾逗号。每一部分都可选，没写的用 Jay 默认值。

```jsonc
{
  // both（默认）/ dark / light。只有一种时，选了这个主题深浅色设置会被忽略。
  "variants": "dark",

  // 种子色：给一两个颜色，Jay 推出整套配色。background 只用在明暗一致的那一套。
  // play 是开始按钮的颜色；只写 accent 的单色主题，开始按钮也用 accent（仍然更大、带光晕）。
  "seed": { "accent": "#5BD75B", "play": "#F6A93B", "background": "#0E1224" },

  // 逐个指定颜色，盖住种子色推出来的值。dark / light / both（两套都用）。
  // 颜色写 #RRGGBB 或 #AARRGGBB（透明度在前）。
  "colors": {
    "dark":  { "Accent": "#5BD75B", "Frame": "#080A16" },
    "light": { "Accent": "#2E9E2E" },
    "both":  { "Danger": "#E5484D" }
  },

  "sizes":  { "FontBody": 15, "FontTitle": 22, "Space4": 16, "ControlHeight": 40, "PlayHeight": 52 },
  "radius": { "RadiusSm": 0, "RadiusMd": 0, "RadiusLg": 0, "RadiusPill": 0 },
  // 动效时长（毫秒），最多默认的 2 倍；用户打开“减少动画”时一律不用主题的值。
  "motion": { "DurationBase": 200 },
  // 线描插画的两种颜色（两套都用）。
  "illustration": { "IlloLine": "#93B4FF", "IlloFill": "#202A3A" },
  // 实例页英雄区：cover / blur / flat；遮罩最低 0.85。
  "hero": { "mode": "cover", "scrim": 0.88 },

  // 字体：包里的文件（fonts/…）或系统字体名。可以按语言指定：default / zh-Hans / zh-Hant / ja / en。
  "fonts": {
    "ui": { "default": "fonts/MyFont.ttf", "ja": "Noto Sans JP" },
    "mono": "Cascadia Mono"
  },

  "background": { "image": "assets/bg.png", "opacity": 0.5 }
}
```

### 图片位（slots）

图片主题用 tokens.json 的 `slots`，四个位置，每个都可以不写。设置 › 外观 › **用图片做主题** 做出来的就是这样的包，不用手写。

```jsonc
"slots": {
  // 整个窗口最底层。dim：盖上多少页面色（0–0.9），blur：模糊半径（按 1920 宽算的像素，0–40），focus：裁剪时保留哪一点（左右、上下，0–1）
  "background": { "image": "assets/background.jpg", "dim": 0.55, "blur": 8, "focus": [0.5, 0.4] },
  // 侧栏底图，盖上侧栏色
  "sidebar": { "image": "assets/sidebar.png", "dim": 0.6, "focus": [0.5, 0.5] },
  // 立绘：内容区下面的角落，在页面后面，不挡点击；size 是占内容区高度的比例（0.15–0.8），最宽占内容区的三分之一；窗口窄于 720 时不显示
  "mascot": { "image": "assets/mascot.png", "size": 0.45, "side": "right", "opacity": 1 },
  // 游戏库“继续玩”卡片和房间横幅的底图（上面照常压一层暗色，文字看得清）
  "banner": { "image": "assets/banner.png", "focus": [0.5, 0.3] }
}
```

- 图片只能是包里 `assets/` 下的 PNG 或 JPEG，每边不超过 4096。数字超出范围会被拉回范围内并在主题卡上提示。
- 写了 `slots.background` 时它优先；旧的 `background` 照样能用（相当于 `dim = 1 - opacity`）。用图片做主题时 Jay 两个都写，旧版 Jay 读不懂 `slots` 也能显示背景。
- 立绘和背景都在内容后面，画不到锁定的安全界面和侧栏的隐私行上。

### Reserved for later: `motion3d` and the `scene` slot

Two names are reserved for pseudo-3D motion and 3D scenes, which a later Jay will read. This version reads past
them without a problem, so a pack written for that later Jay still loads here:

- `tokens.json` → `"motion3d"`: tilt, depth and timing for page and card motion. Data only, never code or shaders.
- `slots` → `"scene"`: a scene behind the pages.

Whatever they will hold, reduced motion turns all of it off, and safety surfaces (knock, file offer, privacy row)
stay flat and unthemed.

### 颜色名

format 2 的名字和 format 1 的名字都认（左边是 format 2 写法，括号里是 format 1 的旧名字，同一个颜色）：

| 名字 | 用在哪 |
|---|---|
| Base（PageBg） / Surface / Raised（SurfaceRaised） | 页面底色 / 卡片 / 卡片上的浮起层 |
| Divider（BorderSubtle） | 边框、分隔线 |
| Frame（SidebarBg；NavBg、TopBarBg 也写到这里）, NavText（SidebarText）, NavTextMuted（SidebarTextMuted）, NavItemSelected（NavSelected）, NavItemHover（NavHover） | 侧边栏和顶栏 |
| Text（TextPrimary） / TextSecondary / TextTertiary | 正文 / 说明 / 次要说明 |
| Accent, AccentHover, AccentPressed, OnAccent, AccentText, AccentTint | 强调色、按下悬停、强调色上的文字、强调色文字、强调色浅底 |
| Success, SuccessText, SuccessTint | 成功 |
| Warning, WarningText, WarningTint | 警告 |
| Danger, DangerText, DangerTint, OnDanger | 危险操作 |
| DisabledFill, TextDisabled（DisabledText） | 禁用状态 |
| Overlay / Sunken / InputBg / Scrim | 菜单和对话框 / 日志和代码的凹陷底 / 输入框底 / 对话框后的遮罩。没写时 Overlay 跟着 Surface，Sunken 和 InputBg 跟着 PageBg |
| ControlBorder, ControlHover, ControlPressed, ControlSelected, Focus | 输入框和次按钮的边、悬停、按下、选中底、焦点环 |
| Play, PlayHover, PlayPressed, OnPlay, PlayBorder, PlayText, PlayGlow, PlayTint | “开始游戏”这一类琥珀按钮（Accent 管普通主按钮） |
| Badge, OnBadge, BadgeMuted, OnBadgeMuted | 角标 |
| IlloLine, IlloFill | 线描插画 |
| LoaderVanilla, LoaderFabric, LoaderForge, LoaderNeoForge, LoaderQuilt, LoaderOptiFine | 加载器标签的文字色 |
| LogDebug, LogInfo, LogWarn, LogError, LogFatal, LogJay | 日志各级别的文字色 |
| WingLight, WingDark | 翅膀条纹里的浅条和深条（见下） |

Jay 默认配色是“夜 / 晨”两套（墨蓝底、松鸦蓝强调色、琥珀只给开始游戏），完整色表和对比度见官方设计 §4.2。

**翅膀条纹**是 Jay 的标记：一段 13 像素，依次是强调色 6、深条 2、浅条 3、深条 2，只出现在选中的导航项、选中的页签、进度条和码卡片的左边。主题改了 `Accent` 时条纹跟着换色；没写 `WingLight` / `WingDark` 时，Jay 用强调色调出同一色相的浅条和深条，所以单色主题不用管它。想去掉条纹，把这两个颜色都写成和 `Accent` 一样。

自动封面（像素夜景）和像素头像是插画，不是令牌，不跟主题变色。

### 数字

| 名字 | 默认 | 允许范围 |
|---|---|---|
| FontCaption / FontBodySmall / FontBody | 12 / 13 / 14 | 最小 11 |
| FontSubtitle / FontTitle / FontHeadline / FontDisplay | 16 / 20 / 24 / 32 | |
| Space0 … Space8 | 2 4 8 12 16 24 32 48 64 | |
| RadiusXs / RadiusSm / RadiusMd / RadiusLg / RadiusXl / RadiusPill | 4 / 6 / 8 / 12 / 16 / 999 | ≥ 0 |
| ControlHeightSm / ControlHeight / ControlHeightLg | 28 / 36 / 44 | 只能调高 |
| PlayHeight（开始按钮高度） | 48 | ≥ 40 |
| motion：DurationFast / DurationBase / DurationSlow / DurationSlower | 100 / 160 / 240 / 320 毫秒 | 0 到默认的 2 倍 |

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

所有组件都能写 `background foreground border borderWidth radius padding fontWeight shadow`；少数组件另有属性。中日文界面里 fontWeight 只分 normal / bold（medium 当 normal，semibold 当 bold）。

| 组件 | 状态 | 另外能设的 |
|---|---|---|
| button、button.primary / secondary / tonal / ghost / danger / icon | :hover :pressed :disabled | |
| button.play（开始按钮） | :hover :pressed | `glow`：光晕，写一个颜色或一条阴影 |
| card、card.cover（游戏库封面卡） | card.cover 有 :hover | |
| hero（实例页英雄区） | | `scrim`（≥ 0.85） |
| chip、badge、tag（筛选芯片 / 角标 / 加载器等标签） | chip 有 :hover :selected | |
| tab（页签） | :hover :selected | `indicator`、`indicatorHeight`（2–4） |
| sidebar、rail.item、nav.item、topbar | 导航项有 :hover :selected | `indicator`（导航项左边的指示条） |
| input、combobox、segmented、switch、checkbox | :hover :focus / :checked | |
| list.row、table.header | :hover :selected | |
| banner（提示条，= format 1 的 callout） | | |
| toast、menu、tooltip、drawer、dialog、coachmark | | dialog 只管普通对话框，管不到安全确认 |
| progress（进度条） | | `track`（轨道）、`fill`（进度） |
| capsule（正在玩胶囊）、requestbar（只管非安全条目） | | |
| heading（页面标题）、window（窗口底色，可用图片） | | |

这一版 Jay 已经画出来的组件：button 全部、card、card.cover、hero、chip、badge、tag、tab、sidebar、rail.item、nav.item（含 indicator）、topbar（背景）、input、combobox、switch（开的颜色）、checkbox、banner、menu、tooltip、progress、heading、window。其余组件（segmented、list.row、table.header、toast、drawer、dialog、coachmark、capsule、requestbar）现在会被读取和检查，后续版本接上，写了不会报错。

值的写法：

- **颜色**：`#RRGGBB`、`#AARRGGBB`，或 `token:名字` 引用上面的颜色 token（跟着深浅色变）。
- **渐变**：`linear(角度, 颜色, 颜色[, 颜色, 颜色])`，2 到 4 个颜色。角度和 CSS 一样：0 从下到上，90 从左到右。
- **图片**（只有 background 能用）：`image:assets/文件.png`，铺满并居中裁剪。
- **阴影**：`x y 模糊 扩展 颜色`，最多两层用逗号分开，可以加 `inset`；数值绝对值不超过 32；`none` 去掉阴影。
- radius 0–64，borderWidth 0–8，padding 写一个数或 `[水平, 垂直]`，fontWeight 是 normal / medium / semibold / bold。

组件样式只作用在 Jay 主窗口里。确认框、远程控制同意框、“正在被远程控制”提示条这些安全窗口永远用 Jay 自带的配色和样式。

## layout/shell.json：窗口外壳

侧栏上有四个一级入口：游戏库、发现、联机、朋友（`library` `discover` `multiplayer` `people`）。设置、头像（“我”页）和隐私行在侧栏底部，主题藏不掉。

```jsonc
{
  "nav": "left",             // 导航放在 left（默认）/ right / top / bottom
  "navStyle": "auto",        // auto（默认：窗口 ≥ 1340 时展开，否则图标栏）/ expanded（总是展开）/ rail（总是图标栏）
  "sidebarWidth": 240,       // 展开时的宽度，200–320
  "railWidth": 76,           // 图标栏的宽度，64–96
  "showBrand": true,         // 显示 Jay 的标志
  "showShelf": true,         // 侧栏的“常玩”（后续版本）
  "contentMaxWidth": 1280,   // 页面内容最宽多少（大屏上居中），640–4000
  "pages": ["library", "multiplayer", "people", "discover"],  // 入口顺序；没写到的按原顺序接在后面
  "hidden": ["discover"]     // 只能藏 discover、multiplayer、people（入口还在，从别的地方还能跳过去）
}
```

`people`（朋友）是后来加的入口：没写它的主题包，Jay 把它排在 `multiplayer` 后面，不管 `multiplayer` 被排到了哪。

用户在 设置 › 侧栏 里选了“总是展开”或“总是只显示图标”时，以用户的为准。“总是展开”只在展开后内容区还有 640 宽时生效。

旧版（format 1）的写法自动升级：
- `navWidth` 小于 120 变成 `navStyle: "rail"`、`railWidth` 取 64–96；其余变成 `sidebarWidth`（夹到 200–320）。
- `navStyle`：`full` → `expanded`，`icons` → `rail`。
- `pages` / `hidden` 里的旧页面名：`home` `instances` `log` → `library`；`downloads` `content` → `discover`；`multiplayer` 不变；`accounts`（现在是头像后面的“我”页）`tools` `settings` 不是导航项，忽略。`hidden` 里一个入口下的旧页面全藏了才藏这个入口（只藏 `content` 不会把整个发现藏掉）。`home`、`settings`、`library` 不能隐藏。

## layout/pages/*.json：页面布局

页面被分成几个**区块**，每个区块是 Jay 自带的一整块功能（按钮、绑定、提示都在里面）。主题不能改区块里面，但可以决定区块放在哪、套什么容器、怎么分栏，还能加装饰性的文字和图片。

Pages and their regions (every region except a title is required):

| 页面文件 | 区块 |
|---|---|
| `me.json` | `me.title` `me.accounts` (game accounts) `me.add` `me.jay` (Jay account) `me.code` (my friend code) `me.devices` `me.appearance` (light/dark, language) `me.status` |
| `people.json` | `people.title` `people.list` (friends, then people I've met) `people.detail` (the conversation with the selected friend, someone I've met, or the empty state) `people.status` (which network delivers to friends, **Add a friend**, backup; the add panel opens under it) |
| `instances.json` | `instances.header` (the hero) `instances.lastrun` `instances.details` `instances.status` |
| `downloads.json` | `downloads.title` `downloads.search` `downloads.filters` `downloads.body` `downloads.status` |
| `content.json` | `content.header` `content.filters` `content.body` `content.status` |
| `multiplayer.json` | `multiplayer.title` `multiplayer.tabs` (the row with Back and the name of a sub-view: the public network or my server; empty on the lobby and in a room, which have no tabs any more) `multiplayer.room` (the room banner with its cover, connection mark and faces, and the knock panel) `multiplayer.body` `multiplayer.status` |

Calls (reserved, not built yet): the call panel sits inside `people.detail` (at the top of the conversation) and inside
`multiplayer.room` (under the banner), so it moves with those regions. The names `call.panel`, `call.stage` (the shared
screen and the people's tiles) and `call.bar` (microphone, camera, share, control, hang up) are reserved for when calls
can be laid out on their own; a pack can't use them yet. The red bar while someone controls the computer and the red
frame while sharing are safety surfaces: theme packs can't move or restyle them.

`home.json`, `tools.json` and `log.json` laid out the launch page, the toolbox and the standalone log page. Those pages are
gone (starting a game happens on each instance, the toolbox moved into Settings, the live log is a dock: Ctrl+`). A pack
that still carries these files installs as before and their regions (`home.*`, `tools.*`, `log.*`) still validate; Jay
just has no page to apply them to.

The accounts page grew into the "Me" page. `accounts.json` still works and lays out that page, and the old region names
map to the new ones: `accounts.title` → `me.title`, `accounts.list` → `me.accounts`, `accounts.jay` → `me.jay`,
`accounts.add` → `me.add`, `accounts.status` → `me.status`. The regions an old pack does not mention (`me.code`,
`me.devices`, `me.appearance`) are added at the end like any other missing required region. When a pack carries both
`accounts.json` and `me.json`, `me.json` wins.

设置页的排版固定，不能改（“恢复默认主题”在那里）。

必需的区块没写进去，Jay 会把它补在最外层容器的最后并在设置页提示，所以功能不会因为主题丢掉。每个区块只能出现一次。

文件可以只写一棵树，也可以按页面宽度分档写：`compact`（窄于 640）、`medium`、`wide`（1100 起）、`default`。某一档没写时用相邻的档或 `default`。

```jsonc
{
  "wide": {
    "type": "grid", "columns": ["*", "360"], "rows": ["auto", "*"], "spacing": 16,
    "children": [
      { "type": "region", "name": "downloads.title", "columnSpan": 2 },
      { "type": "region", "name": "downloads.body", "row": 1 },
      { "type": "stack", "row": 1, "column": 1, "children": ["downloads.search", "downloads.filters", "downloads.status"] }
    ]
  },
  "default": {
    "type": "dock",
    "children": [
      { "type": "region", "name": "downloads.search", "dock": "top" },
      { "type": "region", "name": "downloads.filters", "dock": "top" },
      { "type": "region", "name": "downloads.status", "dock": "bottom" },
      "downloads.body"
    ]
  }
}
```

节点类型：

- 容器：`stack`（`"orientation": "horizontal"` 横排）、`grid`（`columns` / `rows`，如 `["auto", "*", "2*", "240"]`）、`dock`、`wrap`、`panel`（叠在一起）、`scroll`、`card`（套上卡片样式）。
- 叶子：`region`（`"name"` 写区块名；直接写字符串 `"downloads.body"` 是简写）、`spacer`、`text`（`"text"` 不超过 200 字，`"style"`：h1 / h2 / hint / caption / body）、`image`（`"source": "assets/xx.png"`）。
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
- **升级到 format 2**：`Amber.Cli theme-migrate <目录或 .jaytheme> <输出.jaytheme>` 把 Jay 读到的样子（format 2 的名字、升级和自动修正之后的值）写成新包；字体、图片、页面布局和其他文件原样带上，原来的目录或包不动。`theme-check` 也会列出 format 1 包的升级说明。

## 内置主题

Jay 自带三套外观，在设置 › 外观的主题包列表最前面：

- **Jay 默认**（夜 / 晨）。
- **Jay 经典琥珀**：0.7 的暖棕底和琥珀强调色，开始按钮也是琥珀。给喜欢旧样子的人。
- **高对比**：正文、说明在每种底色上 ≥ 7:1，边框加粗到 2，焦点环更醒目。

内置主题就是普通的 format 2 包（源文件在 `themes/builtin/`），可以选、可以导出了再改，但删不掉；导入的包也不能用它们的 id。

## 自动修正

- **对比度**：正文、说明、按钮文字、侧边栏文字和背景的对比度低于 4.5:1（禁用文字 3:1）时，Jay 自动把文字颜色往黑或白推到看得清，并在设置页写“自动调了 N 处颜色”（【复制给作者看】列出每一处）。组件样式里同时写了文字色和背景色（包括渐变的每个颜色、深浅两套 token）也一样检查。
- **先合成再算**：半透明的浅底（AccentTint 这类）先叠到卡片色上，再算上面文字的对比度。文字还要在浮起层、选中底和四种提示条浅底上看得清；`OnAccent`、`OnPlay` 在悬停和按下的底色上也要 ≥ 4.5:1。
- **看得见的线**：输入框边框（ControlBorder）和焦点环（Focus）对卡片和页面底色 ≥ 3:1。
- **深色卡片分得开**：卡片和页面几乎一样深、分隔线又太淡时，把分隔线调亮到 1.6:1。
- **开始按钮的轮廓**：开始按钮和页面底色分不开（< 3:1）、主题又没画轮廓时，自动加一圈 `PlayBorder`。
- **改了卡片色**：没写浮起层和四种浅底时，它们跟着卡片色走（不再配上 Jay 默认的颜色）。
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
- 改区块里面的文字和按钮，或把必需的区块藏起来；隐藏游戏库、设置页和“恢复默认主题”。
