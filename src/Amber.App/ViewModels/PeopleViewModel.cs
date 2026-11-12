using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Amber.App.Localization;
using Amber.Core.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

/// <summary>A friend in the list on the Friends page: the person, and what this computer knows about them right now.</summary>
public sealed partial class FriendRow : ObservableObject
{
    internal FriendRow(ContactItem friend)
    {
        Friend = friend;
        LanguageListeners.Add(this, static o => ((FriendRow)o).OnPropertyChanged(string.Empty));
    }

    public ContactItem Friend { get; }
    public string Name => Friend.Name;
    public bool HasJay => Friend.HasJay;
    public bool HasNoJay => !Friend.HasJay;
    public string JayText => Friend.JayText;
    public IReadOnlyList<ContactDeviceItem> DeviceItems => Friend.DeviceItems;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText), nameof(IsHere), nameof(IsInRoom), nameof(IsQuiet), nameof(NeedsLook), nameof(Summary))]
    public partial FriendState State { get; set; }

    /// <summary>The room they invited me to (<see cref="FriendState.InTheirRoom"/>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText), nameof(Summary))]
    public partial string Room { get; set; } = "";

    public string StateText => State switch
    {
        FriendState.SameRoom => L.T("people.state.same_room"),
        FriendState.InTheirRoom => L.F("people.state.in_room", Room),
        FriendState.NewDevice => L.T("people.state.new_device"),
        _ => L.T("people.state.verified"),
    };

    public bool IsHere => State == FriendState.SameRoom;
    public bool IsInRoom => State == FriendState.InTheirRoom;
    public bool IsQuiet => State == FriendState.Verified;
    public bool NeedsLook => State == FriendState.NewDevice;

    /// <summary>For screen readers: "Alex · In the room with you".</summary>
    public string Summary => Name + " · " + StateText;
}

/// <summary>
/// "Friends", the fourth entry of the navigation (redesign: people, rooms, networks). People on the left, the
/// conversation with the selected one on the right; narrow windows show the list first. Adding a friend (paste their
/// code or send mine, then compare four emoji), the "let them straight in" switch that replaced the trusted list, and
/// the backup of the friend list all live here.
/// </summary>
public sealed partial class PeopleViewModel : PageViewModel
{
    private readonly MultiplayerViewModel _multiplayer;
    private ObservableCollection<FriendLine>? _lines;

    public PeopleViewModel(MultiplayerViewModel multiplayer)
    {
        _multiplayer = multiplayer;
        multiplayer.PropertyChanged += OnMultiplayerChanged;
        multiplayer.FriendsRebuilt += RebuildFriends;
        multiplayer.ContactRequests.CollectionChanged += OnRequestsChanged;
        multiplayer.MetPeople.CollectionChanged += (_, _) => OnMetChanged();
        multiplayer.PeopleFactsChanged += RefreshFacts;
        RebuildFriends();
        SyncBadge();
    }

    public override string Title => L.T("shell.nav.people");
    public override string Icon => "M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z";

    /// <summary>Go to a route (set by the main view model).</summary>
    public Action<string>? Navigate { get; set; }

    /// <summary>The multiplayer identity behind this page: friend requests, the emoji check and the backup are its commands.</summary>
    public MultiplayerViewModel Multiplayer => _multiplayer;

    protected override void OnFirstActivated() => _multiplayer.ShowMetPeople();

    /// <summary><c>people/add</c> opens the add panel; plain <c>people</c> leaves the page as it was.</summary>
    internal void ShowRoute(string? sub)
    {
        if (sub == "add" && !IsChildMode) IsAdding = true;
    }

    // ---- the list ----

