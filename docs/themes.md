# Jay 界面主题包（.jaytheme）

主题包可以换掉 Jay 的配色、字体、字号、间距、圆角和窗口背景图。在 **设置 → 界面主题包 → 导入主题…** 里导入。

主题包里**只有数据**：JSON、字体、图片。不能带代码、脚本、XAML，也不能从网上加载任何东西。所以别人做的主题、联机网络配置文件带来的主题，都不能在你电脑上运行程序。Windows、macOS、Linux 上效果一样。

> 这是第 1 期。组件样式（styles.json）和页面布局（layout/）在后面几期加入；现在的 Jay 读到它们会提示“这个版本还不支持”，其余部分照常生效。

## 文件结构

`.jaytheme` 就是一个 zip，把下面这些文件直接放在 zip 根目录：

```
theme.json        必需：清单
tokens.json       可选：配色、字体、字号、间距、圆角、背景图
fonts/            可选：.ttf / .otf
assets/           可选：.png / .jpg（每边不超过 4096 像素）
README.txt / *.md 可选：说明
```

其他类型的文件会让整个包被拒绝。限制：整个包不超过 20 MB、最多 256 个文件、单个文件不超过 16 MB、JSON 不超过 512 KB。

最快的起点：在设置里点 **导出当前主题…**，解开导出的文件改 tokens.json，再打包。仓库里有两个示例：[`themes/examples/`](../themes/examples/)。

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

### 数字

| 名字 | 默认 | 允许范围 |
|---|---|---|
| FontCaption / FontBodySmall / FontBody | 12 / 13 / 14 | 最小 11 |
| FontSubtitle / FontTitle / FontDisplay | 16 / 20 / 26 | |
| Space1 … Space6 | 4 8 12 16 24 32 | |
| RadiusSm / RadiusMd / RadiusLg / RadiusPill | 4 / 6 / 10 / 999 | ≥ 0 |

超出范围的值会被拉回范围内并提示。

## 自动修正

- **对比度**：正文、说明、按钮文字、侧边栏文字和背景的对比度低于 4.5:1（禁用文字 3:1）时，Jay 自动把文字颜色往黑或白推到看得清，并在设置页提示。
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
- （后面几期）改远程控制同意框、删除确认、隐私说明、微软登录这些安全界面的样式和文字，或隐藏设置入口和“恢复默认主题”。
