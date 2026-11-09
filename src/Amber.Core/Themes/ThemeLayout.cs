using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Amber.Core.Themes;

/// <summary>
/// 主题第 3 层：排版和层级。
/// layout/shell.json 决定外壳（导航放哪、多宽、只显示图标、页面顺序、隐藏哪些页）；
/// layout/pages/{页面}.json 是一棵容器树，叶子是 Jay 内置的“区块”（启动按钮、实例卡这些），主题只能决定区块放在哪、怎么嵌套，改不了区块的功能。
/// 每页可以给 compact / medium / wide 三种宽度各写一棵树。
/// </summary>
public static partial class ThemeLayout
{
    public const int MaxNodes = 2000;
    public const int MaxDepth = 12; // 每层在 JSON 里占两层（对象 + children 数组），JSON 本身最多 32 层
    public const int MaxTextLength = 200;

    /// <summary>页面 id，和导航顺序（默认顺序）一致。</summary>
    public static readonly string[] PageIds = ["home", "accounts", "instances", "downloads", "content", "multiplayer", "tools", "settings", "log"];

    /// <summary>不能隐藏的页面：启动页和设置页（设置页里有“恢复默认主题”）。</summary>
    public static readonly string[] RequiredPages = ["home", "settings"];

    /// <summary>
    /// 每页能用的区块，和必须出现的区块。必须出现的区块主题没放，Jay 会自动补在最后（主题藏不掉它们）。
    /// </summary>
    public static readonly IReadOnlyDictionary<string, (string[] All, string[] Required)> Regions = new Dictionary<string, (string[], string[])>
    {
        ["home"] = (["home.title", "home.pending", "home.onboarding", "home.instance", "home.crash", "home.launch"],
                    ["home.pending", "home.onboarding", "home.instance", "home.crash", "home.launch"]),
    };

    public static readonly string[] Breakpoints = ["compact", "medium", "wide"];

    private static readonly HashSet<string> ContainerTypes = ["stack", "grid", "dock", "wrap", "scroll", "card", "panel"];
    private static readonly HashSet<string> LeafTypes = ["region", "spacer", "text", "image"];

    [GeneratedRegex(@"^(\d{1,4}(\.\d+)?|\d{0,3}\*|auto)$", RegexOptions.IgnoreCase)]
    private static partial Regex GridLengthPattern();

