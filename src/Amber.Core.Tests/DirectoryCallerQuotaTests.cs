using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Amber.Core.Multiplayer.Public;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Tests;

/// <summary>
/// Per-caller limits over the real directory protocol: callers EasyTier maps (127.235.c.d) are limited one by one; callers
/// it can't tell apart (all from 127.0.0.1) share one capped allowance.
/// </summary>
public class DirectoryCallerQuotaTests
{
    private const string Secret = "test-network-secret";
    private DateTimeOffset _now = DateTimeOffset.UtcNow;

    /// <summary>Connects to the directory from <paramref name="from"/> (127.0.0.1 when null), as EasyTier would hand the call over.</summary>
    private sealed class From(int port, string? from, PublicNetworkProfile profile) : IPublicNetwork
    {
        public async Task<Stream> ConnectAsync(string ip, int p, CancellationToken ct)
        {
            var c = from is null ? new TcpClient() : new TcpClient(new IPEndPoint(IPAddress.Parse(from), 0));
            await c.ConnectAsync(IPAddress.Loopback, port, ct);
            return c.GetStream();
        }

        public Task<string?> MyIpAsync(CancellationToken ct) => Task.FromResult<string?>("10.235.0.7");
        public PublicNetworkProfile? Profile => profile;
    }

    private RoomDirectoryServer Server(X25519KeyPair key, bool sourcesMapped = false) =>
        new(IPAddress.Loopback, 0, clock: () => _now) { Identity = key, NetworkSecret = Secret, SourcesMapped = sourcesMapped };

    private static RoomDirectoryClient Client(RoomDirectoryServer server, X25519KeyPair key, string? from) =>
        new(new From(server.Port, from, new PublicNetworkProfile("test", "Test", "jay-net-test", Secret, ["tcp://127.0.0.1:1"])
            { DirectoryKeys = [key.PublicKey] }), ["10.235.0.1"]);

    private static Task Mail(RoomDirectoryClient client) =>
        client.PostMailAsync(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)), RandomNumberGenerator.GetBytes(64),
            Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)), default);

    private static Task<bool> Rendezvous(RoomDirectoryClient client) =>
        client.PutRendezvousAsync(RandomNumberGenerator.GetBytes(Multiplayer.Public.Rendezvous.IdLength), RandomNumberGenerator.GetBytes(64),
            TimeSpan.FromMinutes(5), default);

    private static async Task<string> Refused(Func<Task> call) => (await Assert.ThrowsAsync<DirectoryRejectedException>(call)).Code;

    [NotOnMacFact]
    public async Task OneMappedCallerUsesUpOnlyItsOwnMailAndRendezvous()
    {
        using var key = X25519KeyPair.Generate();
        await using var server = Server(key);
        var alex = Client(server, key, "127.235.1.2");
        var steve = Client(server, key, "127.235.1.3");
        for (var i = 0; i < RoomDirectoryServer.MaxMailPostsPerSource; i++) await Mail(alex);
        Assert.Equal("rate", await Refused(() => Mail(alex)));
        await Mail(steve);
        for (var i = 0; i < RoomDirectoryServer.MaxRendezvousPutsPerSource; i++) Assert.True(await Rendezvous(alex));
        Assert.Equal("rate", await Refused(() => Rendezvous(alex)));
        Assert.True(await Rendezvous(steve));
        Assert.True(server.MappingSeen);
        // The allowances come back after their windows.
        _now += RoomDirectoryServer.MailPostWindow + TimeSpan.FromSeconds(1);
        await Mail(alex);
        _now += Multiplayer.Public.Rendezvous.MaxTtl;
        Assert.True(await Rendezvous(alex));
    }

    [Fact]
    public async Task CallersThatCantBeToldApartShareOneCappedAllowance()
    {
        // The node's EasyTier doesn't map callers: everyone arrives from 127.0.0.1. They share one allowance, wider than
        // one caller's but capped, so one of them can't take the directory's whole budget.
        using var key = X25519KeyPair.Generate();
        await using var server = Server(key);
        var anyone = Client(server, key, from: null);
        for (var i = 0; i < RoomDirectoryServer.MaxMailPostsUnmapped; i++) await Mail(anyone);
        Assert.Equal("rate", await Refused(() => Mail(anyone)));
        for (var i = 0; i < RoomDirectoryServer.MaxRendezvousPutsUnmapped; i++) Assert.True(await Rendezvous(anyone));
        Assert.Equal("rate", await Refused(() => Rendezvous(anyone)));
        Assert.False(server.MappingSeen);
        Assert.True(RoomDirectoryServer.MaxMailPostsUnmapped < RoomDirectoryServer.MailPostWindow.TotalMinutes * RoomDirectoryServer.MaxMailPostsPerMinute);
        Assert.True(RoomDirectoryServer.MaxRendezvousPutsUnmapped < RoomDirectoryServer.MaxRendezvousPutsPerMinute);
    }

    [Fact]
    public async Task ANodeWhoseEasyTierMapsCallersLimitsLocalCallersFromTheStart()
    {
        // No mapped caller seen yet: local callers are still the shared local source, limited like one caller.
        using var key = X25519KeyPair.Generate();
        await using var server = Server(key, sourcesMapped: true);
        var local = Client(server, key, from: null);
        for (var i = 0; i < RoomDirectoryServer.MaxMailPostsPerSource; i++) await Mail(local);
        Assert.Equal("rate", await Refused(() => Mail(local)));
        Assert.False(server.MappingSeen);
    }

    [Fact]
    public async Task CallersThatCantBeToldApartCantFillTheMailStore()
    {
        using var key = X25519KeyPair.Generate();
        await using var server = Server(key);
        JsonObject Post(string? source) => server.Handle(new JsonObject
        {
            ["op"] = "post", ["box"] = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)),
            ["blob"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64)),
            ["drop"] = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)),
        }, source);
        var held = 0;
        while (true)
        {
            var reply = Post(RoomDirectoryServer.UnmappedLoopbackSource);
            if (reply["ok"]?.GetValue<bool>() == true) { held++; continue; }
            if (reply["error"]?.GetValue<string>() == "full") break;
            _now += RoomDirectoryServer.MailPostWindow + TimeSpan.FromSeconds(1);
            Assert.True(_now - DateTimeOffset.UtcNow < RoomDirectoryProtocol.MailTtl, "the store should be capped before mail expires");
        }
        Assert.Equal(RoomDirectoryServer.MaxSealedMailUnmapped, held);
        // Others still get mail in.
        Assert.True(Post("10.235.9.9")["ok"]?.GetValue<bool>());
        // Once that mail expires, the allowance is there again.
        _now += RoomDirectoryProtocol.MailTtl;
        Assert.True(Post(RoomDirectoryServer.UnmappedLoopbackSource)["ok"]?.GetValue<bool>());
    }
}
