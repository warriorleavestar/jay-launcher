using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Amber.Core.Localization;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Gate;
using Amber.Core.Multiplayer.Public;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Tests;

/// <summary>
/// A Jay server with no domain and records on: the invite carries a locator (no IP, no passphrase), the address lives in
/// a signed record on the public network's directories, and a player resolves it before enrolling.
/// </summary>
public sealed class GateRecordTests : IAsyncLifetime
{
    private const string Secret = "test-network-secret";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jay-gate-record-test-" + Guid.NewGuid().ToString("N"));
    private GateStore _store = null!;
    private GateServer _gate = null!;
    private X25519KeyPair _gateKey = null!;
    private TcpListener _backend = null!;
    private X25519KeyPair _dirKey = null!;
    private RoomDirectoryServer _directory = null!;

    private sealed class Loopback(int port, PublicNetworkProfile profile) : IPublicNetwork
    {
        public async Task<Stream> ConnectAsync(string ip, int p, CancellationToken ct)
        {
            var c = new TcpClient();
            await c.ConnectAsync(IPAddress.Loopback, port, ct);
            return c.GetStream();
        }

        public Task<string?> MyIpAsync(CancellationToken ct) => Task.FromResult<string?>("10.235.0.7");
        public PublicNetworkProfile? Profile => profile;
    }

    public async Task InitializeAsync()
    {
        _backend = new TcpListener(IPAddress.Loopback, 0);
        _backend.Start();
        _store = GateStore.Initialize(Path.Combine(_root, "gate"), "127.0.0.1", 1, 0);
        var config = _store.LoadConfig();
        config.ListenAddress = "127.0.0.1";
        config.BackendPort = ((IPEndPoint)_backend.LocalEndpoint).Port;
        _gateKey = _store.LoadServerKey();
        _gate = new GateServer(_store, config, _gateKey, _ => { });
        _gate.Start();
        config.PublicPort = _gate.Port;
        _store.SaveConfig(config);
        _dirKey = X25519KeyPair.Generate();
        _directory = new RoomDirectoryServer(IPAddress.Loopback, 0) { Identity = _dirKey, NetworkSecret = Secret };
        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _gate.DisposeAsync();
        await _directory.DisposeAsync();
        _backend.Stop();
        _gateKey.Dispose();
        _dirKey.Dispose();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private RoomDirectoryClient Directories() =>
        new(new Loopback(_directory.Port, new PublicNetworkProfile("test", "Test", "jay-net-test", Secret, ["tcp://127.0.0.1:1"]) { DirectoryKeys = [_dirKey.PublicKey] }),
            ["10.235.0.1"]);

    private GateInvite Invite() => _store.CreateInvite("Steve", TimeSpan.FromHours(1), 1, _gateKey.PublicKey);

    [Fact]
    public async Task InviteNamesNoAddressAndResolvesThroughTheRecord()
    {
        // Records off: an IP-only server needs a sealed invite.
        Assert.True(Invite().NeedsSeal);

        var locator = _store.EnableRecords("jay");
        Assert.Equal("jay", locator.NetworkId);
        var invite = Invite();
        Assert.NotNull(invite.Locator);
        Assert.False(invite.NeedsSeal);
        var (code, passphrase) = invite.ToCode();
        Assert.Null(passphrase);
        Assert.StartsWith(GateInvite.Prefix, code);
        Assert.DoesNotMatch(@"\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}", code);
        Assert.DoesNotContain("127.0.0.1", invite.ToString());

        var parsed = GateInvite.TryParse("来玩：" + code, out var error)!;
        Assert.Null(error);
        Assert.Equal(locator.Id, parsed.Locator!.Id);
        Assert.Equal(locator.SignerHash, parsed.Locator.SignerHash);
        Assert.Equal(invite.Token, parsed.Token);
        Assert.NotNull(GateInvite.TryParse(code + " thanks, see you at 8", out _));
        Assert.NotNull(GateInvite.TryParse(code + "\nJoin tonight", out _));

        // Not published yet: nothing to find.
        var directories = Directories();
        var missing = await Assert.ThrowsAsync<IOException>(() => GateRecord.ResolveAsync(directories, parsed, default));
        Assert.Equal(L.T("core.gate.record.not_found"), missing.Message);
        // An unresolved invite can't enroll.
        using var identity = MemberIdentity.LoadOrCreate(Path.Combine(_root, "client"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => GateClient.EnrollAsync(parsed, identity));

        await GateRecordPublisher.PublishOnceAsync(directories, _store, _store.LoadConfig(), DateTimeOffset.UtcNow, default);
        var resolved = await GateRecord.ResolveAsync(directories, parsed, default);
        Assert.Null(resolved.Locator);
        Assert.Equal("127.0.0.1", resolved.Host);
        Assert.Equal(_gate.Port, resolved.Port);

        var membership = await GateClient.EnrollAsync(resolved, identity);
        Assert.Equal("Steve", membership.Username);
        Assert.Equal(_gateKey.PublicKey, membership.ServerPublicKey);
    }

    /// <summary>A record put by another key under the same lookup (someone who saw an invite) is never used.</summary>
    [Fact]
    public async Task OnlyTheServersRecordKeyIsAccepted()
    {
        var locator = _store.EnableRecords("jay");
        var parsed = GateInvite.TryParse(Invite().Encode(), out _)!;
        var directories = Directories();
        var squatter = new RecordSigner();
        await directories.PutRecordAsync(GateRecord.Make(squatter, locator.Id, [new GateRecordEntry("203.0.113.66", 25565, null)], DateTimeOffset.UtcNow), default);
        var e = await Assert.ThrowsAsync<IOException>(() => GateRecord.ResolveAsync(directories, parsed, default));
        Assert.Equal(L.T("core.gate.record.not_found"), e.Message);
    }

    private sealed class FakeJoined(RoomDirectoryClient directories) : GateRecordPublisher.IJoined
    {
        public RoomDirectoryClient Directories => directories;
        public bool HasExited => false;
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    private static async Task Until(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            await Task.Delay(50);
        }
    }

    private async Task<GateInvite?> TryResolve(GateInvite invite)
    {
        try { return await GateRecord.ResolveAsync(Directories(), invite, default); }
        catch (IOException) { return null; }
    }

    /// <summary>A record publish that fails in an unexpected way is logged and tried again, not left to end publishing.</summary>
    [Fact]
    public async Task RecordPublishingRetriesAfterAnyFailure()
    {
        _store.EnableRecords("jay");
        var attempts = 0;
        var log = new List<string>();
        await using (GateRecordPublisher.Start(_store, _store.LoadConfig(), (_, _) =>
        {
            Interlocked.Increment(ref attempts);
            throw new NotSupportedException("unexpected");
        }, line => { lock (log) log.Add(line); }, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10)))
        {
            await Until(() => Task.FromResult(Volatile.Read(ref attempts) >= 3));
        }
        lock (log) Assert.True(log.Count(l => l.Contains("unexpected", StringComparison.Ordinal)) >= 3);
    }

    /// <summary>A running gate follows records on/off and republishes when its entries change, without a restart.</summary>
    [Fact]
    public async Task RunningPublisherFollowsTheRecordsSetting()
    {
        var joins = new List<FakeJoined>();
        var log = new List<string>();
        await using (var publisher = GateRecordPublisher.Start(_store, _store.LoadConfig(), (_, _) =>
        {
            var j = new FakeJoined(Directories());
            lock (joins) joins.Add(j);
            return Task.FromResult<GateRecordPublisher.IJoined>(j);
        }, line => { lock (log) log.Add(line); }, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50)))
        {
            await Task.Delay(300);
            lock (joins) Assert.Empty(joins);

            // Turned on while running: published without a restart.
            _store.EnableRecords("jay");
            var invite = GateInvite.TryParse(Invite().Encode(), out _)!;
            await Until(async () => await TryResolve(invite) is not null);
            Assert.Null((await TryResolve(invite))!.Rotation);

            // Entries changed: put again.
            _store.EnableRotation(GateRotation.Default, TimeSpan.FromHours(1));
            await Until(async () => (await TryResolve(invite))?.Rotation is not null);

            // Turned off: the network is left and nothing more is put.
            _store.DisableRecords();
            await Until(() => Task.FromResult(joins.All(j => j.Disposed)));
            int published;
            lock (log) published = log.Count;
            _store.DisableRotation();
            await Task.Delay(300);
            lock (log) Assert.Equal(published, log.Count);
            lock (joins) Assert.Single(joins);
        }
    }

