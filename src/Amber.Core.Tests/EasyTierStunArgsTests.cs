using Amber.Core.Multiplayer;

namespace Amber.Core.Tests;

/// <summary>
/// Rooms and the lobby tell easytier-core which STUN servers to ask, so it never falls back to its built-in list
/// (third-party hosts plus a list read from a DNS TXT record).
/// </summary>
public class EasyTierStunArgsTests
{
    private static readonly RoomCode Room = new("K7Q2M9XD", "4HZP8RW2N6TC", 51234);

    private static void AssertJayStunList(List<string> args)
    {
        var expected = string.Join(',', Stun.DefaultServers);
        Assert.Contains("--stun-servers=" + expected, args);
        Assert.Contains("--stun-servers-v6=" + expected, args);
        Assert.DoesNotContain(args, a => a.Contains("txt:", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RoomInstancesNameTheirStunServers()
    {
        AssertJayStunList(EasyTierArgs.Build(new EasyTierOptions(Room, true, ["tcp://node.example.com:11010"], 1, null)));
        AssertJayStunList(EasyTierArgs.Build(new EasyTierOptions(Room, false, ["tcp://node.example.com:11010"], 1, 2)));
        AssertJayStunList(EasyTierArgs.Build(new EasyTierOptions(Room, false, [], 1, null, null, "g", Tun: true)));
    }

    [Fact]
    public void LobbyInstancesNameTheirStunServers()
    {
        AssertJayStunList(LobbyArgs.Build(new LobbyOptions("jay-l-x", "s", true, ["tcp://node.example.com:11010"], 15888)));
        AssertJayStunList(LobbyArgs.Build(new LobbyOptions("jay-l-x", "s", true, [], 15888, null, 40123)));
    }

    /// <summary>
    /// The IPv6 STUN list is the same list, and EasyTier only uses its AAAA answers. stun.cloudflare.com is the one host with
    /// an AAAA record (checked by DNS on 2026-11-14; a STUN reply over IPv6 is unverified), so keep it in the list.
    /// </summary>
    [Fact]
    public void TheListKeepsAHostWithAnIpv6Address() => Assert.Contains("stun.cloudflare.com:3478", Stun.DefaultServers);

    [Fact]
    public void TheListIsJaysDocumentedOneWithPorts()
    {
        Assert.NotEmpty(Stun.DefaultServers);
        Assert.All(Stun.DefaultServers, s => Assert.Matches(@"^[a-z0-9.-]+:\d+$", s));
    }
}
