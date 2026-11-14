using Amber.Core.Safety;

namespace Amber.Core.Calls;

/// <summary>
/// Voice, video, screen sharing and control (redesign batch 11, reserved). One path that builds up step by step in the
/// same call panel: voice, then the camera, then sharing the screen, then letting the other side control it.
/// The protocol (media encoding, key derivation, forwarding for several people) is not written yet, so the whole
/// feature sits behind <see cref="CallFeature.Enabled"/>, off by default: the layout, routes, theme region names
/// and settings are in place, and no button shows in a release build.
/// </summary>
public static class CallFeature
{
    /// <summary>The developer switch: <c>AMBER_CALLS=1</c> shows the reserved call UI. Self-checks may turn it on in-process.</summary>
    public static bool Enabled { get; set; } = Environment.GetEnvironmentVariable("AMBER_CALLS") == "1";
}

/// <summary>The steps of a call, each adding one thing to the last.</summary>
public enum CallStep { Voice, Camera, Screen, Control }

/// <summary>Who may call me (Settings › Multiplayer › Calls and screen).</summary>
public enum WhoCanCall { FriendsAndRoom, RoomOnly, Nobody }

/// <summary>How the call reaches the other side: the same link the friends or the room already use.</summary>
public enum CallRoute { Direct, Relay }

public static class CallRules
{
    public static WhoCanCall Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "room" => WhoCanCall.RoomOnly,
        "nobody" => WhoCanCall.Nobody,
        _ => WhoCanCall.FriendsAndRoom,
    };

    public static string ToSetting(this WhoCanCall who) => who switch
    {
        WhoCanCall.RoomOnly => "room",
        WhoCanCall.Nobody => "nobody",
        _ => "friends-room",
    };

    /// <summary>
    /// Whether a call from this person rings at all. Only verified friends and people in the same room can call;
    /// people I have only met and strangers never get through. The setting can narrow that to the room, or to nobody.
    /// </summary>
    public static bool MayRing(WhoCanCall who, bool verifiedFriend, bool sameRoom) => who switch
    {
        WhoCanCall.Nobody => false,
        WhoCanCall.RoomOnly => sameRoom,
        _ => sameRoom || verifiedFriend,
    };

    /// <summary>Whether a step is offered at all. "For a child" hides the camera and control.</summary>
    public static bool Offered(CallStep step, ChildModeEffects child) =>
        step is CallStep.Voice or CallStep.Screen || !child.HideRemoteControl;
}

/// <summary>One person in a call: their tile on the stage.</summary>
public sealed record CallMember(string Name, bool IsMe, bool Verified)
{
    public bool Speaking { get; init; }
    public bool CameraOn { get; init; }
    public bool Sharing { get; init; }
}

/// <summary>
/// What the call panel shows, as plain state: who is in it, how it is connected, and what I have turned on. The
/// microphone and camera start off; turning one on is my own act every time. Control is only possible while I share,
/// and every control session needs its own "just this once" (the existing remote-control rules). Ending the call
/// (hang up, dropped link, kicked, leaving the room, closing Jay) ends sharing and control with it.
/// </summary>
public sealed class CallSession
{
    private readonly List<CallMember> _members;

    public CallSession(string title, CallRoute route, ChildModeEffects child, IEnumerable<CallMember> others, string me)
    {
        Title = title;
        Route = route;
        Child = child;
        _members = [new CallMember(me, true, true), .. others];
    }

    /// <summary>The friend's name, or the room's name for room voice.</summary>
    public string Title { get; }
    public CallRoute Route { get; }
    public ChildModeEffects Child { get; }
    public IReadOnlyList<CallMember> Members => _members;

    public bool MicOn { get; private set; }
    public bool CameraOn { get; private set; }
    public bool Sharing { get; private set; }

    /// <summary>Who controls my screen now, or null.</summary>
    public string? ControlledBy { get; private set; }

    public bool Ended { get; private set; }
    public string? EndReason { get; private set; }

    public event Action? Changed;

    public bool CanUseCamera => !Ended && CallRules.Offered(CallStep.Camera, Child);
    public bool CanShare => !Ended;
    public bool CanLetControl => !Ended && Sharing && ControlledBy is null && CallRules.Offered(CallStep.Control, Child);

    public void SetMic(bool on) => Set(() => MicOn = on && !Ended);

    public void SetCamera(bool on)
    {
        if (on && !CanUseCamera) return;
        Set(() => CameraOn = on);
    }

    public void SetSharing(bool on)
    {
        if (on && !CanShare) return;
        Set(() =>
        {
            Sharing = on;
            // Control rides on the shared screen: stopping the share stops control.
            if (!on) ControlledBy = null;
        });
    }

    /// <summary>The other side controls my screen after I said "just this once" in the locked confirmation.</summary>
    public bool StartControl(string by, bool allowedThisOnce)
    {
        if (!allowedThisOnce || !CanLetControl) return false;
        Set(() => ControlledBy = by);
        return true;
    }

    /// <summary>Ctrl+Shift+F12, the red bar's [Stop], or the end of the call.</summary>
    public void StopControl() => Set(() => ControlledBy = null);

    public void MarkSpeaking(string name, bool speaking) => Set(() =>
    {
        var i = _members.FindIndex(m => m.Name == name);
        if (i >= 0) _members[i] = _members[i] with { Speaking = speaking };
    });

    public void End(string reason)
    {
        if (Ended) return;
        Set(() =>
        {
            Ended = true;
            EndReason = reason;
            MicOn = CameraOn = Sharing = false;
            ControlledBy = null;
        });
    }

    private void Set(Action change)
    {
        change();
        Changed?.Invoke();
    }
}
