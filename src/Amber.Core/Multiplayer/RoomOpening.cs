using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Multiplayer;

/// <summary>Who can come into a room the player opens.</summary>
public enum RoomAccess
{
    /// <summary>Only people holding an invite the host made.</summary>
    Invited,
    /// <summary>Verified friends get an invite of their own when the room opens and can knock.</summary>
    Friends,
    /// <summary>Listed on the connected network, with or without a password.</summary>
    Public,
}

/// <summary>How the people in a room reach each other.</summary>
public enum RoomRoute
{
    /// <summary>Introduced through the network; direct when possible, through relays otherwise.</summary>
    Auto,
    /// <summary>Always through relays: members do not see each other's IP address.</summary>
    RelayOnly,
    /// <summary>No server at all: a sealed direct code and a passphrase told another way.</summary>
    Serverless,
}

/// <summary>How much the room's traffic is protected on top of the network's own encryption.</summary>
public enum RoomProtection
{
    Standard,
    Stronger,
    Highest,
    /// <summary>The transport was set by hand to something that is none of the three levels.</summary>
    Custom,
}

/// <summary>What the room carries.</summary>
public enum RoomScope
{
    /// <summary>The one game the host opened.</summary>
    Game,
    /// <summary>A virtual LAN for every program (needs administrator rights).</summary>
    WholeLan,
}

/// <summary>What the lobby knows when it decides which choices can be picked.</summary>
public sealed record RoomOpeningContext(bool ChildMode, bool NetworkConnected, int VerifiedFriends, bool FullLanAvailable, bool HybridSupported);

/// <summary>
/// "Opening a room asks four things" (UI restructure: people, rooms, networks): who can come in, how people connect,
/// protection and scope. This maps the four choices onto what a room is made of, and says why a choice cannot be
/// picked right now. Pure logic; the view model applies it.
/// </summary>
public static class RoomOpening
{
    /// <summary>The transport of a protection level; null for <see cref="RoomProtection.Custom"/> (keep what is set).</summary>
    public static TunnelSettings? Tunnel(RoomProtection level) => level switch
    {
        RoomProtection.Standard => new TunnelSettings(TransportMode.Minecraft, false),
        RoomProtection.Stronger => new TunnelSettings(TransportMode.High, false),
        RoomProtection.Highest => new TunnelSettings(TransportMode.Extreme, true),
        _ => null,
    };

    /// <summary>The level a transport setting amounts to.</summary>
    public static RoomProtection ProtectionOf(TunnelSettings tunnel)
    {
        foreach (var level in (RoomProtection[])[RoomProtection.Standard, RoomProtection.Stronger, RoomProtection.Highest])
            if (Tunnel(level) == tunnel) return level;
        return RoomProtection.Custom;
    }

    /// <summary>
    /// Why this "who can come in" choice cannot be picked now, as a localization key; null when it can.
    /// </summary>
    public static string? Blocked(RoomAccess access, RoomRoute route, RoomOpeningContext context) => access switch
    {
        RoomAccess.Invited => null,
        // "For a child" is invite only.
        _ when context.ChildMode => "multiplayer.open.blocked.child",
        // Friends and the public list are reached through a network; a serverless room uses none.
        _ when route == RoomRoute.Serverless => "multiplayer.open.blocked.serverless",
        _ when !context.NetworkConnected => "multiplayer.open.blocked.network",
        RoomAccess.Friends when context.VerifiedFriends == 0 => "multiplayer.open.blocked.no_friends",
        _ => null,
    };

    /// <summary>Why this protection level cannot be picked now; null when it can.</summary>
    public static string? Blocked(RoomProtection level, RoomRoute route, RoomOpeningContext context) => level switch
    {
        // A serverless room is protected by its own direct link, not by the room transport.
        _ when route == RoomRoute.Serverless => "multiplayer.open.blocked.protection_serverless",
        RoomProtection.Highest when !context.HybridSupported => "multiplayer.open.blocked.highest",
        _ => null,
    };

    /// <summary>Why this scope cannot be picked now; null when it can.</summary>
    public static string? Blocked(RoomScope scope, RoomOpeningContext context) =>
        scope == RoomScope.WholeLan && !context.FullLanAvailable ? "multiplayer.open.blocked.whole_lan" : null;

    /// <summary>
    /// The choice to fall back to when the selected one became unavailable (the network dropped, "for a child" was
    /// turned on, the route changed to serverless).
    /// </summary>
    public static RoomAccess Usable(RoomAccess wanted, RoomRoute route, RoomOpeningContext context) =>
        Blocked(wanted, route, context) is null ? wanted : RoomAccess.Invited;

    public static RoomProtection Usable(RoomProtection wanted, RoomRoute route, RoomOpeningContext context) =>
        wanted == RoomProtection.Highest && Blocked(wanted, RoomRoute.Auto, context) is not null ? RoomProtection.Stronger : wanted;

    public static RoomScope Usable(RoomScope wanted, RoomOpeningContext context) =>
        Blocked(wanted, context) is null ? wanted : RoomScope.Game;
}
