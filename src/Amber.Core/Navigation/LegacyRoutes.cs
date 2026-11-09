namespace Amber.Core.Navigation;

/// <summary>
/// 旧版九个页面在新外壳里的位置（官方设计 §2.4、§11.9）。命令面板、主题包 format 1 的页面名、旧教程都靠它找到新家。
/// 过渡期的路由：library/new 是新建实例（旧下载页），library/logs 是运行记录（旧日志页），settings/tools 是工具（旧工具箱）。
/// </summary>
public static class LegacyRoutes
{
    public const string NewInstance = "library/new";
    public const string Logs = "library/logs";
    public const string Tools = "settings/tools";

    /// <summary>旧页面 id（ThemeLayout.PageIds 里那九个）对应的路由。认不出的是 null。</summary>
    public static Route? For(string pageId) => pageId switch
    {
        "home" => Route.Library,
        "instances" => Route.Parse("library/current"),
        "downloads" => Route.Parse(NewInstance),
        "log" => Route.Parse(Logs),
        "content" => Route.Parse("discover"),
        "multiplayer" => Route.Parse("play"),
        "tools" => Route.Parse(Tools),
        "settings" => Route.Parse("settings"),
        "accounts" => Route.Parse("accounts"),
        _ => null,
    };

    /// <summary>
    /// 旧页面 id 在侧栏上归哪个入口（主题包 shell.json format 1 的 pages / hidden 升级用，§10.8）。
    /// 账号、工具、设置不再是导航项，返回 null。
    /// </summary>
    public static string? NavSectionFor(string pageId) => pageId switch
    {
        "home" or "instances" or "log" or "library" => "library",
        "downloads" or "content" or "discover" => "discover",
        "multiplayer" => "multiplayer",
        _ => null,
    };
}
