namespace Amber.Core.Multiplayer.Public;

/// <summary>What a network can do for the player. The network panel writes these out for each network.</summary>
[Flags]
public enum NetworkAbility
{
    None = 0,
    /// <summary>Carry an invited room (introduce the two sides, relay when they cannot connect directly).</summary>
    Invites = 1,
    /// <summary>List public rooms.</summary>
    PublicRooms = 2,
    /// <summary>Deliver messages to friends (friend requests, invites sent to a friend).</summary>
    Messages = 4,
}

public enum NetworkTrust { BuiltIn, Reviewed, TestReviewed, Unreviewed }

public enum NetworkUse { Off, Connecting, On }

/// <summary>A network the player has, as the settings know it.</summary>
public sealed record NetworkSource(string Id, string Name, bool BuiltIn, bool Reviewed, bool TestReviewed, int Nodes);

/// <summary>One row of the network panel.</summary>
public sealed record NetworkEntry(string Id, string Name, NetworkTrust Trust, NetworkAbility Abilities, NetworkUse Use, bool CanSwitch)
{
    public bool IsBaseRelays => Id == NetworkOverview.BaseRelaysId;
}

/// <summary>
/// The networks behind the top bar's network status and its panel: the built-in relays ("basic relays", invites only)
/// and the public networks the player has. Pure: the app passes in what it knows and shows what comes back.
/// </summary>
public static class NetworkOverview
{
    public const string BaseRelaysId = "relays";

    /// <summary>
    /// The rows, basic relays first. <paramref name="inRoom"/>: a room is open or joined, so the relays are in use.
    /// <paramref name="inviteOnly"/> ("for a child"): only the basic relays are listed. A built-in public network
    /// without servers is left out: there is nothing to connect to.
    /// </summary>
    public static IReadOnlyList<NetworkEntry> Build(IEnumerable<NetworkSource> networks, string? currentId, bool connected, bool connecting,
        bool inRoom, bool inviteOnly)
    {
        var rows = new List<NetworkEntry>
        {
            new(BaseRelaysId, "", NetworkTrust.BuiltIn, NetworkAbility.Invites, inRoom ? NetworkUse.On : NetworkUse.Off, CanSwitch: false),
        };
        if (inviteOnly) return rows;
        foreach (var n in networks)
        {
            if (n.BuiltIn && n.Nodes == 0) continue;
            var use = n.Id != currentId ? NetworkUse.Off : connected ? NetworkUse.On : connecting ? NetworkUse.Connecting : NetworkUse.Off;
            var trust = n.BuiltIn ? NetworkTrust.BuiltIn : n.TestReviewed ? NetworkTrust.TestReviewed : n.Reviewed ? NetworkTrust.Reviewed : NetworkTrust.Unreviewed;
            rows.Add(new(n.Id, n.Name, trust, NetworkAbility.Invites | NetworkAbility.PublicRooms | NetworkAbility.Messages, use, CanSwitch: n.Nodes > 0));
        }
        return rows;
    }

    /// <summary>
    /// The top bar's status: the string key, its arguments and the light. One network in use shows its name
    /// (<c>shell.network.relays</c> for the basic relays), several show how many, none shows "not connected" or the
    /// network being connected.
    /// </summary>
    public static (string Key, object[] Args, NetworkUse Light) Status(IReadOnlyList<NetworkEntry> rows)
    {
        var on = rows.Where(r => r.Use == NetworkUse.On).ToList();
        if (on.Count > 1) return ("shell.network.many", [on.Count], NetworkUse.On);
        if (on.Count == 1) return on[0].IsBaseRelays ? ("shell.network.relays", [], NetworkUse.On) : ("shell.network.single", [on[0].Name], NetworkUse.On);
        if (rows.FirstOrDefault(r => r.Use == NetworkUse.Connecting) is { } connecting) return ("shell.network.connecting", [connecting.Name], NetworkUse.Connecting);
        return ("shell.network.none", [], NetworkUse.Off);
    }

    /// <summary>The string keys of what a network can do, in a fixed order.</summary>
    public static IReadOnlyList<string> AbilityKeys(NetworkAbility abilities)
    {
        var keys = new List<string>(3);
        if (abilities.HasFlag(NetworkAbility.Invites)) keys.Add("shell.network.can.invites");
        if (abilities.HasFlag(NetworkAbility.PublicRooms)) keys.Add("shell.network.can.public_rooms");
        if (abilities.HasFlag(NetworkAbility.Messages)) keys.Add("shell.network.can.messages");
        return keys;
    }
}
