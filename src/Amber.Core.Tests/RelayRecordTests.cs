using System.Net;
using System.Net.Sockets;
using Amber.Core.Localization;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Public;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Tests;

/// <summary>
/// Relay share codes for relays that go by an IP: the code carries a locator (no IP, no passphrase) and the relay list
/// lives in a signed record on the public network's directories.
/// </summary>
public sealed class RelayRecordTests : IAsyncLifetime
{
    private const string Secret = "test-network-secret";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jay-relay-record-test-" + Guid.NewGuid().ToString("N"));
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

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _dirKey = X25519KeyPair.Generate();
        _directory = new RoomDirectoryServer(IPAddress.Loopback, 0) { Identity = _dirKey, NetworkSecret = Secret };
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _directory.DisposeAsync();
        _dirKey.Dispose();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private RoomDirectoryClient Directories() =>
        new(new Loopback(_directory.Port, new PublicNetworkProfile("test", "Test", "jay-net-test", Secret, ["tcp://127.0.0.1:1"]) { DirectoryKeys = [_dirKey.PublicKey] }),
            ["10.235.0.1"]);

    private RelayRecord.Identity Identity() => RelayRecord.LoadOrCreate(Path.Combine(_root, "relay-record.key"));

    [Fact]
    public async Task CodeNamesNoAddressAndResolvesThroughTheRecord()
    {
        var identity = Identity();
        var code = RelayRecord.Encode(identity.Locator("jay"));
        Assert.StartsWith(RelayRecord.Prefix, code);
        Assert.DoesNotMatch(@"\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}", code);
        Assert.Equal(PastedCodeKind.NodeShare, PastedCode.Detect(code));

        var locator = RelayRecord.TryParse("中转：" + code + " 收好")!;
        Assert.Equal("jay", locator.NetworkId);
        Assert.Equal(identity.Id, locator.Id);

        var directories = Directories();
        var missing = await Assert.ThrowsAsync<IOException>(() => RelayRecord.ResolveAsync(directories, locator, default));
        Assert.Equal(L.T("core.mp.nodes.record_not_found"), missing.Message);

        string[] urls = ["tcp://203.0.113.5:11010", "udp://203.0.113.5:11010"];
        await directories.PutRecordAsync(RelayRecord.Make(identity, "Home", urls, DateTimeOffset.UtcNow), default);
        var (name, resolved) = await RelayRecord.ResolveAsync(directories, locator, default);
        Assert.Equal("Home", name);
        Assert.Equal(urls, resolved);
    }

    /// <summary>A record put under the same lookup by another key (someone who saw the code) is never used.</summary>
    [Fact]
    public async Task OnlyTheSharersKeyIsAccepted()
    {
        var identity = Identity();
        var locator = RelayRecord.TryParse(RelayRecord.Encode(identity.Locator("jay")))!;
        var squatter = new RelayRecord.Identity(new RecordSigner(), identity.Id);
        var directories = Directories();
        await directories.PutRecordAsync(RelayRecord.Make(squatter, null, ["tcp://198.51.100.9:11010"], DateTimeOffset.UtcNow), default);
        var e = await Assert.ThrowsAsync<IOException>(() => RelayRecord.ResolveAsync(directories, locator, default));
        Assert.Equal(L.T("core.mp.nodes.record_not_found"), e.Message);
    }

    [Fact]
    public void IdentityPersistsAcrossLoads()
    {
        var first = Identity();
        var again = Identity();
        Assert.Equal(first.Id, again.Id);
        Assert.Equal(first.Signer.PublicKey, again.Signer.PublicKey);
    }

    [Fact]
    public void CodesAreTamperChecked()
    {
        var code = RelayRecord.Encode(Identity().Locator("jay"));
        var flipped = code[..^2] + (code[^2] == 'Z' ? 'Y' : 'Z') + code[^1];
        Assert.Null(RelayRecord.TryParse(flipped));
        Assert.Null(RelayRecord.TryParse("JAYNR1-"));
        Assert.Null(RelayRecord.TryParse("no code here"));
    }

    [Fact]
    public void RecordNeedsAtLeastOneValidRelay() =>
        Assert.Throws<ArgumentException>(() => RelayRecord.Make(Identity(), null, ["not a url"], DateTimeOffset.UtcNow));

    [Fact]
    public void DiagnosticsRedactTheCode()
    {
        var code = RelayRecord.Encode(Identity().Locator("jay"));
        var redacted = NetDiagnostics.Redact("shared " + code, [], false);
        Assert.DoesNotContain(code, redacted);
    }
}
