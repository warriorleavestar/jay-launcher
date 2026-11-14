using Amber.Core.Localization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

/// <summary>
/// [▶ Play and open a room] (official design §6.14, journey J5a): with no LAN world yet, the lobby button starts the
/// focus instance, shows the three in-game steps, and opens the room by itself once the log says "Open to LAN".
/// </summary>
public sealed partial class MultiplayerViewModel
{
    /// <summary>Starts an instance (set by the main view model: the library's ▶).</summary>
    public Action<string>? StartInstance { get; set; }

    /// <summary>The game was started from the lobby and Jay is waiting for "Open to LAN".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HostButtonText))]
    [NotifyPropertyChangedFor(nameof(ShowHostButton))]
    public partial bool IsWaitingForLan { get; set; }

    /// <summary>The instance the lobby would start: the focus instance.</summary>
    public string? HostGameInstance => _service.CurrentOr(Instances, Instances.FirstOrDefault());

    /// <summary>No LAN world and no manual port, but an instance to start: the button starts the game first.</summary>
    public bool StartsGameToHost => !HasHostGame && HostGameInstance is not null && StartInstance is not null && !IsServerlessChosen;

    public string HostButtonText => !IsComponentReady ? ComponentStepText
        : IsServerlessChosen ? L.T("multiplayer.open.make_sealed")
        : StartsGameToHost ? L.T("multiplayer.lobby.play_and_host") : L.T("multiplayer.lobby.host_button");

    public bool ShowHostButton => !IsWaitingForLan;

    /// <summary>Under the button before pressing: which game it will start.</summary>
    public string PlayAndHostHint => HostGameInstance is { } id ? L.F("multiplayer.lobby.play_and_host_hint", id) : "";

    /// <summary>While waiting: the three steps in the game.</summary>
    public string WaitingForLanText => L.F("multiplayer.lobby.waiting_for_lan", _waitingInstance ?? HostGameInstance ?? "");

    private string? _waitingInstance;

    /// <summary>Returns true when the press started the game instead of opening a room.</summary>
    private bool StartGameToHost()
    {
        if (!StartsGameToHost || HostGameInstance is not { } id) return false;
        _waitingInstance = id;
        _waitingSince = DateTimeOffset.Now;
        IsWaitingForLan = true;
        OnPropertyChanged(nameof(WaitingForLanText));
        StartInstance!(id);
        return true;
    }

    /// <summary>
    /// The game's log said "Open to LAN": open the room the player already asked for. Only the game Jay started for
    /// it counts; another instance opening its world to LAN does not open a room.
    /// </summary>
    private void OnLanOpenedWhileWaiting(string versionId)
    {
        if (!IsWaitingForLan || !string.Equals(_waitingInstance, versionId, StringComparison.OrdinalIgnoreCase)) return;
        IsWaitingForLan = false;
        _waitingInstance = null;
        if (IsIdle) HostFromLobbyCommand.Execute(null);
    }

    [RelayCommand]
    private void CancelWaitForLan()
    {
        IsWaitingForLan = false;
        _waitingInstance = null;
    }

    /// <summary>
    /// A game session started (set up by the main view model). When the session of the game started for the wait ends
    /// (quit, crashed, failed or cancelled before "Open to LAN"), the wait ends with it.
    /// </summary>
    public void WatchGameSession(Amber.Core.Launch.LaunchSession session) => session.Changed += s =>
    {
        if (s.IsActive) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (IsWaitingForLan && s.Started >= _waitingSince && string.Equals(_waitingInstance, s.InstanceId, StringComparison.OrdinalIgnoreCase))
                CancelWaitForLan();
        });
    };

    /// <summary>When the lobby started the game it waits on: only sessions from then on end the wait.</summary>
    private DateTimeOffset _waitingSince;

    /// <summary>Tests: a line from a game's log, as if the game wrote it.</summary>
    internal void GameOutputForTest(string versionId, string line) => OnGameOutput(versionId, line);

    private void NotifyPlayAndHost()
    {
        OnPropertyChanged(nameof(StartsGameToHost));
        OnPropertyChanged(nameof(HostButtonText));
        OnPropertyChanged(nameof(PlayAndHostHint));
    }
}
