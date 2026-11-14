# Contributing to Jay

English | [简体中文](CONTRIBUTING.zh-Hans.md)

Thanks for helping. This page covers code, UI translations and translated docs.

## Language policy

Jay is an international project, so **English comes first**:

- Write issues, pull requests, commit messages, new code comments and new docs in English.
- `en.json` is the source and fallback UI language. Add every new UI string to `en.json` first.
- Issues and pull requests in other languages are welcome too. A maintainer or another contributor will help translate if needed.
- Some existing code comments and docs are still in Simplified Chinese. You don't need to rewrite them. Use English when you substantially change one, and English translations of existing docs are welcome (see [Translating docs](#translating-docs)).

## Ground rules

- Original code only. Don't copy or port code from other launchers or from decompiled binaries. Public protocol and format docs are fine.
- No telemetry, analytics or remote kill switches.
- Never test against a real `%APPDATA%\.minecraft`; use a separate test game directory.
- Use established cryptographic primitives; don't invent new ones.
- New behaviour gets unit tests, and a GUI self-check step when it is visible in the UI.

## Build and test

```bash
cd src
dotnet build Amber.slnx -c Release
dotnet test Amber.Core.Tests -c Release
```

GUI self-check (drives the real window and writes `report.txt`):

```bash
powershell -NoProfile -ExecutionPolicy Bypass -File tools/run-selftest.ps1 -Report <dir>
```

Layout check only (every page × every language × 8 window sizes, screenshots plus a list of clipped controls in `layout.txt`):

```bash
AMBER_LAYOUTCHECK=<dir> src/Amber.App/bin/Release/net10.0/Jay.exe
```

## UI translations

### How the UI picks a language

By default Jay follows the system language. If the system language (or its parent, for example `zh-HK` → Traditional Chinese) has a translation, Jay uses it; otherwise it uses English. Users can pick a language in Settings, and that choice wins. A string missing from a translation falls back to English, then to the key itself.

### Files

UI text lives in `src/Amber.Core/Localization/`. Use the terms in the [glossary](docs/i18n-glossary.md) (for example, an installed game folder is an *instance*, never a *version*).

| File | Language |
|---|---|
| `en.json` | English (US): the source and fallback; every key is defined here |
| `zh-Hans.json` | Simplified Chinese |
| `zh-Hant.json` | Traditional Chinese (Taiwan wording) |
| `ja.json` | Japanese |
| `lzh.json` | 文言 (Literary Chinese), playful |
| *(generated)* `en-x-ud` | English (upside down), playful; generated from `en.json` at run time |

Each file is a flat JSON object `"key": "text"`, keys sorted alphabetically.

- In XAML use `{l:T some.key}` (with `xmlns:l="using:Amber.App.Localization"`); in C# use `L.T("some.key")`, `L.F("some.key", a, b)` for text with `{0}`, `{1}` placeholders, or `L.P("some.key", count, …)` for text that changes with a count.
- Plurals: English may add `some.key.one` next to `some.key` (used when the count is 1). Languages without plural forms only need `some.key`.
- Never write user-visible text directly in `Amber.App`; the test `NoHardCodedChineseInUi` catches Chinese and Japanese text, and reviewers catch the rest.
- Keep every placeholder (`{0}`, `{1:F0}`, `{0:yyyyMMdd-HHmm}`) in every language; `TranslationsAreCompleteAndKeepPlaceholders` checks this and that every language has every key from `en.json`.
- Use the official Minecraft terms for each language (World / ワールド / 世界, Resource Pack, Data Pack, Shader Pack, Mod / MOD / 模組, Server / サーバー / 伺服器, Open to LAN / LANに公開 / 在區域網路上開放).
- The key name says where the text appears (`page.area.purpose`). If two places share the same English text but need different words in another language, give them separate keys.
- After changing text, run the layout check above: longer translations must wrap or trim, not get cut off. `--pseudo-loc` (or `AMBER_PSEUDO_LOC=1`) makes every string 40% longer to find tight spots.

### Playful languages

Like Minecraft, Jay has a few just-for-fun languages: 文言 (`lzh`) and English upside down (`en-x-ud`). They are listed in `L.Playful`, are never picked by following the system language, and are only chosen in Settings. If Minecraft has the same language (`L.MinecraftCodes`), a new instance starts the game in it too.

- `lzh.json` may lag behind: a missing key falls back to Traditional Chinese, then English. Translating new keys into 文言 is welcome but not required.
- Upside-down English has no file. It is generated from `en.json`, so new keys appear automatically.

### Adding a language

1. Copy `en.json` to `<code>.json` (a BCP 47 code such as `ko`, `de`, `pt-BR`) and translate every value. Keep the keys and placeholders unchanged.
2. In `src/Amber.Core/Localization/L.cs`, add `("<code>", "<name in that language>")` to `L.Languages` and a culture to `Cultures` for number and date formatting. System-language matching works automatically through the culture's parent chain; add a special case to `Match` only for legacy culture names.
3. Add the code to `TranslationsAreCompleteAndKeepPlaceholders` in `src/Amber.Core.Tests/LocalizationTests.cs`, and add a row to the glossary.
4. Add the code to `ThemePack.FontLanguages` so theme packs can set a font for it.
5. Run the tests and the layout check, and attach a few screenshots to the pull request.

A partial translation is fine to start with: missing strings show in English. Mark it as partial in the pull request.

## Translating docs

Docs use one file per language, side by side:

- The English version has the plain name: `README.md`, `CONTRIBUTING.md`.
- A translation adds the language code before `.md`: `README.zh-Hans.md`, `CONTRIBUTING.ja.md`, `docs/themes.zh-Hans.md`.
- Put a language switcher line at the top of every version, for example `English | [简体中文](README.zh-Hans.md)`, and add the new language to the switcher in the other versions.
- When you change a doc, update the translations you can, or say in the pull request which ones are now out of date.

Many docs under `docs/`, `server/`, `node/` and `networks/` currently exist only in Simplified Chinese. English versions are especially welcome: add the English text as the plain name and move the Chinese text to `<name>.zh-Hans.md`.

## Pull requests

Keep changes focused, describe what you tested, and include before/after screenshots for UI changes.

## License of contributions

Jay is under the [PolyForm Noncommercial License 1.0.0](LICENSE) with a [Commercial Use Exception](COMMERCIAL-EXCEPTION.md). By sending a pull request, you agree that your contribution is licensed under those terms, and that the Jay development team may also license it to others for commercial use, including under the Commercial Use Exception.
