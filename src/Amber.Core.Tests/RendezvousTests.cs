using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Amber.Core.Multiplayer.Public;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Tests;

/// <summary>One-shot rendezvous records on the directory (rv.put / rv.take) and the shared <see cref="Rendezvous"/> helpers.</summary>
public class RendezvousTests
{
    private const string Secret = "test-network-secret";

    private sealed class Loopback(int[] ports, PublicNetworkProfile? profile) : IPublicNetwork
    {
        public async Task<Stream> ConnectAsync(string ip, int p, CancellationToken ct)
        {
            var c = new TcpClient();
            await c.ConnectAsync(IPAddress.Loopback, ports[int.Parse(ip.Split('.')[^1]) - 1], ct);
            return c.GetStream();
        }

        public Task<string?> MyIpAsync(CancellationToken ct) => Task.FromResult<string?>("10.235.0.7");
        public PublicNetworkProfile? Profile => profile;
    }

    private static PublicNetworkProfile Pinned(params byte[][] keys) =>
        new("test", "Test", "jay-net-test", Secret, ["tcp://127.0.0.1:1"]) { DirectoryKeys = keys };

    private static RoomDirectoryServer Server(X25519KeyPair identity, Func<DateTimeOffset>? clock = null) =>
        new(IPAddress.Loopback, 0, clock: clock) { Identity = identity, NetworkSecret = Secret };

    private static RoomDirectoryClient Client(params (RoomDirectoryServer Server, X25519KeyPair Key)[] directories) =>
        new(new Loopback([.. directories.Select(d => d.Server.Port)], Pinned([.. directories.Select(d => d.Key.PublicKey)])),
            [.. directories.Select((_, i) => $"10.235.0.{i + 1}")]);

    private static JsonObject Put(string id, string blob, int ttl = 600) => new() { ["op"] = "rv.put", ["id"] = id, ["blob"] = blob, ["ttl"] = ttl };

    [Fact]
    public async Task PutThenTakeOnceOverTheSecureChannel()
    {
        using var key = X25519KeyPair.Generate();
        await using var server = Server(key);
        var client = Client((server, key));
        var token = RandomNumberGenerator.GetBytes(16);
        var id = Rendezvous.Id(token);

        Assert.True(await client.PutRendezvousAsync(id, Rendezvous.Seal(token, "gate 10.235.3.4:25565"u8), TimeSpan.FromMinutes(10), default));
        Assert.Equal(1, server.RendezvousCount);
        // Never overwritten while it is waiting.
        Assert.False(await client.PutRendezvousAsync(id, Rendezvous.Seal(token, "someone else"u8), TimeSpan.FromMinutes(10), default));

        var taken = await client.TakeRendezvousAsync(id, default);
        Assert.Equal("gate 10.235.3.4:25565"u8.ToArray(), Rendezvous.Open(token, taken!));
        Assert.Equal(0, server.RendezvousCount);
        Assert.Null(await client.TakeRendezvousAsync(id, default));
    }

    [Fact]
    public async Task TakeClearsEveryDirectorysCopy()
    {
        using var k1 = X25519KeyPair.Generate();
        using var k2 = X25519KeyPair.Generate();
        await using var s1 = Server(k1);
        await using var s2 = Server(k2);
        var client = Client((s1, k1), (s2, k2));
        var token = RandomNumberGenerator.GetBytes(32);
        var id = Rendezvous.Id(token);
        Assert.True(await client.PutRendezvousAsync(id, Rendezvous.Seal(token, "x"u8), TimeSpan.FromMinutes(5), default));
        Assert.Equal((1, 1), (s1.RendezvousCount, s2.RendezvousCount));
        Assert.NotNull(await client.TakeRendezvousAsync(id, default));
        Assert.Equal((0, 0), (s1.RendezvousCount, s2.RendezvousCount));
    }

