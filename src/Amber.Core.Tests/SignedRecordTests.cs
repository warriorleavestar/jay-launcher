using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Amber.Core.Multiplayer.Public;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Tests;

/// <summary>Long-lived signed records on the directory (rec.put / rec.get) and the <see cref="SignedRecord"/> helpers.</summary>
public class SignedRecordTests
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

    private static Func<byte[], bool> Is(RecordSigner signer) => key => key.AsSpan().SequenceEqual(signer.PublicKey);

    private static JsonObject PutOp(SignedRecord record) => new() { ["op"] = "rec.put", ["record"] = record.ToJson() };

    [Fact]
    public async Task PutThenGetOverTheSecureChannel()
    {
        using var key = X25519KeyPair.Generate();
        await using var server = Server(key);
        var client = Client((server, key));
        var signer = new RecordSigner();
        var id = RandomNumberGenerator.GetBytes(32);
        var lookup = SignedRecord.Lookup(id);
        var now = DateTimeOffset.UtcNow;

        await client.PutRecordAsync(signer.Sign(lookup, SignedRecord.Seal(id, "gate.example:25565"u8), TimeSpan.FromDays(1), now), default);
        Assert.Equal(1, server.RecordCount);
        var got = await client.GetRecordAsync(lookup, Is(signer), default);
        Assert.NotNull(got);
        Assert.Equal("gate.example:25565"u8.ToArray(), SignedRecord.Open(id, got!.Blob));

        // Renewal by the same signer replaces it.
        await client.PutRecordAsync(signer.Sign(lookup, SignedRecord.Seal(id, "gate.example:25566"u8), TimeSpan.FromDays(1), now), default);
        var renewed = await client.GetRecordAsync(lookup, Is(signer), default);
        Assert.Equal("gate.example:25566"u8.ToArray(), SignedRecord.Open(id, renewed!.Blob));
        Assert.Equal(1, server.RecordCount);
    }

    [Fact]
    public async Task AnotherSignerCannotTakeOverTheLookup()
    {
        using var key = X25519KeyPair.Generate();
        await using var server = Server(key);
        var client = Client((server, key));
        var owner = new RecordSigner();
        var thief = new RecordSigner();
        var lookup = SignedRecord.Lookup(RandomNumberGenerator.GetBytes(16));
        var now = DateTimeOffset.UtcNow;
        await client.PutRecordAsync(owner.Sign(lookup, [1], TimeSpan.FromHours(1), now), default);
        var e = await Assert.ThrowsAsync<DirectoryRejectedException>(() => client.PutRecordAsync(thief.Sign(lookup, [2], TimeSpan.FromHours(1), now), default));
        Assert.Equal("owner", e.Code);
        // And a reader who expects the thief finds nothing.
        Assert.Null(await client.GetRecordAsync(lookup, Is(thief), default));
        Assert.NotNull(await client.GetRecordAsync(lookup, Is(owner), default));
    }

    [Fact]
    public async Task OlderSeqIsStaleAndRepeatIsIdempotent()
    {
        using var key = X25519KeyPair.Generate();
        await using var server = Server(key);
        var signer = new RecordSigner();
        var lookup = SignedRecord.Lookup(RandomNumberGenerator.GetBytes(16));
        var now = DateTimeOffset.UtcNow;
        var older = signer.Sign(lookup, [1], TimeSpan.FromHours(1), now);
        var newer = signer.Sign(lookup, [2], TimeSpan.FromHours(1), now);
        Assert.True(server.Handle(PutOp(newer), source: null)["ok"]?.GetValue<bool>());
        Assert.True(server.Handle(PutOp(newer), source: null)["ok"]?.GetValue<bool>());
        Assert.Equal("stale", server.Handle(PutOp(older), source: null)["error"]?.GetValue<string>());
    }

    [Fact]
    public async Task TamperedOrBadlyTimedRecordsAreRejected()
    {
        var now = DateTimeOffset.UtcNow;
        using var key = X25519KeyPair.Generate();
        await using var server = Server(key, () => now);
        var signer = new RecordSigner();
        var lookup = SignedRecord.Lookup(RandomNumberGenerator.GetBytes(16));
        var good = signer.Sign(lookup, [1, 2, 3], TimeSpan.FromHours(1), now);

        string? Put(SignedRecord r) => server.Handle(PutOp(r), source: null)["error"]?.GetValue<string>();
        Assert.Equal("bad", Put(good with { Blob = [1, 2, 4] }));
        Assert.Equal("bad", Put(good with { Seq = good.Seq + 1 }));
        Assert.Equal("bad", Put(good with { LookupKey = SignedRecord.Lookup(RandomNumberGenerator.GetBytes(16)) }));
        // Already expired, or valid for longer than allowed.
        Assert.Equal("bad", Put(signer.Sign(lookup, [1], TimeSpan.FromHours(1), now - TimeSpan.FromHours(2))));
        var tooLong = new SignedRecord(lookup, signer.PublicKey, 1, now + SignedRecord.MaxLifetime + TimeSpan.FromHours(1), [1], []);
        Assert.Equal("bad", Put(tooLong));
        Assert.Equal(0, server.RecordCount);
    }

    [Fact]
    public async Task RecordsExpireAndFreeTheLookup()
    {
        var now = DateTimeOffset.UtcNow;
        using var key = X25519KeyPair.Generate();
        await using var server = Server(key, () => now);
        var lookup = SignedRecord.Lookup(RandomNumberGenerator.GetBytes(16));
        var first = new RecordSigner();
        Assert.True(server.Handle(PutOp(first.Sign(lookup, [1], TimeSpan.FromMinutes(5), now)), source: null)["ok"]?.GetValue<bool>());
        now += TimeSpan.FromMinutes(6);
        Assert.Equal(0, server.RecordCount);
        Assert.Equal("not_found", server.Handle(new JsonObject { ["op"] = "rec.get", ["lookup"] = Convert.ToHexStringLower(lookup) }, source: null)["error"]?.GetValue<string>());
        // Once expired, another signer may claim it.
        Assert.True(server.Handle(PutOp(new RecordSigner().Sign(lookup, [2], TimeSpan.FromMinutes(5), now)), source: null)["ok"]?.GetValue<bool>());
    }

    [Fact]
    public async Task PutsAreRateLimitedPerSource()
    {
        var now = DateTimeOffset.UtcNow;
        using var key = X25519KeyPair.Generate();
        await using var server = Server(key, () => now);
        var signer = new RecordSigner();
        JsonObject PutFrom(string source) =>
            server.Handle(PutOp(signer.Sign(SignedRecord.Lookup(RandomNumberGenerator.GetBytes(16)), [1], TimeSpan.FromHours(2), now)), source);
        for (var i = 0; i < RoomDirectoryServer.MaxRecordPutsPerSource; i++) Assert.True(PutFrom("10.235.0.9")["ok"]?.GetValue<bool>());
        Assert.Equal("rate", PutFrom("10.235.0.9")["error"]?.GetValue<string>());
        // Another source gets its own allowance, but the signer has used up its hour.
        Assert.Equal("rate", PutFrom("10.235.0.10")["error"]?.GetValue<string>());
        signer = new RecordSigner();
        Assert.True(PutFrom("10.235.0.10")["ok"]?.GetValue<bool>());
    }

    [Fact]
    public async Task RecordPutsHaveADirectoryWideBudget()
    {
        var now = DateTimeOffset.UtcNow;
        using var key = X25519KeyPair.Generate();
        await using var server = Server(key, () => now);
        // Calls with no source address still meet the directory-wide budget.
        var accepted = 0;
        for (var i = 0; i < RoomDirectoryServer.MaxRecordPutsPerMinute; i++)
        {
            var record = new RecordSigner().Sign(SignedRecord.Lookup(RandomNumberGenerator.GetBytes(16)), [1], TimeSpan.FromHours(2), now);
            if (server.Handle(PutOp(record), source: null)["ok"]?.GetValue<bool>() == true) accepted++;
        }
        Assert.Equal(RoomDirectoryServer.MaxRecordPutsPerMinute, accepted);
        var late = new RecordSigner().Sign(SignedRecord.Lookup(RandomNumberGenerator.GetBytes(16)), [1], TimeSpan.FromHours(2), now);
        Assert.Equal("rate", server.Handle(PutOp(late), source: null)["error"]?.GetValue<string>());
        now += TimeSpan.FromMinutes(1.1);
        late = new RecordSigner().Sign(SignedRecord.Lookup(RandomNumberGenerator.GetBytes(16)), [1], TimeSpan.FromHours(2), now);
        Assert.True(server.Handle(PutOp(late), source: null)["ok"]?.GetValue<bool>());
    }

    [Fact]
    public async Task GetPicksTheNewestAcrossDirectories()
    {
        using var k1 = X25519KeyPair.Generate();
        using var k2 = X25519KeyPair.Generate();
        await using var s1 = Server(k1);
        await using var s2 = Server(k2);
        var signer = new RecordSigner();
        var lookup = SignedRecord.Lookup(RandomNumberGenerator.GetBytes(16));
        var now = DateTimeOffset.UtcNow;
        var old = signer.Sign(lookup, [1], TimeSpan.FromHours(1), now);
        var fresh = signer.Sign(lookup, [2], TimeSpan.FromHours(1), now);
        s1.Handle(PutOp(fresh), source: null);
        s2.Handle(PutOp(old), source: null);
        Assert.Equal([2], (await Client((s1, k1), (s2, k2)).GetRecordAsync(lookup, Is(signer), default))!.Blob);
    }

    [Fact]
    public void SignerSeedRoundTripsAndSealNeedsTheId()
    {
        var signer = new RecordSigner();
        Assert.Equal(signer.PublicKey, new RecordSigner(signer.ExportPrivateKey()).PublicKey);
        var id = RandomNumberGenerator.GetBytes(16);
        var blob = SignedRecord.Seal(id, "x"u8);
        Assert.ThrowsAny<CryptographicException>(() => SignedRecord.Open(RandomNumberGenerator.GetBytes(16), blob));
        Assert.Throws<ArgumentException>(() => SignedRecord.Lookup(new byte[15]));
        Assert.Equal(SignedRecord.MaxBlob, SignedRecord.Seal(id, new byte[SignedRecord.MaxPlaintext]).Length);
        // The lookup and rendezvous id derived from the same bytes differ (separate domains).
        Assert.NotEqual(SignedRecord.Lookup(id), Rendezvous.Id(id));
    }
}
