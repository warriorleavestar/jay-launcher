using Amber.Core.Localization;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Transport;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

/// <summary>
/// Open the room first, the game later: with no LAN world and no typed port, a room (direct or invite) starts on a
/// <see cref="GamePortRelay"/> and the invite can go out right away. When the world is opened to LAN (from the game's
/// log or a LAN scan) or a port is typed, the port gets the same check as before a room opens and the relay starts
/// passing connections on. A world reopened on a new port is picked up the same way.
/// </summary>
public sealed partial class MultiplayerViewModel
{
    private GamePortRelay? _gameRelay;
    private int _relayCheck;
    private string? _relayGameLabel;

    /// <summary>The delay before a typed port is checked, so every keystroke doesn't report a closed port.</summary>
    internal TimeSpan RelayTypingDelay { get; set; } = TimeSpan.FromMilliseconds(600);

    /// <summary>The room is open and waiting for the game to be opened to LAN.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GameLinkText))]
    public partial bool IsWaitingForGame { get; set; }

    /// <summary>This room was opened before the game: show the game link line in the room.</summary>
    public bool HasGameRelay => _gameRelay is not null;

    public string GameLinkText => _gameRelay is null ? ""
        : IsWaitingForGame ? L.T("multiplayer.relay.waiting")
        : L.F("multiplayer.relay.linked", _relayGameLabel ?? "", _gameRelay.Target ?? 0);

    /// <summary>Tests: the relay's own port (the room's game port) and where it points.</summary>
    internal GamePortRelay? GameRelayForTest => _gameRelay;

    /// <summary>
    /// The game port for a new room. A typed port or a found LAN world is checked as before (<paramref name="checkManual"/>:
    /// the direct room has never checked typed ports). With neither, a relay is started and returned for the caller to
    /// <see cref="AdoptGameRelay"/> once the room is up (or to dispose if it fails). Null: blocked, status already set.
    /// <para>
    /// Whole-LAN rooms open without a game too: they are as much for other games, sharing a connection and going online
    /// through a member. See <see cref="AdoptGameRelay"/> for how their guests reach the relay.
    /// </para>
    /// </summary>
    private async Task<(int Port, GamePortRelay? Relay)?> HostGamePortAsync(bool checkManual)
    {
        var manual = ManualPortValue;
        var port = manual ?? SelectedLanGame?.Game.Port;
        if (port is { } known)
        {
            if ((manual is null || checkManual) && !await GamePortReadyAsync(known, manual: manual is not null))
            {
                SetStatus(StatusMessage.Error("multiplayer.lan.port_unavailable", known));
                return null;
            }
            return (known, null);
        }
        var relay = new GamePortRelay();
        return (relay.Port, relay);
    }

    /// <summary>
    /// Whether the relay must also listen on this computer's virtual address: a whole-LAN room whose traffic isn't
    /// wrapped, where guests reach that address directly. Wrapped traffic (and every other room) arrives on loopback.
    /// </summary>
    internal static bool RelayOnAdapter(bool fullLan, bool wraps) => fullLan && !wraps;

    /// <param name="onAdapter">Also listen on the virtual address (see <see cref="RelayOnAdapter"/>); a relay that can't
    /// gets the room stopped with the reason.</param>
    private void AdoptGameRelay(GamePortRelay? relay, bool onAdapter = false)
    {
        if (relay is null) return;
        if (onAdapter) _ = ListenOnAdapterAsync(relay);
        _gameRelay = relay;
        _relayGameLabel = null;
        IsWaitingForGame = true;
        OnPropertyChanged(nameof(HasGameRelay));
        OnPropertyChanged(nameof(GameLinkText));
    }

    private async Task ListenOnAdapterAsync(GamePortRelay relay)
    {
        try { await relay.ListenOnAdapterAsync(System.Net.IPAddress.Parse(EasyTierArgs.HostIp)); }
        catch (OperationCanceledException) { }
        catch (System.Net.Sockets.SocketException e)
        {
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_gameRelay != relay) return;
                FailRoomOnAdapter(e, relay.Port);
            });
        }
    }

    private void StopGameRelay()
    {
        _relayCheck++;
        if (_gameRelay is { } relay) _ = relay.DisposeAsync().AsTask();
        _gameRelay = null;
        _relayGameLabel = null;
        IsWaitingForGame = false;
        OnPropertyChanged(nameof(HasGameRelay));
        OnPropertyChanged(nameof(GameLinkText));
    }

    /// <summary>A LAN world was picked or a port typed while the room waits (or runs) on a relay: point it at the game.</summary>
    private async Task RetargetGameRelayAsync(bool typed)
    {
        if (_gameRelay is not { } relay) return;
        var version = ++_relayCheck;
        var manual = ManualPortValue;
        var game = SelectedLanGame;
        if ((manual ?? game?.Game.Port) is not { } port || port == relay.Port || port == relay.Target) return;
        if (typed && RelayTypingDelay > TimeSpan.Zero)
        {
            await Task.Delay(RelayTypingDelay);
            if (version != _relayCheck || _gameRelay != relay) return;
        }
        var ok = await GamePortReadyAsync(port, manual: manual is not null);
        if (version != _relayCheck || _gameRelay != relay) return;
        if (!ok)
        {
            SetStatus(StatusMessage.Error("multiplayer.lan.port_unavailable", port));
            return;
        }
        relay.SetTarget(port);
        _relayGameLabel = manual is not null ? L.F("multiplayer.guide.port_label", port) : game?.Label ?? "";
        var wasWaiting = IsWaitingForGame;
        IsWaitingForGame = false;
        OnPropertyChanged(nameof(GameLinkText));
        SetStatus(StatusMessage.Success(wasWaiting ? "multiplayer.relay.connected" : "multiplayer.relay.switched", _relayGameLabel));
    }

    /// <summary>Tests: what a room-opening command does about the game port, without starting a room.</summary>
    internal async Task<bool> PrepareGameRelayForTest()
    {
        if (await HostGamePortAsync(checkManual: false) is not (_, var relay)) return false;
        AdoptGameRelay(relay);
        return true;
    }

    /// <summary>Self-check: back to a room without a relay.</summary>
    internal void StopGameRelayForTest() => StopGameRelay();

    /// <summary>The room card's [Find]: listen for LAN worlds; a found one is picked up by the relay.</summary>
    [RelayCommand]
    private Task FindGameForRoomAsync() => ScanAsync();
}
