using Amber.Core.Calls;
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
    bool NetworkExit,
    string CallsWho = "friends-room",
    bool CallsNoise = true,
    bool CallsPushToTalk = false,
    string CallsQuality = "auto",
    bool CallsShareGameOnly = true,
    bool CallsAllowControl = true);

/// <summary>
/// The multiplayer advanced settings panel (redesign batch 10): Settings › Multiplayer, one group open at a time.
/// The files group comes with the batch that adds files; the calls group shows only while calls are switched on
/// (<see cref="CallFeature.Enabled"/>), since calls are reserved.
/// </summary>
public static class AdvancedSettings
{
    public const string Protection = "protection";
    public const string Connection = "connection";
    public const string Invites = "invites";
    public const string Room = "room";
    public const string Network = "network";
    public const string Troubleshoot = "troubleshoot";
    public const string Calls = "calls";

    /// <summary>The groups in panel order (calls last, and only while calls are switched on).</summary>
    public static IReadOnlyList<string> Groups => GroupsFor(CallFeature.Enabled);

    public static IReadOnlyList<string> GroupsFor(bool calls) =>
        calls ? [Protection, Connection, Invites, Room, Network, Troubleshoot, Calls] : [Protection, Connection, Invites, Room, Network, Troubleshoot];

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
    public static IReadOnlyList<string> Customized(AdvancedValues values) => Customized(values, CallFeature.Enabled);

    /// <summary><see cref="Customized(AdvancedValues)"/> with calls switched on or off.</summary>
    public static IReadOnlyList<string> Customized(AdvancedValues values, bool calls)
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
        // The calls group is hidden while calls are off, so its values do not count then.
        if (calls && (CallRules.Parse(values.CallsWho) != CallRules.Parse(d.CallsWho) || values.CallsNoise != d.CallsNoise || values.CallsPushToTalk != d.CallsPushToTalk
            || !string.Equals(values.CallsQuality, d.CallsQuality, StringComparison.OrdinalIgnoreCase) || values.CallsShareGameOnly != d.CallsShareGameOnly
            || values.CallsAllowControl != d.CallsAllowControl))
            changed.Add(Calls);
        return changed;
    }
}