    public static void ReadShell(ThemePack pack, JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) { pack.Problems.Add(new("bad_value", "layout/shell.json")); return; }
        var shell = new ShellLayout();
        foreach (var p in root.EnumerateObject())
        {
            var at = "shell." + p.Name;
            switch (p.Name)
            {
                case "nav":
                    if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is "left" or "right" or "top" or "bottom") shell.Nav = p.Value.GetString()!;
                    else pack.Problems.Add(new("bad_value", at));
                    break;
                case "navWidth":
                    if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetDouble(out var w)) shell.NavWidth = Math.Clamp(w, 56, 360);
                    else pack.Problems.Add(new("bad_value", at));
                    break;
                case "navStyle":
                    if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is "full" or "icons") shell.NavStyle = p.Value.GetString()!;
                    else pack.Problems.Add(new("bad_value", at));
                    break;
                case "showBrand":
                    if (p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False) shell.ShowBrand = p.Value.GetBoolean();
                    else pack.Problems.Add(new("bad_value", at));
                    break;
                case "contentMaxWidth":
                    if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetDouble(out var cw)) shell.ContentMaxWidth = Math.Clamp(cw, 640, 4000);
                    else pack.Problems.Add(new("bad_value", at));
                    break;
                case "pages":
                    if (p.Value.ValueKind != JsonValueKind.Array) { pack.Problems.Add(new("bad_value", at)); break; }
                    var order = new List<string>();
                    foreach (var item in p.Value.EnumerateArray())
                    {
                        var id = item.ValueKind == JsonValueKind.String ? item.GetString()! : "";
                        if (!PageIds.Contains(id)) { pack.Problems.Add(new("unknown_page", id)); continue; }
                        if (!order.Contains(id)) order.Add(id);
                    }
                    shell.PageOrder = order;
                    break;
                case "hidden":
                    if (p.Value.ValueKind != JsonValueKind.Array) { pack.Problems.Add(new("bad_value", at)); break; }
                    foreach (var item in p.Value.EnumerateArray())
                    {
                        var id = item.ValueKind == JsonValueKind.String ? item.GetString()! : "";
                        if (!PageIds.Contains(id)) pack.Problems.Add(new("unknown_page", id));
                        else if (RequiredPages.Contains(id)) pack.Problems.Add(new("required_page", id));
                        else shell.Hidden.Add(id);
                    }
                    break;
                default: pack.Problems.Add(new("unknown_key", at)); break;
            }
        }
        // 没写进顺序的页面接在后面（新版本 Jay 加的页面也不会丢）。
        shell.PageOrder = [.. shell.PageOrder, .. PageIds.Where(id => !shell.PageOrder.Contains(id))];
        pack.Shell = shell;
    }

    public static void ReadPage(ThemePack pack, string page, JsonElement root)
    {
        if (!Regions.TryGetValue(page, out var regions)) { pack.Problems.Add(new("page_not_supported_yet", page)); return; }
        if (root.ValueKind != JsonValueKind.Object) { pack.Problems.Add(new("bad_value", $"layout/pages/{page}.json")); return; }
        var layout = new PageLayout();
        // 整个文件就是一棵树（有 "type"），或者按宽度分：{ "wide": 树, "compact": 树, "default": 树 }。
        if (root.TryGetProperty("type", out _))
        {
            if (Build(pack, page, root, regions.All) is { } tree) layout.Trees["default"] = tree;
        }
        else
            foreach (var p in root.EnumerateObject())
            {
                if (p.Name != "default" && !Breakpoints.Contains(p.Name)) { pack.Problems.Add(new("unknown_key", $"{page}.{p.Name}")); continue; }
                if (Build(pack, $"{page}.{p.Name}", p.Value, regions.All) is { } tree) layout.Trees[p.Name] = tree;
            }
        foreach (var (name, tree) in layout.Trees)
            EnsureRequired(pack, $"{page}.{name}", tree, regions.Required);
        if (layout.Trees.Count > 0) pack.Pages[page] = layout;
    }

    /// <summary>某个宽度用哪棵树：先找这一档，再找相邻的档，最后 default。都没有就是 null（用 Jay 自带的布局）。</summary>
    public static LayoutNode? TreeFor(PageLayout layout, string breakpoint)
    {
        string[] order = breakpoint switch
        {
            "compact" => ["compact", "medium", "default", "wide"],
            "medium" => ["medium", "default", "wide", "compact"],
            _ => ["wide", "default", "medium", "compact"],
        };
        foreach (var key in order)
            if (layout.Trees.TryGetValue(key, out var tree)) return tree;
        return null;
    }

    private static LayoutNode? Build(ThemePack pack, string where, JsonElement root, string[] allowedRegions)
    {
        var count = 0;
        var used = new HashSet<string>();
        var tree = Node(pack, where, root, allowedRegions, used, 0, ref count);
        if (tree is not null && !ContainerTypes.Contains(tree.Type))
        {
            var wrapper = new LayoutNode { Type = "stack" };
            wrapper.Children.Add(tree);
            tree = wrapper;
        }
        return tree;
    }

    private static LayoutNode? Node(ThemePack pack, string where, JsonElement e, string[] allowedRegions, HashSet<string> used, int depth, ref int count)
    {
        if (depth > MaxDepth) { pack.Problems.Add(new("layout_too_deep", where)); return null; }
        if (++count > MaxNodes) { if (count == MaxNodes + 1) pack.Problems.Add(new("layout_too_large", where)); return null; }
        // 只写一个字符串就是区块的简写："home.launch"。
        if (e.ValueKind == JsonValueKind.String) return Region(pack, where, e.GetString()!, allowedRegions, used);
        if (e.ValueKind != JsonValueKind.Object) { pack.Problems.Add(new("bad_value", where)); return null; }
        var type = e.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : "";
        if (!ContainerTypes.Contains(type) && !LeafTypes.Contains(type)) { pack.Problems.Add(new("unknown_node", $"{where}: {type}")); return null; }
        LayoutNode node;
        if (type == "region")
        {
            var name = e.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : "";
            if (Region(pack, where, name, allowedRegions, used) is not { } region) return null;
            node = region;
        }
        else node = new LayoutNode { Type = type };

        foreach (var p in e.EnumerateObject())
        {
            var at = $"{where}.{p.Name}";
            var v = p.Value;
            switch (p.Name)
            {
                case "type" or "name": break;
                case "children":
                    if (!ContainerTypes.Contains(type) || v.ValueKind != JsonValueKind.Array) { pack.Problems.Add(new("bad_value", at)); break; }
                    var i = 0;
                    foreach (var child in v.EnumerateArray())
                        if (Node(pack, $"{where}[{i++}]", child, allowedRegions, used, depth + 1, ref count) is { } c) node.Children.Add(c);
                    break;
                case "orientation":
                    node.Horizontal = v.ValueKind == JsonValueKind.String && v.GetString() == "horizontal";
                    break;
                case "spacing": node.Spacing = Num(pack, v, at, 0, 64); break;
                case "columns": node.Columns = Lengths(pack, v, at); break;
                case "rows": node.Rows = Lengths(pack, v, at); break;
                case "column": node.Column = (int?)Num(pack, v, at, 0, 31); break;
                case "row": node.Row = (int?)Num(pack, v, at, 0, 31); break;
                case "columnSpan": node.ColumnSpan = (int?)Num(pack, v, at, 1, 32); break;
                case "rowSpan": node.RowSpan = (int?)Num(pack, v, at, 1, 32); break;
                case "dock":
                    if (v.ValueKind == JsonValueKind.String && v.GetString() is "left" or "right" or "top" or "bottom") node.Dock = v.GetString();
                    else pack.Problems.Add(new("bad_value", at));
                    break;
                case "width": node.Width = Num(pack, v, at, 0, 4000); break;
                case "height": node.Height = Num(pack, v, at, 0, 4000); break;
                case "minWidth": node.MinWidth = Num(pack, v, at, 0, 4000); break;
                case "maxWidth": node.MaxWidth = Num(pack, v, at, 0, 4000); break;
                case "minHeight": node.MinHeight = Num(pack, v, at, 0, 4000); break;
                case "maxHeight": node.MaxHeight = Num(pack, v, at, 0, 4000); break;
                case "margin": node.Margin = Box(pack, v, at); break;
                case "padding": node.Padding = Box(pack, v, at); break;
                case "align":
                    if (v.ValueKind == JsonValueKind.String && v.GetString() is "stretch" or "start" or "center" or "end") node.HAlign = v.GetString();
                    else pack.Problems.Add(new("bad_value", at));
                    break;
                case "valign":
                    if (v.ValueKind == JsonValueKind.String && v.GetString() is "stretch" or "start" or "center" or "end") node.VAlign = v.GetString();
                    else pack.Problems.Add(new("bad_value", at));
                    break;
                case "text":
                    if (type != "text" || v.ValueKind != JsonValueKind.String) { pack.Problems.Add(new("bad_value", at)); break; }
                    var text = new string(v.GetString()!.Where(ch => !char.IsControl(ch) || ch == '\n').ToArray());
                    node.Text = text.Length > MaxTextLength ? text[..MaxTextLength] : text;
                    break;
                case "style":
                    if (type == "text" && v.ValueKind == JsonValueKind.String && v.GetString() is "h1" or "h2" or "hint" or "caption" or "body") node.TextStyle = v.GetString();
                    else pack.Problems.Add(new("bad_value", at));
                    break;
                case "source":
                    if (type != "image" || v.ValueKind != JsonValueKind.String) { pack.Problems.Add(new("bad_value", at)); break; }
                    if (ThemeStyles.ParsePaint("image:" + v.GetString(), pack.Open, allowImage: true) is { ImagePath: { } img }) node.Image = img;
                    else pack.Problems.Add(new("bad_paint", at));
                    break;
                case "hideBelow":
                    // 窄于这个宽度就不显示（装饰性的东西用，区块不能这样藏起来）。
                    if (type == "region") { pack.Problems.Add(new("bad_value", at)); break; }
                    node.HideBelow = Num(pack, v, at, 0, 4000);
                    break;
                default: pack.Problems.Add(new("unknown_key", at)); break;
            }
        }
        if (type == "image" && node.Image is null) return null;
        return node;
    }

    private static LayoutNode? Region(ThemePack pack, string where, string name, string[] allowed, HashSet<string> used)
    {
        if (!allowed.Contains(name)) { pack.Problems.Add(new("unknown_region", $"{where}: {name}")); return null; }
        if (!used.Add(name)) { pack.Problems.Add(new("duplicate_region", $"{where}: {name}")); return null; }
        return new LayoutNode { Type = "region", Region = name };
    }

    /// <summary>必须出现的区块没放进树里：补在根容器最后，并记一条提示。</summary>
    private static void EnsureRequired(ThemePack pack, string where, LayoutNode tree, string[] required)
    {
        var present = new HashSet<string>();
        void collect(LayoutNode n)
        {
            if (n.Region is { } r) present.Add(r);
            foreach (var c in n.Children) collect(c);
        }
        collect(tree);
        var added = new List<LayoutNode>();
        foreach (var r in required.Where(r => !present.Contains(r)))
        {
            var node = new LayoutNode { Type = "region", Region = r };
            added.Add(node);
            tree.Children.Add(node);
            pack.Problems.Add(new("required_region_added", $"{where}: {r}"));
        }
        // 网格根容器：补进来的区块各占新的一行、横跨所有列，不跟已有内容叠在一起。
        if (tree.Type == "grid" && added.Count > 0)
        {
            var rows = tree.Rows?.ToList() ?? ["auto"];
            foreach (var node in added)
            {
                node.Row = rows.Count;
                node.ColumnSpan = Math.Max(1, tree.Columns?.Count ?? 1);
                rows.Add("auto");
            }
            tree.Rows = rows;
        }
    }

    private static double? Num(ThemePack pack, JsonElement v, string at, double min, double max)
    {
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out var d) || !double.IsFinite(d)) { pack.Problems.Add(new("bad_value", at)); return null; }
        return Math.Clamp(d, min, max);
    }

    private static (double L, double T, double R, double B)? Box(ThemePack pack, JsonElement v, string at)
    {
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var all)) { var a = Math.Clamp(all, -64, 128); return (a, a, a, a); }
        if (v.ValueKind == JsonValueKind.Array && v.GetArrayLength() is 2 or 4 && v.EnumerateArray().All(x => x.ValueKind == JsonValueKind.Number))
        {
            var n = v.EnumerateArray().Select(x => Math.Clamp(x.GetDouble(), -64, 128)).ToArray();
            return n.Length == 2 ? (n[0], n[1], n[0], n[1]) : (n[0], n[1], n[2], n[3]);
        }
        pack.Problems.Add(new("bad_value", at));
        return null;
    }

    private static List<string>? Lengths(ThemePack pack, JsonElement v, string at)
    {
        if (v.ValueKind != JsonValueKind.Array || v.GetArrayLength() is 0 or > 32) { pack.Problems.Add(new("bad_value", at)); return null; }
        var list = new List<string>();
        foreach (var item in v.EnumerateArray())
        {
            var s = item.ValueKind == JsonValueKind.Number ? item.GetDouble().ToString(CultureInfo.InvariantCulture) : item.ValueKind == JsonValueKind.String ? item.GetString()! : "";
            if (!GridLengthPattern().IsMatch(s)) { pack.Problems.Add(new("bad_value", at)); return null; }
            list.Add(s.ToLowerInvariant());
        }
        return list;
    }
}

