using Amber.Core.Navigation;

namespace Amber.Core.Help;

/// <summary>One block of an article: a heading, a paragraph or a bullet. Links stay inline as <c>[text](target)</c> spans.</summary>
public enum HelpBlockKind { Heading, Paragraph, Bullet, Shot }

/// <summary>A link inside a block: <c>jay:</c> routes open in Jay, <c>help:</c> opens another article. Nothing goes online.</summary>
public sealed record HelpLink(string Text, string Target)
{
    public bool IsRoute => Target.StartsWith("jay:", StringComparison.OrdinalIgnoreCase);
    public bool IsArticle => Target.StartsWith("help:", StringComparison.OrdinalIgnoreCase);
    public string Value => Target[(Target.IndexOf(':') + 1)..];
}

/// <summary>A block; for <see cref="HelpBlockKind.Shot"/>, <see cref="Shot"/> names the screenshot and <c>Text</c> describes it.</summary>
public sealed record HelpBlock(HelpBlockKind Kind, string Text, IReadOnlyList<HelpLink> Links, string? Shot = null);

/// <summary>
/// A help article (official design §9.7): what this is → how to (mouse first, then the shortcut) → if something goes
/// wrong → related. Written in Markdown with a small front matter: <c>id</c>, <c>category</c>, <c>routes</c>, <c>related</c>.
/// </summary>
public sealed record HelpArticle(string Id, string Category, string Title, string Summary, IReadOnlyList<string> Routes,
    IReadOnlyList<string> Related, IReadOnlyList<HelpBlock> Blocks)
{
    public string PlainText => string.Join("\n", Blocks.Select(b => b.Text));

    /// <summary>Parses the Markdown subset the help articles use.</summary>
    public static HelpArticle Parse(string text, string fallbackId = "")
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var i = 0;
        if (lines.Length > 0 && lines[0].Trim() == "---")
        {
            for (i = 1; i < lines.Length && lines[i].Trim() != "---"; i++)
            {
                var colon = lines[i].IndexOf(':');
                if (colon > 0) meta[lines[i][..colon].Trim()] = lines[i][(colon + 1)..].Trim();
            }
            i++;
        }
        string title = "", summary = "";
        var blocks = new List<HelpBlock>();
        var para = new List<string>();
        void Flush()
        {
            if (para.Count == 0) return;
            var joined = string.Join(" ", para);
            blocks.Add(Block(HelpBlockKind.Paragraph, joined));
            if (summary.Length == 0) summary = Strip(joined);
            para.Clear();
        }
        for (; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd();
            if (line.Trim().Length == 0) { Flush(); continue; }
            if (line.StartsWith("# ", StringComparison.Ordinal)) { Flush(); title = line[2..].Trim(); continue; }
            if (line.StartsWith("## ", StringComparison.Ordinal)) { Flush(); blocks.Add(Block(HelpBlockKind.Heading, line[3..].Trim())); continue; }
            var trimmed = line.TrimStart();
            // A screenshot on its own line: ![what it shows](shot:name). Made by the help-shots self-check, per language.
            if (trimmed.StartsWith("![", StringComparison.Ordinal) && trimmed.IndexOf("](shot:", StringComparison.Ordinal) is var at and > 1 && trimmed.EndsWith(')'))
            {
                Flush();
                blocks.Add(new HelpBlock(HelpBlockKind.Shot, trimmed[2..at], [], trimmed[(at + 7)..^1].Trim()));
                continue;
            }
            if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal))
            {
                Flush();
                blocks.Add(Block(HelpBlockKind.Bullet, trimmed[2..].Trim()));
                continue;
            }
            para.Add(line.Trim());
        }
        Flush();
        return new HelpArticle(
            meta.GetValueOrDefault("id") is { Length: > 0 } id ? id : fallbackId,
            meta.GetValueOrDefault("category") ?? "",
            title.Length > 0 ? title : fallbackId,
            summary,
            List(meta.GetValueOrDefault("routes")),
            List(meta.GetValueOrDefault("related")),
            blocks);
    }

    private static string[] List(string? value) =>
        (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Turns <c>[text](target)</c> into "text" and collects the links.</summary>
    private static HelpBlock Block(HelpBlockKind kind, string source)
    {
        var links = new List<HelpLink>();
        var sb = new System.Text.StringBuilder();
        var pos = 0;
        while (pos < source.Length)
        {
            var open = source.IndexOf('[', pos);
            var mid = open < 0 ? -1 : source.IndexOf("](", open, StringComparison.Ordinal);
            var close = mid < 0 ? -1 : source.IndexOf(')', mid);
            if (open < 0 || mid < 0 || close < 0)
            {
                sb.Append(source, pos, source.Length - pos);
                break;
            }
            sb.Append(source, pos, open - pos);
            var text = source[(open + 1)..mid];
            var target = source[(mid + 2)..close];
            sb.Append(text);
            if (target.StartsWith("jay:", StringComparison.OrdinalIgnoreCase) || target.StartsWith("help:", StringComparison.OrdinalIgnoreCase))
                links.Add(new HelpLink(text, target));
            pos = close + 1;
        }
        return new HelpBlock(kind, sb.ToString().Replace("**", "", StringComparison.Ordinal).Replace("`", "", StringComparison.Ordinal), links);
    }

    private static string Strip(string text) => Block(HelpBlockKind.Paragraph, text).Text;
}

