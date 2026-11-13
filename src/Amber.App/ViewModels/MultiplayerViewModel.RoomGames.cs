using System.Collections.ObjectModel;
using Amber.Core.Localization;
using Amber.Core.Multiplayer;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

/// <summary>A row in the room's game list: a world open to LAN on this computer, or the port the player typed.</summary>
public sealed record RoomGameItem(LanGame Game, bool IsLinked, bool IsTyped)
{
    public string Name => IsTyped ? L.T("multiplayer.roomgames.typed") : Game.Motd;
    public string PortText => L.F("multiplayer.guide.port_label", Game.Port);
    public bool CanUse => !IsLinked && !IsTyped;
}

/// <summary>
/// The room's game list. While a room runs on a game relay, this computer's LAN broadcasts are heard all the time:
/// each world open to LAN is listed, one that closes drops off, and clicking one points the room at it. A room
/// waiting for its game takes the first world opened; a room whose world closed follows it to a new port (see
/// <see cref="LanGameWatch.AutoPick"/>). A port the player typed stays theirs to change. A typed port is for games that
/// don't broadcast, and sits under "Other port…".
/// </summary>
public sealed partial class MultiplayerViewModel
{
    private readonly LanGameWatch _roomWatch = new();
    private readonly HashSet<int> _roomHeard = [];
    private CancellationTokenSource? _roomWatchCts;

    public ObservableCollection<RoomGameItem> RoomGames { get; } = [];
    public bool HasRoomGames => RoomGames.Count > 0;

    /// <summary>The typed-port box under the list is open.</summary>
    [ObservableProperty] public partial bool RoomOtherPortOpen { get; set; }

    /// <summary>Tests: stands in for listening to LAN broadcasts, and for the clock that ages them.</summary>
    internal Func<Action<LanGame>, CancellationToken, Task>? RoomGameListenForTest { get; set; }
    internal Func<DateTime>? RoomGamesClockForTest { get; set; }
    private DateTime RoomGamesNow => RoomGamesClockForTest?.Invoke() ?? DateTime.UtcNow;

    private void StartRoomGameWatch()
    {
        StopRoomGameWatch();
        var cts = _roomWatchCts = new CancellationTokenSource();
        var ct = cts.Token;
        var listen = RoomGameListenForTest ?? Amber.Core.Multiplayer.LanGames.WatchAsync;
        _ = Task.Run(async () =>
        {
            try
            {
                await listen(game => Dispatcher.UIThread.Post(() => { if (!ct.IsCancellationRequested) RoomGameHeard(game); }), ct);
            }
            catch (Exception e) when (e is System.Net.Sockets.SocketException or ObjectDisposedException or InvalidOperationException)
            {
                // Can't listen (another program holds the port without sharing it): the list stays empty, and the
                // game's log and a typed port still link the room.
                if (!ct.IsCancellationRequested)
                    Dispatcher.UIThread.Post(() => _diag.Add(L.T("multiplayer.transport.diagnostics_category"), $"LAN: {e.Message} ({e.GetType().Name})"));
            }
        }, CancellationToken.None);
        RebuildRoomGames();
    }

    private void StopRoomGameWatch()
    {
        _roomWatchCts?.Cancel();
        _roomWatchCts?.Dispose();
        _roomWatchCts = null;
        _roomWatch.Clear();
        _roomHeard.Clear();
        RoomOtherPortOpen = false;
        RebuildRoomGames();
    }

    /// <summary>A broadcast from this computer. Tests feed it directly.</summary>
    internal void RoomGameHeard(LanGame game)
    {
        if (_gameRelay is not { } relay || game.Port == relay.Port) return;
        _roomHeard.Add(game.Port);
        if (!_roomWatch.Saw(game, RoomGamesNow)) return;
        RebuildRoomGames();
        FollowRoomGame();
    }

    /// <summary>On the room refresh: drop worlds that went quiet, and follow one that moved.</summary>
    internal void ExpireRoomGames()
    {
        if (_gameRelay is null || !_roomWatch.Expire(RoomGamesNow)) return;
        RebuildRoomGames();
        FollowRoomGame();
    }

    private void FollowRoomGame()
    {
        if (_gameRelay is not { } relay) return;
        var target = relay.Target;
        // A game someone is playing in stays linked, whatever its broadcasts do.
        if (target is { } t && relay.OpenConnectionsTo(t) > 0) return;
        var typed = target is null ? ManualPortValue is not null : _relayTyped;
        var pick = LanGameWatch.AutoPick(_roomWatch.Current(RoomGamesNow), target, typed, relay.Port,
            linkedWasHeard: target is { } heard && _roomHeard.Contains(heard));
        if (pick is { } port && _roomWatch.Current(RoomGamesNow).FirstOrDefault(g => g.Port == port) is { } game) UseRoomGame(game);
    }

    /// <summary>Points the room at a listed world: it goes through the same check and link as a world found by [Find].</summary>
    private void UseRoomGame(LanGame game)
    {
        if (LanGames.FirstOrDefault(g => g.Game.Port == game.Port) is { } known)
        {
            // The game's log names the version; the broadcast names the world, which is what the player recognises.
            if (known.Game.Motd != game.Motd) LanGames[LanGames.IndexOf(known)] = known with { Game = game };
        }
        else LanGames.Add(new LanGameItem(game, L.T("multiplayer.lan.broadcast_source")));
        LinkFoundGame(game.Port);
    }

    [RelayCommand]
    private void UseRoomGameItem(RoomGameItem? item)
    {
        if (item is not { CanUse: true }) return;
        UseRoomGame(item.Game);
    }

    [RelayCommand]
    private void ToggleRoomOtherPort() => RoomOtherPortOpen = !RoomOtherPortOpen;

    /// <summary>
    /// The listed worlds (newest first), with the linked one marked. A linked port that isn't broadcasting (typed, or
    /// linked from the game's log before its first broadcast) is listed too, so the list always shows what the room uses.
    /// </summary>
    private void RebuildRoomGames()
    {
        var relay = _gameRelay;
        var rows = new List<RoomGameItem>();
        if (relay is not null)
        {
            var target = relay.Target;
            foreach (var game in _roomWatch.Current(RoomGamesNow))
                rows.Add(new RoomGameItem(game, game.Port == target, _relayTyped && game.Port == target));
            if (target is { } t && rows.All(r => r.Game.Port != t))
            {
                var name = _relayTyped ? "" : SelectedLanGame?.Game.Port == t ? SelectedLanGame.Game.Motd : _relayGameLabel ?? "";
                rows.Insert(0, new RoomGameItem(new LanGame(name, t), true, _relayTyped));
            }
        }
        if (rows.SequenceEqual(RoomGames)) return;
        RoomGames.Clear();
        foreach (var row in rows) RoomGames.Add(row);
        OnPropertyChanged(nameof(HasRoomGames));
    }
}
