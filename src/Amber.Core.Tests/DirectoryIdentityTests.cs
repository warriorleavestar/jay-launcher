using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Public;
using Amber.Core.Multiplayer.Transport;
using Amber.NetworkServer;

namespace Amber.Core.Tests;

/// <summary>Directory identity (docs/security/directory-identity.md): a network that pins directory keys only talks to the real directory.</summary>
public class DirectoryIdentityTests
{
    private const string Secret = "test-network-secret";

    private sealed class Loopback(int port, PublicNetworkProfile? profile = null) : IPublicNetwork
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

    /// <summary>Announces a public room: a directory that got the request would hold it afterwards.</summary>
    private static Task Announce(RoomDirectoryClient client)
    {
        using var host = X25519KeyPair.Generate();
        var listing = new RoomListing
        {
            Id = "", Name = "Survival", Host = "Alex", Mode = RoomVisibility.Public, HostKey = host.PublicKey, Ip = "10.235.0.7",
            Game = "1.21.1", Players = 2, Invite = new InviteBook().CreateStanding(false, TunnelSettings.Default).Invite.ToString(),
        };
        return client.AnnounceAsync(new RoomSigner().Sign(listing, DateTimeOffset.UtcNow), true, [], default);
    }

    private static PublicNetworkProfile Pinned(params byte[][] keys) =>
        new("test", "Test", "jay-net-test", Secret, ["tcp://127.0.0.1:1"]) { DirectoryKeys = keys };

    private static RoomDirectoryServer Server(X25519KeyPair? identity, bool legacy = true, string secret = Secret) =>
        new(IPAddress.Loopback, 0) { Identity = identity, NetworkSecret = secret, AcceptLegacyClients = legacy };

    [Fact]
    public async Task PinnedClientTalksToTheRealDirectory()
    {
        using var identity = X25519KeyPair.Generate();
        await using var server = Server(identity);
        var client = new RoomDirectoryClient(new Loopback(server.Port, Pinned(identity.PublicKey)));
        Assert.Empty(await client.ListAsync(default));
        await Announce(client);
        Assert.Single(await client.ListAsync(default));
    }

    [Fact]
    public async Task AnyOfSeveralPinnedKeysIsAccepted()
    {
        using var identity = X25519KeyPair.Generate();
        using var other = X25519KeyPair.Generate();
        await using var server = Server(identity);
        var client = new RoomDirectoryClient(new Loopback(server.Port, Pinned(other.PublicKey, identity.PublicKey)));
        Assert.Empty(await client.ListAsync(default));
    }