/// <summary>
/// The help articles that ship inside Jay (<c>docs/help/{zh-Hans,zh-Hant,en,ja}/</c>, embedded): read offline, never
/// fetched. Finds the article for a route (F1), searches, and lists them by category for the help center.
/// </summary>
public sealed class HelpLibrary
{
    /// <summary>Category ids in catalog order (§9.7); titles are the localization keys <c>help.category.{id}</c>.</summary>
    public static readonly IReadOnlyList<string> Categories =
        ["start", "play", "content", "worlds", "trouble", "multiplayer", "appearance", "storage", "privacy", "parents", "reference"];

    public static readonly IReadOnlyList<string> Languages = ["zh-Hans", "zh-Hant", "en", "ja"];

    /// <summary>The screenshots the help-shots self-check makes for every language (<c>docs/help/shots/{lang}/{name}.png</c>).</summary>
    public static readonly IReadOnlyList<string> ShotNames = ["library", "multiplayer-lobby", "connection", "drill", "appearance"];

    private readonly Dictionary<string, HelpArticle> _articles;

    public HelpLibrary(IEnumerable<HelpArticle> articles, string language = "en")
    {
        _articles = articles.GroupBy(a => a.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        Language = language;
    }

    /// <summary>The help language (one of <see cref="Languages"/>), for the screenshots.</summary>
    public string Language { get; }

    /// <summary>A screenshot as PNG, in this library's language or else English; null when there is none.</summary>
    public Stream? OpenShot(string name) => OpenShot(Language, name) ?? OpenShot("en", name);

    /// <summary>One language's screenshot (<c>docs/help/shots/{lang}/{name}.png</c>, embedded), or null.</summary>
    public static Stream? OpenShot(string lang, string name)
    {
        if (name.Length == 0 || name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-'))) return null;
        var assembly = typeof(HelpLibrary).Assembly;
        var wanted = $"help/shots/{lang}/{name}.png";
        var resource = assembly.GetManifestResourceNames().FirstOrDefault(n => n.Replace('\\', '/') == wanted);
        return resource is null ? null : assembly.GetManifestResourceStream(resource);
    }

    public IReadOnlyCollection<HelpArticle> All => _articles.Values;

    public HelpArticle? Get(string? id) => id is not null && _articles.TryGetValue(id, out var a) ? a : null;

    /// <summary>Articles in catalog order: by category, then title.</summary>
    public IReadOnlyList<HelpArticle> Catalog =>
        [.. _articles.Values.OrderBy(a => CategoryIndex(a.Category)).ThenBy(a => a.Title, StringComparer.CurrentCulture)];

    private static int CategoryIndex(string category)
    {
        for (var i = 0; i < Categories.Count; i++) if (Categories[i] == category) return i;
        return Categories.Count;
    }

    /// <summary>
    /// The article for a route (F1): an exact route wins over a pattern (<c>library/*</c>), a longer pattern over a
    /// shorter one; a section root ("settings") also matches its sub-routes when nothing closer does.
    /// </summary>
    public HelpArticle? ForRoute(Route route)
    {
        var path = string.Join("/", route.Segments);
        if (path.Length == 0) path = "library";
        HelpArticle? best = null;
        var bestScore = -1;
        foreach (var a in _articles.Values)
            foreach (var pattern in a.Routes)
            {
                var score = Match(pattern, path, route.Segments);
                if (score > bestScore) { best = a; bestScore = score; }
            }
        return best;
    }

    private static int Match(string pattern, string path, IReadOnlyList<string> segments)
    {
        if (string.Equals(pattern, path, StringComparison.OrdinalIgnoreCase)) return 1000 + pattern.Length;
        if (pattern.EndsWith("/*", StringComparison.Ordinal))
        {
            var prefix = pattern[..^2];
            if (path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase)) return 500 + prefix.Length;
        }
        // "library/*/logs": the same number of segments, each one equal or a star. Ranks above the trailing-star
        // prefix ("library/*") and below an exact route.
        if (pattern.Contains("/*/", StringComparison.Ordinal))
        {
            var parts = pattern.Split('/');
            if (parts.Length == segments.Count
                && parts.Select((part, i) => part == "*" || string.Equals(part, segments[i], StringComparison.OrdinalIgnoreCase)).All(ok => ok))
                return 700 + pattern.Length;
        }
        if (segments.Count > 0 && string.Equals(pattern, segments[0], StringComparison.OrdinalIgnoreCase)) return 100;
        return -1;
    }

    /// <summary>Search by title, summary, body and title initials (pinyin first letters for Chinese).</summary>
    public IReadOnlyList<HelpArticle> Search(string? query)
    {
        var q = (query ?? "").Trim();
        if (q.Length == 0) return Catalog;
        return
        [
            .. _articles.Values
                .Select(a => (a, score: Score(a, q)))
                .Where(x => x.score > 0)
                .OrderByDescending(x => x.score)
                .ThenBy(x => x.a.Title, StringComparer.CurrentCulture)
                .Select(x => x.a),
        ];
    }

    private static int Score(HelpArticle a, string q)
    {
        const StringComparison c = StringComparison.CurrentCultureIgnoreCase;
        if (a.Title.Contains(q, c)) return a.Title.StartsWith(q, c) ? 40 : 30;
        if (Pinyin.Initials(a.Title).Contains(q, StringComparison.OrdinalIgnoreCase)) return 25;
        if (a.Summary.Contains(q, c)) return 20;
        return a.PlainText.Contains(q, c) ? 10 : 0;
    }

    /// <summary>
    /// Language code of the help files to use for a UI language. Classical Chinese reads the traditional ones, the same
    /// language its interface falls back to.
    /// </summary>
    public static string HelpLanguage(string uiLanguage) => uiLanguage switch
    {
        "zh-Hant" or "lzh" => "zh-Hant",
        "en" => "en",
        "ja" => "ja",
        _ when uiLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase) => "zh-Hans",
        _ => "en",
    };

