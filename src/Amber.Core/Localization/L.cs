using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace Amber.Core.Localization;

/// <summary>
/// UI text translations. One JSON file per language (key → text), embedded in the assembly:
///   Localization/en.json (the source; every key is defined here), zh-Hans.json, zh-Hant.json, ja.json.
/// A key missing from the current language falls back to English, then to the key itself (so gaps are easy to spot).
/// The UI language follows the system language; when the system language has no translation, English is used.
/// 用法：C# 里 L.T("home.launch")、L.F("home.running", id, memory)（{0}{1} 占位）、L.P("…", n, …)（区分单复数）；
/// XAML 里 {l:T home.launch}。换语言时触发 Changed，界面上用 {l:T} 绑定的文字立刻跟着换。
/// </summary>
public static class L
{
    /// <summary>Source and fallback language: new keys are written in en.json first.</summary>
    public const string Source = "en";

    /// <summary>支持的语言（代码, 用这种语言写的名字）。</summary>
    public static readonly IReadOnlyList<(string Code, string Name)> Languages =
    [
        ("en", "English"),
        ("zh-Hans", "简体中文"),
        ("zh-Hant", "繁體中文"),
        ("ja", "日本語"),
        ("lzh", "文言"),
        ("en-x-ud", "ɥsᴉꞁᵷuƎ uʍop ǝpᴉsd∩"),
    ];

    /// <summary>
    /// Playful languages, the same idea as Minecraft's own 文言 and "English (Upside Down)". No operating system uses them,
    /// so they are never picked by following the system language; players choose them in Settings.
    /// </summary>
    public static readonly IReadOnlySet<string> Playful = new HashSet<string>(StringComparer.Ordinal) { "lzh", "en-x-ud" };

    /// <summary>A language whose missing keys come from another translation before English (文言 falls back to Traditional Chinese).</summary>
    private static readonly Dictionary<string, string> Parents = new() { ["lzh"] = "zh-Hant" };

    /// <summary>Minecraft's language code for each Jay language, so the game can match the launcher.</summary>
    public static readonly IReadOnlyDictionary<string, string> MinecraftCodes = new Dictionary<string, string>
    {
        ["en"] = "en_us", ["zh-Hans"] = "zh_cn", ["zh-Hant"] = "zh_tw", ["ja"] = "ja_jp", ["lzh"] = "lzh", ["en-x-ud"] = "en_ud",
    };

    /// <summary>每种语言格式化数字、日期用的区域（不跟 Windows 的区域设置走，界面是哪种语言就按哪种写）。</summary>
    private static readonly Dictionary<string, CultureInfo> Cultures = new()
    {
        ["zh-Hans"] = CultureInfo.GetCultureInfo("zh-CN"),
        ["zh-Hant"] = CultureInfo.GetCultureInfo("zh-TW"),
        ["en"] = CultureInfo.GetCultureInfo("en-US"),
        ["ja"] = CultureInfo.GetCultureInfo("ja-JP"),
        ["lzh"] = CultureInfo.GetCultureInfo("zh-TW"),
        ["en-x-ud"] = CultureInfo.GetCultureInfo("en-US"),
    };

    /// <summary>当前语言的全部状态放在一个不可变对象里，换语言时整体替换，别的线程读到的总是一致的一份。</summary>
    private sealed record State(string Language, IReadOnlyDictionary<string, string> Table, CultureInfo Culture);

    private static readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> Tables = new();
    private static readonly IReadOnlyDictionary<string, string> Fallback = Load(Source);
    private static volatile State _state = new(Source, Fallback, Cultures[Source]);

    public static string Language => _state.Language;

    /// <summary>当前语言格式化数字用的区域。</summary>
    public static CultureInfo Culture => _state.Culture;

    /// <summary>换了语言。可能在任何线程上触发，界面那边自己切到界面线程。</summary>
    public static event Action? Changed;

    /// <summary>按系统语言选：沿区域的上级链找支持的语言（zh-TW、zh-HK、zh-MO → 繁体；zh-CN、zh-SG、zh-Hans-HK → 简体）；都不是时用英语。</summary>
    public static string FromCulture(CultureInfo culture) => Match(culture) ?? "en";

