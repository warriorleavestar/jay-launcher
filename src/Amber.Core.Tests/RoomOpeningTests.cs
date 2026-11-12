using System.Text.Json.Nodes;
using Amber.Core.Localization;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Tests;

/// <summary>The four choices of opening a room: what each maps to and when it cannot be picked.</summary>
public sealed class RoomOpeningTests
{
    private static readonly RoomOpeningContext Ready = new(ChildMode: false, NetworkConnected: true, VerifiedFriends: 2, FullLanAvailable: true, HybridSupported: true);

    [Fact]
    public void ProtectionLevelsMapToTransportSettingsAndBack()
    {
        Assert.Equal(new TunnelSettings(TransportMode.Minecraft, false), RoomOpening.Tunnel(RoomProtection.Standard));
        Assert.Equal(new TunnelSettings(TransportMode.High, false), RoomOpening.Tunnel(RoomProtection.Stronger));
        Assert.Equal(new TunnelSettings(TransportMode.Extreme, true), RoomOpening.Tunnel(RoomProtection.Highest));
        Assert.Null(RoomOpening.Tunnel(RoomProtection.Custom));
        foreach (var level in new[] { RoomProtection.Standard, RoomProtection.Stronger, RoomProtection.Highest })
            Assert.Equal(level, RoomOpening.ProtectionOf(RoomOpening.Tunnel(level)!));
        // Anything set by hand that is none of the three is "customised".
        Assert.Equal(RoomProtection.Custom, RoomOpening.ProtectionOf(new TunnelSettings(TransportMode.General, false)));
        Assert.Equal(RoomProtection.Custom, RoomOpening.ProtectionOf(new TunnelSettings(TransportMode.Minecraft, true)));
        Assert.Equal(RoomProtection.Custom, RoomOpening.ProtectionOf(new TunnelSettings(TransportMode.Extreme, false)));
    }

    [Fact]
    public void StandardIsWhatARoomUsesOutOfTheBox() =>
        Assert.Equal(Amber.App.ViewModels.MultiplayerViewModel.DefaultTransport, RoomOpening.Tunnel(RoomProtection.Standard)!.Mode);

    [Fact]
    public void InvitedCanAlwaysBePicked()
    {
        var worst = new RoomOpeningContext(true, false, 0, false, false);
        foreach (var route in Enum.GetValues<RoomRoute>())
            Assert.Null(RoomOpening.Blocked(RoomAccess.Invited, route, worst));
    }

    [Theory]
    [InlineData(RoomAccess.Friends)]
    [InlineData(RoomAccess.Public)]
    public void FriendsAndPublicNeedAConnectedNetworkAndNoChildMode(RoomAccess access)
    {
        Assert.Null(RoomOpening.Blocked(access, RoomRoute.Auto, Ready));
        Assert.Null(RoomOpening.Blocked(access, RoomRoute.RelayOnly, Ready));
        Assert.Equal("multiplayer.open.blocked.child", RoomOpening.Blocked(access, RoomRoute.Auto, Ready with { ChildMode = true }));
        Assert.Equal("multiplayer.open.blocked.serverless", RoomOpening.Blocked(access, RoomRoute.Serverless, Ready));
        Assert.Equal("multiplayer.open.blocked.network", RoomOpening.Blocked(access, RoomRoute.Auto, Ready with { NetworkConnected = false }));
        // Child mode is said first: connecting a network would not help.
        Assert.Equal("multiplayer.open.blocked.child", RoomOpening.Blocked(access, RoomRoute.Serverless, Ready with { ChildMode = true, NetworkConnected = false }));
    }

    [Fact]
    public void FriendsNeedsAFriend()
    {
        Assert.Equal("multiplayer.open.blocked.no_friends", RoomOpening.Blocked(RoomAccess.Friends, RoomRoute.Auto, Ready with { VerifiedFriends = 0 }));
        Assert.Null(RoomOpening.Blocked(RoomAccess.Public, RoomRoute.Auto, Ready with { VerifiedFriends = 0 }));
    }

    [Fact]
    public void ProtectionIsNotAskedForAServerlessRoomAndHighestNeedsTheHybridHandshake()
    {
        foreach (var level in new[] { RoomProtection.Standard, RoomProtection.Stronger, RoomProtection.Highest })
            Assert.Equal("multiplayer.open.blocked.protection_serverless", RoomOpening.Blocked(level, RoomRoute.Serverless, Ready));
        Assert.Null(RoomOpening.Blocked(RoomProtection.Highest, RoomRoute.Auto, Ready));
        Assert.Equal("multiplayer.open.blocked.highest", RoomOpening.Blocked(RoomProtection.Highest, RoomRoute.RelayOnly, Ready with { HybridSupported = false }));
        Assert.Null(RoomOpening.Blocked(RoomProtection.Stronger, RoomRoute.Auto, Ready with { HybridSupported = false }));
    }

