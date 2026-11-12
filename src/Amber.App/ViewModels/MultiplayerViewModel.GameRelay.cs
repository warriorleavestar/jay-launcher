using Amber.Core.Localization;
using Amber.Core.Multiplayer;
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
    /// </summary>
    private async Task<(int Port, GamePortRelay? Relay)?> HostGamePortAsync(bool checkManual)
    {
        var manual = ManualPortValue;
        var port = manual ?? SelectedLanGame?.Game.Port;
        if (port is { } known)
        {
            if ((manual is null || checkManual) && !await GamePortReadyAsync(known, manual: manual is not null))
            {
                SetStatus(await PortUnavailableAsync(known));
                return null;
            }
            // A game in the strong sandbox can't be reached by EasyTier or the direct bridge over loopback, only
            // through Jay (GameLanBridge): put the relay in front of the game whenever such a game is running.
            if (!FullLanMode && GameLanBridge.HasAgents)
            {
                var front = new GamePortRelay();
                front.SetTarget(known);
                return (front.Port, front);
            }
            return (known, null);
        }
        // Full-LAN rooms reach the host's virtual address, not loopback, so the relay can't sit in front of the game.
        if (FullLanMode)
        {
            SetStatus(StatusMessage.Warning("multiplayer.relay.full_lan_needs_game"));
            return null;
        }
        var relay = new GamePortRelay();
        return (relay.Port, relay);
    }

    /// <summary>
    /// The game port didn't answer. When something does listen there, it is most likely a game in the strong sandbox
    /// whose LAN bridge isn't connected (started before Jay was restarted): say to restart the game.
    /// </summary>
    private static async Task<StatusMessage> PortUnavailableAsync(int port) =>
        await GameLanBridge.ListenedButUnreachableAsync(port)
            ? StatusMessage.Error("multiplayer.lan.port_unreachable_sandbox", port)
            : StatusMessage.Error("multiplayer.lan.port_unavailable", port);

    private void AdoptGameRelay(GamePortRelay? relay)
    {
        if (relay is null) return;
        _gameRelay = relay;
        // A relay put in front of a known game (strong sandbox) is linked already.
        _relayGameLabel = relay.Target is { } port
            ? ManualPortValue is not null ? L.F("multiplayer.guide.port_label", port) : SelectedLanGame?.Label ?? L.F("multiplayer.guide.port_label", port)
            : null;
        IsWaitingForGame = relay.Target is null;
        OnPropertyChanged(nameof(HasGameRelay));
        OnPropertyChanged(nameof(GameLinkText));
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
            SetStatus(await PortUnavailableAsync(port));
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
