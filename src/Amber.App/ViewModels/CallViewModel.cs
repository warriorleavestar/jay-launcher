using System.Collections.ObjectModel;
using Amber.App.Services;
using Amber.Core.Activity;
using Amber.Core.Calls;
using Amber.Core.Localization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

/// <summary>One tile on the call stage: a person (their face while the camera is off), or the screen they share.</summary>
public sealed record CallTile(string Name, bool IsMe, bool Speaking, bool CameraOn, bool IsScreen)
{
    public string Label => IsScreen ? L.F("call.screen_of", Name) : IsMe ? L.T("call.me") : Name;
    public bool ShowFace => !IsScreen && !CameraOn;
}

/// <summary>
/// The call panel, the top bar's "In a call" chip, the incoming call request, the control confirmation and the red
/// bar and frame (redesign batch 11, reserved). There is no media yet: this is the layout and the rules, behind
/// <see cref="CallFeature.Enabled"/> (off by default). The microphone and camera start off; control needs "just this
/// once" every time and stops with Ctrl+Shift+F12; ending the call ends sharing and control.
/// </summary>
public sealed partial class CallViewModel : ObservableObject
{
    private readonly LauncherService _service;
    private CallSession? _session;
    /// <summary>Calls ringing now, each with who it is (two people can ring one after the other).</summary>
    private readonly Dictionary<RequestHandle, Ringing> _ringing = [];

    private sealed record Ringing(string Name, CallRoute Route, bool Verified);

    private readonly bool? _available;

    /// <param name="available">Tests pass true to switch calls on for this view model only; otherwise <see cref="CallFeature.Enabled"/>.</param>
    public CallViewModel(LauncherService service, bool? available = null)
    {
        _service = service;
        _available = available;
        service.Requests.Resolved += OnRequestResolved;
    }

    /// <summary>Whether any call button shows at all (the reserved feature's switch).</summary>
    public bool Available => _available ?? CallFeature.Enabled;

    /// <summary>Goes to the page that holds the panel ("people" for a friend, "play" for room voice).</summary>
    public Action<string>? Navigate { get; set; }

    private Amber.Core.Safety.ChildModeEffects Child => _service.Settings.ChildMode?.Effects ?? default;

    /// <summary>Video (the camera) is offered: not for a child.</summary>
    public bool OffersVideo => Available && CallRules.Offered(CallStep.Camera, Child);

    /// <summary>Control is offered: not for a child.</summary>
    public bool OffersControl => Available && CallRules.Offered(CallStep.Control, Child);

    public CallSession? Session => _session;
    public bool IsInCall => _session is { Ended: false };
    public bool IsRoomCall { get; private set; }
    public bool ShowInPeople => IsInCall && !IsRoomCall;
    public bool ShowInRoom => IsInCall && IsRoomCall;

    /// <summary>A new call can start: one call at a time.</summary>
    public bool CanStartShare => Available && !IsInCall;

    /// <summary>The friend for a friend call; the other person a share or control is with.</summary>
    public string Peer { get; private set; } = "";

    public string Title => _session?.Title ?? "";
    public string ChipText => L.F("call.chip", Title);
    public string RouteText => _session?.Route == CallRoute.Direct ? L.T("call.route.direct") : L.T("call.route.relay");

    public ObservableCollection<CallTile> Tiles { get; } = [];

    /// <summary>The shared screen takes the big tile; null while nobody shares.</summary>
    [ObservableProperty] public partial CallTile? StageTile { get; set; }

    public bool HasStage => StageTile is not null;

    public bool MicOn => _session?.MicOn == true;
    public bool CameraOn => _session?.CameraOn == true;
    public bool Sharing => _session?.Sharing == true;
    public bool CanLetControl => _session?.CanLetControl == true;
    public bool IsControlled => _session?.ControlledBy is not null;
    public string ControlBarText => L.F("call.control.bar", _session?.ControlledBy ?? "");
    public string ControlQuestion => L.F("call.control.ask", Peer);

    /// <summary>The locked "just this once" confirmation is showing (focus on [Don't allow]).</summary>
    [ObservableProperty] public partial bool ConfirmingControl { get; set; }

    /// <summary>The last call's end, said once on the panel's page.</summary>
    [ObservableProperty] public partial string EndedText { get; set; } = "";