    [Fact]
    public async Task ImpostorWithAnotherKeyGetsNoRequest()
    {
        using var real = X25519KeyPair.Generate();
        using var impostor = X25519KeyPair.Generate();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accepted = Task.Run(async () =>
        {
            using var c = await listener.AcceptTcpClientAsync();
            var s = c.GetStream();
            var first = new byte[1];
            Assert.True(await SecureStream.ReadExactAsync(s, first, default));
            Assert.NotEqual(DirectoryChannel.LegacyFirstByte, first[0]);
            return await DirectoryChannel.AcceptAsync(s, first[0], impostor, Secret, default);
        });
        var client = new RoomDirectoryClient(new Loopback(port, Pinned(real.PublicKey)));
        var e = await Assert.ThrowsAsync<DirectoryImpostorException>(() => client.ListAsync(default));
        Assert.IsAssignableFrom<IOException>(e);
        // The impostor saw the handshake but never a request.
        Assert.Null(await accepted.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task ImpostorRoomDirectoryServerIsRefusedAndHandlesNothing()
    {
        using var real = X25519KeyPair.Generate();
        using var impostor = X25519KeyPair.Generate();
        await using var server = Server(impostor);
        var client = new RoomDirectoryClient(new Loopback(server.Port, Pinned(real.PublicKey)));
        await Assert.ThrowsAsync<DirectoryImpostorException>(() => Announce(client));
        Assert.Equal(0, server.Count);
    }

    [Fact]
    public async Task PinnedClientNeverFallsBackToALegacyDirectory()
    {
        using var real = X25519KeyPair.Generate();
        await using var server = Server(identity: null);
        var client = new RoomDirectoryClient(new Loopback(server.Port, Pinned(real.PublicKey)));
        await Assert.ThrowsAnyAsync<IOException>(() => Announce(client));
        Assert.Equal(0, server.Count);
        // The same server answers a legacy client, so it was the pinning that stopped the request.
        await Announce(new RoomDirectoryClient(new Loopback(server.Port)));
        Assert.Equal(1, server.Count);
    }

    [Fact]
    public async Task LegacyClientsStillWorkUnlessTurnedOff()
    {
        using var identity = X25519KeyPair.Generate();
        await using (var server = Server(identity))
            Assert.Empty(await new RoomDirectoryClient(new Loopback(server.Port)).ListAsync(default));
        await using (var strict = Server(identity, legacy: false))
        {
            await Assert.ThrowsAnyAsync<IOException>(() => new RoomDirectoryClient(new Loopback(strict.Port)).ListAsync(default));
            Assert.Empty(await new RoomDirectoryClient(new Loopback(strict.Port, Pinned(identity.PublicKey))).ListAsync(default));
        }
    }

    [Fact]
    public async Task OtherNetworksSecretFailsTheHandshake()
    {
        using var identity = X25519KeyPair.Generate();
        await using var server = Server(identity, secret: "another-network");
        var client = new RoomDirectoryClient(new Loopback(server.Port, Pinned(identity.PublicKey)));
        var e = await Assert.ThrowsAnyAsync<Exception>(() => client.ListAsync(default));
        Assert.True(e is IOException or InvalidDataException, e.GetType().Name);
        Assert.IsNotType<DirectoryImpostorException>(e);
    }

    [Fact]
    public async Task ChannelCarriesRequestAndReply()
    {
        using var identity = X25519KeyPair.Generate();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverSide = Task.Run(async () =>
        {
            using var c = await listener.AcceptTcpClientAsync();
            var s = c.GetStream();
            var first = new byte[1];
            await SecureStream.ReadExactAsync(s, first, default);
            var accepted = await DirectoryChannel.AcceptAsync(s, first[0], identity, Secret, default) ?? throw new InvalidOperationException();
            await accepted.Reply(new JsonObject { ["echo"] = accepted.Request["op"]!.GetValue<string>() }, default);
        });
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port);
        var reply = await DirectoryChannel.CallAsync(tcp.GetStream(), [identity.PublicKey], Secret, new JsonObject { ["op"] = "hello" }, default);
        Assert.Equal("hello", reply["echo"]!.GetValue<string>());
        await serverSide;
    }

    [Fact]
    public void FirstNoiseFrameNeverLooksLikeLegacyJson()
    {
        // The directory tells the two apart by the first byte: '{' for JSON, a length's high byte for Noise (messages are well under 0x7B00 bytes).
        Assert.True(RoomDirectoryProtocol.MaxLine + 256 < DirectoryChannel.LegacyFirstByte << 8);
    }

    [Fact]
    public void DirKeysRoundTripThroughASignedNetworkString()
    {
        using var key = X25519KeyPair.Generate();
        var signer = NetworkSigner.Generate(out _);
        var info = new NetworkInfo("mynet", "My net", "jay-net-mynet", "s", ["tcp://203.0.113.5:11010"], DirectoryKeys: [key.PublicKey]);
        var text = NetworkString.Create(info, signer);
        var parsed = NetworkString.Parse(text, []);
        Assert.Equal(key.PublicKey, Assert.Single(parsed.Info.DirectoryKeys!));
        Assert.Equal(key.PublicKey, Assert.Single(parsed.ToProfile().DirectoryKeys));

        // No dirKeys: nothing pinned (legacy protocol).
        var plain = NetworkString.Parse(NetworkString.Create(info with { DirectoryKeys = null }, signer), []);
        Assert.Null(plain.Info.DirectoryKeys);
        Assert.Empty(plain.ToProfile().DirectoryKeys);
        Assert.DoesNotContain("dirKeys", plain.Info.ToJson().ToJsonString());
    }

    [Fact]
    public void DirKeysAreValidated()
    {
        var info = new NetworkInfo("mynet", "My net", "jay-net-mynet", "s", ["tcp://203.0.113.5:11010"]);
        Assert.NotNull(NetworkString.Validate(info with { DirectoryKeys = [new byte[31]] }));
        Assert.NotNull(NetworkString.Validate(info with { DirectoryKeys = [] }));
        Assert.NotNull(NetworkString.Validate(info with { DirectoryKeys = [.. Enumerable.Range(0, 5).Select(_ => new byte[32])] }));
        Assert.Null(NetworkString.Validate(info with { DirectoryKeys = [.. Enumerable.Range(0, 4).Select(_ => new byte[32])] }));
    }

    [Fact]
    public void BuiltInNetworkPinsNothingYet() => Assert.Empty(PublicNetworkProfile.Jay().DirectoryKeys);

    [Fact]
    public void IdentityFileIsMadeOnceAndPrivate()
    {
        var dir = Directory.CreateTempSubdirectory("jay-dirkey-");
        var path = Path.Combine(dir.FullName, "directory.key");
        using var a = DirectoryChannel.LoadOrCreateIdentity(path);
        using var b = DirectoryChannel.LoadOrCreateIdentity(path);
        Assert.Equal(a.PublicKey, b.PublicKey);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        dir.Delete(true);
    }

    [Fact]
    public void AttachDirectoryKeyBumpsSeqOnce()
    {
        var dir = Directory.CreateTempSubdirectory("jay-dirkey-");
        var path = Path.Combine(dir.FullName, "mynet.json");
        var info = new NetworkInfo("mynet", "My net", "jay-net-mynet", "s", ["tcp://203.0.113.5:11010"], Seq: 3);
        File.WriteAllText(path, info.ToJson().ToJsonString());
        using var key = X25519KeyPair.Generate();
        var (first, added) = NetworkScaffold.AttachDirectoryKey(path, key.PublicKey);
        Assert.True(added);
        Assert.Equal(4, first.Seq);
        var (second, again) = NetworkScaffold.AttachDirectoryKey(path, key.PublicKey);
        Assert.False(again);
        Assert.Equal(4, second.Seq);
        Assert.Single(second.DirectoryKeys!);
        dir.Delete(true);
    }
}
