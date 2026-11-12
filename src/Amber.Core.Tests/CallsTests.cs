using Amber.App.Services;
using Amber.App.ViewModels;
using Amber.Core.Activity;
using Amber.Core.Calls;
using Amber.Core.Multiplayer;
using Amber.Core.Navigation;
using Amber.Core.Safety;
using Xunit;

namespace Amber.Core.Tests;

/// <summary>Calls and screen sharing (redesign batch 11, reserved): the rules, the session state and the call panel's view model.</summary>
public sealed class CallsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amber-calls-" + Guid.NewGuid().ToString("N"));

    public CallsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static readonly ChildModeEffects Child = new ChildModeSettings { Enabled = true }.Effects;

    [Fact]
    public void CallsAreOffByDefault() =>
        // Release builds show no call button: only the developer switch turns them on.
        Assert.Equal(Environment.GetEnvironmentVariable("AMBER_CALLS") == "1", CallFeature.Enabled);

    [Theory]
    [InlineData("friends-room", true, false, true)]
    [InlineData("friends-room", false, true, true)]
    [InlineData("friends-room", false, false, false)]
    [InlineData("room", true, false, false)]
    [InlineData("room", false, true, true)]
    [InlineData("nobody", true, true, false)]
    [InlineData("garbage", true, false, true)]
    public void OnlyVerifiedFriendsAndTheRoomCanRing(string setting, bool verifiedFriend, bool sameRoom, bool rings) =>
        Assert.Equal(rings, CallRules.MayRing(CallRules.Parse(setting), verifiedFriend, sameRoom));

    [Fact]
    public void WhoCanCallRoundTrips()
    {
        foreach (var who in Enum.GetValues<WhoCanCall>()) Assert.Equal(who, CallRules.Parse(who.ToSetting()));
        Assert.Equal(WhoCanCall.RoomOnly, CallRules.Parse(" ROOM "));
    }

    [Fact]
    public void ForAChildTheCameraAndControlAreNotOffered()
    {
        Assert.True(Child.HideRemoteControl);
        Assert.True(CallRules.Offered(CallStep.Voice, Child));
        Assert.True(CallRules.Offered(CallStep.Screen, Child));
        Assert.False(CallRules.Offered(CallStep.Camera, Child));
        Assert.False(CallRules.Offered(CallStep.Control, Child));
        foreach (var step in Enum.GetValues<CallStep>()) Assert.True(CallRules.Offered(step, ChildModeEffects.None));
    }

    [Fact]
    public void TheMicrophoneAndCameraStartOffAndControlNeedsSharingAndConsent()
    {
        var call = new CallSession("Alex", CallRoute.Relay, ChildModeEffects.None, [new CallMember("Alex", false, true)], "Me");
        Assert.False(call.MicOn);
        Assert.False(call.CameraOn);
        Assert.Equal(["Me", "Alex"], call.Members.Select(m => m.Name));
        Assert.True(call.Members[0].IsMe);

        // Not sharing: control is not possible. Sharing without "just this once": still not.
        Assert.False(call.StartControl("Alex", allowedThisOnce: true));
        call.SetSharing(true);
        Assert.False(call.StartControl("Alex", allowedThisOnce: false));
        Assert.Null(call.ControlledBy);
        Assert.True(call.StartControl("Alex", allowedThisOnce: true));
        Assert.Equal("Alex", call.ControlledBy);
        Assert.False(call.CanLetControl);

        // Stopping the share stops control with it.
        call.SetSharing(false);
        Assert.Null(call.ControlledBy);
    }

    [Fact]
    public void EndingTheCallEndsEverything()
    {
        var call = new CallSession("Room", CallRoute.Direct, ChildModeEffects.None, [new CallMember("Alex", false, false)], "Me");
        var changes = 0;
        call.Changed += () => changes++;
        call.SetMic(true);
        call.SetCamera(true);
        call.SetSharing(true);
        call.StartControl("Alex", true);
        call.End("call.ended");
        Assert.True(call.Ended);
        Assert.Equal("call.ended", call.EndReason);
        Assert.False(call.MicOn || call.CameraOn || call.Sharing);
        Assert.Null(call.ControlledBy);
        // Nothing comes back on after the end.
        call.SetMic(true);
        call.SetSharing(true);
        Assert.False(call.MicOn || call.Sharing);
        var before = changes;
        call.End("call.ended_room");
        Assert.Equal("call.ended", call.EndReason);
        Assert.Equal(before, changes);
    }

    [Fact]
    public void AChildCannotTurnTheCameraOnOrLetSomeoneControl()
    {
        var call = new CallSession("Alex", CallRoute.Relay, Child, [new CallMember("Alex", false, true)], "Me");
        call.SetCamera(true);
        call.SetSharing(true);
        Assert.False(call.CameraOn);
        Assert.True(call.Sharing);
        Assert.False(call.StartControl("Alex", true));
    }

    [Fact]
    public void TheCallsGroupCountsOnlyWhileCallsAreOn()
    {
        var d = AdvancedSettings.Defaults;
        Assert.DoesNotContain(AdvancedSettings.Calls, AdvancedSettings.GroupsFor(calls: false));
        Assert.Equal(AdvancedSettings.Calls, AdvancedSettings.GroupsFor(calls: true)[^1]);
        Assert.Empty(AdvancedSettings.Customized(d with { CallsWho = "nobody" }, calls: false));
        Assert.Equal([AdvancedSettings.Calls], AdvancedSettings.Customized(d with { CallsWho = "nobody" }, calls: true));
        Assert.Equal([AdvancedSettings.Calls], AdvancedSettings.Customized(d with { CallsAllowControl = false }, calls: true));
        // Same choice, other spelling: not a change.
        Assert.Empty(AdvancedSettings.Customized(d with { CallsWho = "FRIENDS-ROOM", CallsQuality = "AUTO" }, calls: true));
    }

    [Fact]
    public void TheCallSettingsAreFindableOnlyWhileCallsAreOn()
    {
        var calls = SettingsCatalog.All.Where(f => f.CallsOnly).ToList();
        Assert.Equal(8, calls.Count);
        Assert.All(calls, f => Assert.Equal(SettingsCatalog.Multiplayer, f.Group));
        Assert.DoesNotContain(SettingsCatalog.FieldsFor(calls: false), f => f.CallsOnly);
        Assert.All(calls, f => Assert.Contains(f, SettingsCatalog.FieldsFor(calls: true)));
    }

    [Fact]
    public void TheCallSettingsMatchTheDefaults()
    {
        var s = new AppSettings();
        var d = AdvancedSettings.Defaults;
        Assert.Equal(d.CallsWho, s.CallsWhoCanCall);
        Assert.Equal(d.CallsNoise, s.CallsNoiseSuppression);
        Assert.Equal(d.CallsPushToTalk, s.CallsPushToTalk);
        Assert.Equal(d.CallsQuality, s.CallsVideoQuality);
        Assert.Equal(d.CallsShareGameOnly, s.CallsShareGameOnly);
        Assert.Equal(d.CallsAllowControl, s.CallsAllowControlRequests);
    }

    [Fact]
    public void ARingDefaultsToDeclineAndAnsweringKeepsTheMicrophoneOff()
    {
        UiTestThread.Run(() =>
        {
            using var service = new LauncherService(new AppSettings { GameDirectory = _root, PlayerName = "Me" });
            var call = new CallViewModel(service, available: true);
            string? went = null;
            call.Navigate = r => went = r;

            // Someone I have only met, outside my room: it never rings.
            Assert.Null(call.Ring("Stranger", verifiedFriend: false, sameRoom: false));
            Assert.Empty(service.Requests.Pending);

            var ring = call.Ring("Alex", verifiedFriend: true, sameRoom: false);
            Assert.NotNull(ring);
            Assert.Equal("call", ring!.Spec.Kind);
            Assert.Equal("decline", ring.Spec.Choices.Single(c => c.IsDefault).Id);
            Assert.Equal("decline", ring.Spec.TimeoutChoice);
            Assert.False(call.IsInCall);

            service.Requests.Resolve(ring, "answer", RequestSource.Bar);
            Assert.True(call.IsInCall);
            Assert.True(call.ShowInPeople);
            Assert.False(call.MicOn);
            Assert.Equal("people", went);
            Assert.Equal(["Me", "Alex"], call.Tiles.Select(t => t.Name));

            // Control: share, ask, the locked question, then "just this once".
            call.AskControlCommand.Execute(null);
            Assert.False(call.ConfirmingControl);
            call.ToggleShareCommand.Execute(null);
            Assert.True(call.HasStage);
            call.AskControlCommand.Execute(null);
            Assert.True(call.ConfirmingControl);
            call.RefuseControlCommand.Execute(null);
            Assert.False(call.IsControlled);
            call.AskControlCommand.Execute(null);
            call.AllowControlOnceCommand.Execute(null);
            Assert.True(call.IsControlled);
            call.StopControl();
            Assert.False(call.IsControlled);

            call.HangUpCommand.Execute(null);
            Assert.False(call.IsInCall);
            Assert.False(call.Sharing);
            Assert.True(call.ShowEndedInPeople);
            Assert.Empty(call.Tiles);
        });
    }

    [Fact]
    public void NobodyMeansNoRingAndADeclineStartsNothing()
    {
        UiTestThread.Run(() =>
        {
            using var service = new LauncherService(new AppSettings { GameDirectory = _root, CallsWhoCanCall = "nobody" });
            var call = new CallViewModel(service, available: true);
            Assert.Null(call.Ring("Alex", verifiedFriend: true, sameRoom: true));

            service.Settings.CallsWhoCanCall = "friends-room";
            var ring = call.Ring("Alex", verifiedFriend: false, sameRoom: true)!;
            service.Requests.Resolve(ring, "decline", RequestSource.Popup);
            Assert.False(call.IsInCall);

            // With calls switched off, nothing rings and no call starts.
            var off = new CallViewModel(service, available: false);
            Assert.Null(off.Ring("Alex", true, true));
            off.StartWithFriend("Alex", CallStep.Voice);
            Assert.False(off.IsInCall);
            Assert.False(off.OffersVideo || off.OffersControl);
        });
    }

    [Fact]
    public void ForAChildTheCallLosesTheCameraAtOnce()
    {
        UiTestThread.Run(() =>
        {
            using var service = new LauncherService(new AppSettings { GameDirectory = _root });
            var call = new CallViewModel(service, available: true);
            call.StartWithFriend("Alex", CallStep.Camera);
            Assert.True(call.CameraOn);
            Assert.True(call.OffersVideo);

            service.Settings.ChildMode = new ChildModeSettings { Enabled = true };
            call.ApplyChildMode();
            Assert.False(call.CameraOn);
            Assert.False(call.OffersVideo);
            Assert.False(call.OffersControl);
        });
    }
}