    public bool ShowEndedInPeople => !IsInCall && !IsRoomCall && EndedText.Length > 0;
    public bool ShowEndedInRoom => !IsInCall && IsRoomCall && EndedText.Length > 0;

    partial void OnEndedTextChanged(string value)
    {
        OnPropertyChanged(nameof(ShowEndedInPeople));
        OnPropertyChanged(nameof(ShowEndedInRoom));
    }

    private string Me => string.IsNullOrWhiteSpace(_service.Settings.PlayerName) ? L.T("call.me") : _service.Settings.PlayerName;

    /// <summary>Starts a call with a friend from their conversation: voice, video (camera on) or straight to sharing.</summary>
    public void StartWithFriend(string friend, CallStep step, CallRoute route = CallRoute.Relay)
    {
        if (!Available) return;
        Start(new CallSession(friend, route, Child, [new CallMember(friend, false, true)], Me), room: false, friend);
        if (step == CallStep.Camera) _session!.SetCamera(true);
        if (step == CallStep.Screen) _session!.SetSharing(true);
        Navigate?.Invoke("people");
    }

    /// <summary>[Voice] on the room banner: joins the room's voice. Nobody hears me until I turn the microphone on.</summary>
    public void JoinRoomVoice(string room, IEnumerable<string> members, CallRoute route = CallRoute.Relay)
    {
        if (!Available) return;
        Start(new CallSession(room, route, Child, members.Select(m => new CallMember(m, false, false)), Me), room: true, "");
    }

    /// <summary>[Share my screen with them] on a room member: a call with that one person, already sharing.</summary>
    public void ShareWith(string person, CallRoute route = CallRoute.Relay)
    {
        if (!Available) return;
        Start(new CallSession(person, route, Child, [new CallMember(person, false, false)], Me), room: true, person);
        _session!.SetSharing(true);
    }

    /// <summary>
    /// Someone calls: a request on the request bar (and the popup when Jay is in the background) that says who it is and
    /// whether they are a verified friend, with [Decline] and [Answer]. Returns null when this person may not ring.
    /// </summary>
    public RequestHandle? Ring(string name, bool verifiedFriend, bool sameRoom, CallRoute route = CallRoute.Relay)
    {
        if (!Available || !CallRules.MayRing(CallRules.Parse(_service.Settings.CallsWhoCanCall), verifiedFriend, sameRoom)) return null;
        // Busy: one call at a time. Whoever rings meanwhile does not get through (hang up first to take another).
        if (IsInCall) return null;
        var handle = _service.Requests.Post(new RequestSpec("call", L.F("call.ring.title", name),
            L.T(verifiedFriend ? "call.ring.friend" : "call.ring.room"),
            [new("decline", L.T("call.ring.decline"), IsDefault: true), new("answer", L.T("call.ring.answer"))],
            Timeout: TimeSpan.FromSeconds(45), DedupeKey: "call:" + name, TimeoutChoice: "decline") { Popup = true });
        _ringing[handle] = new Ringing(name, route, verifiedFriend);
        return handle;
    }

    /// <summary>Calls ringing now (tests).</summary>
    internal int RingingCount => _ringing.Count;

    private void OnRequestResolved(RequestHandle handle)
    {
        void Resolve()
        {
            if (!_ringing.Remove(handle, out var ring) || handle.Choice != "answer") return;
            // Answering one call declines the others still ringing: one call at a time.
            foreach (var other in _ringing.Keys.ToList()) _service.Requests.Resolve(other, "decline", RequestSource.Program);
            _ringing.Clear();
            // Answering does not turn the microphone on. A friend's call shows in their conversation; someone else in
            // the room (not a friend) talks in the room's panel, which is where they are.
            Start(new CallSession(ring.Name, ring.Route, Child, [new CallMember(ring.Name, false, ring.Verified)], Me), room: !ring.Verified, ring.Name);
            Navigate?.Invoke(ring.Verified ? "people" : "play");
        }
        if (Dispatcher.UIThread.CheckAccess()) Resolve();
        else Dispatcher.UIThread.Post(Resolve);
    }

