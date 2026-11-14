using Amber.App.Localization;
using Amber.Core.Localization;
using Amber.Core.Multiplayer;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

/// <summary>
/// "Share with them" under a person in the room (UI restructure: people, rooms, networks): remote help, letting people
/// get online through my computer, and forwarding their game. Each one is turned on from here behind the locked
/// confirmation, and each row says plainly who it reaches: remote help is between two people, the other two hold for
/// everyone in the room.
/// </summary>
public sealed partial class MultiplayerViewModel
{
    /// <summary>The group shows for someone else who is in the room right now.</summary>
    public bool ShowShareGroup => SelectedPerson is { IsMe: false, IsOnline: true };

    // ---- remote help ----

    /// <summary>Remote help runs between the host and one member, so a guest can only ask the host.</summary>
    private bool RemoteHelpFits => SelectedPerson is { } person && (IsHosting ? person.Member is not null : person.IsHost);

    /// <summary>With calls on, remote help is the last step of a call (share, then let them control) and has no entry of its own.</summary>
    public bool ShowRemoteHelp => ShowShareGroup && ShowRemoteControl && !Amber.Core.Calls.CallFeature.Enabled;

    public bool CanAskRemoteHelp => ShowRemoteHelp && RemoteHelpFits && CanRemoteControl && !RemoteWaiting;

    /// <summary>Why the button is off (empty when it can be pressed).</summary>
    public string RemoteHelpNote => !ShowRemoteHelp ? ""
        : !RemoteHelpFits ? L.T("multiplayer.share.remote.host_only")
        : !CanRemoteControl ? RemoteUnavailableText
        : "";

    public bool HasRemoteHelpNote => RemoteHelpNote.Length > 0;

    /// <summary>Asks first on this side (what I am about to see and do); their computer then asks them too.</summary>
    [RelayCommand]
    private async Task AskRemoteHelpAsync()
    {
        if (!CanAskRemoteHelp || SelectedPerson is not { } person) return;
        var name = person.Name.Length > 0 ? person.Name : L.T("multiplayer.peers.player");
        if (!await Confirm(L.F("multiplayer.share.remote.confirm_title", name), L.F("multiplayer.share.remote.confirm_body", name), L.T("multiplayer.share.remote.confirm"))) return;
        if (SelectedPerson != person) return;
        if (IsHosting) await RequestRemoteCommand.ExecuteAsync(person.Member);
        else await ControlHostCommand.ExecuteAsync(null);
    }

    // ---- forwarding their game ----

    /// <summary>Shown outside "For a child".</summary>
    public bool ShowShareNetwork => ShowShareGroup && ShowNetworkExit;

    /// <summary>My computer forwards the game of people in the room right now.</summary>
    public bool SharingGameProxy
    {
        get => _currentOptions?.AllowProxy == true;
        set => _ = SetGameProxySharingAsync(value);
    }

    /// <summary>In an invite room whose protection wraps traffic, members only hold a key with the host: only the host can forward.</summary>
    private bool GameProxyFits => _currentOptions is { } o && (o.IsHost || !(o.Room.FromInvite && o.Room.Tunnel.Wraps));

    /// <summary>Not while another switch is restarting the component (see <see cref="_componentRestarting"/>).</summary>
    public bool CanShareGameProxy => ShowShareNetwork && GameProxyFits && !_componentRestarting && !IsRekeying;

    public string GameProxyShareNote => ShowShareNetwork && _currentOptions is not null && !GameProxyFits ? L.T("multiplayer.proxy.host_only") : "";

    public bool HasGameProxyShareNote => GameProxyShareNote.Length > 0;

    /// <summary>Tests and self-checks: stands in for restarting the multiplayer component with the new setting.</summary>
    internal Func<EasyTierOptions, bool>? TestRestartForProxy { get; set; }

    /// <summary>Tests and self-checks: the options my side of the room would be running with.</summary>
    internal void UseTestOptions(EasyTierOptions? options)
    {
        _currentOptions = options;
        NotifyShare();
    }

    /// <summary>
    /// Turning it on asks first, with what it means: their game reaches the internet as my IP. It is a start-up
    /// setting of the multiplayer component, so the component restarts (a few seconds; chat reconnects).
    /// </summary>
    internal async Task SetGameProxySharingAsync(bool on, bool forChildMode = false)
    {
        // "For a child" hides the switch, but still turns forwarding off in the open room.
        var allowed = forChildMode ? !on && !_componentRestarting && !IsRekeying : CanShareGameProxy;
        if (_currentOptions is not { } current || current.AllowProxy == on || !allowed)
        {
            NotifyShare();
            return;
        }
        SetComponentRestarting(true);
        try
        {
            if (on && !await Confirm(L.T("multiplayer.share.proxy.confirm_title"), L.T("multiplayer.proxy.allow_warning"), L.T("multiplayer.share.proxy.confirm"))) return;
            if (_currentOptions != current) return;
            var room = _room;
            var next = current with { AllowProxy = on };
            bool done;
            if (TestRestartForProxy is { } stand)
            {
                done = stand(next);
                if (done) _currentOptions = next;
            }
            else done = await RestartForProxyAsync(next);
            if (!done)
            {
                // Still in the same room, so nothing changed: say so. (A restart that failed has closed the room
                // and left its own message.)
                if (ReferenceEquals(_room, room)) SetStatus(StatusMessage.Warning("multiplayer.share.proxy.failed"));
                return;
            }
            AllowGameProxy = on;
            SetStatus(StatusMessage.Info(on ? "multiplayer.share.proxy.turned_on" : "multiplayer.share.proxy.turned_off"));
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            SetStatus(StatusMessage.Failure("multiplayer.room.start_failed", e, ErrorText.Of(e)));
        }
        finally
        {
            SetComponentRestarting(false);
        }
    }

    // ---- getting online through my computer ----

    /// <summary>Whole traffic through my computer: decided when my side of the room starts (it needs the whole-LAN scope).</summary>
    public bool SharingExit => _currentOptions is { Tun: true, AllowExit: true };

    public string ExitShareText => L.T(SharingExit ? "multiplayer.share.exit.on" : "multiplayer.share.exit.off");

    private void NotifyShare()
    {
        foreach (var name in (string[])[nameof(ShowShareGroup), nameof(ShowRemoteHelp), nameof(CanAskRemoteHelp), nameof(RemoteHelpNote), nameof(HasRemoteHelpNote),
                     nameof(ShowShareNetwork), nameof(SharingGameProxy), nameof(CanShareGameProxy), nameof(GameProxyShareNote), nameof(HasGameProxyShareNote),
                     nameof(SharingExit), nameof(ExitShareText)])
            OnPropertyChanged(name);
    }

    private void InitShare()
    {
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SelectedPerson) or nameof(Mode) or nameof(CanRemoteControl) or nameof(ShowRemoteControl) or nameof(RemoteWaiting)
                or nameof(ShowNetworkExit) or nameof(IsRekeying) or nameof(CanUseGameProxy) or nameof(RemoteUnavailableText))
                NotifyShare();
        };
    }
}
