using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Amber.Core.Identity;
using Amber.Core.Multiplayer.Public;
using Amber.NetworkServer;
using Amber.Server;

namespace Amber.Core.Tests;

/// <summary>
/// Volunteer registrations signed with a Jay account: the directory checks the device signature and certificate, keeps
/// the account as the node's operator, hands out one node per operator first, gives members only the addresses, and
/// lets the network's operator require signatures and ban an operator. Loopback only.
/// </summary>
public sealed class SignedVolunteersTests : IAsyncDisposable
{
    private DateTimeOffset _now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jay-signed-volunteer-test-" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _keys = [];
    private readonly List<IAsyncDisposable> _cleanup = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var c in _cleanup) await c.DisposeAsync();
        _keys.ForEach(k => k.Dispose());
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private sealed class LoopbackNetwork(int port) : IPublicNetwork
    {
        public async Task<Stream> ConnectAsync(string ip, int p, CancellationToken ct)
        {
            var c = new TcpClient();
            await c.ConnectAsync(IPAddress.Loopback, port, ct);
            return c.GetStream();
        }

        public Task<string?> MyIpAsync(CancellationToken ct) => Task.FromResult<string?>("10.235.0.7");
    }

    /// <summary>A person: a Jay account and one device it certified.</summary>
    private JayProof Person(JayAccount? account = null)
    {
        account ??= JayAccount.FromMnemonic(Bip39.Generate(MnemonicLanguage.English), null);
        var device = DeviceKeys.Generate();
        _keys.Add(account);
        _keys.Add(device);
        return new JayProof(account.CertifyDevice(device, "Laptop", 0, _now), device);
    }

    private static string? ErrorOf(JsonObject reply) => reply["error"]?.GetValue<string>();
    private static bool Ok(JsonObject reply) => reply["ok"]?.GetValue<bool>() == true;

    private const string Net = "test-net";

    private VolunteerRegistry Registry() => new((_, _) => Task.FromResult(true), () => _now) { NetworkId = Net };

    private async Task<JsonObject> RegisterAsync(VolunteerRegistry registry, string url, JayProof? by)
    {
        // Each renewal is signed anew, a little later than the last.
        _now += TimeSpan.FromSeconds(1);
        var reply = registry.Register(url, by is null ? null : VolunteerOperator.Sign(by, Net, VolunteerNodes.Normalize(url)!, _now));
        await registry.LastProbe;
        return reply;
    }

    [Fact]
    public void ASignatureOnlyCountsForItsNetworkAddressAndTime()
    {
        var alice = Person();
        const string address = "tcp://x.example.org:11010";
        var signed = VolunteerOperator.Sign(alice, Net, address, _now);
        Assert.Equal((alice.Certificate.Account, _now.ToUnixTimeSeconds()), VolunteerOperator.Verify(signed, Net, address, _now));
        // Another network, another address, too far from the directory's clock, or a certificate for another device: not counted.
        Assert.Null(VolunteerOperator.Verify(signed, "other-net", address, _now));
        Assert.Null(VolunteerOperator.Verify(signed, Net, "tcp://y.example.org:11010", _now));
        Assert.Null(VolunteerOperator.Verify(signed, Net, address, _now + VolunteerOperator.MaxClockSkew + TimeSpan.FromSeconds(1)));
        var bob = Person();
        var swapped = (JsonObject)signed.DeepClone();
        swapped["cert"] = bob.Certificate.ToJson();
        Assert.Null(VolunteerOperator.Verify(swapped, Net, address, _now));
        var tampered = (JsonObject)signed.DeepClone();
        tampered["at"] = _now.ToUnixTimeSeconds() + 1;
        Assert.Null(VolunteerOperator.Verify(tampered, Net, address, _now));
        Assert.Null(VolunteerOperator.Verify(new JsonObject { ["cert"] = "x", ["sig"] = 5 }, Net, address, _now));
        // The whole request still fits in one directory request.
        var request = new JsonObject { ["op"] = "volunteer", ["url"] = address, ["operator"] = signed, ["jay"] = "0.8.0" };
        Assert.True(Encoding.UTF8.GetByteCount(request.ToJsonString()) < RoomDirectoryProtocol.MaxLine);
    }

    [Fact]
    public async Task AnAddressKeepsItsOperatorUntilItLapses()
    {
        var registry = Registry();
        var alice = Person();
        var bob = Person();
        Assert.True(Ok(await RegisterAsync(registry, "tcp://x.example.org:11010", alice)));
        Assert.Equal(alice.Certificate.Account, registry.Operators().Single().Operator);
        // Someone else can't take the address over, or strip the signature off it. The network's operator still sees
        // who registered it, and the signed conflicting claims; members' spreading no longer counts it as Alice's.
        var disputed = new List<string>();
        registry.Disputed += disputed.Add;
        Assert.Equal("owner", ErrorOf(await RegisterAsync(registry, "tcp://x.example.org:11010", bob)));
        Assert.Equal("owner", ErrorOf(await RegisterAsync(registry, "tcp://x.example.org:11010", null)));
        Assert.True(Ok(await RegisterAsync(registry, "tcp://x.example.org:11010", alice)));
        var x = registry.Operators().Single();
        Assert.Equal((alice.Certificate.Account, true), (x.Operator, x.Disputed));
        // The unsigned attempt was turned away but isn't a claim: it says nothing about who runs the node.
        Assert.Equal([bob.Certificate.Account], x.ClaimedBy!);
        Assert.Equal(["tcp://x.example.org:11010"], disputed);
        Assert.Equal("", registry.Pick().Single().Operator);
        // A broken signature is refused outright.
        var broken = VolunteerOperator.Sign(alice, Net, "tcp://y.example.org:11010", _now);
        Assert.Equal("operator", ErrorOf(registry.Register("tcp://z.example.org:11010", broken)));
        // After it lapses the address is free again.
        _now += VolunteerNodes.Ttl + TimeSpan.FromSeconds(1);
        Assert.True(Ok(await RegisterAsync(registry, "tcp://x.example.org:11010", bob)));
        Assert.Equal(bob.Certificate.Account, registry.Operators().Single().Operator);
        // An unsigned address can't be claimed by an account before it lapses either.
        Assert.True(Ok(await RegisterAsync(registry, "tcp://z.example.org:11010", null)));
        Assert.Equal("owner", ErrorOf(await RegisterAsync(registry, "tcp://z.example.org:11010", alice)));
        Assert.True(Ok(await RegisterAsync(registry, "tcp://z.example.org:11010", null)));
        var z = registry.Operators().Single(n => n.Url == "tcp://z.example.org:11010");
        Assert.Null(z.Operator);
        Assert.Equal([alice.Certificate.Account], z.ClaimedBy!);
    }

    [Fact]
    public async Task AClaimNeitherHidesNorTakesTheOperator()
    {
        var registry = Registry();
        var alice = Person();
        var mallory = Person();
        // Alice registering again under another identity doesn't hide who registered the address.
        Assert.True(Ok(await RegisterAsync(registry, "tcp://x.example.org:11010", alice)));
        Assert.Equal("owner", ErrorOf(await RegisterAsync(registry, "tcp://x.example.org:11010", Person())));
        Assert.Equal(alice.Certificate.Account, registry.Operators().Single().Operator);
        // Someone registering Bob's address first keeps it, but Bob's claim shows, and once the network's operator
        // drops the address Bob gets it.
        var bob = Person();
        Assert.True(Ok(await RegisterAsync(registry, "tcp://b.example.org:11010", mallory)));
        Assert.Equal("owner", ErrorOf(await RegisterAsync(registry, "tcp://b.example.org:11010", bob)));
        var entry = registry.Operators().Single(n => n.Url == "tcp://b.example.org:11010");
        Assert.Equal((mallory.Certificate.Account, true), (entry.Operator, entry.Disputed));
        // One-off claims filling the list can't keep Bob out while he keeps renewing: the claim not seen for longest makes room.
        for (var i = 0; i < 10; i++)
        {
            _now += TimeSpan.FromSeconds(1);
            await RegisterAsync(registry, "tcp://b.example.org:11010", Person());
            _now += TimeSpan.FromSeconds(1);
            await RegisterAsync(registry, "tcp://b.example.org:11010", bob);
        }
        entry = registry.Operators().Single(n => n.Url == "tcp://b.example.org:11010");
        Assert.Equal(VolunteerRegistry.MaxClaims, entry.ClaimedBy!.Count);
        Assert.Contains(bob.Certificate.Account, entry.ClaimedBy);
        Assert.True(registry.Remove("tcp://b.example.org:11010"));
        Assert.True(Ok(await RegisterAsync(registry, "tcp://b.example.org:11010", bob)));
        var mine = registry.Operators().Single(n => n.Url == "tcp://b.example.org:11010");
        Assert.Equal((bob.Certificate.Account, false), (mine.Operator, mine.Disputed));
    }

    [Fact]
    public async Task BanningASquatterDoesNotBanTheAddressItTook()
    {
        var server = await StartAsync();
        var mallory = Person();
        var victim = Person();
        Assert.True(Ok(Volunteer(server, "tcp://v.example.org:11010", VolunteerOperator.Sign(mallory, VolunteerOperator.NetworkTag(server.Profile), "tcp://v.example.org:11010", _now))));
        Assert.True(Ok(Volunteer(server, "tcp://m.example.org:11010", VolunteerOperator.Sign(mallory, VolunteerOperator.NetworkTag(server.Profile), "tcp://m.example.org:11010", _now))));
        Assert.Equal("owner", ErrorOf(Volunteer(server, "tcp://v.example.org:11010", VolunteerOperator.Sign(victim, VolunteerOperator.NetworkTag(server.Profile), "tcp://v.example.org:11010", _now))));
        await server.Volunteers!.LastProbe;
        Assert.Equal(2, server.Ban("operator", mallory.Certificate.Account, "abuse", TimeSpan.FromDays(7)));
        // Mallory's own address is banned with them; the one the victim also claimed is only dropped, and is the victim's now.
        Assert.Contains(server.Bans!.List(), b => b.Kind == "node" && b.Value == "tcp://m.example.org:11010");
        Assert.DoesNotContain(server.Bans.List(), b => b.Kind == "node" && b.Value == "tcp://v.example.org:11010");
        _now += TimeSpan.FromSeconds(1);
        Assert.True(Ok(Volunteer(server, "tcp://v.example.org:11010", VolunteerOperator.Sign(victim, VolunteerOperator.NetworkTag(server.Profile), "tcp://v.example.org:11010", _now))));
    }

    [Fact]
    public async Task OnlyClaimsFromOtherUnbannedAccountsSpareAnAddress()
    {
        var server = await StartAsync();
        var mallory = Person();
        var other = Person();
        string Url(string name) => $"tcp://{name}.example.org:11010";
        JsonObject As(JayProof who, string url) => VolunteerOperator.Sign(who, VolunteerOperator.NetworkTag(server.Profile), url, _now);
        foreach (var name in new[] { "a", "b", "c" }) Assert.True(Ok(Volunteer(server, Url(name), As(mallory, Url(name)))));
        // An unsigned registration of a signed address is turned away without counting as a claim, but it is counted.
        Assert.Equal("owner", ErrorOf(Volunteer(server, Url("a"), null)));
        var a = server.Volunteers!.Operators().Single(n => n.Url == Url("a"));
        Assert.False(a.Disputed);
        Assert.Equal(1, a.UnsignedAttempts);
        // A claim from another account spares the address; one from an account that is banned by then doesn't.
        Assert.Equal("owner", ErrorOf(Volunteer(server, Url("b"), As(other, Url("b")))));
        var banned = Person();
        Assert.Equal("owner", ErrorOf(Volunteer(server, Url("c"), As(banned, Url("c")))));
        server.Ban("operator", banned.Certificate.Account, "spam");
        await server.Volunteers.LastProbe;
        var result = server.BanOperator(mallory.Certificate.Account, "abuse", TimeSpan.FromDays(1));
        Assert.Equal(3, result.Removed);
        // "a" may be run by someone who never signed (Mallory registered it first): dropped, not banned.
        Assert.Equal([Url("a"), Url("b")], result.DisputedNotBanned.Order());
        Assert.Equal([Url("c")], result.BannedNodes);
        Assert.Equal([Url("c")], server.Bans!.List().Where(b => b.Kind == "node").Select(b => b.Value));

        // Banning again for longer moves the end of the address bans with it.
        server.BanOperator(mallory.Certificate.Account, "again", TimeSpan.FromDays(30));
        Assert.All(server.Bans.List().Where(b => b.Kind == "node"), b => Assert.Equal(_now + TimeSpan.FromDays(30), b.Until));
        // And for good.
        server.BanOperator(mallory.Certificate.Account, "for good");
        Assert.All(server.Bans.List().Where(b => b.Kind == "node"), b => Assert.Null(b.Until));
    }

    [Fact]
    public async Task BanningAHijackerOfAnUnsignedVolunteerSparesTheirAddress()
    {
        var server = await StartAsync();
        var victim = "tcp://v.example.org:11010";
        // The victim's entry lapsed; someone signs for the address before the victim (who never signs) renews.
        var hijacker = Person();
        Assert.True(Ok(Volunteer(server, victim, VolunteerOperator.Sign(hijacker, VolunteerOperator.NetworkTag(server.Profile), victim, _now))));
        Assert.Equal("owner", ErrorOf(Volunteer(server, victim, null)));
        await server.Volunteers!.LastProbe;
        var result = server.BanOperator(hijacker.Certificate.Account, "hijack");
        Assert.Equal([victim], result.DisputedNotBanned);
        Assert.Empty(result.BannedNodes);
        Assert.True(Ok(Volunteer(server, victim, null)));
    }

    [Fact]
    public void AnUnsignedFloodDoesNotKeepSignedVolunteersOut()
    {
        var registry = Registry();
        for (var i = 0; i < VolunteerRegistry.NewPerMinute; i++) Assert.True(Ok(registry.Register($"tcp://f{i}.example.org:11010")));
        Assert.Equal("rate", ErrorOf(registry.Register("tcp://more.example.org:11010")));
        // A signed volunteer has its own allowance.
        var alice = Person();
        Assert.True(Ok(registry.Register("tcp://alice.example.org:11010", VolunteerOperator.Sign(alice, Net, "tcp://alice.example.org:11010", _now))));
        // One account gets only a few new addresses a minute.
        for (var i = 1; i < VolunteerRegistry.NewPerAccountPerMinute; i++)
            Assert.True(Ok(registry.Register($"tcp://a{i}.example.org:11010", VolunteerOperator.Sign(alice, Net, $"tcp://a{i}.example.org:11010", _now))));
        Assert.Equal("rate", ErrorOf(registry.Register("tcp://a99.example.org:11010", VolunteerOperator.Sign(alice, Net, "tcp://a99.example.org:11010", _now))));
        // One caller gets only a few new addresses a minute, signed or not (a loopback caller isn't told apart).
        var bob = Person();
        for (var i = 0; i < VolunteerRegistry.NewPerSourcePerMinute; i++)
        {
            var url = $"tcp://s{i}.example.org:11010";
            Assert.True(Ok(registry.Register(url, VolunteerOperator.Sign(i % 2 == 0 ? bob : Person(), Net, url, _now), "10.235.0.9")));
        }
        Assert.Equal("rate", ErrorOf(registry.Register("tcp://s99.example.org:11010", VolunteerOperator.Sign(Person(), Net, "tcp://s99.example.org:11010", _now), "10.235.0.9")));
        Assert.True(Ok(registry.Register("tcp://s98.example.org:11010", VolunteerOperator.Sign(Person(), Net, "tcp://s98.example.org:11010", _now), "127.0.0.1")));
        // A minute later there is room again.
        _now += TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1);
        Assert.True(Ok(registry.Register("tcp://more.example.org:11010")));
    }

    [Fact]
    public void AVolunteerComingBackIsNotNew()
    {
        var registry = Registry();
        var alice = Person();
        var url = "tcp://alice.example.org:11010";
        Assert.True(Ok(registry.Register(url, VolunteerOperator.Sign(alice, Net, url, _now))));
        Assert.True(registry.Remove(url));
        // Alice's allowance is used up by other addresses; coming back to her own address still works.
        for (var i = 1; i < VolunteerRegistry.NewPerAccountPerMinute; i++)
            Assert.True(Ok(registry.Register($"tcp://a{i}.example.org:11010", VolunteerOperator.Sign(alice, Net, $"tcp://a{i}.example.org:11010", _now))));
        _now += TimeSpan.FromSeconds(1);
        Assert.True(Ok(registry.Register(url, VolunteerOperator.Sign(alice, Net, url, _now))));
    }

    [Fact]
    public async Task AnUnreachableAddressIsNotDialedOverAndOver()
    {
        var dials = 0;
        var registry = new VolunteerRegistry((_, _) => { Interlocked.Increment(ref dials); return Task.FromResult(false); }, () => _now) { NetworkId = Net };
        var mallory = Person();
        var target = "tcp://target.example.org:11010";
        // Signed again and again a second apart: each failed probe leaves only a replay guard, not a way back in, and the
        // same address is dialed at most once per check interval.
        var accepted = 0;
        for (var i = 0; i < 60; i++)
        {
            _now += TimeSpan.FromSeconds(1);
            if (Ok(registry.Register(target, VolunteerOperator.Sign(mallory, Net, target, _now)))) accepted++;
            await registry.LastProbe;
        }
        var allowed = (int)(TimeSpan.FromSeconds(60) / VolunteerRegistry.CheckSameAddressEvery) + 1;
        Assert.InRange(dials, 1, allowed);
        Assert.Equal(dials, accepted);
        // Many addresses: the account's allowance holds (failed probes don't make them "returning").
        var others = 0;
        for (var i = 0; i < 40; i++)
        {
            var url = $"tcp://t{i}.example.org:11010";
            if (Ok(registry.Register(url, VolunteerOperator.Sign(mallory, Net, url, _now)))) others++;
            await registry.LastProbe;
        }
        Assert.True(others <= VolunteerRegistry.NewPerAccountPerMinute);
    }

    [Fact]
    public async Task AFloodOfClaimsCannotPushOutTheLongestClaimant()
    {
        var registry = Registry();
        var squatter = Person();
        var victim = Person();
        var url = "tcp://v.example.org:11010";
        Assert.True(Ok(await RegisterAsync(registry, url, squatter)));
        await registry.LastProbe;
        Assert.Equal("owner", ErrorOf(await RegisterAsync(registry, url, victim)));
        // Throwaway accounts claim over and over while the victim keeps renewing.
        for (var round = 0; round < 3; round++)
        {
            for (var i = 0; i < 6; i++) await RegisterAsync(registry, url, Person());
            Assert.Equal("owner", ErrorOf(await RegisterAsync(registry, url, victim)));
        }
        var entry = registry.Operators().Single();
        Assert.Contains(victim.Certificate.Account, entry.ClaimedBy!);
        // The operator sees how many claims and accounts there were.
        Assert.Equal(1 + 3 * 6 + 3, entry.Claims);
        Assert.Equal(1 + 3 * 6, entry.Claimants);
        // One account can't claim without limit either.
        var spammer = Person();
        string? last = null;
        for (var i = 0; i < VolunteerRegistry.NewPerAccountPerMinute + 2; i++)
        {
            _now += TimeSpan.FromMilliseconds(1100);
            last = ErrorOf(registry.Register(url, VolunteerOperator.Sign(spammer, Net, url, _now)));
        }
        Assert.Equal("rate", last);
    }

    [Fact]
    public async Task AnOperatorBanHoldsWhateverTheAccountsSpelling()
    {
        // The account comes from the certificate's keys, always in canonical form; a ban written another way still matches.
        var server = await StartAsync();
        var alice = Person();
        var url = "tcp://x.example.org:11010";
        Assert.Equal(alice.Certificate.Account, JayAddress.Canonical(alice.Certificate.Account));
        Assert.Equal(alice.Certificate.Account, VolunteerOperator.Verify(VolunteerOperator.Sign(alice, Net, url, _now), Net, url, _now)!.Value.Account);
        server.Ban("operator", alice.Certificate.Account.ToUpperInvariant(), "spam");
        Assert.Equal("banned", ErrorOf(Volunteer(server, url, VolunteerOperator.Sign(alice, VolunteerOperator.NetworkTag(server.Profile), url, _now))));
    }

    [Fact]
    public void BrokenCertificatesAndSignaturesAreRefused()
    {
        var signed = VolunteerOperator.Sign(Person(), Net, "tcp://x.example.org:11010", _now);
        JsonObject With(string field, JsonNode? value)
        {
            var copy = (JsonObject)signed.DeepClone();
            copy[field] = value;
            return copy;
        }
        var cert = (JsonObject)signed["cert"]!;
        var brokenKey = (JsonObject)cert.DeepClone();
        foreach (var field in brokenKey.Select(p => p.Key).ToList())
            if (brokenKey[field] is JsonValue v && v.TryGetValue<string>(out var text) && text.Length > 40) brokenKey[field] = Convert.ToBase64String(new byte[text.Length * 3 / 4]);
        foreach (var bad in new[]
        {
            With("sig", "not base64!"), With("sig", Convert.ToBase64String(new byte[10])), With("sig", Convert.ToBase64String(new byte[5000])),
            With("cert", "x"), With("cert", new JsonObject()), With("cert", brokenKey), With("at", "now"), With("sig", 5),
        })
            Assert.Null(VolunteerOperator.Verify(bad, Net, "tcp://x.example.org:11010", _now));
        // A little ahead of the directory's clock is fine; more isn't.
        Assert.NotNull(VolunteerOperator.Verify(VolunteerOperator.Sign(Person(), Net, "tcp://x.example.org:11010", _now + TimeSpan.FromSeconds(50)), Net, "tcp://x.example.org:11010", _now));
        Assert.Null(VolunteerOperator.Verify(VolunteerOperator.Sign(Person(), Net, "tcp://x.example.org:11010", _now + VolunteerOperator.MaxClockAhead + TimeSpan.FromSeconds(1)), Net, "tcp://x.example.org:11010", _now));
    }

    [Theory]
    [InlineData(long.MinValue)]
    [InlineData(long.MaxValue)]
    [InlineData(0L)]
    public void AnOutOfRangeTimeIsRefused(long offset)
    {
        var signed = VolunteerOperator.Sign(Person(), Net, "tcp://x.example.org:11010", _now);
        // Far from now in either direction, including values whose difference from now doesn't fit in 64 bits.
        signed["at"] = offset == 0 ? long.MinValue + _now.ToUnixTimeSeconds() : offset;
        Assert.Null(VolunteerOperator.Verify(signed, Net, "tcp://x.example.org:11010", _now));
        Assert.Equal("operator", ErrorOf(Registry().Register("tcp://x.example.org:11010", signed)));
    }

    [Fact]
    public async Task ASignedRegistrationCountsOnce()
    {
        var registry = Registry();
        var alice = Person();
        const string address = "tcp://x.example.org:11010";
        var first = VolunteerOperator.Sign(alice, Net, address, _now);
        Assert.True(Ok(registry.Register(address, first)));
        await registry.LastProbe;
        // The same registration again, or an older one, is refused; a newer one renews.
        Assert.Equal("stale", ErrorOf(registry.Register(address, first)));
        Assert.Equal("stale", ErrorOf(registry.Register(address, VolunteerOperator.Sign(alice, Net, address, _now - TimeSpan.FromSeconds(5)))));
        Assert.True(Ok(registry.Register(address, VolunteerOperator.Sign(alice, Net, address, _now + TimeSpan.FromSeconds(1)))));
        // Even after the address dropped out, an old registration can't bring it back.
        Assert.True(registry.Remove(address));
        Assert.Equal("stale", ErrorOf(registry.Register(address, first)));
        // A registration for another network is refused.
        Assert.Equal("operator", ErrorOf(registry.Register("tcp://z.example.org:11010", VolunteerOperator.Sign(alice, "other-net", "tcp://z.example.org:11010", _now))));
    }

    [Fact]
    public async Task MembersGetOneNodePerOperatorFirst()
    {
        var registry = Registry();
        var alice = Person();
        var bob = Person();
        // Alice runs six nodes, Bob one, and two are unsigned.
        for (var i = 0; i < 6; i++) await RegisterAsync(registry, $"tcp://a{i}.example.org:11010", alice);
        await RegisterAsync(registry, "tcp://b.example.org:11010", bob);
        await RegisterAsync(registry, "tcp://u1.example.org:11010", null);
        await RegisterAsync(registry, "tcp://u2.example.org:11010", null);
        for (var round = 0; round < 20; round++)
        {
            var four = registry.Pick(4);
            Assert.Equal(4, four.Count);
            // One of Alice's, Bob's, and the two unsigned ones (each counts as its own operator).
            Assert.Single(four, n => n.Url.StartsWith("tcp://a", StringComparison.Ordinal));
            Assert.Contains(four, n => n.Url == "tcp://b.example.org:11010");
            Assert.Equal(2, four.Count(n => n.Operator.Length == 0));
        }
        var all = registry.Pick(9);
        Assert.Equal(9, all.Count);
        Assert.Equal(6, all.Count(n => n.Operator == alice.Certificate.Account));
    }

    [Fact]
    public async Task TheClientSignsAndMembersGetOnlyAddresses()
    {
        await using var directory = new RoomDirectoryServer(IPAddress.Loopback, 0, clock: () => _now) { Volunteers = Registry() };
        var client = new RoomDirectoryClient(new LoopbackNetwork(directory.Port)) { NetworkId = Net };
        var alice = Person();
        var bob = Person();
        await client.VolunteerAsync("tcp://x.example.org:11010", alice, default);
        await client.VolunteerAsync("tcp://y.example.org:11010", alice, default);
        await client.VolunteerAsync("tcp://z.example.org:11010", bob, default);
        await directory.Volunteers.LastProbe;
        Assert.Equal(alice.Certificate.Account, directory.Volunteers.Operators().Single(n => n.Url == "tcp://x.example.org:11010").Operator);
        // Members get the addresses, one per operator first, and nothing about who runs them.
        var reply = directory.Handle(new JsonObject { ["op"] = "nodes" });
        Assert.Equal(["nodes", "ok"], reply.Select(p => p.Key).Order());
        for (var round = 0; round < 10; round++)
        {
            var first = (await client.NodesAsync(default)).Take(2).ToList();
            Assert.Contains("tcp://z.example.org:11010", first);
        }
        // Someone else's account on Alice's address: told why.
        var e = await Assert.ThrowsAsync<DirectoryRejectedException>(() => client.VolunteerAsync("tcp://x.example.org:11010", Person(), default));
        Assert.Equal("owner", e.Code);
    }

    [Fact]
    public void AFloodOfNewAddressesDoesNotStopRenewals()
    {
        // Many accounts signing for many addresses nobody can reach: new addresses are taken at a limited rate, what's
        // remembered about them stays bounded, and the volunteer already on the list keeps renewing.
        const string listedUrl = "tcp://x.example.org:11010";
        var registry = new VolunteerRegistry((url, _) => Task.FromResult(url == listedUrl), () => _now) { NetworkId = Net };
        var alice = Person();
        Assert.True(Ok(registry.Register(listedUrl, VolunteerOperator.Sign(alice, Net, listedUrl, _now))));
        registry.LastProbe.GetAwaiter().GetResult();
        var people = Enumerable.Range(0, 4).Select(_ => Person()).ToList();
        var refused = registry.Register("tcp://r.example.org:11010", VolunteerOperator.Sign(people[0], "other-net", "tcp://r.example.org:11010", _now));
        Assert.Equal("operator", ErrorOf(refused));
        var rate = 0;
        for (var minute = 0; minute < 6; minute++)
        {
            for (var i = 0; i < VolunteerRegistry.NewPerMinute + 20; i++)
            {
                var url = $"tcp://n{minute}-{i}.example.org:11010";
                var reply = registry.Register(url, VolunteerOperator.Sign(people[i % people.Count], Net, url, _now));
                if (ErrorOf(reply) == "rate") rate++;
                registry.LastProbe.GetAwaiter().GetResult();
            }
            Assert.True(registry.TombstoneCount <= VolunteerRegistry.MaxTombstones);
            _now += TimeSpan.FromSeconds(61);
            // The listed volunteer renews as usual.
            Assert.True(Ok(registry.Register(listedUrl, VolunteerOperator.Sign(alice, Net, listedUrl, _now))));
        }
        Assert.True(rate >= 6 * 20);
        Assert.Equal([listedUrl], registry.Listed());
    }

    [Fact]
    public async Task TheClientNeverSignsTheSameSecondTwice()
    {
        await using var directory = new RoomDirectoryServer(IPAddress.Loopback, 0, clock: () => _now) { Volunteers = Registry() };
        var client = new RoomDirectoryClient(new LoopbackNetwork(directory.Port)) { NetworkId = Net, Clock = () => _now };
        var alice = Person();
        // Renewals right after each other (same second) are all accepted.
        for (var i = 0; i < 3; i++) await client.VolunteerAsync("tcp://x.example.org:11010", alice, default);
        // A directory that already has a newer one (clock went back) says so with its own code.
        var e = await Assert.ThrowsAsync<DirectoryRejectedException>(() =>
            new RoomDirectoryClient(new LoopbackNetwork(directory.Port)) { NetworkId = Net, Clock = () => _now }.VolunteerAsync("tcp://x.example.org:11010", alice, default));
        Assert.Equal("stale", e.Code);
    }

    private async Task<JayNetworkServer> StartAsync(Action<JayNetworkServerBuilder>? configure = null)
    {
        var builder = new JayNetworkServerBuilder().UseDataDir(_dir).UseClock(() => _now).WithoutEasyTier(new IPEndPoint(IPAddress.Loopback, 0))
            .UseVolunteerProbe((_, _) => Task.FromResult(true)).UseAdmin(0);
        configure?.Invoke(builder);
        var server = builder.Build();
        _cleanup.Add(server);
        await server.StartAsync();
        return server;
    }

    private sealed class ProfiledNetwork(PublicNetworkProfile profile) : IPublicNetwork
    {
        public Task<Stream> ConnectAsync(string ip, int p, CancellationToken ct) => throw new IOException();
        public Task<string?> MyIpAsync(CancellationToken ct) => Task.FromResult<string?>("10.235.0.7");
        public PublicNetworkProfile? Profile => profile;
    }

    [Fact]
    public async Task ClientAndDirectoryNameTheSameNetwork()
    {
        // The built-in network: the app's profile and the node's.
        var jay = await StartAsync();
        var client = new RoomDirectoryClient(new ProfiledNetwork(new PublicNetworkList(null).Current(null, null)));
        Assert.Equal(VolunteerOperator.NetworkTag(jay.Profile), client.NetworkId);
        Assert.StartsWith("jay:", client.NetworkId);
        var alice = Person();
        Assert.True(Ok(Volunteer(jay, "tcp://x.example.org:11010", VolunteerOperator.Sign(alice, client.NetworkId, "tcp://x.example.org:11010", _now))));
        await jay.DisposeAsync();
        _cleanup.Remove(jay);

        // A network of its own, through its connection string: the node runs from the parsed string, the app from the
        // profile it saved.
        var owner = TrustSigner.Generate(out _);
        var text = NetworkString.Create(new NetworkInfo("mynet", "My net", "jay-net-mynet", "s", ["tcp://node.example.net:11010"]), owner);
        var own = await StartAsync(b => b.UseNetwork(NetworkString.Parse(text, [])));
        client = new RoomDirectoryClient(new ProfiledNetwork(NetworkString.Parse(text, []).ToProfile()));
        Assert.StartsWith("mynet:", client.NetworkId);
        Assert.Equal(VolunteerOperator.NetworkTag(own.Profile), client.NetworkId);
        Assert.True(Ok(Volunteer(own, "tcp://y.example.org:11010", VolunteerOperator.Sign(alice, client.NetworkId, "tcp://y.example.org:11010", _now))));
        // A registration signed for another network isn't taken, not even for another network that took the same id.
        Assert.Equal("operator", ErrorOf(Volunteer(own, "tcp://z.example.org:11010", VolunteerOperator.Sign(alice, "jay", "tcp://z.example.org:11010", _now))));
        var twin = NetworkString.Parse(NetworkString.Create(new NetworkInfo("mynet", "My net", "jay-net-other", "t", ["tcp://node.example.net:11010"]), owner), []).ToProfile();
        Assert.Equal("operator", ErrorOf(Volunteer(own, "tcp://z.example.org:11010",
            VolunteerOperator.Sign(alice, VolunteerOperator.NetworkTag(twin), "tcp://z.example.org:11010", _now))));
    }

    private static JsonObject Volunteer(JayNetworkServer server, string url, JsonObject? signed) =>
        server.Directory!.Handle(new JsonObject { ["op"] = "volunteer", ["url"] = url, ["operator"] = signed });

    [Fact]
    public async Task TheNetworksOperatorCanRequireSignaturesAndBanAPerson()
    {
        var alice = Person();
        var server = await StartAsync(b => b.RequireSignedVolunteers());
        Assert.Equal("operator", ErrorOf(Volunteer(server, "tcp://x.example.org:11010", null)));
        Assert.True(Ok(Volunteer(server, "tcp://x.example.org:11010", VolunteerOperator.Sign(alice, VolunteerOperator.NetworkTag(server.Profile), "tcp://x.example.org:11010", _now))));
        Assert.True(Ok(Volunteer(server, "tcp://y.example.org:11010", VolunteerOperator.Sign(alice, VolunteerOperator.NetworkTag(server.Profile), "tcp://y.example.org:11010", _now))));
        await server.Volunteers!.LastProbe;

        // The admin API shows who runs each node.
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", File.ReadAllText(Path.Combine(_dir, "admin.token")).Trim());
        var nodes = JsonNode.Parse(await http.GetStringAsync($"http://127.0.0.1:{server.AdminPort}/nodes"))!;
        Assert.All(nodes["operators"]!.AsArray(), n => Assert.Equal(alice.Certificate.Account, n!["operator"]!.GetValue<string>()));

        // Banning the person drops all their nodes and keeps them out, whatever address they come back with; their
        // addresses are banned too, so coming back from them unsigned doesn't work either.
        Assert.Equal(2, server.Ban("operator", alice.Certificate.Account, "abuse"));
        Assert.Contains(server.Bans!.List(), b => b.Kind == "node" && b.Value == "tcp://x.example.org:11010");
        Assert.Empty(server.Volunteers.Listed());
        Assert.Equal("banned", ErrorOf(Volunteer(server, "tcp://z.example.org:11010", VolunteerOperator.Sign(alice, VolunteerOperator.NetworkTag(server.Profile), "tcp://z.example.org:11010", _now))));
        Assert.Throws<ArgumentException>(() => server.Ban("operator", "not-an-address", null));
        // Unbanning the person lifts the address bans that came with it.
        Assert.True(server.Unban("operator", alice.Certificate.Account));
        Assert.DoesNotContain(server.Bans!.List(), b => b.Kind == "node");
        _now += TimeSpan.FromSeconds(1);
        Assert.True(Ok(Volunteer(server, "tcp://z.example.org:11010", VolunteerOperator.Sign(alice, VolunteerOperator.NetworkTag(server.Profile), "tcp://z.example.org:11010", _now))));

        // The CLI switch turns the same requirement on, and leaves the other switches alone.
        var builder = Program.DirectoryBuilder(Path.Combine(_dir, "cli"), Options.Parse(["directory", "--signed-volunteers", "--rate-limit", "3,10,100", "--no-admin"]), new StringWriter(), _ => { });
        Assert.Equal((3, 10, 100), builder.RateLimit!.Limits);
        var cli = builder.WithoutEasyTier(new IPEndPoint(IPAddress.Loopback, 0)).Build();
        _cleanup.Add(cli);
        await cli.StartAsync();
        Assert.True(cli.Volunteers!.RequireOperator);
    }
}
