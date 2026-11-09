using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Amber.Core.Themes;

/// <summary>
/// 界面主题包（.jaytheme）：一个 zip，里面只有数据（JSON、字体、图片），没有任何代码。
/// 读 theme.json、tokens.json（配色、字体、字号、间距、圆角、背景图）、styles.json（组件样式）和 layout/（外壳和页面的排版、层级）。
/// 自己导入的主题和联机网络带来的主题都走这一个读取器，规则完全一样。
/// </summary>
public sealed class ThemePack
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Author { get; init; }
    public string Version { get; init; } = "1.0.0";

    /// <summary>both（深浅色都有）/ dark / light（只有一种，强制用这一种）。</summary>
    public string Variants { get; init; } = "both";

    /// <summary>颜色：键是 token 名，值是 #AARRGGBB。Dark / Light 两套；只有一种时两套一样。</summary>
    public Dictionary<string, uint> DarkColors { get; } = [];
    public Dictionary<string, uint> LightColors { get; } = [];

    /// <summary>数字 token（字号、间距），键是 token 名。</summary>
    public Dictionary<string, double> Numbers { get; } = [];

    /// <summary>圆角 token。</summary>
    public Dictionary<string, double> Radii { get; } = [];

    /// <summary>界面字体：键是语言（default、zh-Hans、zh-Hant、ja、en），值是包里的字体文件（fonts/…）或系统字体名。</summary>
    public Dictionary<string, string> UiFonts { get; } = [];
    public string? MonoFont { get; init; }

    /// <summary>窗口背景图（包里的 assets/…），没有就是 null。</summary>
    public string? BackgroundImage { get; init; }
    public double BackgroundOpacity { get; init; } = 1;

    /// <summary>第 2 层组件样式：组件名 → 状态（"" 是普通状态）→ 属性。</summary>
    public Dictionary<string, Dictionary<string, StyleProps>> Styles { get; } = [];

    /// <summary>第 3 层外壳布局（layout/shell.json）；null 表示用 Jay 自带的。</summary>
    public ShellLayout? Shell { get; set; }

    /// <summary>第 3 层页面布局（layout/pages/{页面}.json），键是页面 id。</summary>
    public Dictionary<string, PageLayout> Pages { get; } = [];

    /// <summary>读取时用来取包里文件的函数（布局里的图片要核对文件）。</summary>
    internal Func<string, byte[]?>? Open { get; set; }

    /// <summary>解压后的目录（已安装的主题）；只在内存里读的为 null。</summary>
    public string? Directory { get; set; }

    /// <summary>读取和校验时发现的问题（能用的部分照常用，有问题的部分用默认值）。</summary>
    public List<ThemeProblem> Problems { get; } = [];
}

/// <summary>一条问题。Code 是固定的英文代码（测试和日志用），Detail 是给人看的位置或值。</summary>
public sealed record ThemeProblem(string Code, string Detail, bool Fatal = false)
{
    public override string ToString() => $"{Code}: {Detail}";
}

public sealed class ThemePackException(string code, string detail) : Exception($"{code}: {detail}")
{
    public string Code { get; } = code;
    public string Detail { get; } = detail;
}

public static partial class ThemePackReader
{
    public const string Extension = ".jaytheme";
    public const long MaxPackBytes = 20L * 1024 * 1024;
    public const long MaxEntryBytes = 16L * 1024 * 1024;
    public const long MaxJsonBytes = 512 * 1024;
    public const int MaxEntries = 256;
    public const int MaxImageSide = 4096;

    /// <summary>当前主题格式版本。包里写的比这个大，说明是给更新的 Jay 做的：能读的照常读，并提示。</summary>
    public const int FormatVersion = 1;

    /// <summary>颜色 token：和 Themes/Tokens.axaml 的名字一一对应。</summary>
    public static readonly string[] ColorKeys =
    [
        "PageBg", "Surface", "SurfaceRaised", "BorderSubtle", "SidebarBg", "TextPrimary", "TextSecondary", "TextTertiary",
        "Accent", "AccentHover", "AccentPressed", "OnAccent", "AccentText", "Success", "SuccessText", "Warning",
        "WarningText", "Danger", "DangerText", "OnDanger", "DisabledFill", "DisabledText", "SidebarText", "SidebarTextMuted",
        "NavSelected", "NavHover", "AccentTint", "SuccessTint", "WarningTint", "DangerTint", "Overlay", "Sunken",
        "InputBg", "Scrim", "ControlBorder", "ControlHover", "ControlPressed", "ControlSelected", "Focus", "Play",
        "PlayHover", "PlayPressed", "OnPlay", "PlayBorder", "PlayText", "PlayGlow", "PlayTint", "Badge",
        "OnBadge", "BadgeMuted", "OnBadgeMuted", "IlloLine", "IlloFill", "LoaderVanilla", "LoaderFabric", "LoaderForge",
        "LoaderNeoForge", "LoaderQuilt", "LoaderOptiFine", "LogDebug", "LogInfo", "LogWarn", "LogError", "LogFatal",
        "LogJay",
    ];