    [Fact]
    public async Task LegacyRequestsAreRefused()
    {
        using var key = X25519KeyPair.Generate();
        await using var server = Server(key);
        var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        Assert.Equal("secure", server.Handle(Put(id, "AAAA"))["error"]?.GetValue<string>());
        Assert.Equal("secure", server.Handle(new JsonObject { ["op"] = "rv.take", ["id"] = id })["error"]?.GetValue<string>());
        Assert.Equal(0, server.RendezvousCount);
    }

    [Fact]
    public async Task RecordsExpire()
    {
        var now = DateTimeOffset.UtcNow;
        using var key = X25519KeyPair.Generate();
        await using var server = Server(key, () => now);
        var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        Assert.True(server.Handle(Put(id, "AAAA", ttl: 60), secure: true, source: "10.235.0.9")["ok"]?.GetValue<bool>());
        now += TimeSpan.FromSeconds(61);
        Assert.Equal(0, server.RendezvousCount);
        Assert.Equal("not_found", server.Handle(new JsonObject { ["op"] = "rv.take", ["id"] = id }, secure: true, source: null)["error"]?.GetValue<string>());
    }

    [Theory]
    [InlineData("short", "AAAA", 60)]
    [InlineData(null, "", 60)]
    [InlineData(null, "!!!!", 60)]
    [InlineData(null, "AAAA", 0)]
    [InlineData(null, "AAAA", 601)]
    public async Task MalformedPutsAreRejected(string? id, string blob, int ttl)
    {
        using var key = X25519KeyPair.Generate();
        await using var server = Server(key);
        id ??= Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        Assert.Equal("bad", server.Handle(Put(id, blob, ttl), secure: true, source: null)["error"]?.GetValue<string>());
        // Over the size limit.
        var big = Convert.ToBase64String(new byte[Rendezvous.MaxSealed + 1]);
        Assert.Equal("bad", server.Handle(Put(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)), big), secure: true, source: null)["error"]?.GetValue<string>());
    }

    [Fact]
    public async Task PutsAreRateLimitedPerSource()
    {
        var now = DateTimeOffset.UtcNow;
        using var key = X25519KeyPair.Generate();
        await using var server = Server(key, () => now);
        JsonObject PutFrom(string source) => server.Handle(Put(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)), "AAAA"), secure: true, source);
        for (var i = 0; i < RoomDirectoryServer.MaxRendezvousPutsPerSource; i++) Assert.True(PutFrom("10.235.0.9")["ok"]?.GetValue<bool>());
        Assert.Equal("rate", PutFrom("10.235.0.9")["error"]?.GetValue<string>());
        Assert.True(PutFrom("10.235.0.10")["ok"]?.GetValue<bool>());
        now += Rendezvous.MaxTtl + TimeSpan.FromSeconds(1);
        Assert.True(PutFrom("10.235.0.9")["ok"]?.GetValue<bool>());
    }

    [Fact]
    public void SealOpensOnlyWithTheSameToken()
    {
        var token = RandomNumberGenerator.GetBytes(16);
        var other = RandomNumberGenerator.GetBytes(16);
        var record = Rendezvous.Seal(token, "hello"u8);
        Assert.Equal("hello"u8.ToArray(), Rendezvous.Open(token, record));
        Assert.ThrowsAny<CryptographicException>(() => Rendezvous.Open(other, record));
        record[^1] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => Rendezvous.Open(token, record));
        // Two seals of the same plaintext differ (fresh nonce), and the id never equals the seal key's use.
        Assert.NotEqual(Rendezvous.Seal(token, "hello"u8), Rendezvous.Seal(token, "hello"u8));
        Assert.Equal(Rendezvous.Id(token), Rendezvous.Id(token));
        Assert.NotEqual(Rendezvous.Id(token), Rendezvous.Id(other));
        Assert.Throws<ArgumentException>(() => Rendezvous.Id(new byte[15]));
        Assert.Throws<ArgumentException>(() => Rendezvous.Seal(token, new byte[Rendezvous.MaxPlaintext + 1]));
        Assert.Equal(Rendezvous.MaxSealed, Rendezvous.Seal(token, new byte[Rendezvous.MaxPlaintext]).Length);
    }
}
