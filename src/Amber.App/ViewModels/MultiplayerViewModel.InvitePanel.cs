using Amber.App.Localization;
using Amber.Core.Localization;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Transport;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

/// <summary>"How long" in the invite panel.</summary>
public sealed record InviteLifeChoice(InviteLife Life, string Key) : LocalizedRecord
{
    public string Label => L.T(Key);
    public override string ToString() => Label;
}

/// <summary>"How many times" in the invite panel.</summary>
public sealed record InviteUseChoice(InviteUse Use, string Key) : LocalizedRecord
{
    public string Label => L.T(Key);
    public override string ToString() => Label;
}

/// <summary>
/// The room's invite panel (UI restructure: people, rooms, networks): two choices (how long, once or unlimited), the
/// invite as a small card instead of a long string, three lines that are always there (what an invite holds, what it
/// lets its holder do, when it stops working), and "send to a friend", which seals an invite to that friend's own key.
/// The thirty-day choice is the long-lived invite that used to live in multiplayer settings.
/// </summary>
public sealed partial class MultiplayerViewModel
{
    /// <summary>The invite on the card, when it is one of this room's (its uses and expiry are read live).</summary>
    private PendingInvite? _lastPending;

    /// <summary>The invite on the card, when it is a thirty-day one.</summary>
    private LongInviteItem? _lastLongInvite;

    public IReadOnlyList<InviteLifeChoice> InviteLifeChoices { get; } =
    [
        new(InviteLife.TenMinutes, "multiplayer.invite.life.ten_minutes"),
        new(InviteLife.OneDay, "multiplayer.invite.life.day"),
        new(InviteLife.ThirtyDays, "multiplayer.invite.life.month"),
    ];

    public IReadOnlyList<InviteUseChoice> InviteUseChoices { get; } =
    [
        new(InviteUse.Once, "multiplayer.invite.use.once"),
        new(InviteUse.Unlimited, "multiplayer.invite.use.unlimited"),
    ];

    private InviteLifeChoice? _inviteLife;
    private InviteUseChoice? _inviteUse;