    /// <summary>数字 token 和允许的范围（字号最小 11，太小的看不清）。</summary>
    public static readonly Dictionary<string, (double Min, double Max)> NumberKeys = new()
    {
        ["FontCaption"] = (11, 24), ["FontBodySmall"] = (11, 28), ["FontBody"] = (12, 32),
        ["FontSubtitle"] = (12, 40), ["FontTitle"] = (14, 56), ["FontHeadline"] = (14, 64), ["FontDisplay"] = (16, 72),
        ["Space0"] = (0, 16), ["Space7"] = (0, 160), ["Space8"] = (0, 192),
        ["Space1"] = (0, 32), ["Space2"] = (0, 48), ["Space3"] = (0, 64),
        ["Space4"] = (0, 80), ["Space5"] = (0, 96), ["Space6"] = (0, 128),
    };

    public static readonly Dictionary<string, (double Min, double Max)> RadiusKeys = new()
    {
        ["RadiusXs"] = (0, 24), ["RadiusSm"] = (0, 32), ["RadiusMd"] = (0, 48), ["RadiusLg"] = (0, 64), ["RadiusXl"] = (0, 80), ["RadiusPill"] = (0, 999),
    };

    public static readonly string[] FontLanguages = ["default", "zh-Hans", "zh-Hant", "ja", "en", "lzh", "en-x-ud"];

    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".json", ".ttf", ".otf", ".png", ".jpg", ".jpeg", ".txt", ".md",
    };

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{1,63}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex("^#([0-9a-fA-F]{6}|[0-9a-fA-F]{8})$")]
    private static partial Regex ColorPattern();

    public static bool IsValidId(string id) => IdPattern().IsMatch(id) && !id.Contains("..");

    /// <summary>从 .jaytheme 文件读（只读进内存，不解压到磁盘）。</summary>
    public static ThemePack Read(string packPath)
    {
        var info = new FileInfo(packPath);
        if (!info.Exists) throw new ThemePackException("not_found", packPath);
        if (info.Length > MaxPackBytes) throw new ThemePackException("pack_too_large", $"{info.Length}");
        using var stream = info.OpenRead();
        return Read(stream);
    }

    public static ThemePack Read(Stream zipStream)
    {
        var files = ReadEntries(zipStream);
        return Parse(name => files.TryGetValue(name, out var bytes) ? bytes : null);
    }

    internal static ThemePack ParseFiles(Dictionary<string, byte[]> files) => Parse(name => files.TryGetValue(name, out var bytes) ? bytes : null);

    /// <summary>从已解压的目录读（已安装的主题、开发时直接用文件夹）。</summary>
    public static ThemePack ReadDirectory(string directory)
    {
        var root = Path.GetFullPath(directory);
        var pack = Parse(name =>
        {
            var full = Path.GetFullPath(Path.Combine(root, name));
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return null;
            var file = new FileInfo(full);
            if (!file.Exists || file.Length > MaxEntryBytes) return null;
            return File.ReadAllBytes(full);
        });
        pack.Directory = root;
        return pack;
    }

    /// <summary>读 zip 的每一项，路径不安全、类型不允许、太大的直接拒绝整个包（这类包不是正常做出来的）。</summary>
    internal static Dictionary<string, byte[]> ReadEntries(Stream zipStream)
    {
        ZipArchive zip;
        try { zip = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true); }
        catch (InvalidDataException e) { throw new ThemePackException("not_zip", e.Message); }
        using (zip)
        {
            if (zip.Entries.Count > MaxEntries) throw new ThemePackException("too_many_files", $"{zip.Entries.Count}");
            var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            long total = 0;
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith('/')) continue; // 目录
                var name = NormalizeEntryName(entry.FullName);
                if (!AllowedExtensions.Contains(Path.GetExtension(name)))
                    throw new ThemePackException("file_type_not_allowed", name);
                if (entry.Length > MaxEntryBytes) throw new ThemePackException("file_too_large", name);
                total += entry.Length;
                if (total > MaxPackBytes) throw new ThemePackException("pack_too_large", $"{total}");
                using var input = entry.Open();
                using var buffer = new MemoryStream();
                // 不信 entry.Length：按实际读出的字节数再限一次（防伪造长度的压缩炸弹）。
                var chunk = new byte[81920];
                int read;
                while ((read = input.Read(chunk)) > 0)
                {
                    buffer.Write(chunk, 0, read);
                    if (buffer.Length > MaxEntryBytes) throw new ThemePackException("file_too_large", name);
                }
                if (!files.TryAdd(name, buffer.ToArray())) throw new ThemePackException("duplicate_file", name);
            }
            return files;
        }
    }

    /// <summary>zip 里的路径：只允许相对路径、正斜杠，不允许 ..、盘符、绝对路径。</summary>
    internal static string NormalizeEntryName(string raw)
    {
        var name = raw.Replace('\\', '/');
        if (name.Length == 0 || name.Length > 200 || name.StartsWith('/') || name.Contains(':') || name.Contains('\0'))
            throw new ThemePackException("unsafe_path", raw);
        var parts = name.Split('/');
        if (parts.Any(p => p is "" or "." or ".."))
            throw new ThemePackException("unsafe_path", raw);
        return name;
    }

    private static ThemePack Parse(Func<string, byte[]?> open)
    {
        var manifestBytes = open("theme.json") ?? throw new ThemePackException("missing_manifest", "theme.json");
        using var manifest = ParseJson(manifestBytes, "theme.json");
        var m = manifest.RootElement;
        if (m.ValueKind != JsonValueKind.Object) throw new ThemePackException("bad_manifest", "theme.json");
        var id = Str(m, "id") ?? throw new ThemePackException("bad_manifest", "id");
        if (!IsValidId(id)) throw new ThemePackException("bad_id", id);
        var name = Str(m, "name") is { Length: > 0 } n ? Clip(n, 64) : id;
        // 主题不能冒充安全提示：名字、作者里有保留词就整包拒绝（布局里的文字在 ThemeLayout 里查）。
        if (SafetyWords.Contains(name)) throw new ThemePackException("reserved_words", "name");
        if (SafetyWords.Contains(Str(m, "author"))) throw new ThemePackException("reserved_words", "author");
        var problems = new List<ThemeProblem>();
        if (m.TryGetProperty("format", out var fmt) && fmt.TryGetInt32(out var format) && format > FormatVersion)
            problems.Add(new("newer_format", $"{format}"));

        // tokens.json 先读成中间结果，再组装 ThemePack（ThemePack 的部分属性是 init）。
        var tokensBytes = open("tokens.json");
        JsonDocument? tokens = tokensBytes is null ? null : ParseJsonOrProblem(tokensBytes, "tokens.json", problems);
        using var _ = tokens;
        var t = tokens?.RootElement ?? default;
        bool has(string key, out JsonElement value)
        {
            value = default;
            return tokens is not null && t.ValueKind == JsonValueKind.Object && t.TryGetProperty(key, out value);
        }

        var variants = has("variants", out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is "dark" or "light" ? v.GetString()! : "both";
        string? background = null;
        double backgroundOpacity = 1;
        if (has("background", out var bg) && bg.ValueKind == JsonValueKind.Object)
        {
            if (Str(bg, "image") is { } img)
            {
                var error = CheckAsset(open, img, ["assets/"], [".png", ".jpg", ".jpeg"]);
                if (error is null) background = img; else problems.Add(error);
            }
            if (bg.TryGetProperty("opacity", out var op) && op.TryGetDouble(out var o)) backgroundOpacity = Math.Clamp(o, 0, 1);
        }
        string? mono = null;
        var uiFonts = new Dictionary<string, string>();
        if (has("fonts", out var fonts) && fonts.ValueKind == JsonValueKind.Object)
        {
            if (fonts.TryGetProperty("ui", out var ui))
            {
                if (ui.ValueKind == JsonValueKind.String) AddFont(open, uiFonts, "default", ui.GetString()!, problems);
                else if (ui.ValueKind == JsonValueKind.Object)
                    foreach (var p in ui.EnumerateObject())
                    {
                        if (!FontLanguages.Contains(p.Name)) { problems.Add(new("unknown_font_language", p.Name)); continue; }
                        if (p.Value.ValueKind == JsonValueKind.String) AddFont(open, uiFonts, p.Name, p.Value.GetString()!, problems);
                    }
            }
            if (Str(fonts, "mono") is { } monoValue)
            {
                var tmp = new Dictionary<string, string>();
                AddFont(open, tmp, "mono", monoValue, problems);
                mono = tmp.GetValueOrDefault("mono");
            }
        }

        var pack = new ThemePack
        {
            Id = id,
            Name = name,
            Author = Str(m, "author") is { } a ? Clip(a, 64) : null,
            Version = Str(m, "version") is { } ver ? Clip(ver, 32) : "1.0.0",
            Variants = variants,
            BackgroundImage = background,
            BackgroundOpacity = backgroundOpacity,
            MonoFont = mono,
        };
        pack.Problems.AddRange(problems);
        foreach (var (k, val) in uiFonts) pack.UiFonts[k] = val;

        if (has("seed", out var seed) && seed.ValueKind == JsonValueKind.Object)
            ApplySeed(pack, seed);
        if (has("colors", out var colors) && colors.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in colors.EnumerateObject())
            {
                switch (p.Name)
                {
                    case "dark": ReadColors(p.Value, pack.DarkColors, pack.Problems, "colors.dark"); break;
                    case "light": ReadColors(p.Value, pack.LightColors, pack.Problems, "colors.light"); break;
                    case "both":
                        ReadColors(p.Value, pack.DarkColors, pack.Problems, "colors.both");
                        ReadColors(p.Value, pack.LightColors, pack.Problems, "colors.both");
                        break;
                    default: pack.Problems.Add(new("unknown_key", "colors." + p.Name)); break;
                }
            }
        }
        // 只有一种的主题：另一套跟它一样，切换深浅色时外观不变。
        if (variants == "dark") Copy(pack.DarkColors, pack.LightColors);
        if (variants == "light") Copy(pack.LightColors, pack.DarkColors);
        if (has("sizes", out var sizes)) ReadNumbers(sizes, NumberKeys, pack.Numbers, pack.Problems, "sizes");
        if (has("radius", out var radius)) ReadNumbers(radius, RadiusKeys, pack.Radii, pack.Problems, "radius");


        ThemeContrast.Enforce(pack);
        // 组件样式在配色修正之后读：组件里引用的 token 要按修正后的颜色算对比度。
        if (open("styles.json") is { } stylesBytes && ParseJsonOrProblem(stylesBytes, "styles.json", pack.Problems) is { } styles)
            using (styles) ThemeStyles.Read(pack, styles.RootElement, open);
        pack.Open = open;
        if (open("layout/shell.json") is { } shellBytes && ParseJsonOrProblem(shellBytes, "layout/shell.json", pack.Problems) is { } shell)
            using (shell) ThemeLayout.ReadShell(pack, shell.RootElement);
        foreach (var page in ThemeLayout.PageIds)
            if (open($"layout/pages/{page}.json") is { } pageBytes && ParseJsonOrProblem(pageBytes, $"layout/pages/{page}.json", pack.Problems) is { } pageDoc)
                using (pageDoc) ThemeLayout.ReadPage(pack, page, pageDoc.RootElement);
        pack.Open = null;
        return pack;
    }

    private static void Copy(Dictionary<string, uint> from, Dictionary<string, uint> to)
    {
        to.Clear();
        foreach (var (k, v) in from) to[k] = v;
    }

    private static void AddFont(Func<string, byte[]?> open, Dictionary<string, string> into, string lang, string value, List<ThemeProblem> problems)
    {
        if (value.StartsWith("fonts/", StringComparison.Ordinal))
        {
            var error = CheckAsset(open, value, ["fonts/"], [".ttf", ".otf"]);
            if (error is null) into[lang] = value; else problems.Add(error);
            return;
        }
        // 系统字体名：只允许普通字符，长度有限（字体名里不能塞 URI 去加载别处的东西）。
        if (value.Length is > 0 and <= 64 && value.All(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.'))
            into[lang] = value;
        else problems.Add(new("bad_font", value));
    }

    /// <summary>检查包里的资源文件：路径前缀、扩展名、文件头和图片尺寸。</summary>
    private static ThemeProblem? CheckAsset(Func<string, byte[]?> open, string path, string[] prefixes, string[] extensions)
    {
        string name;
        try { name = NormalizeEntryName(path); }
        catch (ThemePackException) { return new("unsafe_path", path); }
        if (!prefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal)) || !extensions.Contains(Path.GetExtension(name).ToLowerInvariant()))
            return new("bad_asset_path", path);
        var bytes = open(name);
        if (bytes is null) return new("missing_file", path);
        var ext = Path.GetExtension(name).ToLowerInvariant();
        if (ext is ".ttf" or ".otf")
            return ThemeAssets.LooksLikeFont(bytes) ? null : new("bad_font_file", path);
        if (ThemeAssets.ImageSize(bytes) is not { } size) return new("bad_image", path);
        return size.Width > MaxImageSide || size.Height > MaxImageSide ? new("image_too_large", $"{path} {size.Width}x{size.Height}") : null;
    }

    private static void ApplySeed(ThemePack pack, JsonElement seed)
    {
        uint? accent = Str(seed, "accent") is { } a && TryColor(a, out var ac) ? ac : null;
        uint? background = Str(seed, "background") is { } b && TryColor(b, out var bc) ? bc : null;
        if (accent is null && background is null) { pack.Problems.Add(new("bad_seed", "seed")); return; }
        foreach (var (dark, target) in new[] { (true, pack.DarkColors), (false, pack.LightColors) })
            foreach (var (k, val) in ThemeColor.Derive(accent, background, dark)) target[k] = val;
    }

    private static void ReadColors(JsonElement obj, Dictionary<string, uint> into, List<ThemeProblem> problems, string where)
    {
        if (obj.ValueKind != JsonValueKind.Object) { problems.Add(new("bad_value", where)); return; }
        foreach (var p in obj.EnumerateObject())
        {
            if (!ColorKeys.Contains(p.Name)) { problems.Add(new("unknown_key", $"{where}.{p.Name}")); continue; }
            if (p.Value.ValueKind == JsonValueKind.String && TryColor(p.Value.GetString()!, out var c)) into[p.Name] = c;
            else problems.Add(new("bad_color", $"{where}.{p.Name}"));
        }
    }

    private static void ReadNumbers(JsonElement obj, Dictionary<string, (double Min, double Max)> allowed, Dictionary<string, double> into, List<ThemeProblem> problems, string where)
    {
        if (obj.ValueKind != JsonValueKind.Object) { problems.Add(new("bad_value", where)); return; }
        foreach (var p in obj.EnumerateObject())
        {
            if (!allowed.TryGetValue(p.Name, out var range)) { problems.Add(new("unknown_key", $"{where}.{p.Name}")); continue; }
            if (!p.Value.TryGetDouble(out var value) || double.IsNaN(value)) { problems.Add(new("bad_value", $"{where}.{p.Name}")); continue; }
            var clamped = Math.Clamp(value, range.Min, range.Max);
            if (clamped != value) problems.Add(new("value_clamped", $"{where}.{p.Name} {value.ToString(CultureInfo.InvariantCulture)}"));
            into[p.Name] = clamped;
        }
    }

    /// <summary>#RRGGBB 或 #AARRGGBB（和 Avalonia 一样，透明度在前）。</summary>
    public static bool TryColor(string text, out uint argb)
    {
        argb = 0;
        if (!ColorPattern().IsMatch(text)) return false;
        var hex = uint.Parse(text.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        argb = text.Length == 7 ? 0xFF000000 | hex : hex;
        return true;
    }

    public static string FormatColor(uint argb) => (argb >> 24) == 0xFF ? $"#{argb & 0xFFFFFF:X6}" : $"#{argb:X8}";

    private static JsonDocument ParseJson(byte[] bytes, string name)
    {
        if (bytes.Length > MaxJsonBytes) throw new ThemePackException("json_too_large", name);
        try { return JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32, CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
        catch (JsonException e) { throw new ThemePackException("bad_json", $"{name}: {e.Message}"); }
    }

    private static JsonDocument? ParseJsonOrProblem(byte[] bytes, string name, List<ThemeProblem> problems)
    {
        try { return ParseJson(bytes, name); }
        catch (ThemePackException e) { problems.Add(new(e.Code, e.Detail)); return null; }
    }

    private static string? Str(JsonElement obj, string key) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Clip(string text, int max)
    {
        var clean = new string(text.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return clean.Length <= max ? clean : clean[..max];
    }
}
