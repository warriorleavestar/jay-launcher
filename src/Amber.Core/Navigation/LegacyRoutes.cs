namespace Amber.Core.Navigation;

/// <summary>
/// Where the nine pages of the old launcher live in the shell (official design §2.4, §11.9). The command palette,
/// format 1 theme packs and old tutorials find their way through this table.
/// The library has one path: library → library/{instance} → its tabs. The old launch page, the standalone log page and
/// the toolbox are gone; their routes resolve to where each thing lives now.
/// </summary>
public static class LegacyRoutes
{
    /// <summary>The new-instance view (the old download page).</summary>
    public const string NewInstance = "library/new";

    /// <summary>The instance in focus, when a route cannot name one.</summary>
    public const string CurrentInstance = "library/current";

    /// <summary>Logs of the instance in focus (the old log page).</summary>
    public const string Logs = "library/current/logs";

    /// <summary>Settings › Storage, where cleaning up lives (the old toolbox).</summary>
    public const string Tools = "settings#Junk";

    /// <summary>The route for an old page id (the nine in ThemeLayout.PageIds). Null when unknown.</summary>
    public static Route? For(string pageId) => pageId switch
    {
        "home" => Route.Library,
        "instances" => Route.Parse(CurrentInstance),
        "downloads" => Route.Parse(NewInstance),
        "log" => Route.Parse(Logs),
        "content" => Route.Parse("discover"),
        "multiplayer" => Route.Parse("play"),
        "tools" => Route.Parse(Tools),
        "settings" => Route.Parse("settings"),
        "accounts" => Route.Parse("me"),
        _ => null,
    };

    /// <summary>
    /// Routes that pointed at a removed page (saved in help links, tips or history) go to the place that replaced it:
    /// library/start → library, library/logs → the focused instance's logs, settings/tools → Settings › Storage.
    /// Every other route comes back unchanged.
    /// </summary>
    public static Route Resolve(Route route)
    {
        if (route.Section == Section.Library && route.Segments.Count == 2)
        {
            if (route[1] == "start") return Route.Library;
            if (route[1] == "logs") return Route.Parse(Logs);
        }
        if (route.Section == Section.Settings && route[1] == "tools") return Route.Parse(Tools);
        return route;
    }

    /// <summary>
    /// The sidebar section an old page id belongs to (upgrading pages / hidden in a format 1 shell.json, §10.8).
    /// Accounts ("Me"), tools and settings are not navigation items: null.
    /// </summary>
    public static string? NavSectionFor(string pageId) => pageId switch
    {
        "home" or "instances" or "log" or "library" => "library",
        "downloads" or "content" or "discover" => "discover",
        "multiplayer" => "multiplayer",
        "people" => "people",
        _ => null,
    };
}