    public InviteLifeChoice SelectedInviteLife
    {
        get => _inviteLife ??= InviteLifeChoices[0];
        set
        {
            if (value is null || ReferenceEquals(value, _inviteLife)) return;
            _inviteLife = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedInviteUse));
            OnPropertyChanged(nameof(InviteUsesEnabled));
            OnPropertyChanged(nameof(ShowInviteMonthNote));
            NotifyInviteAutoApprove();
        }
    }

    /// <summary>A thirty-day invite has no use count: the box shows "unlimited" and cannot be changed.</summary>
    public InviteUseChoice SelectedInviteUse
    {
        get => InviteUseChoices.First(c => c.Use == InviteOffer.Normalize(SelectedInviteLife.Life, (_inviteUse ??= InviteUseChoices[0]).Use));
        set
        {
            if (value is null || !InviteUsesEnabled || ReferenceEquals(value, _inviteUse)) return;
            _inviteUse = value;
            OnPropertyChanged();
            NotifyInviteAutoApprove();
        }
    }

    public bool InviteUsesEnabled => InviteOffer.CanPickUses(SelectedInviteLife.Life);

    /// <summary>Thirty days is picked: one line on what that invite is.</summary>
    public bool ShowInviteMonthNote => InviteOffer.IsLongLived(SelectedInviteLife.Life);

    /// <summary>
    /// "Auto-approve" can be ticked: outside "For a child", and only for an invite that lets one person in. An invite
    /// without a use limit (thirty days included) always asks me first, so the box is not there for it.
    /// </summary>
    public bool CanInviteAutoApprove => ShowInviteAutoApprove && InviteOffer.CanAutoApprove(SelectedInviteLife.Life, SelectedInviteUse.Use);

    /// <summary>The tick counts for the invite about to be made.</summary>
    private bool InviteAutoApproveNow => InviteAutoApprove && CanInviteAutoApprove;

    /// <summary>
    /// One line where the box would be: an unlimited invite always asks first. (Thirty days says so in its own note.)
    /// </summary>
    public bool ShowInviteAlwaysAsksNote => ShowInviteAutoApprove && !CanInviteAutoApprove && !ShowInviteMonthNote;

    private void NotifyInviteAutoApprove()
    {
        OnPropertyChanged(nameof(CanInviteAutoApprove));
        OnPropertyChanged(nameof(ShowInviteAlwaysAsksNote));
        OnPropertyChanged(nameof(InviteKnockFact));
    }

    internal void PickInviteChoices(InviteLife life, InviteUse use)
    {
        SelectedInviteLife = InviteLifeChoices.First(c => c.Life == life);
        if (InviteUsesEnabled) SelectedInviteUse = InviteUseChoices.First(c => c.Use == use);
    }

    // ---- the invite as a card ----

    /// <summary>"Invite · 9 minutes left · 1 use": what the card says instead of the code.</summary>
    public string InviteSummary
    {
        get
        {
            if (_lastInvite is null) return "";
            if (_lastLongInvite is { } month)
                return L.F("multiplayer.invite.summary", month.Left, L.T("multiplayer.invite.use.unlimited"));
            if (_lastPending is not { } pending) return L.T("multiplayer.invite.kind");
            var left = pending.ExpiresAt is { } at ? TimeLeft(at - DateTimeOffset.UtcNow) : L.T("multiplayer.invite.terms_room");
            var uses = pending.Unlimited ? L.T("multiplayer.invite.use.unlimited") : L.P("multiplayer.invite.uses_left", pending.UsesLeft, pending.UsesLeft);
            return pending.IsUsable(DateTimeOffset.UtcNow) ? L.F("multiplayer.invite.summary", left, uses) : L.T("multiplayer.invite.summary_spent");
        }
    }

    /// <summary>"9 minutes left", "23 hours left".</summary>
    private static string TimeLeft(TimeSpan left)
    {
        if (left < TimeSpan.Zero) left = TimeSpan.Zero;
        return left.TotalHours >= 1
            ? L.F("multiplayer.invite.left", L.P("multiplayer.invite.hours", (int)Math.Ceiling(left.TotalHours), (int)Math.Ceiling(left.TotalHours)))
            : L.F("multiplayer.invite.left", L.P("multiplayer.invite.minutes", (int)Math.Ceiling(left.TotalMinutes), (int)Math.Ceiling(left.TotalMinutes)));
    }

    /// <summary>The second of the three lines: with "let in without asking" on, an invite is more than a knock.</summary>
    public string InviteKnockFact => L.T(InviteAutoApproveNow ? "multiplayer.invite.fact.auto" : "multiplayer.invite.fact.knock");

    /// <summary>The third line: this invite's own terms, or how invites end when there is none yet.</summary>
    public string InviteTermsFact => HasInvite && InviteTerms.Length > 0 ? InviteTerms : L.T("multiplayer.invite.fact.ends");

    partial void OnInviteAutoApproveChanged(bool value) => OnPropertyChanged(nameof(InviteKnockFact));

    private bool _longInviteWaits;

    /// <summary>
    /// The thirty-day invite on the card cannot open this room (the room keeps the key its other invites use, and
    /// thirty-day invites have one of their own): it works from the next room on. Said on the card, not hidden.
    /// </summary>
    public bool ShowInviteNextRoomNote => HasInvite && _lastLongInvite is not null && _longInviteWaits;

    private void ForgetShownInvite()
    {
        _longInviteWaits = false;
        _lastInvite = null;
        _lastPending = null;
        _lastLongInvite = null;
        InviteShown = false;
        OnPropertyChanged(nameof(InviteDisplay));
        OnPropertyChanged(nameof(HasInvite));
        OnPropertyChanged(nameof(ShowCreateInvite));
        NotifyInvitePanel();
    }

    private void NotifyInvitePanel()
    {
        foreach (var name in (string[])[nameof(InviteSummary), nameof(InviteKnockFact), nameof(InviteTerms), nameof(InviteTermsFact), nameof(InviteFriendsNote), nameof(HasInviteFriendsNote),
                     nameof(CanMailInvites), nameof(ShowInviteFriends), nameof(ShowInviteNextRoomNote), nameof(CanInviteAutoApprove), nameof(ShowInviteAlwaysAsksNote)])
            OnPropertyChanged(name);
    }

    /// <summary>Thirty days: a long-lived invite. With a room open it goes on the card, with whether this room accepts it.</summary>
    private void ShowLongInvite(LongLivedInvite invite, bool worksNow)
    {
        _lastLongInvite = LongInviteItems.FirstOrDefault(i => i.Invite.Id == invite.Id);
        _longInviteWaits = !worksNow;
        _lastPending = null;
        _lastInvite = LongInvites.CodeOf(invite, _credentials?.Tunnel ?? TunnelSettings.Default, _lobby?.Book.Game);
        RememberInviteTerms(InviteBook.UnlimitedUses, LongLivedInvites.DefaultLifetime, false);
        InviteShown = false;
        OnPropertyChanged(nameof(InviteDisplay));
        OnPropertyChanged(nameof(HasInvite));
        OnPropertyChanged(nameof(ShowCreateInvite));
        NotifyInvitePanel();
    }

    // ---- send to a friend ----

    /// <summary>The friends list under [Send to a friend] is open.</summary>
    [ObservableProperty] public partial bool InviteFriendsOpen { get; set; }

    [RelayCommand]
    private void ToggleInviteFriends() => InviteFriendsOpen = !InviteFriendsOpen;

    /// <summary>Sending straight to a friend exists for hosts, outside "For a child" (it needs a network).</summary>
    public bool ShowInviteFriends => IsHosting && ShowPublicNetworks;

    /// <summary>A sealed invite travels through the connected network's mailbox.</summary>
    public bool CanMailInvites => ShowInviteFriends && HasContacts && IsPublicConnected;

    /// <summary>Why nobody can be sent an invite right now (empty when the list is usable).</summary>
    public string InviteFriendsNote => !ShowInviteFriends ? ""
        : !HasContacts ? L.T("multiplayer.invite.friends.none")
        : !IsPublicConnected ? L.T("multiplayer.invite.friends.no_network")
        : "";

    public bool HasInviteFriendsNote => InviteFriendsNote.Length > 0;

    private void InitInvitePanel()
    {
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(IsHosting) or nameof(ShowPublicNetworks) or nameof(IsPublicConnected) or nameof(HasContacts) or nameof(ShowInviteAutoApprove))
                NotifyInvitePanel();
            if (e.PropertyName == nameof(Mode) && IsIdle) InviteFriendsOpen = false;
        };
    }
}
