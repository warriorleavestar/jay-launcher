using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Amber.Core.Localization;
using Amber.Core.Multiplayer.Public;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

/// <summary>One network in the top bar's network panel.</summary>
public sealed partial class NetworkStatusRow : ObservableObject
{
    private readonly Action<NetworkStatusRow> _toggle;

    public NetworkStatusRow(NetworkEntry entry, Action<NetworkStatusRow> toggle)
    {
        _toggle = toggle;
        Entry = entry;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Name), nameof(Tip), nameof(TrustText), nameof(CanText), nameof(Detail), nameof(HasDetail), nameof(IsOn), nameof(IsOff),
        nameof(IsConnecting), nameof(CanSwitch), nameof(IsBaseRelays), nameof(SwitchName), nameof(IsUnreviewed), nameof(Wanted))]
    public partial NetworkEntry Entry { get; set; }

    public string Id => Entry.Id;
    public bool IsBaseRelays => Entry.IsBaseRelays;
    public string Name => IsBaseRelays ? L.T("shell.network.relays") : Entry.Name;

    /// <summary>Hover text of the name: what the basic relays are, or the full name of a network when it is cut short.</summary>
    public string Tip => IsBaseRelays ? L.T("shell.network.relays.detail") : Entry.Name;

    public string TrustText => L.T(Entry.Trust switch
    {
        NetworkTrust.BuiltIn => "shell.network.trust.builtin",
        NetworkTrust.Reviewed => "shell.network.trust.reviewed",
        NetworkTrust.TestReviewed => "shell.network.trust.test",
        _ => "shell.network.trust.unreviewed",
    });

    public bool IsUnreviewed => Entry.Trust == NetworkTrust.Unreviewed;

    /// <summary>What it can do, written out: "Invites, Public rooms, Messages to friends".</summary>
    public string CanText => string.Join(L.T("common.list_separator"), NetworkOverview.AbilityKeys(Entry.Abilities).Select(L.T));

    /// <summary>The line under the name: how the basic relays are used, or why a network cannot be connected.</summary>
    public string Detail => IsBaseRelays ? L.T(IsOn ? "shell.network.relays.in_use" : "shell.network.relays.idle")
        : Entry.CanSwitch ? "" : L.T("shell.network.no_nodes");

    public bool HasDetail => Detail.Length > 0;

    public bool IsOn => Entry.Use == NetworkUse.On;
    public bool IsConnecting => Entry.Use == NetworkUse.Connecting;
    public bool IsOff => Entry.Use == NetworkUse.Off;

    /// <summary>The basic relays have no switch: they are used whenever a room needs them.</summary>
    public bool CanSwitch => Entry.CanSwitch;

    public string SwitchName => L.F("shell.network.switch", Name);

    /// <summary>
    /// The switch. Setting it asks the panel to connect or disconnect; what it shows afterwards is the real state, so a
    /// connect that could not start (something is asked first) leaves the switch off.
    /// </summary>
    public bool Wanted
    {
        get => !IsOff;
        set
        {
            if (value != !IsOff) _toggle(this);
            Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(Wanted)));
        }
    }

    internal void Relabel() => OnPropertyChanged((string?)null);
}

/// <summary>
/// The network status in the top bar and the panel behind it (redesign §"network status"): which networks Jay is using
/// right now, on every page. The basic relays (built in, invites only) are one row; each public network the player has
/// is another, with a switch. The multiplayer page still owns the connections; this is a view over its state.
/// </summary>
public sealed partial class NetworkStatusViewModel : ObservableObject
{
    private readonly MultiplayerViewModel _multiplayer;
    private readonly Action<string> _navigate;

    public NetworkStatusViewModel(MultiplayerViewModel multiplayer, Action<string> navigate)
    {
        _multiplayer = multiplayer;
        _navigate = navigate;
        multiplayer.PropertyChanged += OnMultiplayerChanged;
        multiplayer.NetworkChoices.CollectionChanged += OnChoicesChanged;
        L.Changed += () => Dispatcher.UIThread.Post(() =>
        {
            foreach (var row in Rows) row.Relabel();
            Refresh();
        });
        Refresh();
    }

    public ObservableCollection<NetworkStatusRow> Rows { get; } = [];

