using System.Collections.Specialized;
using System.ComponentModel;
using Amber.App.Services;
using Amber.Core.Localization;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Public;
using Amber.Core.Multiplayer.Transport;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

/// <summary>One option of one of the four choices on the open-a-room card.</summary>
public sealed partial class OpenChoice(string id, string labelKey, string detailKey) : ObservableObject
{
    /// <summary>The enum member's name.</summary>
    public string Id { get; } = id;
    public string Label => L.T(labelKey);
    public string Detail => L.T(detailKey);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Line))]
    public partial bool IsEnabled { get; set; } = true;

    /// <summary>Why it cannot be picked right now.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Line))]
    public partial string Reason { get; set; } = "";

    /// <summary>The second line in the drop-down: what the option means, or why it is greyed out.</summary>
    public string Line => IsEnabled ? Detail : Reason;

    internal void Relabel() => OnPropertyChanged((string?)null);

    public override string ToString() => Label;
}

/// <summary>
/// Opening a room asks four things (UI restructure: people, rooms, networks): who can come in, how people connect,
/// protection and scope. Each has a default, so the button works without touching any of them. The choices replace the
/// switches that used to be spread over the lobby's "more ways", the public rooms tab and the advanced settings; what
/// each maps to is in <see cref="RoomOpening"/>.
/// </summary>
public sealed partial class MultiplayerViewModel
{
    /// <summary>How long the invite mailed to each friend lasts when "my friends can knock" is picked.</summary>
    public static readonly TimeSpan FriendsKnockLifetime = TimeSpan.FromHours(12);

    public IReadOnlyList<OpenChoice> WhoChoices { get; } =
    [
        new(nameof(RoomAccess.Invited), "multiplayer.open.who.invited", "multiplayer.open.who.invited_detail"),
        new(nameof(RoomAccess.Friends), "multiplayer.open.who.friends", "multiplayer.open.who.friends_detail"),
        new(nameof(RoomAccess.Public), "multiplayer.open.who.public", "multiplayer.open.who.public_detail"),
    ];

    public IReadOnlyList<OpenChoice> RouteChoices { get; } =
    [
        new(nameof(RoomRoute.Auto), "multiplayer.open.route.auto", "multiplayer.open.route.auto_detail"),
        new(nameof(RoomRoute.RelayOnly), "multiplayer.open.route.relay", "multiplayer.open.route.relay_detail"),
        new(nameof(RoomRoute.Serverless), "multiplayer.open.route.serverless", "multiplayer.open.route.serverless_detail"),
    ];

    public IReadOnlyList<OpenChoice> ProtectionChoices { get; } =
    [
        new(nameof(RoomProtection.Standard), "multiplayer.open.protection.standard", "multiplayer.open.protection.standard_detail"),
        new(nameof(RoomProtection.Stronger), "multiplayer.open.protection.stronger", "multiplayer.open.protection.stronger_detail"),
        new(nameof(RoomProtection.Highest), "multiplayer.open.protection.highest", "multiplayer.open.protection.highest_detail"),
    ];

    public IReadOnlyList<OpenChoice> ScopeChoices { get; } =
    [
        new(nameof(RoomScope.Game), "multiplayer.open.scope.game", "multiplayer.open.scope.game_detail"),
        new(nameof(RoomScope.WholeLan), "multiplayer.open.scope.lan", "multiplayer.open.scope.lan_detail"),
    ];

