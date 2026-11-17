using System.Collections.ObjectModel;
using System.ComponentModel;
using Amber.Core.Localization;
using Amber.Core.Multiplayer.Contacts;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

/// <summary>The filter chips over the lobby's "where to play" list.</summary>
public enum WhereFilter
{
    All,
    Friends,
    Public,
    Servers,
}

/// <summary>
/// A room a friend invited me to: one row under "where to play" until the invite runs out or is used, or until
/// <see cref="Until"/>, whichever comes first.
/// </summary>
public sealed class FriendRoomItem(string key, MailedInvite invite, string who, string room, DateTimeOffset received)
{
    /// <summary>When it arrived here, by this computer's clock.</summary>
    public DateTimeOffset Received { get; } = received;

    /// <summary>
    /// When the row goes: the invite's own end, but never later than <see cref="MultiplayerViewModel.MaxFriendRoomAge"/>
    /// after it arrived, so an invite with no end (or a far one) can't stay for good.
    /// </summary>
    public DateTimeOffset Until { get; } = invite.Expires is { } e && e < received + MultiplayerViewModel.MaxFriendRoomAge
        ? e : received + MultiplayerViewModel.MaxFriendRoomAge;

    /// <summary>The device that sent it (a friend's rows are counted per device).</summary>
    internal string Sender { get; } = Convert.ToHexStringLower(invite.From.PublicKey);

    public string Key { get; } = key;
    public MailedInvite Invite { get; } = invite;
    public string Who { get; } = who;
    public string Title { get; } = room;
    public string Meta => string.Join(" · ", new[]
    {
        L.F("multiplayer.where.friend_room.from", Who),
        Invite.Invite.Game is { } game ? (game.Loader is { } loader ? $"{game.McVersion} {loader}" : game.McVersion) : null,
        L.F("multiplayer.invite.fingerprint", Invite.From.Fingerprint),
    }.Where(s => !string.IsNullOrEmpty(s)));
    public bool IsExpired(DateTimeOffset now) => Until <= now;
}