    /// <summary>沿上级链找支持的语言，找不到返回 null。</summary>
    private static string? Match(CultureInfo culture)
    {
        for (var c = culture; !string.IsNullOrEmpty(c.Name); c = c.Parent)
        {
            foreach (var (code, _) in Languages)
                if (!Playful.Contains(code) && string.Equals(c.Name, code, StringComparison.OrdinalIgnoreCase)) return code;
            // 旧写法：zh-CHT / zh-CHS。
            if (string.Equals(c.Name, "zh-CHT", StringComparison.OrdinalIgnoreCase)) return "zh-Hant";
            if (string.Equals(c.Name, "zh-CHS", StringComparison.OrdinalIgnoreCase) || string.Equals(c.Name, "zh", StringComparison.OrdinalIgnoreCase)) return "zh-Hans";
            if (ReferenceEquals(c, c.Parent)) break;
        }
        return null;
    }

    /// <summary>
    /// 设置里存的语言代码换成支持的语言：大小写、地区写法（en-US、zh-CN）都认；null、"system"、认不出的代码返回 null（跟随系统）。
    /// </summary>
    public static string? Resolve(string? code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Equals("system", StringComparison.OrdinalIgnoreCase)) return null;
        // Codes Jay knows exactly, including the playful ones that aren't real cultures (en-x-ud).
        foreach (var (known, _) in Languages)
            if (string.Equals(code.Trim(), known, StringComparison.OrdinalIgnoreCase)) return known;
        try
        {
            return Match(CultureInfo.GetCultureInfo(code.Trim()));
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
    }

    /// <summary>设置语言；null、"system" 或认不出的代码表示跟随系统。</summary>
    public static void SetLanguage(string? code)
    {
        var resolved = Resolve(code) ?? FromCulture(CultureInfo.CurrentUICulture);
        _state = new State(resolved, TableFor(resolved), Cultures[resolved]);
        Changed?.Invoke();
    }

    /// <summary>The language's own table, with its parent's text under it for keys it doesn't have yet.</summary>
    internal static IReadOnlyDictionary<string, string> TableFor(string code)
    {
        var own = Load(code);
        if (!Parents.TryGetValue(code, out var parent)) return own;
        var merged = new Dictionary<string, string>(TableFor(parent), StringComparer.Ordinal);
        foreach (var (k, v) in own) merged[k] = v;
        return merged;
    }

    /// <summary>"English (upside down)" isn't a file: it is generated from en.json, so it never falls behind.</summary>
    private const string UpsideDown = "en-x-ud";

    private const string FlipFrom = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789.,'\"!?()[]{}<>_&;";
    private const string FlipTo = "ɐqɔpǝɟᵷɥᴉɾʞꞁɯuodbɹsʇnʌʍxʎz∀ᗺƆᗡƎℲ⅁HIſʞ˥WNOԀΌᴚS⊥∩ΛMX⅄Z0ƖᄅƐㄣϛ9ㄥ86˙',„¡¿)(][}{><‾⅋؛";

    /// <summary>
    /// Turns each line upside down: characters are mirrored and the order is reversed, like Minecraft's "English (Upside Down)".
    /// Placeholders such as {0} or {1:F1} are kept as they are, only moved, so formatting still works.
    /// </summary>
    internal static string FlipUpsideDown(string text) => string.Join('\n', text.Split('\n').Select(FlipLine));

    private static string FlipLine(string line)
    {
        var parts = new List<string>();
        var start = 0;
        foreach (System.Text.RegularExpressions.Match m in PlaceholderPattern.Matches(line))
        {
            parts.Add(FlipPlain(line[start..m.Index]));
            parts.Add(m.Value);
            start = m.Index + m.Length;
        }
        parts.Add(FlipPlain(line[start..]));
        parts.Reverse();
        return string.Concat(parts);
    }

