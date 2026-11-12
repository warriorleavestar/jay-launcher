using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Amber.Core.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

/// <summary>
/// "Friends", the fourth entry of the navigation (redesign: people, rooms, networks). It lists the friends the
/// multiplayer identity knows, shows a friend's devices and safety codes, and says through which network an invite
/// would reach them. Adding and removing friends still happens in the multiplayer settings; this page links there.
/// </summary>
public sealed partial class PeopleViewModel : PageViewModel
{
    private readonly MultiplayerViewModel _multiplayer;

    public PeopleViewModel(MultiplayerViewModel multiplayer)
    {
        _multiplayer = multiplayer;
        multiplayer.PropertyChanged += OnMultiplayerChanged;
        multiplayer.ContactList.CollectionChanged += OnFriendsChanged;
        multiplayer.ContactRequests.CollectionChanged += OnRequestsChanged;
        SyncBadge();
    }

    public override string Title => L.T("shell.nav.people");
    public override string Icon => "M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z";

    /// <summary>Go to a route (set by the main view model).</summary>
    public Action<string>? Navigate { get; set; }

    public ObservableCollection<ContactItem> Friends => _multiplayer.ContactList;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelected), nameof(ShowPickHint), nameof(SelectedHasNoJay))]
    public partial ContactItem? Selected { get; set; }

    // "For a child" hides the friends, so it hides the selected one too.
    public bool HasSelected => !IsChildMode && Selected is not null;

    // The multiplayer page rebuilds its friend list from scratch on every change: remember who was selected by safety
    // code and select the same person again.
    private string? _selectedKey;

    partial void OnSelectedChanged(ContactItem? value)
    {
        if (value is not null) _selectedKey = value.Fingerprint;
    }

    /// <summary>"For a child": friends are reached through public networks, and those are off.</summary>
    public bool IsChildMode => !_multiplayer.ShowPublicNetworks;

    public bool HasFriends => !IsChildMode && Friends.Count > 0;
    public bool IsEmpty => !IsChildMode && Friends.Count == 0;
    public bool ShowPickHint => HasFriends && Selected is null;
    public bool SelectedHasNoJay => Selected is { HasJay: false };

    /// <summary>Which network delivers an invite to a friend, or that none is connected.</summary>
    public string DeliveryText => _multiplayer.IsPublicConnected && _multiplayer.SelectedNetworkChoice is { } network
        ? L.F("people.delivery.via", network.Name)
        : L.T("people.delivery.none");

    public bool CanDeliver => _multiplayer.IsPublicConnected;
    public bool ShowConnect => !IsChildMode && !CanDeliver;

    /// <summary>Friend requests that are somewhere between "sent" and "emoji compared".</summary>
    public int RequestCount => _multiplayer.ContactRequests.Count;
    public bool HasRequests => !IsChildMode && RequestCount > 0;
    public string RequestsText => L.F("people.requests", RequestCount);

    /// <summary>A room is open and public networks are allowed: the selected friend can be invited from here.</summary>
    public bool CanInvite => _multiplayer.CanInviteContacts;
    public bool ShowInviteHint => !CanInvite;

    private void OnFriendsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        var match = _selectedKey is null ? null : Friends.FirstOrDefault(f => f.Fingerprint == _selectedKey);
        if (!ReferenceEquals(match, Selected)) Selected = match;
        OnPropertyChanged(nameof(CanInvite));
        OnPropertyChanged(nameof(ShowInviteHint));
        OnPropertyChanged(nameof(HasFriends));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(ShowPickHint));
    }

    private void OnRequestsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(RequestCount));
        OnPropertyChanged(nameof(HasRequests));
        OnPropertyChanged(nameof(RequestsText));
        SyncBadge();
    }

    /// <summary>The red dot on the entry: someone is asking to add me.</summary>
    private void SyncBadge() => Badge = IsChildMode ? 0 : _multiplayer.ContactRequests.Count(r => r.IsAskingMe);

    private void OnMultiplayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case null:
                OnPropertyChanged((string?)null);
                break;
            case nameof(MultiplayerViewModel.IsPublicConnected) or nameof(MultiplayerViewModel.SelectedNetworkChoice):
                OnPropertyChanged(nameof(DeliveryText));
                OnPropertyChanged(nameof(CanDeliver));
                OnPropertyChanged(nameof(ShowConnect));
                break;
            case nameof(MultiplayerViewModel.CanInviteContacts) or nameof(MultiplayerViewModel.IsHosting):
                OnPropertyChanged(nameof(CanInvite));
                OnPropertyChanged(nameof(ShowInviteHint));
                break;
            case nameof(MultiplayerViewModel.ShowPublicNetworks):
                OnPropertyChanged((string?)null);
                SyncBadge();
                break;
        }
    }

    [RelayCommand]
    private void CopyMyCode() => _multiplayer.CopyContactCodeCommand.Execute(null);

    /// <summary>Adding, removing and the emoji check are in the multiplayer settings.</summary>
    [RelayCommand]
    private void Manage() => Navigate?.Invoke("play/settings#ContactsCard");

    [RelayCommand]
    private void Connect() => Navigate?.Invoke("play/public");

    [RelayCommand]
    private void Invite()
    {
        if (Selected is { } friend && CanInvite) _multiplayer.InviteContactCommand.Execute(friend);
    }
}