/// <summary>
/// "Where to play" in the lobby (UI restructure: people, rooms, networks): rooms friends invited me to, public rooms of
/// the connected network and the servers I saved, in one list with filter chips. It replaces the public rooms tab and
/// the Jay servers list of the servers tab; the rows still come from the same places.
/// </summary>
public sealed partial class MultiplayerViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWhereAll), nameof(IsWhereFriends), nameof(IsWherePublic), nameof(IsWhereServers))]
    public partial WhereFilter Where { get; set; }

    public bool IsWhereAll { get => Where == WhereFilter.All; set { if (value) Where = WhereFilter.All; } }
    public bool IsWhereFriends { get => Where == WhereFilter.Friends; set { if (value) Where = WhereFilter.Friends; } }
    public bool IsWherePublic { get => Where == WhereFilter.Public; set { if (value) Where = WhereFilter.Public; } }
    public bool IsWhereServers { get => Where == WhereFilter.Servers; set { if (value) Where = WhereFilter.Servers; } }

    partial void OnWhereChanged(WhereFilter value)
    {
        // "For a child" has no networks, so no friends' rooms and no public rooms to filter by.
        if (!ShowPublicNetworks && value is WhereFilter.Friends or WhereFilter.Public)
        {
            Where = WhereFilter.All;
            return;
        }
        PruneFriendRooms();
        NotifyWhere();
    }

    /// <summary>Rooms friends invited me to (mailed invites that have not run out).</summary>
    public ObservableCollection<FriendRoomItem> FriendRooms { get; } = [];

    private bool WhereShows(WhereFilter filter) => Where == WhereFilter.All || Where == filter;

    public bool ShowFriendRooms => ShowPublicNetworks && WhereShows(WhereFilter.Friends) && FriendRooms.Count > 0;
    public bool ShowFriendRoomsEmpty => Where == WhereFilter.Friends && FriendRooms.Count == 0;

    /// <summary>
    /// The public part of the list, once a network is connected. Under "all" it shows only when there are rooms; the
    /// search, the filters and "no public rooms right now" belong to the "public" filter.
    /// </summary>
    public bool ShowPublicRoomList => ShowPublicNetworks && IsPublicConnected
        && (Where == WhereFilter.Public || Where == WhereFilter.All && PublicRooms.Count > 0);

    /// <summary>The password box under the public rooms: only while a locked room is listed.</summary>
    public bool ShowPublicRoomPassword => VisibleRooms.Any(r => r.NeedsPassword);

    /// <summary>"Public" is picked and no network has been added yet: say where public rooms come from.</summary>
    public bool ShowWherePublicSetup => ShowPublicNetworks && Where == WhereFilter.Public && ShowPublicSetup;

    /// <summary>"Public" is picked, there is a network, and it is not connected: offer to connect.</summary>
    public bool ShowWherePublicConnect => ShowPublicNetworks && Where == WhereFilter.Public && !ShowPublicSetup && !IsPublicConnected;

    public string WherePublicConnectText => IsPublicConnecting ? L.T("multiplayer.public.connecting")
        : L.F("multiplayer.where.public.connect_hint", SelectedNetworkChoice?.Name ?? "");

    public bool ShowSavedServers => WhereShows(WhereFilter.Servers) && JayServers.Servers.Count > 0;
    public bool ShowSavedServersEmpty => Where == WhereFilter.Servers && JayServers.Servers.Count == 0;

    /// <summary>"All" and nothing to list: one line saying what will show up here.</summary>
    public bool ShowWhereEmpty => Where == WhereFilter.All && !ShowFriendRooms && !ShowSavedServers && !ShowPublicRoomList;

    private void InitWhere()
    {
        PropertyChanged += OnWhereInputChanged;
        FriendRooms.CollectionChanged += (_, _) =>
        {
            NotifyWhere();
            WatchFriendRoomExpiry();
        };
        PublicRooms.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ShowPublicRoomList));
            OnPropertyChanged(nameof(ShowWhereEmpty));
        };
        VisibleRooms.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ShowPublicRoomPassword));
    }

    /// <summary>The servers list exists only after the constructor made <see cref="JayServers"/>.</summary>
    private void WatchSavedServers() => JayServers.Servers.CollectionChanged += (_, _) => NotifyWhere();

    private void OnWhereInputChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(IsPublicConnected) or nameof(IsPublicConnecting) or nameof(ShowPublicSetup) or nameof(SelectedNetworkChoice):
                NotifyWhere();
                break;
            case nameof(ShowPublicNetworks):
                if (!ShowPublicNetworks && Where is WhereFilter.Friends or WhereFilter.Public) Where = WhereFilter.All;
                NotifyWhere();
                break;
        }
    }

    private void NotifyWhere()
    {
        foreach (var name in (string[])[nameof(ShowFriendRooms), nameof(ShowFriendRoomsEmpty), nameof(ShowPublicRoomList), nameof(ShowWherePublicSetup),
                     nameof(ShowWherePublicConnect), nameof(WherePublicConnectText), nameof(ShowSavedServers), nameof(ShowSavedServersEmpty), nameof(ShowWhereEmpty)])
            OnPropertyChanged(name);
    }

    /// <summary>The longest a friend's room stays listed after its invite arrived, whatever the invite says.</summary>
    internal static readonly TimeSpan MaxFriendRoomAge = TimeSpan.FromHours(12);

    /// <summary>Rooms listed from one device at a time: a newer invite from it takes the place of its oldest.</summary>
    internal const int MaxFriendRoomsPerDevice = 3;

    /// <summary>Rooms listed at a time from all friends: past it the oldest row goes.</summary>
    internal const int MaxFriendRooms = 16;

    /// <summary>Invites I said "not now" to, by key, until they would have left the list anyway (bounded).</summary>
    private readonly Dictionary<string, DateTimeOffset> _ignoredFriendRooms = new(StringComparer.Ordinal);
    private const int MaxIgnoredFriendRooms = 256;

    /// <summary>The clock for friends' rooms (tests move it).</summary>
    internal Func<DateTimeOffset> FriendRoomClock { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>
    /// Lists a friend's room. False when it is not listed: already there, said "not now" to, or run out. A device's
    /// fourth room replaces its oldest; past <see cref="MaxFriendRooms"/> the oldest row of anyone goes.
    /// </summary>
    private bool ListFriendRoom(string key, MailedInvite invite, string who, string room)
    {
        PruneFriendRooms();
        var now = FriendRoomClock();
        if (FriendRooms.Any(r => r.Key == key) || _ignoredFriendRooms.ContainsKey(key)) return false;
        var row = new FriendRoomItem(key, invite, who, room, now);
        if (row.IsExpired(now)) return false;
        while (FriendRooms.Count(r => r.Sender == row.Sender) >= MaxFriendRoomsPerDevice)
            DropFriendRoom(FriendRooms.Last(r => r.Sender == row.Sender).Key);
        while (FriendRooms.Count >= MaxFriendRooms) DropFriendRoom(FriendRooms[^1].Key);
        FriendRooms.Insert(0, row);
        return true;
    }

    /// <summary>"Not now" on a friend's room: the row, the invite and the request all go, and the same invite stays away.</summary>
    private void IgnoreFriendRoom(string key)
    {
        var until = FriendRooms.FirstOrDefault(r => r.Key == key)?.Until ?? FriendRoomClock() + MaxFriendRoomAge;
        DropFriendRoom(key);
        var now = FriendRoomClock();
        foreach (var old in _ignoredFriendRooms.Where(p => p.Value <= now).Select(p => p.Key).ToList()) _ignoredFriendRooms.Remove(old);
        while (_ignoredFriendRooms.Count >= MaxIgnoredFriendRooms)
            _ignoredFriendRooms.Remove(_ignoredFriendRooms.MinBy(p => p.Value).Key);
        _ignoredFriendRooms[key] = until;
    }

    /// <summary>Tests: how many invites from friends are still held (listed or waiting on a request).</summary>
    internal int HeldFriendInvites => _mailedInvites.Count;

    private DispatcherTimer? _friendRoomTimer;

    /// <summary>How often a listed friend's room is checked for having run out (while the list shows one that can).</summary>
    internal static readonly TimeSpan FriendRoomCheckInterval = TimeSpan.FromSeconds(5);

    /// <summary>Tests: whether listed friends' rooms are being checked for running out.</summary>
    internal bool WatchingFriendRoomExpiry => _friendRoomTimer?.IsEnabled == true;

    /// <summary>
    /// While a listed room can run out, check it every few seconds, so it leaves the list on its own: nobody has to
    /// switch the filter, click it or answer its request.
    /// </summary>
    private void WatchFriendRoomExpiry()
    {
        // Every row runs out (at the latest MaxFriendRoomAge after it arrived), so the timer runs while there is one.
        if (FriendRooms.Count > 0)
        {
            _friendRoomTimer ??= new DispatcherTimer(FriendRoomCheckInterval, DispatcherPriority.Background, (_, _) => PruneFriendRooms());
            _friendRoomTimer.Start();
        }
        else _friendRoomTimer?.Stop();
    }

    /// <summary>Drops one room from the list, with its invite and the request that offered to join.</summary>
    private void DropFriendRoom(string key)
    {
        if (FriendRooms.FirstOrDefault(r => r.Key == key) is { } row) FriendRooms.Remove(row);
        _mailedInvites.Remove(key);
        _service.Requests.Withdraw(key);
    }

    /// <summary>Drops rows that have run out, with the request that offered to join, and any invite held without a row.</summary>
    internal void PruneFriendRooms()
    {
        var now = FriendRoomClock();
        foreach (var row in FriendRooms.Where(r => r.IsExpired(now)).ToList()) DropFriendRoom(row.Key);
        if (_mailedInvites.Count > FriendRooms.Count)
            foreach (var key in _mailedInvites.Keys.Where(k => FriendRooms.All(r => r.Key != k)).ToList()) DropFriendRoom(key);
    }

    /// <summary>
    /// Someone is no longer a friend (removed, or a device taken off): the invites I mailed to their devices stop
    /// working, so they can't keep knocking on the room that is open. Only invites mailed to friends are looked at,
    /// and nothing happens while the contacts file can't be read.
    /// </summary>
    private void RevokeInvitesOfRemovedFriends()
    {
        if (_lobby is not { } lobby || _friendInvites.Count == 0) return;
        var stillOut = lobby.Book.Pending.ToHashSet(ReferenceEqualityComparer.Instance);
        _friendInvites.RemoveWhere(i => !stillOut.Contains(i));
        if (_friendInvites.Count == 0 || Contacts.Unreadable) return;
        var devices = Contacts.All.Select(c => Convert.ToHexString(c.PublicKey)).ToHashSet();
        var gone = _friendInvites.Where(i => i.BoundTo is { } key && !devices.Contains(Convert.ToHexString(key))).ToList();
        if (gone.Count == 0) return;
        foreach (var invite in gone)
        {
            lobby.Book.Revoke(invite);
            _friendInvites.Remove(invite);
        }
        RefreshInvites();
    }

    /// <summary>Someone is no longer a friend: the rooms they invited me to go, with their requests.</summary>
    private void DropRoomsOfRemovedFriends()
    {
        if (FriendRooms.Count == 0) return;
        var friends = ContactList.SelectMany(f => f.Devices).Select(d => Convert.ToHexString(d.PublicKey)).ToHashSet();
        foreach (var row in FriendRooms.Where(r => !friends.Contains(Convert.ToHexString(r.Invite.From.PublicKey))).ToList())
        {
            FriendRooms.Remove(row);
            _mailedInvites.Remove(row.Key);
            _service.Requests.Withdraw(row.Key);
        }
    }

    /// <summary>Joins a room from its row. The knock stops before saying anything if the host is not the friend the invite names.</summary>
    [RelayCommand]
    private async Task JoinFriendRoomAsync(FriendRoomItem? row)
    {
        if (row is null) return;
        if (row.IsExpired(DateTimeOffset.UtcNow))
        {
            PruneFriendRooms();
            SetStatus(StatusMessage.Warning("multiplayer.where.friend_room.expired"));
            return;
        }
        if (!IsIdle || IsKnocking)
        {
            SetStatus(StatusMessage.Warning("multiplayer.contacts.invite.busy"));
            return;
        }
        // The request goes (the join is under way); the row stays until the host has answered, so a knock that
        // never got through (no connection, the host busy) can be tried again while the invite lasts.
        _service.Requests.Withdraw(row.Key);
        if (!await JoinMailedInviteAsync(row.Invite)) return;
        _mailedInvites.Remove(row.Key);
        FriendRooms.Remove(row);
    }

    /// <summary>"Public" with a network that is not connected: connect it; what is still needed is asked on the network view.</summary>
    [RelayCommand]
    private async Task ConnectForPublicRoomsAsync()
    {
        await ConnectPublicCommand.ExecuteAsync(null);
        if (!IsPublicConnected && !IsPublicConnecting) Navigate?.Invoke("play/public");
    }
}