    [Fact]
    public void RecordsTurnOffAndBackOnWithTheSameLocator()
    {
        var first = _store.EnableRecords("jay");
        _store.DisableRecords();
        Assert.Null(_store.Locator());
        Assert.True(Invite().NeedsSeal);
        var again = _store.EnableRecords("jay");
        Assert.Equal(first.Id, again.Id);
        Assert.Equal(first.SignerHash, again.SignerHash);
    }

    [Fact]
    public void EntriesRoundTripWithRotation()
    {
        var rotation = new GateEntryRotation(new GateRotation(600, 20000, 45000), RandomNumberGenerator.GetBytes(GateRotation.SeedLength));
        GateRecordEntry[] entries = [new("203.0.113.5", 47670, rotation), new("relay.example.com", 47671, null)];
        var decoded = GateRecord.Decode(GateRecord.Encode(entries))!;
        Assert.Equal(2, decoded.Count);
        Assert.Equal("203.0.113.5", decoded[0].Host);
        Assert.Equal(rotation.Rotation, decoded[0].Rotation!.Rotation);
        Assert.Equal(rotation.Seed, decoded[0].Rotation!.Seed);
        Assert.Null(decoded[1].Rotation);
        Assert.Null(GateRecord.Decode([1, 0]));
        Assert.Null(GateRecord.Decode([1, 1, 200, 1]));
        Assert.Null(GateRecord.Decode([.. GateRecord.Encode(entries), 0]));
    }

    [Fact]
    public void LocatorInvitesAreTamperChecked()
    {
        _store.EnableRecords("jay");
        var code = Invite().Encode();
        var flipped = code[..^2] + (code[^2] == 'Z' ? 'Y' : 'Z') + code[^1];
        Assert.Null(GateInvite.TryParse(flipped, out var error));
        Assert.NotNull(error);
    }
}