    /// <summary>The text next to the light: "Not connected", "Basic relays", the network's name or "2 networks".</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Tip))] public partial string StatusText { get; set; } = "";
    [ObservableProperty] public partial bool IsOn { get; set; }
    [ObservableProperty] public partial bool IsConnecting { get; set; }
    [ObservableProperty] public partial bool IsOff { get; set; } = true;

    public string Tip => L.F("shell.network.tip", StatusText);

    /// <summary>"For a child": only the basic relays, and no way to add a network from here.</summary>
    public bool IsChildMode => !_multiplayer.ShowPublicNetworks;

    public bool CanAddNetworks => !IsChildMode;

    /// <summary>No public network to list (and not because of "for a child"): say that invites still work.</summary>
    public bool ShowEmptyNote => !IsChildMode && Rows.Count <= 1;

    /// <summary>Raised when an action leaves the panel (it should close).</summary>
    public event Action? Done;

    private void OnChoicesChanged(object? sender, NotifyCollectionChangedEventArgs e) => Refresh();

    private void OnMultiplayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or nameof(MultiplayerViewModel.IsPublicConnected) or nameof(MultiplayerViewModel.IsPublicConnecting)
            or nameof(MultiplayerViewModel.IsHosting) or nameof(MultiplayerViewModel.IsJoined) or nameof(MultiplayerViewModel.IsIdle)
            or nameof(MultiplayerViewModel.SelectedNetworkChoice) or nameof(MultiplayerViewModel.ShowPublicNetworks)
            or nameof(MultiplayerViewModel.HasPublicNodes))
            Refresh();
    }

    /// <summary>Rebuild the rows from the multiplayer state. Rows that stay keep their object (and the keyboard focus).</summary>
    public void Refresh()
    {
        var sources = _multiplayer.NetworkChoices.Select(c => new NetworkSource(c.Id, c.Name, c.IsBuiltIn, c.Reviewed, c.TestReviewed, c.NodeCount));
        var entries = NetworkOverview.Build(sources, _multiplayer.SelectedNetworkChoice?.Id, _multiplayer.IsPublicConnected, _multiplayer.IsPublicConnecting,
            inRoom: _multiplayer.IsHosting || _multiplayer.IsJoined, inviteOnly: IsChildMode);
        for (var i = 0; i < entries.Count; i++)
        {
            if (i < Rows.Count && Rows[i].Id == entries[i].Id) Rows[i].Entry = entries[i];
            else if (i < Rows.Count) Rows[i] = new NetworkStatusRow(entries[i], Toggle);
            else Rows.Add(new NetworkStatusRow(entries[i], Toggle));
        }
        while (Rows.Count > entries.Count) Rows.RemoveAt(Rows.Count - 1);

        var (key, args, light) = NetworkOverview.Status(entries);
        StatusText = L.F(key, args);
        IsOn = light == NetworkUse.On;
        IsConnecting = light == NetworkUse.Connecting;
        IsOff = light == NetworkUse.Off;
        OnPropertyChanged(nameof(IsChildMode));
        OnPropertyChanged(nameof(CanAddNetworks));
        OnPropertyChanged(nameof(ShowEmptyNote));
    }

    /// <summary>
    /// The switch on a network's row. On: make it the current network and connect. When connecting needs something first
    /// (the multiplayer component, the public network notice), go to Public rooms, where that is asked. Off: disconnect.
    /// </summary>
    [RelayCommand]
    private void Toggle(NetworkStatusRow? row)
    {
        if (row is null || !row.CanSwitch) return;
        if (!row.IsOff)
        {
            _multiplayer.DisconnectPublicCommand.Execute(null);
            Refresh();
            return;
        }
        if (_multiplayer.NetworkChoices.FirstOrDefault(c => c.Id == row.Id) is not { } choice) return;
        if (!ReferenceEquals(_multiplayer.SelectedNetworkChoice, choice)) _multiplayer.SelectedNetworkChoice = choice;
        _multiplayer.ConnectPublicCommand.Execute(null);
        Refresh();
        if (!_multiplayer.IsPublicConnecting && !_multiplayer.IsPublicConnected)
        {
            Done?.Invoke();
            _navigate("play/public");
        }
    }

    [RelayCommand]
    private void AddNetwork()
    {
        Done?.Invoke();
        _navigate(Amber.Core.Navigation.LegacyRoutes.PublicNetworks);
    }

    [RelayCommand]
    private void OpenSettings()
    {
        Done?.Invoke();
        _navigate(Amber.Core.Navigation.LegacyRoutes.NetworkSettings);
    }
}