    private void Start(CallSession session, bool room, string peer)
    {
        if (_session is { } old)
        {
            // A new call never runs alongside the old one: that one ends first (and stops sharing and control with it).
            if (!old.Ended) old.End("call.ended");
            old.Changed -= Sync;
        }
        _session = session;
        IsRoomCall = room;
        Peer = peer.Length > 0 ? peer : session.Members.FirstOrDefault(m => !m.IsMe)?.Name ?? "";
        EndedText = "";
        ConfirmingControl = false;
        session.Changed += Sync;
        Sync();
    }

    private void Sync()
    {
        Tiles.Clear();
        if (_session is { Ended: false } s)
            foreach (var m in s.Members)
                Tiles.Add(new CallTile(m.Name, m.IsMe, m.Speaking, m.IsMe ? s.CameraOn : m.CameraOn, false));
        StageTile = _session is { Sharing: true } shared ? new CallTile(Me, true, false, false, true) : null;
        if (_session is { Ended: true } ended)
        {
            EndedText = L.T(ended.EndReason ?? "call.ended");
            ConfirmingControl = false;
        }
        foreach (var name in (string[])[nameof(Available), nameof(IsInCall), nameof(IsRoomCall), nameof(ShowInPeople), nameof(ShowInRoom), nameof(Title), nameof(ChipText),
                     nameof(RouteText), nameof(MicOn), nameof(CameraOn), nameof(Sharing), nameof(CanLetControl), nameof(IsControlled), nameof(ControlBarText),
                     nameof(ControlQuestion), nameof(HasStage), nameof(Peer), nameof(OffersVideo), nameof(OffersControl),
                     nameof(ShowEndedInPeople), nameof(ShowEndedInRoom), nameof(CanStartShare)])
            OnPropertyChanged(name);
    }

    partial void OnStageTileChanged(CallTile? value) => OnPropertyChanged(nameof(HasStage));

    /// <summary>Called when the child-mode settings change: a child loses the camera and control at once.</summary>
    internal void ApplyChildMode()
    {
        if (_session is { } s && !CallRules.Offered(CallStep.Camera, Child))
        {
            s.SetCamera(false);
            s.StopControl();
        }
        Sync();
    }

    [RelayCommand] private void ToggleMic() => _session?.SetMic(!MicOn);
    [RelayCommand] private void ToggleCamera() => _session?.SetCamera(!CameraOn);

    [RelayCommand]
    private void ToggleShare()
    {
        _session?.SetSharing(!Sharing);
        if (!Sharing) ConfirmingControl = false;
    }

    /// <summary>[Let them control]: the locked confirmation first.</summary>
    [RelayCommand]
    private void AskControl()
    {
        if (CanLetControl) ConfirmingControl = true;
    }

    [RelayCommand]
    private void AllowControlOnce()
    {
        ConfirmingControl = false;
        _session?.StartControl(Peer, allowedThisOnce: true);
    }

    [RelayCommand] private void RefuseControl() => ConfirmingControl = false;

    /// <summary>The red bar's [Stop] and Ctrl+Shift+F12.</summary>
    [RelayCommand]
    public void StopControl() => _session?.StopControl();

    [RelayCommand] private void HangUp() => End("call.ended");

    /// <summary>The top bar chip: back to the page that holds the panel.</summary>
    [RelayCommand]
    private void OpenPanel() => Navigate?.Invoke(IsRoomCall ? "play" : "people");

    /// <summary>
    /// I left the room (or it closed): every call ends, room voice and a friend's call alike, since a call rides on the
    /// room's link as messages do. Calls still ringing stop too.
    /// </summary>
    public void RoomClosed()
    {
        foreach (var ring in _ringing.Keys.ToList()) _service.Requests.Resolve(ring, "decline", RequestSource.Program);
        _ringing.Clear();
        if (IsInCall) End("call.ended_room");
    }

    /// <summary>Ends the call: hang up, dropped link, kicked, left the room, closing Jay.</summary>
    public void End(string reasonKey) => _session?.End(reasonKey);

    /// <summary>Calls were switched on or off while Jay runs (self-checks): every button and panel looks again.</summary>
    internal void Refresh() => Sync();

    /// <summary>Self-checks: someone in the call starts or stops speaking.</summary>
    internal void MarkSpeaking(string name, bool speaking) => _session?.MarkSpeaking(name, speaking);
}