    private static string FlipPlain(string text)
    {
        var chars = new System.Text.StringBuilder(text.Length);
        for (var i = text.Length - 1; i >= 0; i--)
        {
            var c = text[i];
            if (char.IsLowSurrogate(c) && i > 0 && char.IsHighSurrogate(text[i - 1]))
            {
                chars.Append(text[i - 1]).Append(c);
                i--;
                continue;
            }
            var at = FlipFrom.IndexOf(c);
            chars.Append(at < 0 ? c : FlipTo[at]);
        }
        return chars.ToString();
    }

    private static readonly System.Text.RegularExpressions.Regex PlaceholderPattern = new(@"\{\d+(?::[^}]*)?\}", System.Text.RegularExpressions.RegexOptions.Compiled);

    public static string T(string key) => Lookup(_state.Table, key);

    private static string Lookup(IReadOnlyDictionary<string, string> table, string key)
    {
        var text = table.TryGetValue(key, out var found) ? found : FallbackText(key) ?? key;
        return Pseudo ? PseudoLocalize(text) : text;
    }

    /// <summary>
    /// 伪本地化（开发用，启动参数 --pseudo-loc 或环境变量 AMBER_PSEUDO_LOC=1）：每段文字加长 40% 并加上括号，
    /// 用来检查长译文会不会被切掉、有没有漏掉没走翻译的文字。占位符原样保留。
    /// </summary>
    public static bool Pseudo { get; set; }

    internal static string PseudoLocalize(string text)
    {
        if (text.Length == 0) return text;
        var padding = (int)Math.Ceiling(text.Length * 0.4);
        return "⟦" + text + new string('·', padding) + "⟧";
    }

    /// <summary>English source text; also covers plural variants (key.one / key.other) via the base key.</summary>
    private static string? FallbackText(string key) =>
        Fallback.TryGetValue(key, out var source) ? source
        : PluralBaseKey(key) is { } b && Fallback.TryGetValue(b, out var baseText) ? baseText
        : null;

    private static string? PluralBaseKey(string key) =>
        key.EndsWith(".one", StringComparison.Ordinal) ? key[..^4] : key.EndsWith(".other", StringComparison.Ordinal) ? key[..^6] : null;

    /// <summary>按当前语言的区域格式化。译文的占位符写错了（格式异常）时先用英文原文，再不行就直接显示文字，不让界面崩掉。</summary>
    public static string F(string key, params object?[] args) => Format(_state, key, args);

    private static string Format(State state, string key, object?[] args)
    {
        var text = Lookup(state.Table, key);
        try
        {
            return string.Format(state.Culture, text, args);
        }
        catch (FormatException)
        {
            try
            {
                return FallbackText(key) is { } source ? string.Format(state.Culture, source, args) : text;
            }
            catch (FormatException)
            {
                return text;
            }
        }
    }

    /// <summary>
    /// 区分单复数：count 是 1 时优先用 key.one，否则用 key.other，都没有就用 key（中文、日文只有一种写法）。
    /// args 照常传给 F（count 本身要显示的话也放在 args 里）。
    /// </summary>
    public static string P(string key, long count, params object?[] args)
    {
        var state = _state;
        return Format(state, PluralKey(state.Table, key, count), args);
    }

    /// <summary>在这张表里，count 对应用哪个键。</summary>
    internal static string PluralKey(IReadOnlyDictionary<string, string> table, string key, long count) =>
        count == 1 && table.ContainsKey(key + ".one") ? key + ".one"
        : table.ContainsKey(key + ".other") ? key + ".other"
        : key;

    /// <summary>某种语言的全部文字（检查漏翻用）。文件坏了（JSON 格式不对）时返回空表，界面退回英文原文，不会打不开。</summary>
    public static IReadOnlyDictionary<string, string> Load(string code) => Tables.GetOrAdd(code, static c =>
    {
        if (c == UpsideDown) return Load(Source).ToDictionary(p => p.Key, p => FlipUpsideDown(p.Value), StringComparer.Ordinal);
        try
        {
            using var stream = typeof(L).Assembly.GetManifestResourceStream($"Amber.Core.Localization.{c}.json");
            if (stream is null) return new Dictionary<string, string>();
            using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var table = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var p in doc.RootElement.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String) table[p.Name] = p.Value.GetString()!;
            return table;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            return new Dictionary<string, string>();
        }
    });
}
