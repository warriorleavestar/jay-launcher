using System.Collections.ObjectModel;
using Amber.Core.Localization;
using Amber.Core.Multiplayer.Public;
using Amber.App.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

/// <summary>A game version in the public room list's filter ("" = all versions).</summary>
public sealed record RoomGameChoice(string Game) : LocalizedRecord
{
    public string Label => Game.Length == 0 ? L.T("multiplayer.public.all_versions") : Game;
    public override string ToString() => Label;
}

/// <summary>A choice in the public room list's sort box.</summary>
public sealed record RoomSortChoice(RoomSort Sort, string Key) : LocalizedRecord
{
    public string Label => L.T(Key);
    public override string ToString() => Label;
}

/// <summary>
/// Public rooms tab (official §6.16): search, version filter, "walk right in" and sort over the listed rooms,
/// the empty states, and a way to cancel a slow connect.
/// </summary>
public sealed partial class MultiplayerViewModel
{
    /// <summary>The rooms after search, filter and sort.</summary>
    public ObservableCollection<PublicRoomItem> VisibleRooms { get; } = [];

    [ObservableProperty] public partial string RoomSearch { get; set; } = "";
    [ObservableProperty] public partial bool OpenRoomsOnly { get; set; }

    /// <summary>"All versions" first, then each game version in the list.</summary>
    public ObservableCollection<RoomGameChoice> RoomGameChoices { get; } = [];

    [ObservableProperty] public partial RoomGameChoice? SelectedRoomGame { get; set; }

    public IReadOnlyList<RoomSortChoice> RoomSortChoices { get; } =
    [
        new(RoomSort.MostPlayers, "multiplayer.public.sort.players"),
        new(RoomSort.Name, "multiplayer.public.sort.name"),
        new(RoomSort.Newest, "multiplayer.public.sort.newest"),
    ];

    [ObservableProperty] public partial RoomSortChoice? SelectedRoomSort { get; set; }

    /// <summary>When the list was last fetched ("refreshed 12:04").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PublicRefreshedText))]
    public partial DateTimeOffset? PublicRefreshed { get; set; }

    public string PublicRefreshedText => PublicRefreshed is { } at ? L.F("multiplayer.public.refreshed", at.ToLocalTime().ToString("HH:mm")) : "";

    /// <summary>No public network set up yet (the default): explain where public rooms come from.</summary>
    public bool ShowPublicSetup => !HasPublicNodes && !IsPublicConnected;

    /// <summary>Connected, but nobody has a public room right now.</summary>
    public bool ShowNoPublicRooms => IsPublicConnected && !IsRefreshingRooms && PublicRooms.Count == 0;

    /// <summary>Rooms are listed, but none match the search and filters.</summary>
    public bool ShowNoMatchingRooms => IsPublicConnected && PublicRooms.Count > 0 && VisibleRooms.Count == 0;

    private static readonly RoomGameChoice AllGames = new("");

    private void InitPublicList()
    {
        SelectedRoomSort = RoomSortChoices[0];
        PublicRooms.CollectionChanged += (_, _) =>
        {
            if (PublicRooms.Count > 0 || IsPublicConnected) PublicRefreshed = DateTimeOffset.Now;
            var keep = SelectedRoomGame;
            RoomGameChoices.Clear();
            RoomGameChoices.Add(AllGames);
            foreach (var g in RoomFilter.Games(PublicRooms.Select(r => r.Listing))) RoomGameChoices.Add(new RoomGameChoice(g));
            SelectedRoomGame = keep is not null && RoomGameChoices.Contains(keep) ? keep : AllGames;
            ApplyRoomFilter();
        };
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(HasPublicNodes) or nameof(IsPublicConnected) or nameof(IsRefreshingRooms) or nameof(PublicStateText))
            {
                OnPropertyChanged(nameof(ShowPublicSetup));
                OnPropertyChanged(nameof(ShowNoPublicRooms));
                OnPropertyChanged(nameof(ShowNoMatchingRooms));
            }
        };
        RoomGameChoices.Add(AllGames);
        SelectedRoomGame = AllGames;
    }

    partial void OnRoomSearchChanged(string value) => ApplyRoomFilter();
    partial void OnOpenRoomsOnlyChanged(bool value) => ApplyRoomFilter();
    partial void OnSelectedRoomGameChanged(RoomGameChoice? value) => ApplyRoomFilter();
    partial void OnSelectedRoomSortChanged(RoomSortChoice? value) => ApplyRoomFilter();

    private void ApplyRoomFilter()
    {
        var byListing = PublicRooms.ToDictionary(r => r.Listing);
        var shown = RoomFilter.Apply(PublicRooms.Select(r => r.Listing), RoomSearch, SelectedRoomGame?.Game, OpenRoomsOnly, SelectedRoomSort?.Sort ?? RoomSort.MostPlayers);
        VisibleRooms.Clear();
        foreach (var listing in shown) VisibleRooms.Add(byListing[listing]);
        OnPropertyChanged(nameof(ShowNoPublicRooms));
        OnPropertyChanged(nameof(ShowNoMatchingRooms));
    }

    /// <summary>"Add a public network…": multiplayer settings › public networks.</summary>
    [RelayCommand]
    private void AddPublicNetwork() => Navigate?.Invoke(Amber.Core.Navigation.LegacyRoutes.PublicNetworks);
}