public sealed class ShellLayout
{
    /// <summary>left / right / top / bottom</summary>
    public string Nav { get; set; } = "left";
    public double? NavWidth { get; set; }
    /// <summary>full（图标加文字）/ icons（只有图标，文字放在提示里）</summary>
    public string NavStyle { get; set; } = "full";
    public bool ShowBrand { get; set; } = true;
    public double? ContentMaxWidth { get; set; }
    public List<string> PageOrder { get; set; } = [];
    public List<string> Hidden { get; } = [];
}

public sealed class PageLayout
{
    /// <summary>键是 default / compact / medium / wide。</summary>
    public Dictionary<string, LayoutNode> Trees { get; } = [];
}

public sealed class LayoutNode
{
    /// <summary>容器：stack grid dock wrap scroll card panel；叶子：region spacer text image。</summary>
    public required string Type { get; init; }
    public string? Region { get; init; }
    public List<LayoutNode> Children { get; } = [];
    public bool Horizontal { get; set; }
    public double? Spacing { get; set; }
    public List<string>? Columns { get; set; }
    public List<string>? Rows { get; set; }
    public int? Column { get; set; }
    public int? Row { get; set; }
    public int? ColumnSpan { get; set; }
    public int? RowSpan { get; set; }
    public string? Dock { get; set; }
    public double? Width { get; set; }
    public double? Height { get; set; }
    public double? MinWidth { get; set; }
    public double? MaxWidth { get; set; }
    public double? MinHeight { get; set; }
    public double? MaxHeight { get; set; }
    public (double L, double T, double R, double B)? Margin { get; set; }
    public (double L, double T, double R, double B)? Padding { get; set; }
    public string? HAlign { get; set; }
    public string? VAlign { get; set; }
    public string? Text { get; set; }
    public string? TextStyle { get; set; }
    public string? Image { get; set; }
    public double? HideBelow { get; set; }
}
