using System.Text.Json.Nodes;
using Amber.Core.Game;

namespace Amber.Core.Launch;

/// <summary>Where a start goes once the game is up ("Start and go to…").</summary>
public enum StartInto { Title, World, Server, Room }

/// <summary>
/// Skip the title screen: open one singleplayer world, or join one server. Exactly one of World and Server is set.
/// The game does it itself on 1.20 and later (<c>--quickPlaySingleplayer</c> / <c>--quickPlayMultiplayer</c>), older
/// versions only join servers (<c>--server</c> / <c>--port</c>); the universal loader (bake interface 1.3.3) does
/// both, also singleplayer on Forge 1.12.2.
/// </summary>
public sealed record QuickPlay
{
    public string? World { get; }
    public string? Server { get; }
    /// <summary>Echoed by the loader: "game" (the game's own arguments) or "amber-ul" (the loader opens the world).</summary>
    public string? Via { get; init; }

    private QuickPlay(string? world, string? server)
    {
        World = world;
        Server = server;
    }

    public static QuickPlay ToWorld(string folder) => new(folder, null);
    public static QuickPlay ToServer(string address) => new(null, address);

    /// <summary>The same target, ignoring how it is done.</summary>
    public bool SameTarget(QuickPlay? other) => other is not null && other.World == World && other.Server == Server;

    /// <summary>A world folder name the loader accepts: one path segment, no <c>/ \ :</c> or control characters, not . or ..</summary>
    public static bool IsWorldFolder(string? folder) =>
        !string.IsNullOrEmpty(folder) && folder is not ("." or "..") && !folder.Any(c => c is '/' or '\\' or ':' || char.IsControl(c));

    /// <summary>The request field for bake-instance (interface 1.3.3).</summary>
    public JsonObject ToRequest() => World is { } world ? new JsonObject { ["singleplayer"] = world } : new JsonObject { ["multiplayer"] = Server };

    /// <summary>The loader's echo in the launch block, null when it has none.</summary>
    public static QuickPlay? FromLaunch(JsonNode? node)
    {
        if (node is not JsonObject o) return null;
        var via = o["via"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        if (o["singleplayer"] is JsonValue w && w.TryGetValue<string>(out var world) && world.Length > 0) return ToWorld(world) with { Via = via };
        if (o["multiplayer"] is JsonValue m && m.TryGetValue<string>(out var server) && server.Length > 0) return ToServer(server) with { Via = via };
        return null;
    }

    /// <summary>
    /// Where this start goes. A world or server the start was asked for (a world's [Enter], a room's [Join]) wins;
    /// else the instance's "Start and go to…". Note is a localization key when the choice could not be kept (no room
    /// joined, the world is gone): the start then opens on the title screen.
    /// </summary>
    public static (QuickPlay? Target, string? Note) Choose(string? requestWorld, string? requestServer, InstanceSettings settings, string? roomAddress, string savesDir)
    {
        if (!string.IsNullOrWhiteSpace(requestWorld)) return WorldIn(requestWorld, savesDir);
        if (!string.IsNullOrWhiteSpace(requestServer)) return (ToServer(requestServer.Trim()), null);
        switch (Parse(settings.StartInto, settings.JoinServer))
        {
            case StartInto.World:
                return string.IsNullOrWhiteSpace(settings.StartWorld) ? (null, null) : WorldIn(settings.StartWorld, savesDir);
            case StartInto.Server:
                return string.IsNullOrWhiteSpace(settings.JoinServer) ? (null, null) : (ToServer(settings.JoinServer.Trim()), null);
            case StartInto.Room:
                return roomAddress is { Length: > 0 } room ? (ToServer(room), null) : (null, "quickplay.note.no_room");
            default:
                return (null, null);
        }
    }

    private static (QuickPlay?, string?) WorldIn(string folder, string savesDir) =>
        IsWorldFolder(folder) && File.Exists(Path.Combine(savesDir, folder, "level.dat")) ? (ToWorld(folder), null) : (null, "quickplay.note.no_world");

    /// <summary>The version's own arguments can open a world (1.20 and later list <c>quickPlaySingleplayer</c>).</summary>
    public static bool GameOpensWorlds(VersionJson version) =>
        version.Arguments?.Game.Any(e => e.ToString().Contains("quickPlaySingleplayer", StringComparison.Ordinal)) == true;

    /// <summary>Settings value for <see cref="StartInto"/>; null (never chosen) reads as "the server, if one is set".</summary>
    public static string Key(StartInto into) => into switch
    {
        StartInto.World => "world",
        StartInto.Server => "server",
        StartInto.Room => "room",
        _ => "title",
    };

    public static StartInto Parse(string? key, string? joinServer) => key switch
    {
        "world" => StartInto.World,
        "server" => StartInto.Server,
        "room" => StartInto.Room,
        "title" => StartInto.Title,
        _ => string.IsNullOrWhiteSpace(joinServer) ? StartInto.Title : StartInto.Server,
    };
}
