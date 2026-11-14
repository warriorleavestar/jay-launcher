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
        // Pinning nothing still needs the network's secret: the channel's psk comes from it.
        public PublicNetworkProfile? Profile => profile ?? Pinned();
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

    private static RoomDirectoryServer Server(X25519KeyPair? identity, string secret = Secret) =>
        new(IPAddress.Loopback, 0) { Identity = identity, NetworkSecret = secret };

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
    public async Task PinnedClientRefusesADirectoryWithAnotherKey()
    {
        using var real = X25519KeyPair.Generate();
        await using var server = Server(identity: null);
        var client = new RoomDirectoryClient(new Loopback(server.Port, Pinned(real.PublicKey)));
        await Assert.ThrowsAnyAsync<IOException>(() => Announce(client));
        Assert.Equal(0, server.Count);
        // The same server answers a client that pins nothing (encrypted, unauthenticated), so it was the pinning that stopped the request.
        await Announce(new RoomDirectoryClient(new Loopback(server.Port)));
        Assert.Equal(1, server.Count);
    }

    [Fact]
    public async Task PlaintextJsonRequestsGetNoReply()
    {
        using var identity = X25519KeyPair.Generate();
        await using var server = Server(identity);
        using var raw = new TcpClient();
        await raw.ConnectAsync(IPAddress.Loopback, server.Port);
        var stream = raw.GetStream();
        await stream.WriteAsync("{\"op\":\"list\"}\n"u8.ToArray());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var buffer = new byte[64];
        // The server drops the connection without answering. Closing with our request still unread can arrive as a reset, not a clean end of stream.
        int read;
        try { read = await stream.ReadAsync(buffer, timeout.Token); }
        catch (IOException) { read = 0; }
        Assert.Equal(0, read);
        // A client that pins nothing still gets through, over the encrypted channel.
        Assert.Empty(await new RoomDirectoryClient(new Loopback(server.Port)).ListAsync(default));
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

    [Theory]
    [InlineData("""{"ok":"yes"}""")]
    [InlineData("""{"ok":1,"error":7}""")]
    [InlineData("""{"ok":false,"error":["rate"],"reason":{},"min":3}""")]
    [InlineData("""{"ok":true,"ip":5,"key":false,"id":[],"room":"x","rooms":7,"nodes":[1,{},"tcp://203.0.113.9:11010"],"listed":"no","reachable":"yes","next":"2","items":[{"box":1,"blob":2}],"blob":9}""")]
    public async Task ARogueDirectorysWronglyTypedReplyNeverEscapesAsAnUnexpectedException(string replyJson)
    {
        // On a network without pinned directory keys any member can answer as a backup directory. Whatever it sends,
        // the client's calls end in a result or in what its callers handle: the IOException family, or the
        // InvalidDataException the channel uses for a reply it can't read.
        using var identity = X25519KeyPair.Generate();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var stop = new CancellationTokenSource();
        // Requests that got through the handshake and were answered with the rogue reply: each call must reach one,
        // or a handshake failure would let the test pass without the client ever reading the reply.
        var answered = new int[1];
        var rogue = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                TcpClient c;
                try { c = await listener.AcceptTcpClientAsync(stop.Token); }
                catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException) { return; }
                _ = Task.Run(async () =>
                {
                    using (c)
                    {
                        try
                        {
                            var s = c.GetStream();
                            var first = new byte[1];
                            if (!await SecureStream.ReadExactAsync(s, first, stop.Token)) return;
                            if (await DirectoryChannel.AcceptAsync(s, first[0], identity, Secret, stop.Token) is { } accepted)
                            {
                                Interlocked.Increment(ref answered[0]);
                                await accepted.Reply((JsonObject)JsonNode.Parse(replyJson)!, stop.Token);
                            }
                        }
                        catch (Exception e) when (e is IOException or OperationCanceledException or InvalidDataException) { }
                    }
                });
            }
        });
        var client = new RoomDirectoryClient(new Loopback(port, Pinned(identity.PublicKey)));
        var calls = new (string Name, Func<Task> Run)[]
        {
            ("list", () => client.ListAsync(default)),
            ("lookup", () => client.LookupAsync(new byte[32], default)),
            ("find", () => client.FindAsync("JAYF1-TEST", default)),
            ("nodes", () => client.NodesAsync(default)),
            ("probe", () => client.ProbeAsync("tcp://relay.example.org:11010", default)),
            ("volunteer", () => client.VolunteerAsync("tcp://relay.example.org:11010", null, default)),
            ("fetch", () => client.FetchMailAsync(["box"], default)),
            ("take", () => client.TakeRendezvousAsync(new byte[Rendezvous.IdLength], default)),
            ("put", () => client.PutRendezvousAsync(new byte[Rendezvous.IdLength], new byte[8], TimeSpan.FromMinutes(1), default)),
            ("get", () => client.GetRecordAsync(new byte[SignedRecord.LookupLength], _ => true, default)),
        };
        foreach (var (name, run) in calls)
        {
            var before = Volatile.Read(ref answered[0]);
            try { await run(); }
            catch (Exception e) when (e is IOException or InvalidDataException) { }
            catch (Exception e) { Assert.Fail($"{name}: {e.GetType().Name}: {e.Message}"); }
            Assert.True(Volatile.Read(ref answered[0]) > before, $"{name}: the rogue reply was never sent");
        }
        stop.Cancel();
        listener.Stop();
        await rogue;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnlyASnapshotCallTakesASnapshotReply(bool askedForSnapshot)
    {
        // A directory answering an ordinary request with a length frame (asking the reader to set aside a big buffer)
        // gets a bad reply; a snapshot call reads it, chunk by chunk.
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
            try { await accepted.Snapshot(System.Text.Encoding.UTF8.GetBytes("{\"ok\":true}"), 1024, TimeSpan.FromSeconds(5), default); }
            catch (IOException) { }
        });
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port);
        var trust = new DirectoryTrust { Network = "", Pinned = [identity.PublicKey] };
        var lengths = new List<int>();
        var call = DirectoryChannel.CallAsync(tcp.GetStream(), trust, "", Secret, new JsonObject { ["op"] = "hello" }, default,
            askedForSnapshot ? lengths.Add : null);
        if (askedForSnapshot)
        {
            Assert.True((await call)["ok"]!.GetValue<bool>());
            Assert.Equal(1024, Assert.Single(lengths));
        }
        else await Assert.ThrowsAsync<InvalidDataException>(() => call);
        tcp.Dispose();
        await serverSide;
    }

    [Fact]
    public async Task ClientsFirstMessageHasTheExpectedLength()
    {
        // The directory drops any first frame that isn't exactly e + encrypted e1 + encrypted empty payload before doing any
        // DH or ML-KEM work; make sure a real client's is.
        using var identity = X25519KeyPair.Generate();
        var stream = new MemoryStream();
        await Assert.ThrowsAnyAsync<Exception>(() =>
            DirectoryChannel.CallAsync(stream, [identity.PublicKey], Secret, new JsonObject { ["op"] = "list" }, default));
        var written = stream.ToArray();
        const int expected = 32 + 1184 + 16 + 16;
        Assert.Equal(2 + expected, written.Length);
        Assert.Equal(expected, (written[0] << 8) | written[1]);
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
        var signer = TrustSigner.Generate(out _);
        var info = new NetworkInfo("mynet", "My net", "jay-net-mynet", "s", ["tcp://node.example.net:11010"], DirectoryKeys: [key.PublicKey]);
        var text = NetworkString.Create(info, signer);
        var parsed = NetworkString.Parse(text, []);
        Assert.Equal(key.PublicKey, Assert.Single(parsed.Info.DirectoryKeys!));
        Assert.Equal(key.PublicKey, Assert.Single(parsed.ToProfile().DirectoryKeys));

        // No dirKeys: nothing pinned (trust on first use).
        var plain = NetworkString.Parse(NetworkString.Create(info with { DirectoryKeys = null }, signer), []);
        Assert.Null(plain.Info.DirectoryKeys);
        Assert.Empty(plain.ToProfile().DirectoryKeys);
        Assert.DoesNotContain("dirKeys", plain.Info.ToJson().ToJsonString());
    }

    [Fact]
    public void DirKeysAreValidated()
    {
        var info = new NetworkInfo("mynet", "My net", "jay-net-mynet", "s", ["tcp://node.example.net:11010"]);
        Assert.NotNull(NetworkString.Validate(info with { DirectoryKeys = [new byte[31]] }));
        Assert.NotNull(NetworkString.Validate(info with { DirectoryKeys = [] }));
        // Eight: four nodes, each with a spare slot for rotation.
        Assert.NotNull(NetworkString.Validate(info with { DirectoryKeys = [.. Enumerable.Range(0, 9).Select(_ => new byte[32])] }));
        Assert.Null(NetworkString.Validate(info with { DirectoryKeys = [.. Enumerable.Range(0, 8).Select(_ => new byte[32])] }));
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
        var info = new NetworkInfo("mynet", "My net", "jay-net-mynet", "s", ["tcp://node.example.net:11010"], Seq: 3);
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