    public ObservableCollection<FriendRow> Friends { get; } = [];
    public ObservableCollection<MetPersonItem> MetPeople => _multiplayer.MetPeople;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelected), nameof(HasPicked), nameof(ShowPickHint), nameof(ShowEmpty), nameof(ShowChildNote))]
    public partial FriendRow? Selected { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedMet), nameof(HasPicked), nameof(ShowPickHint), nameof(ShowEmpty), nameof(ShowChildNote))]
    public partial MetPersonItem? SelectedMet { get; set; }

    // The multiplayer page rebuilds its lists from scratch on every change (and a bound list box drops its selection
    // when its list is cleared): remember who was selected, a friend by safety code and someone I've met by key, and
    // select the same person again. The keys are forgotten only when the player picks someone else or goes back, or
    // when the person is gone after a rebuild. A friend is remembered by every device's safety code, so removing one of
    // their devices (even the one their name comes from) keeps them selected.
    private HashSet<string>? _selectedKey;
    private string? _selectedMetKey;

    /// <summary>"For a child": friends are reached through public networks, and those are off.</summary>
    public bool IsChildMode => !_multiplayer.ShowPublicNetworks;

    public bool HasFriends => !IsChildMode && Friends.Count > 0;
    public bool HasMet => MetPeople.Count > 0;
    public bool HasList => HasFriends || HasMet;

    // "For a child" hides the friends, so it hides the selected one too.
    public bool HasSelected => !IsChildMode && Selected is not null;
    public bool HasSelectedMet => SelectedMet is not null;

    /// <summary>Someone is open on the right (narrow windows then show them instead of the list).</summary>
    public bool HasPicked => HasSelected || HasSelectedMet;

    public bool ShowChildNote => IsChildMode && !HasPicked;
    public bool ShowEmpty => !IsChildMode && !HasList && !HasPicked;
    public bool ShowPickHint => !IsChildMode && HasList && !HasPicked;

    partial void OnSelectedChanged(FriendRow? value)
    {
        // A question about removing someone is about that person only, so it goes when someone else is picked; a refresh
        // that picks the same person again keeps it.
        if (value is null) { if (!_rebuilding) IsConfirmingRemove = false; }
        else
        {
            // Someone else than before: their safety codes start folded and the draft is not carried over.
            if (_selectedKey is null || !value.Friend.Devices.Any(d => _selectedKey.Contains(d.Fingerprint)))
            {
                ShowSafety = false;
                Draft = "";
                IsConfirmingRemove = false;
            }
            _selectedKey = [.. value.Friend.Devices.Select(d => d.Fingerprint)];
            _selectedMetKey = null;
            SelectedMet = null;
        }
        WatchLines();
        NotifyFriend();
    }

    partial void OnSelectedMetChanged(MetPersonItem? value)
    {
        if (value is not null)
        {
            _selectedMetKey = Convert.ToHexString(value.PublicKey);
            _selectedKey = null;
            Selected = null;
        }
        NotifyMet();
    }

    // True while the list is rebuilt: the list briefly drops its selection, which is not the user picking nobody.
    private bool _rebuilding;

    private void RebuildFriends()
    {
        var keys = _selectedKey;
        _rebuilding = true;
        try
        {
            Friends.Clear();
            foreach (var friend in _multiplayer.ContactList) Friends.Add(new FriendRow(friend));
            RefreshStates();
            Selected = keys is null ? null : Friends.FirstOrDefault(f => f.Friend.Devices.Any(d => keys.Contains(d.Fingerprint)));
        }
        finally { _rebuilding = false; }
        if (Selected is null) { _selectedKey = null; IsConfirmingRemove = false; }
        NotifyList();
        NotifyFriend();
    }

    private void OnMetChanged()
    {
        if (_selectedMetKey is { } key)
        {
            var match = MetPeople.FirstOrDefault(p => Convert.ToHexString(p.PublicKey) == key);
            if (!ReferenceEquals(match, SelectedMet)) SelectedMet = match;
        }
        NotifyList();
    }

    /// <summary>Who is in the room, who invited me, who is let straight in: nothing in the lists moved, what they say did.</summary>
    private void RefreshFacts()
    {
        // The list of people I've met is refilled by now: someone who is not in it any more is not selected any more.
        if (SelectedMet is null) _selectedMetKey = null;
        RefreshStates();
        NotifyFriend();
        NotifyMet();
    }

    private void RefreshStates()
    {
        foreach (var row in Friends)
        {
            var state = _multiplayer.StateOf(row.Friend);
            row.Room = state == FriendState.InTheirRoom ? _multiplayer.RoomOf(row.Friend)?.Title ?? "" : "";
            row.State = state;
        }
    }

    private void NotifyList()
    {
        foreach (var name in (string[])[nameof(HasFriends), nameof(HasMet), nameof(HasList), nameof(ShowEmpty), nameof(ShowPickHint), nameof(ShowChildNote),
                     nameof(HasSelected), nameof(HasPicked)])
            OnPropertyChanged(name);
    }

    // ---- the top row: which network delivers, add a friend, backup ----

    /// <summary>Which network delivers mail to a friend, or that none is connected.</summary>
    public string DeliveryText => _multiplayer.IsPublicConnected && _multiplayer.SelectedNetworkChoice is { } network
        ? L.F("people.delivery.via", network.Name)
        : L.T("people.delivery.none");

    public bool CanDeliver => _multiplayer.IsPublicConnected;
    public bool ShowConnect => !IsChildMode && !CanDeliver;
    public bool ShowTopRow => !IsChildMode;

    /// <summary>The add panel: my friend code, the box for theirs, requests in progress and the emoji check.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAddPanel), nameof(ShowRequestsLine))]
    public partial bool IsAdding { get; set; }

    public bool ShowAddPanel => IsAdding && !IsChildMode;

    public string MyCode => _multiplayer.MyContactCode;

    public string CodeInput
    {
        get => _multiplayer.ContactCodeInput;
        set => _multiplayer.ContactCodeInput = value;
    }

    /// <summary>Friend requests that are somewhere between "sent" and "emoji compared".</summary>
    public int RequestCount => _multiplayer.ContactRequests.Count;
    public bool HasRequests => !IsChildMode && RequestCount > 0;
    public string RequestsText => L.F("people.requests", RequestCount);

    /// <summary>The add panel is closed and something in it waits: one line with [Open].</summary>
    public bool ShowRequestsLine => HasRequests && !IsAdding;

    private void OnRequestsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(RequestCount));
        OnPropertyChanged(nameof(HasRequests));
        OnPropertyChanged(nameof(RequestsText));
        OnPropertyChanged(nameof(ShowRequestsLine));
        SyncBadge();
    }

    /// <summary>The red dot on the entry: someone is asking to add me.</summary>
    private void SyncBadge() => Badge = IsChildMode ? 0 : _multiplayer.ContactRequests.Count(r => r.IsAskingMe);

    [RelayCommand]
    private void OpenAdd()
    {
        if (!IsChildMode) IsAdding = true;
    }

    [RelayCommand]
    private void CloseAdd() => IsAdding = false;

    [RelayCommand]
    private void CopyMyCode() => _multiplayer.CopyContactCodeCommand.Execute(null);

    [RelayCommand]
    private Task AddAsync() => _multiplayer.AddContactCommand.ExecuteAsync(null);

    [RelayCommand]
    private void Connect() => Navigate?.Invoke("play/public");

    [RelayCommand]
    private Task BackupAsync() => _multiplayer.BackupFriendsCommand.ExecuteAsync(null);

    [RelayCommand]
    private Task RestoreAsync() => _multiplayer.RestoreFriendsCommand.ExecuteAsync(null);

    // ---- the selected friend ----

    /// <summary>The conversation with the selected friend (this run of Jay only).</summary>
    public ObservableCollection<FriendLine>? Lines => _lines;
    public bool HasNoLines => _lines is not { Count: > 0 };

    private void WatchLines()
    {
        var lines = HasSelected ? _multiplayer.ConversationWith(Selected!.Friend) : null;
        if (ReferenceEquals(lines, _lines)) return;
        if (_lines is not null) _lines.CollectionChanged -= OnLinesChanged;
        _lines = lines;
        if (_lines is not null) _lines.CollectionChanged += OnLinesChanged;
        OnPropertyChanged(nameof(Lines));
        OnPropertyChanged(nameof(HasNoLines));
    }

    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasNoLines));
        if (e.Action == NotifyCollectionChangedAction.Add) LineAdded?.Invoke();
    }

    /// <summary>A line was added to the open conversation (the page scrolls to it).</summary>
    internal event Action? LineAdded;

    /// <summary>The line above the conversation: "End-to-end encrypted · checked", or that a new device waits. It opens the safety codes.</summary>
    public string SafetyText => Selected is { NeedsLook: true } ? L.T("people.state.new_device") : L.T("people.safety.verified");
    /// <summary>The same in a narrow window: "Encrypted".</summary>
    public string SafetyShortText => Selected is { NeedsLook: true } ? L.T("people.state.new_device") : L.T("people.safety.short");
    public bool SafetyNeedsLook => Selected is { NeedsLook: true };
    public bool SafetyIsFine => Selected is { NeedsLook: false };

    /// <summary>Safety codes and devices of the selected friend, under the header.</summary>
    [ObservableProperty] public partial bool ShowSafety { get; set; }

    public string SafetyHint => Selected is { } row ? L.F("people.safety.hint", row.Name) : "";

    [RelayCommand]
    private void ToggleSafety() => ShowSafety = !ShowSafety;

    /// <summary>They mailed me an invite that still works: [Join their room].</summary>
    public bool CanJoinRoom => Selected is { } row && _multiplayer.RoomOf(row.Friend) is not null;
    public string JoinRoomText => Selected is { } row && _multiplayer.RoomOf(row.Friend) is { } room ? L.F("people.chat.join", room.Title) : "";

    /// <summary>A room is open and public networks are allowed: the selected friend can be invited from here.</summary>
    public bool CanInvite => _multiplayer.CanInviteContacts;
    public bool ShowInviteHint => HasSelected && !CanInvite;

    /// <summary>"Let them straight in when they knock" for the selected friend (the old trusted list, per person).</summary>
    public bool AutoAdmit
    {
        get => Selected is { } row && _multiplayer.IsAutoAdmitted(row.Friend);
        set
        {
            if (Selected is not { } row || value == AutoAdmit) return;
            _multiplayer.SetAutoAdmit(row.Friend, value);
            OnPropertyChanged();
        }
    }

    /// <summary>Messages reach a friend only while we are in the same room (see <see cref="MultiplayerViewModel.SendToFriendAsync"/>).</summary>
    public bool CanMessage => Selected is { } row && _multiplayer.PeerOf(row.Friend) is not null;
    public bool ShowMessageHint => HasSelected && !CanMessage;
    public string DraftPlaceholder => Selected is { } row ? L.F("people.chat.placeholder", row.Name) : "";

    [ObservableProperty] public partial string Draft { get; set; } = "";

    private void NotifyFriend()
    {
        foreach (var name in (string[])[nameof(SafetyText), nameof(SafetyShortText), nameof(SafetyNeedsLook), nameof(SafetyIsFine), nameof(SafetyHint), nameof(CanJoinRoom), nameof(JoinRoomText),
                     nameof(CanInvite), nameof(ShowInviteHint), nameof(AutoAdmit), nameof(CanMessage), nameof(ShowMessageHint), nameof(DraftPlaceholder)])
            OnPropertyChanged(name);
    }

    [RelayCommand]
    private async Task JoinRoomAsync()
    {
        if (Selected is { } row && _multiplayer.RoomOf(row.Friend) is { } room) await _multiplayer.JoinFriendRoomCommand.ExecuteAsync(room);
    }

    [RelayCommand]
    private void Invite()
    {
        if (Selected is { } row && CanInvite) _multiplayer.InviteContactCommand.Execute(row.Friend);
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        if (Selected is not { } row || string.IsNullOrWhiteSpace(Draft)) return;
        var text = Draft;
        Draft = "";
        if (!await _multiplayer.SendToFriendAsync(row.Friend, text) && Draft.Length == 0) Draft = text;
    }

    [RelayCommand]
    private void ConfirmDevice(ContactDeviceItem? device)
    {
        if (device is not null) _multiplayer.AcknowledgeDevice(device);
    }

    [RelayCommand]
    private void RemoveDevice(ContactDeviceItem? device) => _multiplayer.RemoveContactDeviceCommand.Execute(device);

    /// <summary>
    /// [Remove from friends] asks first: adding them back means comparing the emoji again, and the button sits right under
    /// their devices. [Keep] is the safe default.
    /// </summary>
    [ObservableProperty]
    public partial bool IsConfirmingRemove { get; set; }

    public string RemoveQuestion => Selected is { } row ? L.F("people.safety.remove_confirm", row.Name) : "";

    [RelayCommand]
    private void RemoveFriend()
    {
        if (Selected is null) return;
        OnPropertyChanged(nameof(RemoveQuestion));
        IsConfirmingRemove = true;
    }

    [RelayCommand]
    private void ConfirmRemoveFriend()
    {
        IsConfirmingRemove = false;
        if (Selected is { } row) _multiplayer.RemoveContactCommand.Execute(row.Friend);
    }

    [RelayCommand]
    private void KeepFriend() => IsConfirmingRemove = false;

    /// <summary>Narrow windows: back from the person to the list.</summary>
    [RelayCommand]
    private void BackToList()
    {
        _selectedKey = null;
        _selectedMetKey = null;
        Selected = null;
        SelectedMet = null;
    }

    // ---- someone I've met ----

    public bool MetAutoAdmit
    {
        get => SelectedMet is { AutoAdmit: true };
        set
        {
            if (SelectedMet is not { } person || value == person.AutoAdmit) return;
            _multiplayer.SetAutoAdmit(person, value);
            OnPropertyChanged();
        }
    }

    /// <summary>"For a child" asks about every knock: the switch can be turned off there, not on.</summary>
    public bool CanChangeMetAutoAdmit => SelectedMet is { } person && (person.AutoAdmit || !_multiplayer.KnocksAlwaysAsk);
    public bool ShowKnocksAlwaysAsk => HasSelectedMet && _multiplayer.KnocksAlwaysAsk;

    /// <summary>Asking them to be friends goes by mail, so it needs public networks ("For a child" has none).</summary>
    public bool CanAddMet => HasSelectedMet && !IsChildMode;

    private void NotifyMet()
    {
        foreach (var name in (string[])[nameof(MetAutoAdmit), nameof(CanChangeMetAutoAdmit), nameof(ShowKnocksAlwaysAsk), nameof(CanAddMet)]) OnPropertyChanged(name);
    }

    [RelayCommand]
    private async Task AddMetAsync()
    {
        if (SelectedMet is not { } person || IsChildMode) return;
        await _multiplayer.AddMetPersonAsync(person);
        // The request, and later the emoji, show in the add panel.
        if (_multiplayer.HasContactRequests) IsAdding = true;
    }

    private void OnMultiplayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case null or "":
                OnPropertyChanged(string.Empty);
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
            case nameof(MultiplayerViewModel.ContactCodeInput):
                OnPropertyChanged(nameof(CodeInput));
                break;
            case nameof(MultiplayerViewModel.MyContactCode):
                OnPropertyChanged(nameof(MyCode));
                break;
            case nameof(MultiplayerViewModel.ShowPublicNetworks):
                if (IsChildMode)
                {
                    IsAdding = false;
                    _selectedKey = null;
                    Selected = null;
                }
                WatchLines();
                OnPropertyChanged(string.Empty);
                SyncBadge();
                break;
        }
    }
}
