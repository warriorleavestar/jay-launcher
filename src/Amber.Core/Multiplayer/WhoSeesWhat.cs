namespace Amber.Core.Multiplayer;

/// <summary>One row of the "who can see what" table: localization keys, never free text.</summary>
/// <param name="Id">Stable name of the row (tests, self-checks, highlighting the row that applies now).</param>
public sealed record WhoSeesRow(string Id, string WhoKey, string IpKey, string ContentKey);

/// <summary>
/// The fixed "who can see what" table behind a room's connection mark and on a network's card (UI restructure: people,
/// rooms, networks). It states facts only, the same five rows everywhere: who sees my IP address, who sees what I send.
/// The rows follow from how Jay works: codes carry no address, connecting directly (or trying to) shows each side the
/// other's address, with "relays only" Jay asks the others not to connect to me directly (which holds for unmodified
/// programs, hence "not in normal use" and never a plain "no"), a relay sees my address itself but carries only
/// ciphertext, and a private chat is sealed to the one recipient.
/// </summary>
public static class WhoSeesWhat
{
    public const string CodeHolders = "code";
    public const string RoomDirect = "direct";
    public const string RoomRelayed = "relayed";
    public const string Operators = "operator";
    public const string PrivateChat = "private";

    public static IReadOnlyList<WhoSeesRow> Rows { get; } =
    [
        new(CodeHolders, "multiplayer.sees.row.code", "multiplayer.sees.no", "multiplayer.sees.no"),
        new(RoomDirect, "multiplayer.sees.row.direct", "multiplayer.sees.yes", "multiplayer.sees.room_content"),
        new(RoomRelayed, "multiplayer.sees.row.relayed", "multiplayer.sees.not_normally", "multiplayer.sees.room_content"),
        new(Operators, "multiplayer.sees.row.operator", "multiplayer.sees.yes", "multiplayer.sees.no"),
        new(PrivateChat, "multiplayer.sees.row.private", "multiplayer.sees.as_above", "multiplayer.sees.only_them"),
    ];

    /// <summary>
    /// The room row that describes me right now (none before anyone is connected). The relayed row applies only
    /// while my side is on "relays only" and no link is direct. A link that merely happens to go through a relay does
    /// not count: Jay keeps trying to connect directly, and trying already tells the other side my address.
    /// </summary>
    public static IReadOnlyList<string> AppliesNow(LinkKind room, bool relayOnly) => room switch
    {
        LinkKind.None => [],
        LinkKind.Relay when relayOnly => [RoomRelayed],
        _ => [RoomDirect],
    };
}
