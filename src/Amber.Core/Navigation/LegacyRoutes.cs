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

    /// <summary>
    /// Route segments under library/ that are not instances ("library/new", "library/current"). New instances can't take
    /// these names; an older instance that has one is opened as the instance in focus (<see cref="InstanceRoute"/>).
    /// </summary>
    public static bool IsReservedInstanceSegment(string id) =>
        string.Equals(id, "new", StringComparison.OrdinalIgnoreCase) || string.Equals(id, "current", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The route of an instance's page: library/{id}, or <see cref="CurrentInstance"/> for an instance whose name is a
    /// reserved segment (the caller puts that instance in focus first).
    /// </summary>
    public static string InstanceRoute(string id) => IsReservedInstanceSegment(id) ? CurrentInstance : "library/" + id;

    /// <summary>Logs of the instance in focus (the old log page).</summary>
    public const string Logs = "library/current/logs";

    /// <summary>The Friends page with its add panel open (the old friends card in multiplayer settings).</summary>
    public const string AddFriend = "people/add";

    /// <summary>Settings › Storage, where cleaning up lives (the old toolbox).</summary>
    public const string Tools = "settings#Junk";

    /// <summary>Settings › Multiplayer: invites, what a new room starts with, the component, emergency disconnect.</summary>
    public const string MultiplayerSettings = "settings/multiplayer";

    /// <summary>What a new room starts with (the open-a-room card's "Advanced settings").</summary>
    public const string RoomDefaults = "settings#DefaultRelayOnly";

    /// <summary>Settings › Network: public networks, relays, STUN.</summary>
    public const string NetworkSettings = "settings/network";

    /// <summary>The public networks card in Settings › Network.</summary>
    public const string PublicNetworks = "settings#NetworksCard";

    /// <summary>The relay servers in Settings › Network.</summary>
    public const string RelaySettings = "settings#RelayGroup";

    /// <summary>This device's safety code and "reset identity", under My devices on the Me page.</summary>
    public const string DeviceIdentity = "me#IdentityCard";

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
    /// library/start → library, library/logs → the focused instance's logs, settings/tools → Settings › Storage, and
    /// and the old multiplayer settings tab (play/settings): its friends card (#ContactsCard) → the Friends page's add panel,
    /// the safety code (#IdentityCard) → Me › My devices, a card that is now a setting (#NetworksCard, #RelayGroup, …) →
    /// that setting, anything else → Settings › Multiplayer. Every other route comes back unchanged.
    /// </summary>
    public static Route Resolve(Route route)
    {
        if (route.Section == Section.Library && route.Segments.Count == 2)
        {
            if (route[1] == "start") return Route.Library;
            if (route[1] == "logs") return Route.Parse(Logs);
        }
        if (route.Section == Section.Settings && route[1] == "tools") return Route.Parse(Tools);
        if (route.Section == Section.Play && route[1] == "settings")
        {
            if (route.Fragment == "ContactsCard") return Route.Parse(AddFriend);
            if (route.Fragment == "IdentityCard") return Route.Parse(DeviceIdentity);
            if (route.Fragment == "StunCard") return Route.Parse("settings#Stun");
            if (SettingsCatalog.Find(route.Fragment) is { } field) return Route.Parse("settings#" + field.Field);
            return Route.Parse(MultiplayerSettings);
        }
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