    /// <summary>
    /// Who can come in. Never null, and never a greyed-out option: such a value is ignored here, and the page puts the
    /// drop-down back (<c>MultiplayerPage.OnOpenChoiceChanged</c>).
    /// </summary>
    public OpenChoice SelectedWho
    {
        get => _selectedWho ??= WhoChoices[0];
        set
        {
            if (value is null || !value.IsEnabled || ReferenceEquals(value, _selectedWho)) return;
            _selectedWho = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(OpenAccess));
            OnPropertyChanged(nameof(ShowPublicOptions));
        }
    }

    private OpenChoice? _selectedWho;

    /// <summary>How people connect.</summary>
    public OpenChoice SelectedRoute
    {
        get => _selectedRoute ??= RouteChoices[0];
        set
        {
            if (value is null || !value.IsEnabled || ReferenceEquals(value, _selectedRoute)) return;
            _selectedRoute = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(OpenRoute));
            OnPropertyChanged(nameof(IsServerlessChosen));
            RefreshOpenChoices();
            NotifyPlayAndHost();
        }
    }

    private OpenChoice? _selectedRoute;

    /// <summary>
    /// The protection level the transport settings amount to, or null when they were set by hand to something else
    /// (the card then says "customised"). Picking a level sets the transport.
    /// </summary>
    public OpenChoice? SelectedProtection
    {
        get
        {
            var level = RoomOpening.ProtectionOf(new TunnelSettings(SelectedTransport.Mode, CanDisguise && DisguiseAsMinecraft));
            return ProtectionChoices.FirstOrDefault(c => c.Id == level.ToString());
        }
        set
        {
            if (value is null || !value.IsEnabled || ReferenceEquals(value, SelectedProtection)) return;
            ApplyProtection(Enum.Parse<RoomProtection>(value.Id));
        }
    }

    public bool IsProtectionCustom => SelectedProtection is null;

    /// <summary>"Customised: Plain" under the protection row.</summary>
    public string ProtectionCustomText => L.F("multiplayer.open.protection.custom", SelectedTransport.Label);

    /// <summary>Just this game, or a virtual LAN for everything.</summary>
    public OpenChoice SelectedScope
    {
        get => ScopeChoices[FullLanMode ? 1 : 0];
        set
        {
            if (value is null || !value.IsEnabled) return;
            FullLanMode = value.Id == nameof(RoomScope.WholeLan);
        }
    }

    public RoomAccess OpenAccess => Enum.Parse<RoomAccess>(SelectedWho.Id);
    public RoomRoute OpenRoute => Enum.Parse<RoomRoute>(SelectedRoute.Id);
    public bool IsServerlessChosen => OpenRoute == RoomRoute.Serverless;

    /// <summary>"Public" is picked: the room name and an optional password show under the row.</summary>
    public bool ShowPublicOptions => OpenAccess == RoomAccess.Public;

    private RoomOpeningContext OpenContext => new(Child.InviteOnly, IsPublicConnected,
        ContactList.Count(f => f.Devices.Any(d => d.Verified)), CanUseFullLan);

    private void InitOpenRoom()
    {
        PropertyChanged += OnOpenRoomInputChanged;
        // RefreshContacts clears the list and fills it again: the choices are checked once it's done, not on the empty
        // list in between (which would drop "friends can knock" whenever a friend is renamed or added).
        ContactList.CollectionChanged += (_, _) => { if (!_rebuildingContacts) RefreshOpenChoices(); };
        RefreshOpenChoices();
    }

    private void OnOpenRoomInputChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(IsPublicConnected) or nameof(ShowPublicNetworks):
                RefreshOpenChoices();
                break;
            case nameof(SelectedTransport) or nameof(DisguiseAsMinecraft):
                OnPropertyChanged(nameof(SelectedProtection));
                OnPropertyChanged(nameof(IsProtectionCustom));
                OnPropertyChanged(nameof(ProtectionCustomText));
                break;
            case nameof(FullLanMode):
                OnPropertyChanged(nameof(SelectedScope));
                break;
            case nameof(IsComponentReady) or nameof(IsDownloading):
                NotifyPlayAndHost();
                OnPropertyChanged(nameof(JoinButtonText));
                OnPropertyChanged(nameof(ShowComponentStep));
                break;
        }
    }

    /// <summary>Greys out what cannot be picked now (with the reason) and moves the selection off a greyed-out option.</summary>
    internal void RefreshOpenChoices()
    {
        var context = OpenContext;
        var route = OpenRoute;
        foreach (var choice in WhoChoices) Set(choice, RoomOpening.Blocked(Enum.Parse<RoomAccess>(choice.Id), route, context));
        foreach (var choice in ProtectionChoices) Set(choice, RoomOpening.Blocked(Enum.Parse<RoomProtection>(choice.Id), route, context));
        foreach (var choice in ScopeChoices)
        {
            var blocked = RoomOpening.Blocked(Enum.Parse<RoomScope>(choice.Id), context);
            // The platform says why in its own words (no driver, not this system).
            choice.Reason = blocked is null ? "" : FullLanUnavailableText.Length > 0 ? FullLanUnavailableText : L.T(blocked);
            choice.IsEnabled = blocked is null;
        }
        var who = RoomOpening.Usable(OpenAccess, route, context);
        if (who != OpenAccess)
        {
            _selectedWho = WhoChoices.First(c => c.Id == who.ToString());
            OnPropertyChanged(nameof(SelectedWho));
            OnPropertyChanged(nameof(OpenAccess));
            OnPropertyChanged(nameof(ShowPublicOptions));
        }
        OnPropertyChanged(nameof(ProtectionEnabled));

        static void Set(OpenChoice choice, string? blocked)
        {
            choice.Reason = blocked is null ? "" : L.T(blocked);
            choice.IsEnabled = blocked is null;
        }
    }

    /// <summary>A serverless room is protected by its own direct link: the protection row is not asked.</summary>
    public bool ProtectionEnabled => !IsServerlessChosen;

    private void ApplyProtection(RoomProtection level)
    {
        if (RoomOpening.Tunnel(level) is not { } tunnel || TransportChoices.FirstOrDefault(c => c.Mode == tunnel.Mode) is not { } transport) return;
        SelectedTransport = transport;
        DisguiseAsMinecraft = tunnel.Disguise;
        OnPropertyChanged(nameof(SelectedProtection));
        OnPropertyChanged(nameof(IsProtectionCustom));
    }

    [RelayCommand]
    private void ResetProtection() => ApplyProtection(RoomProtection.Standard);

    /// <summary>Picks a choice by its enum name (repeat the last room, tests). Unknown or greyed-out names are ignored.</summary>
    internal void PickOpenChoices(string? who, string? route, string? protection, string? scope)
    {
        if (RouteChoices.FirstOrDefault(c => c.Id == route) is { } r) SelectedRoute = r;
        if (WhoChoices.FirstOrDefault(c => c.Id == who) is { } w) SelectedWho = w;
        if (ProtectionChoices.FirstOrDefault(c => c.Id == protection) is { } p) SelectedProtection = p;
        if (ScopeChoices.FirstOrDefault(c => c.Id == scope) is { } s) SelectedScope = s;
    }

    // ---- the component as a step on the buttons ----

    /// <summary>The multiplayer component is missing: opening and joining first install it, right on the button.</summary>
    public bool ShowComponentStep => !IsComponentReady;

    public string ComponentStepText => L.F("multiplayer.open.install_first", EasyTierRelease.Current.Size / 1024 / 1024);

    // ---- after the room is open ----

    /// <summary>
    /// "My friends can knock": every verified friend gets an invite bound to their own key through the connected
    /// network (sealed, only they can open it). They still knock and the host says yes. Returns how many were mailed.
    /// </summary>
    internal async Task<int> InviteFriendsToKnockAsync()
    {
        var sent = 0;
        foreach (var friend in ContactList.ToList())
            if (await MailInviteAsync(friend, FriendsKnockUses, FriendsKnockLifetime, askFirst: true, quiet: true)) sent++;
        if (sent > 0) SetStatus(StatusMessage.Success("multiplayer.open.friends_told", sent));
        else SetStatus(StatusMessage.Warning("multiplayer.open.friends_not_told"));
        return sent;
    }

    private const int FriendsKnockUses = 20;

    /// <summary>"Public": lists the room on the connected network, with the password when one was typed.</summary>
    private async Task PublishFromChoiceAsync()
    {
        var mode = PublishPassword.Length > 0 ? RoomVisibility.Password : RoomVisibility.Public;
        SelectedVisibility = VisibilityChoices.First(v => v.Mode == mode);
        await PublishRoomAsync();
    }

    protected override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        foreach (var choice in WhoChoices.Concat(RouteChoices).Concat(ProtectionChoices).Concat(ScopeChoices)) choice.Relabel();
        RefreshOpenChoices();
        RefreshRoomFacts();
        RefreshLinks();
        NotifyRelayOnly();
        NotifyScene();
        NotifyInvitePanel();
    }
}