    /// <summary>The articles embedded in Amber.Core for a language, falling back to English for any article it lacks.</summary>
    public static HelpLibrary Load(string uiLanguage)
    {
        var lang = HelpLanguage(uiLanguage);
        var mine = LoadLanguage(lang);
        if (lang != "en")
            foreach (var en in LoadLanguage("en"))
                if (!mine.Any(a => string.Equals(a.Id, en.Id, StringComparison.OrdinalIgnoreCase))) mine.Add(en);
        return new HelpLibrary(mine, lang);
    }

    /// <summary>Every article of one language as embedded (tests check that the four languages match).</summary>
    public static List<HelpArticle> LoadLanguage(string lang)
    {
        var assembly = typeof(HelpLibrary).Assembly;
        var list = new List<HelpArticle>();
        foreach (var name in assembly.GetManifestResourceNames())
        {
            var normalized = name.Replace('\\', '/');
            var prefix = "help/" + lang + "/";
            if (!normalized.StartsWith(prefix, StringComparison.Ordinal) || !normalized.EndsWith(".md", StringComparison.Ordinal)) continue;
            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null) continue;
            using var reader = new StreamReader(stream);
            list.Add(HelpArticle.Parse(reader.ReadToEnd(), Path.GetFileNameWithoutExtension(normalized)));
        }
        return list;
    }
}
