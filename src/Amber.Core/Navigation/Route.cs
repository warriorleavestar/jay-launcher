namespace Amber.Core.Navigation;

/// <summary>
/// The top-level places. The sidebar lists the first four; settings, "Me" (the avatar), about and help sit at its foot.
/// </summary>
public enum Section { Library, Discover, Play, People, Settings, Me, About, Help }

/// <summary>
/// 界面路由（官方设计 §2.2、§11.5）：一串用 / 分开的段，可以带 ?查询 和 #字段。
/// 例：library、library/{实例}/mods、discover/mod?target={实例}、play/servers、people/{朋友}、me#FriendCode、settings/general#motion。
/// 路由只在 Jay 里面用，不注册系统协议。
/// </summary>
public sealed record Route
{
    public IReadOnlyList<string> Segments { get; }
    public IReadOnlyDictionary<string, string> Query { get; }
    public string? Fragment { get; }

    private Route(IReadOnlyList<string> segments, IReadOnlyDictionary<string, string> query, string? fragment)
    {
        Segments = segments;
        Query = query;
        Fragment = fragment;
    }

    public static readonly Route Library = Parse("library");

    public Section Section => SectionOf(Segments.Count > 0 ? Segments[0] : "");

    /// <summary>第 i 段（从 0 数，第 0 段是入口名），没有时是 null。</summary>
    public string? this[int i] => i < Segments.Count ? Segments[i] : null;

    /// <summary>不带 #字段 的路由。返回、前进按它判断是不是同一处。</summary>
    public Route WithoutFragment => Fragment is null ? this : new(Segments, Query, null);

    public static string RootOf(Section s) => s switch
    {
        Section.Discover => "discover",
        Section.Play => "play",
        Section.People => "people",
        Section.Settings => "settings",
        Section.Me => "me",
        Section.About => "about",
        Section.Help => "help",
        _ => "library",
    };

    private static Section SectionOf(string head) => head switch
    {
        "discover" => Section.Discover,
        "play" => Section.Play,
        "people" => Section.People,
        "settings" => Section.Settings,
        "me" => Section.Me,
        "about" => Section.About,
        "help" => Section.Help,
        _ => Section.Library,
    };

    /// <summary>认不出的入口名当成游戏库；空段、多余的斜杠去掉。段和查询值按 URL 编码解码。</summary>
    public static Route Parse(string? text)
    {
        text = (text ?? "").Trim();
        if (text.StartsWith("jay:", StringComparison.OrdinalIgnoreCase)) text = text[4..];
        string? fragment = null;
        var hash = text.IndexOf('#');
        if (hash >= 0)
        {
            fragment = Uri.UnescapeDataString(text[(hash + 1)..]);
            if (fragment.Length == 0) fragment = null;
            text = text[..hash];
        }
        var query = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var q = text.IndexOf('?');
        if (q >= 0)
        {
            foreach (var pair in text[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                var key = Uri.UnescapeDataString(eq < 0 ? pair : pair[..eq]);
                if (key.Length > 0) query[key] = eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..]);
            }
            text = text[..q];
        }
        var segments = text.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Uri.UnescapeDataString).ToList();
        // The accounts page became "Me": routes saved under its old name still open it.
        if (segments.Count > 0 && segments[0] == "accounts") segments[0] = "me";
        if (segments.Count == 0 || RootOf(SectionOf(segments[0])) != segments[0]) segments.Insert(0, "library");
        return new Route(segments, query, fragment);
    }

    public static Route Of(Section section, params string[] rest) =>
        Parse(string.Join('/', new[] { RootOf(section) }.Concat(rest.Select(Uri.EscapeDataString))));

    public Route With(string key, string value)
    {
        var query = new SortedDictionary<string, string>(Query.ToDictionary(), StringComparer.Ordinal) { [key] = value };
        return new Route(Segments, query, Fragment);
    }

    public override string ToString()
    {
        var s = string.Join('/', Segments.Select(Uri.EscapeDataString));
        if (Query.Count > 0) s += "?" + string.Join('&', Query.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value)));
        if (Fragment is not null) s += "#" + Uri.EscapeDataString(Fragment);
        return s;
    }

    public bool Equals(Route? other) => other is not null && ToString() == other.ToString();

    public override int GetHashCode() => ToString().GetHashCode(StringComparison.Ordinal);
}
