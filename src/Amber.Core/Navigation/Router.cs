namespace Amber.Core.Navigation;

/// <summary>怎么到的这一处：普通跳转、返回、前进、切换入口。</summary>
public enum NavigationKind { Navigate, Back, Forward, Section, Return }

/// <summary>
/// 带上下文的跳转（官方设计 §2.5 第 3 条）：从 A 去 B 办事，B 记着从哪来、顶栏显示什么，办完【完成，返回】回到 A。
/// </summary>
public sealed record ReturnContext(Route To, string? Label = null);

/// <summary>
/// 路由和历史栈（官方设计 §2.5、§11.5）。每个一级入口有自己的历史：切走再切回，回到离开时的位置。
/// 返回、前进只在当前入口的历史里走。不碰界面，App 和单元测试共用。
/// </summary>
public sealed class Router
{
    private sealed class History
    {
        public readonly List<(Route Route, ReturnContext? Return)> Entries = [];
        public int Index = -1;
    }

    private readonly Dictionary<Section, History> _histories = [];

    /// <summary>每个入口最多记多少步，再多就丢掉最早的。</summary>
    public const int MaxDepth = 50;

    public Router(Route? start = null)
    {
        start ??= Route.Library;
        Push(start, null);
        Current = start;
    }

    public Route Current { get; private set; }

    public Section CurrentSection => Current.Section;

    /// <summary>当前这一处的返回上下文（不是带上下文跳过来的是 null）。</summary>
    public ReturnContext? CurrentReturn => _histories[CurrentSection] is var h && h.Index >= 0 ? h.Entries[h.Index].Return : null;

    /// <summary>上一次带结果返回时要高亮的东西（比如刚装好的模组 id），取走一次就清掉。</summary>
    public string? PendingHighlight { get; private set; }

    public string? TakeHighlight()
    {
        var h = PendingHighlight;
        PendingHighlight = null;
        return h;
    }

    public event Action<Route, NavigationKind>? Navigated;

    public bool CanGoBack => _histories[CurrentSection].Index > 0;

    public bool CanGoForward => _histories[CurrentSection] is var h && h.Index < h.Entries.Count - 1;

    /// <summary>某个入口上次停在哪（没去过是入口首页）。</summary>
    public Route LastIn(Section section) =>
        _histories.TryGetValue(section, out var h) && h.Index >= 0 ? h.Entries[h.Index].Route : Route.Parse(Route.RootOf(section));

    /// <summary>Applied to every route before navigating: the shell sends routes of removed pages to their new place.</summary>
    public Func<Route, Route>? Rewrite { get; set; }

    /// <summary>去一处。和当前一样（不算 #字段）时只更新字段，不加历史。</summary>
    public void Navigate(Route route, ReturnContext? returnTo = null)
    {
        if (Rewrite is { } rewrite) route = rewrite(route);
        var kind = route.Section == CurrentSection ? NavigationKind.Navigate : NavigationKind.Section;
        var h = HistoryOf(route.Section);
        if (h.Index >= 0 && h.Entries[h.Index].Route.WithoutFragment.Equals(route.WithoutFragment))
            h.Entries[h.Index] = (route, returnTo ?? h.Entries[h.Index].Return);
        else
            Push(route, returnTo);
        Set(route, kind);
    }

    public void Navigate(string route, ReturnContext? returnTo = null) => Navigate(Route.Parse(route), returnTo);

    /// <summary>切到一个入口：回到它上次停的地方。已经在这个入口时回到入口首页。</summary>
    public void SwitchTo(Section section)
    {
        if (section == CurrentSection)
        {
            var root = Route.Parse(Route.RootOf(section));
            if (!Current.WithoutFragment.Equals(root)) Navigate(root);
            return;
        }
        HistoryOf(section);
        Set(LastIn(section), NavigationKind.Section);
    }

    public bool Back()
    {
        var h = _histories[CurrentSection];
        if (h.Index <= 0) return false;
        h.Index--;
        Set(h.Entries[h.Index].Route, NavigationKind.Back);
        return true;
    }

    public bool Forward()
    {
        var h = _histories[CurrentSection];
        if (h.Index >= h.Entries.Count - 1) return false;
        h.Index++;
        Set(h.Entries[h.Index].Route, NavigationKind.Forward);
        return true;
    }

    /// <summary>
    /// 办完事返回（【完成，返回】）：回到带上下文跳过来之前的地方，highlight 是回去后要高亮的东西。
    /// 没有返回上下文时等于 Back()。
    /// </summary>
    public bool Return(string? highlight = null)
    {
        if (CurrentReturn is not { } ctx) return Back();
        PendingHighlight = highlight;
        var target = ctx.To;
        var h = HistoryOf(target.Section);
        // 回到原来那一处：它就在目标入口历史里的话直接退回去，免得历史越走越长。
        var at = h.Entries.FindLastIndex(e => e.Route.WithoutFragment.Equals(target.WithoutFragment));
        if (target.Section == CurrentSection && at >= 0 && at < h.Index) h.Index = at;
        else if (at >= 0 && target.Section != CurrentSection) h.Index = at;
        else Push(target, null);
        Set(h.Entries[h.Index].Route, NavigationKind.Return);
        return true;
    }

    private History HistoryOf(Section section)
    {
        if (!_histories.TryGetValue(section, out var h))
        {
            h = new History();
            h.Entries.Add((Route.Parse(Route.RootOf(section)), null));
            h.Index = 0;
            _histories[section] = h;
        }
        return h;
    }

    private void Push(Route route, ReturnContext? returnTo)
    {
        if (!_histories.TryGetValue(route.Section, out var h))
        {
            h = new History();
            _histories[route.Section] = h;
        }
        if (h.Index < h.Entries.Count - 1) h.Entries.RemoveRange(h.Index + 1, h.Entries.Count - h.Index - 1);
        h.Entries.Add((route, returnTo));
        if (h.Entries.Count > MaxDepth) h.Entries.RemoveAt(0);
        h.Index = h.Entries.Count - 1;
    }

    private void Set(Route route, NavigationKind kind)
    {
        Current = route;
        Navigated?.Invoke(route, kind);
    }
}
