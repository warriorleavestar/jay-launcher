using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Multiplayer;

/// <summary>
/// Every value the multiplayer advanced settings panel shows that has a default. The panel compares this against
/// <see cref="AdvancedSettings.Defaults"/> to mark the groups someone changed and to put everything back.
/// </summary>
public sealed record AdvancedValues(
    TransportMode Transport,
    bool Disguise,
    string Attestation,
    bool Breaker,
    int BreakerDelay,
    bool BreakerSkip,
    bool RelayOnly,
    bool FullLan,
    bool AutoCopyInvite,
    string KnockWait,
    bool RequestPopup,
    bool ModSync,
    bool GameProxy,
    bool NetworkExit);

/// <summary>
/// The multiplayer advanced settings panel (redesign batch 10): Settings › Multiplayer, one group open at a time.
/// Files and calls get their groups with the batches that add them.
/// </summary>
public static class AdvancedSettings
{
    public const string Protection = "protection";
    public const string Connection = "connection";
    public const string Invites = "invites";
    public const string Room = "room";
    public const string Network = "network";
    public const string Troubleshoot = "troubleshoot";

    /// <summary>The groups in panel order.</summary>
    public static readonly IReadOnlyList<string> Groups = [Protection, Connection, Invites, Room, Network, Troubleshoot];

    /// <summary>What a fresh install starts with: the "standard" protection level and everything else off or at its usual value.</summary>
    public static readonly AdvancedValues Defaults = new(
        Transport: RoomOpening.Tunnel(RoomProtection.Standard)!.Mode,
        Disguise: RoomOpening.Tunnel(RoomProtection.Standard)!.Disguise,
        Attestation: "on",
        Breaker: true,
        BreakerDelay: SecurityPolicy.DefaultRestartDelay,
        BreakerSkip: false,
        RelayOnly: false,
        FullLan: false,
        AutoCopyInvite: true,
        KnockWait: KnockWaits.Default.ToSetting(),
        RequestPopup: true,
        ModSync: true,
        GameProxy: false,
        NetworkExit: false);

    /// <summary>The groups (in panel order) with at least one value that is not the default.</summary>
    public static IReadOnlyList<string> Customized(AdvancedValues values)
    {
        var d = Defaults;
        var changed = new List<string>();
        if (values.Transport != d.Transport || values.Disguise != d.Disguise
            || !string.Equals(values.Attestation, d.Attestation, StringComparison.OrdinalIgnoreCase)
            || values.Breaker != d.Breaker || values.BreakerDelay != d.BreakerDelay || values.BreakerSkip != d.BreakerSkip)
            changed.Add(Protection);
        if (values.RelayOnly != d.RelayOnly || values.FullLan != d.FullLan) changed.Add(Connection);
        if (values.AutoCopyInvite != d.AutoCopyInvite || KnockWaits.Parse(values.KnockWait) != KnockWaits.Parse(d.KnockWait)
            || values.RequestPopup != d.RequestPopup)
            changed.Add(Invites);
        // Routing through me needs the full LAN; without it the switch has no effect, so it is not a change.
        if (values.ModSync != d.ModSync || values.GameProxy != d.GameProxy || (values.FullLan && values.NetworkExit != d.NetworkExit))
            changed.Add(Room);
        return changed;
    }
}
