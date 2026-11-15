namespace Amber.Core.Multiplayer;

/// <summary>How long an invite made in the room stays valid.</summary>
public enum InviteLife
{
    TenMinutes,
    OneDay,
    /// <summary>A long-lived invite (<see cref="LongLivedInvites"/>): every room I open accepts it until it runs out or is revoked.</summary>
    ThirtyDays,
}

/// <summary>How many people one invite lets knock.</summary>
public enum InviteUse
{
    Once,
    Unlimited,
}

/// <summary>
/// The two choices of the room's invite panel (UI restructure: people, rooms, networks) and what they mean for the
/// invite book. Pure logic.
/// </summary>
public static class InviteOffer
{
    public static TimeSpan Lifetime(InviteLife life) => life switch
    {
        InviteLife.TenMinutes => TimeSpan.FromMinutes(10),
        InviteLife.OneDay => TimeSpan.FromDays(1),
        _ => LongLivedInvites.DefaultLifetime,
    };

    /// <summary>Thirty days is the long-lived invite: kept on disk, accepted by every room I open.</summary>
    public static bool IsLongLived(InviteLife life) => life == InviteLife.ThirtyDays;

    /// <summary>A long-lived invite has no use count, so it is always "unlimited".</summary>
    public static InviteUse Normalize(InviteLife life, InviteUse use) => IsLongLived(life) ? InviteUse.Unlimited : use;

    /// <summary>"Once" can be picked unless the invite is long-lived.</summary>
    public static bool CanPickUses(InviteLife life) => !IsLongLived(life);

    public static int Uses(InviteUse use) => use == InviteUse.Once ? 1 : InviteBook.UnlimitedUses;

    /// <summary>Only an invite for one person can let that person in without asking the host.</summary>
    public static bool CanAutoApprove(InviteLife life, InviteUse use) => Normalize(life, use) == InviteUse.Once;

    /// <summary>
    /// The panel's choice for a lifetime remembered from an earlier room (minutes; null was "while the room is open").
    /// Never longer than what was remembered: anything under a day becomes ten minutes. "Open like last time" never
    /// makes a thirty-day invite either; that one is made on purpose.
    /// </summary>
    public static InviteLife LifeFromMinutes(int? minutes) => minutes is { } m && m < 24 * 60 ? InviteLife.TenMinutes : InviteLife.OneDay;

    /// <summary>The panel's choice for a remembered use count. Never more: only "unlimited" stays unlimited.</summary>
    public static InviteUse UseFromCount(int uses) => uses == InviteBook.UnlimitedUses ? InviteUse.Unlimited : InviteUse.Once;
}