    [Fact]
    public void WholeLanNeedsThePlatformToAllowIt()
    {
        Assert.Null(RoomOpening.Blocked(RoomScope.WholeLan, Ready));
        Assert.Equal("multiplayer.open.blocked.whole_lan", RoomOpening.Blocked(RoomScope.WholeLan, Ready with { FullLanAvailable = false }));
        Assert.Null(RoomOpening.Blocked(RoomScope.Game, Ready with { FullLanAvailable = false }));
    }

    [Fact]
    public void UsableFallsBackToTheChoiceThatAlwaysWorks()
    {
        Assert.Equal(RoomAccess.Public, RoomOpening.Usable(RoomAccess.Public, RoomRoute.Auto, Ready));
        Assert.Equal(RoomAccess.Invited, RoomOpening.Usable(RoomAccess.Public, RoomRoute.Serverless, Ready));
        Assert.Equal(RoomAccess.Invited, RoomOpening.Usable(RoomAccess.Friends, RoomRoute.Auto, Ready with { NetworkConnected = false }));
        Assert.Equal(RoomProtection.Stronger, RoomOpening.Usable(RoomProtection.Highest, RoomRoute.Auto, Ready with { HybridSupported = false }));
        // A serverless room keeps the level for when the route changes back.
        Assert.Equal(RoomProtection.Highest, RoomOpening.Usable(RoomProtection.Highest, RoomRoute.Serverless, Ready));
        Assert.Equal(RoomScope.Game, RoomOpening.Usable(RoomScope.WholeLan, Ready with { FullLanAvailable = false }));
    }

    [Fact]
    public void EveryReasonHasText()
    {
        foreach (var key in new[] { "child", "serverless", "network", "no_friends", "protection_serverless", "highest", "whole_lan" })
            Assert.NotEqual("multiplayer.open.blocked." + key, L.T("multiplayer.open.blocked." + key));
    }

    [Fact]
    public void RelayOnlyTravelsInTheRoomCredentialsAndTurnsDirectConnectionsOff()
    {
        using var host = X25519KeyPair.Generate();
        var plain = RoomCredentials.Create(host.PublicKey, 25565, TunnelSettings.Default) with { Member = MemberKey.Create() };
        var relayOnly = plain with { RelayOnly = true };

        // Off: the field is not written at all, so rooms look as they did before.
        Assert.Null(plain.ToJson()["relayOnly"]);
        Assert.False(RoomCredentials.FromJson(JsonNode.Parse(plain.ToJson().ToJsonString())!).RelayOnly);
        Assert.False(plain.ToHostRoom(new MemberTable(), "Host").RelayOnly);

        // On: the member who is let in gets it with the credentials, and both sides start EasyTier with p2p off.
        var back = RoomCredentials.FromJson(JsonNode.Parse(relayOnly.ToJson().ToJsonString())!);
        Assert.True(back.RelayOnly);
        var hostRoom = relayOnly.ToHostRoom(new MemberTable(), "Host");
        var guestRoom = back.ToRoom();
        foreach (var (room, asHost) in new[] { (hostRoom, true), (guestRoom, false) })
        {
            Assert.True(room.RelayOnly);
            var options = new EasyTierOptions(room, asHost, ["tcp://relay.example:11010"], 15999, LocalForwardPort: asHost ? null : 40000) { LocalTunnelPort = asHost ? null : 40001 };
            Assert.Single(EasyTierArgs.Build(options), a => a == "--disable-p2p");
            Assert.DoesNotContain("--disable-p2p", EasyTierArgs.Build(options with { Room = room with { RelayOnly = false } }));

            // Relays only never listens for other computers, even when a listener was asked for.
            var listening = options with { Listeners = ["tcp://0.0.0.0:11010", "udp://0.0.0.0:11010"] };
            var args = EasyTierArgs.Build(listening);
            Assert.Contains("--no-listener", args);
            Assert.DoesNotContain("--listeners", args);
            Assert.Contains("--listeners", EasyTierArgs.Build(listening with { Room = room with { RelayOnly = false } }));
        }
    }
}
