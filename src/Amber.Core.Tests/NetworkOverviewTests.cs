using Amber.Core.Multiplayer.Public;

namespace Amber.Core.Tests;

public class NetworkOverviewTests
{
    private static readonly NetworkSource Jay = new("jay", "Jay", BuiltIn: true, Reviewed: true, TestReviewed: false, Nodes: 0);
    private static readonly NetworkSource Club = new("club", "Club", BuiltIn: false, Reviewed: true, TestReviewed: false, Nodes: 2);
    private static readonly NetworkSource Shed = new("shed", "Shed", BuiltIn: false, Reviewed: false, TestReviewed: false, Nodes: 1);

    [Fact]
    public void BasicRelaysComeFirstAndOnlyCarryInvites()
    {
        var rows = NetworkOverview.Build([Club], null, false, false, inRoom: false, inviteOnly: false);
        Assert.True(rows[0].IsBaseRelays);
        Assert.Equal(NetworkAbility.Invites, rows[0].Abilities);
        Assert.False(rows[0].CanSwitch);
        Assert.Equal(NetworkUse.Off, rows[0].Use);
        Assert.Equal(["shell.network.can.invites"], NetworkOverview.AbilityKeys(rows[0].Abilities));
    }

    [Fact]
    public void ABuiltInNetworkWithoutServersIsNotListed()
    {
        var rows = NetworkOverview.Build([Jay, Club], "jay", false, false, false, false);
        Assert.Equal(["relays", "club"], rows.Select(r => r.Id));
        var withServers = NetworkOverview.Build([Jay with { Nodes = 3 }], "jay", true, false, false, false);
        Assert.Equal(NetworkUse.On, withServers[1].Use);
        Assert.Equal(NetworkTrust.BuiltIn, withServers[1].Trust);
    }

    [Fact]
    public void TheBuiltInNetworkWithATestSignedUpdateSaysSo()
    {
        var rows = NetworkOverview.Build([Jay with { Nodes = 2, TestReviewed = true }, Club with { TestReviewed = true }], "jay", true, false, false, false);
        Assert.Equal(NetworkTrust.TestReviewed, rows[1].Trust);
        Assert.Equal(NetworkTrust.TestReviewed, rows[2].Trust);
    }

    [Fact]
    public void OnlyTheCurrentNetworkIsConnectedOrConnecting()
    {
        var rows = NetworkOverview.Build([Club, Shed], "shed", connected: false, connecting: true, false, false);
        Assert.Equal(NetworkUse.Off, rows[1].Use);
        Assert.Equal(NetworkUse.Connecting, rows[2].Use);
        Assert.Equal(NetworkTrust.Reviewed, rows[1].Trust);
        Assert.Equal(NetworkTrust.Unreviewed, rows[2].Trust);
        Assert.Equal(["shell.network.can.invites", "shell.network.can.public_rooms", "shell.network.can.messages"], NetworkOverview.AbilityKeys(rows[2].Abilities));
    }

    [Fact]
    public void ForAChildOnlyTheBasicRelaysAreListed()
    {
        var rows = NetworkOverview.Build([Club, Shed], "club", true, false, false, inviteOnly: true);
        Assert.Single(rows);
        Assert.True(rows[0].IsBaseRelays);
    }

    [Fact]
    public void TheStatusNamesTheNetworkInUse()
    {
        (string Key, object[] Args, NetworkUse Light) Of(string? current, bool connected, bool connecting, bool inRoom) =>
            NetworkOverview.Status(NetworkOverview.Build([Club, Shed], current, connected, connecting, inRoom, false));

        Assert.Equal(("shell.network.none", NetworkUse.Off), (Of(null, false, false, false).Key, Of(null, false, false, false).Light));
        var connecting = Of("club", false, true, false);
        Assert.Equal("shell.network.connecting", connecting.Key);
        Assert.Equal("Club", connecting.Args[0]);
        Assert.Equal(NetworkUse.Connecting, connecting.Light);
        var one = Of("club", true, false, false);
        Assert.Equal("shell.network.single", one.Key);
        Assert.Equal("Club", one.Args[0]);
        Assert.Equal("shell.network.relays", Of(null, false, false, true).Key);
        var many = Of("shed", true, false, true);
        Assert.Equal("shell.network.many", many.Key);
        Assert.Equal(2, many.Args[0]);
        Assert.Equal(NetworkUse.On, many.Light);
    }
}
