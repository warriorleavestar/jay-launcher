using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Tests;

/// <summary>
/// Stage 2 of the multi-path split: split settings in the room credentials and the rekey message, the host's per-leg
/// listeners and the member's split connector (Off / Auto / Strict), all over loopback.
/// </summary>
public sealed class SplitRoomTests
{
    private static readonly TunnelSettings Wrapped = new(TransportMode.High, false);

    // ---- settings, credentials and rekey ----

    [Fact]
    public void SettingsRoundTripAndHideTheirValues()
    {
        var split = SplitSettings.Create(SplitPolicy.Auto, SplitPolicy.Strict, 3);
        Assert.True(split.Enabled);
        Assert.Equal(2, split.Nets.Count);
        Assert.NotEqual(split.Nets[0].Network, split.Nets[1].Network);
        var back = SplitSettings.FromJson(JsonNode.Parse(split.ToJson().ToJsonString()));
        Assert.Equal(SplitPolicy.Auto, back.Game);
        Assert.Equal(SplitPolicy.Strict, back.Chat);
        Assert.Equal(3, back.Paths);
        Assert.Equal(split.Nets, back.Nets);
        Assert.Equal("SplitSettings", split.ToString());
        Assert.Equal("SplitNet", split.Nets[0].ToString());
        Assert.Equal(SplitPolicy.Off, split.For(TunnelService.Remote));
        Assert.Equal(SplitPolicy.Off, split.For(TunnelService.Socks));
    }

    [Theory]
    [InlineData("""{"game":"auto","chat":"auto","paths":4,"nets":[]}""")]
    [InlineData("""{"game":"sometimes","chat":"auto","paths":2,"nets":[{"net":"jay-0011223344556677","secret":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="}]}""")]
    [InlineData("""{"game":"auto","chat":"auto","paths":3,"nets":[{"net":"jay-0011223344556677","secret":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="}]}""")]
    [InlineData("""{"game":"auto","chat":"auto","paths":3,"nets":[{"net":"jay-0011223344556677","secret":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="},{"net":"jay-0011223344556677","secret":"BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB="}]}""")]
    [InlineData("""{"game":"auto","chat":"auto","paths":2,"nets":[{"net":"10.0.0.1:11010","secret":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="}]}""")]
    [InlineData("""{"game":"off","chat":"off","paths":2,"nets":[{"net":"jay-0011223344556677","secret":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="}]}""")]
    [InlineData("""[]""")]
    public void SettingsThisVersionDoesNotUnderstandMeanNoSplit(string json)
    {
        var split = SplitSettings.FromJson(JsonNode.Parse(json));
        Assert.Same(SplitSettings.None, split);
        Assert.False(split.Enabled);
    }

    [Fact]
    public void CredentialsCarryTheSplitToTheMemberAndNoAddress()
    {
        var (credentials, _, _, guest) = NewRoom(SplitSettings.Create(SplitPolicy.Auto, SplitPolicy.Auto));
        var text = credentials.ToJson().ToJsonString();
        Assert.Contains("\"split\"", text);
        Assert.DoesNotMatch(@"\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}", text);
        var parsed = RoomCredentials.FromJson(JsonNode.Parse(text)!);
        Assert.Equal(credentials.Split.Nets, parsed.Split.Nets);
        Assert.Equal(SplitPolicy.Auto, parsed.ToRoom().Split.Game);
        Assert.Equal(guest.Key.Id, parsed.Member!.Id);
    }

    [Fact]
    public void OldCredentialsWithoutSplitStillParse()
    {
        var (credentials, _, _, _) = NewRoom(SplitSettings.None);
        var json = credentials.ToJson();
        Assert.Null(json["split"]);
        var parsed = RoomCredentials.FromJson(JsonNode.Parse(json.ToJsonString())!);
        Assert.Same(SplitSettings.None, parsed.Split);
    }

    [Fact]
    public void GeneralModeNeverSplits()
    {
        using var host = X25519KeyPair.Generate();
        var credentials = RoomCredentials.Create(host.PublicKey, 25565, TunnelSettings.Default) with { Split = SplitSettings.Create(SplitPolicy.Strict, SplitPolicy.Strict) };
        var table = new MemberTable();
        Assert.False(credentials.ToHostRoom(table, "Host").Split.Enabled);
        var entry = table.Issue(RandomNumberGenerator.GetBytes(32), "Alice");
        var member = credentials with { Member = new MemberKey(entry.Key.Id, entry.Key.CopyPsk()) };
        Assert.Null(member.ToJson()["split"]);
    }

    [Fact]
    public void AnExtraNetworkEqualToTheRoomNetworkIsIgnored()
    {
        var (credentials, _, _, _) = NewRoom(SplitSettings.Create(SplitPolicy.Auto, SplitPolicy.Auto));
        var json = credentials.ToJson();
        json["split"]!["nets"]![0]!["net"] = credentials.Network;
        Assert.Same(SplitSettings.None, RoomCredentials.FromJson(JsonNode.Parse(json.ToJsonString())!).Split);
    }

    [Fact]
    public void RekeyReplacesEveryExtraNetwork()
    {
        var (credentials, _, _, guest) = NewRoom(SplitSettings.Create(SplitPolicy.Auto, SplitPolicy.Strict, 3));
        var room = credentials.ToRoom();
        var next = credentials.Rotate();
        Assert.Equal(credentials.Split.Game, next.Split.Game);
        Assert.Equal(credentials.Split.Chat, next.Split.Chat);
        Assert.Equal(2, next.Split.Nets.Count);
        Assert.Empty(next.Split.Nets.Intersect(credentials.Split.Nets));

        // The rekey goes over the member's channel as JSON.
        var rekey = RoomRekey.FromJson(JsonNode.Parse(next.ToRekey().ToJson().ToJsonString())!);
        Assert.NotNull(rekey);
        Assert.Equal(next.Split.Nets, rekey.SplitNets);
        var moved = room.Rekeyed(rekey);
        Assert.Equal(next.Epoch, moved.Epoch);
        Assert.Equal(next.Split.Nets, moved.Split.Nets);
        Assert.Equal(SplitPolicy.Strict, moved.Split.Chat);
        foreach (var net in next.Split.Nets) Assert.Contains(net.Secret, moved.Secrets);
        Assert.Equal("RoomRekey", rekey.ToString());
    }

    [Fact]
    public void ARekeyWithoutTheExtraNetworksStopsSplitting()
    {
        var (credentials, _, _, _) = NewRoom(SplitSettings.Create(SplitPolicy.Auto, SplitPolicy.Auto));
        var room = credentials.ToRoom();
        var next = credentials.Rotate();
        var bare = RoomRekey.FromJson(JsonNode.Parse(new RoomRekey(next.Network, next.Secret, next.Epoch).ToJson().ToJsonString())!);
        Assert.NotNull(bare);
        Assert.Empty(bare.SplitNets);
        // The old extra networks are known to whoever was kicked: they must not stay in use.
        Assert.False(room.Rekeyed(bare).Split.Enabled);
    }

    // ---- host listeners and member connector over loopback ----

    [Theory]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(2, true)]
    public async Task GameAndChatRunSplitEndToEnd(int paths, bool disguise)
    {
        await using var rig = await Rig.StartAsync(SplitSettings.Create(SplitPolicy.Auto, SplitPolicy.Auto, paths), new TunnelSettings(TransportMode.High, disguise));
        foreach (var service in new[] { TunnelService.Game, TunnelService.Chat })
        {
            var connector = rig.Connector(service);
            await using var stream = await connector.OpenAsync(CancellationToken.None);
            Assert.IsType<SplitStream>(stream);
            var data = RandomNumberGenerator.GetBytes(40_000);
            await stream.WriteAsync(data);
            await stream.FlushAsync();
            var back = new byte[data.Length];
            Assert.True(await SecureStream.ReadExactAsync(stream, back, CancellationToken.None));
            Assert.Equal(data, back);
            Assert.Equal(1, connector.SplitConnections);
            Assert.Equal(0, connector.SinglePathConnections);
            Assert.Null(connector.LastReason);
        }
        Assert.Equal(0, rig.SingleCalls);
        await WaitUntilAsync(() => rig.Server.Accepted == 2);
        Assert.Equal(new[] { TunnelService.Game, TunnelService.Chat }, rig.Served);
    }

    [Fact]
    public async Task StrictWithoutPathsRefusesAndNeverUsesOnePath()
    {
        await using var rig = await Rig.StartAsync(SplitSettings.Create(SplitPolicy.Strict, SplitPolicy.Strict), Wrapped);
        var connector = rig.Connector(TunnelService.Game, legs: false);
        await Assert.ThrowsAsync<IOException>(() => connector.OpenAsync(CancellationToken.None));
        Assert.Equal(0, rig.SingleCalls);
        Assert.NotNull(connector.LastReason);
    }

    [Fact]
    public async Task AutoWithoutPathsUsesOnePathAndSaysWhy()
    {
        await using var rig = await Rig.StartAsync(SplitSettings.Create(SplitPolicy.Auto, SplitPolicy.Auto), Wrapped);
        var connector = rig.Connector(TunnelService.Game, legs: false);
        await using var stream = await connector.OpenAsync(CancellationToken.None);
        Assert.IsNotType<SplitStream>(stream);
        Assert.Equal(1, rig.SingleCalls);
        Assert.Equal(1, connector.SinglePathConnections);
        Assert.NotNull(connector.LastReason);
    }

    [Fact]
    public async Task OffNeverTriesTheLegs()
    {
        await using var rig = await Rig.StartAsync(SplitSettings.Create(SplitPolicy.Off, SplitPolicy.Auto), Wrapped);
        var connector = rig.Connector(TunnelService.Game);
        Assert.Equal(SplitPolicy.Off, connector.Policy);
        await using var stream = await connector.OpenAsync(CancellationToken.None);
        Assert.Equal(1, rig.SingleCalls);
        Assert.Equal(0, rig.LegOpens);
    }

    /// <summary>A dead path: Auto falls back to one path each time, and after a few failures in a row stops trying the legs.</summary>
    [Fact]
    public async Task AutoGivesUpAfterRepeatedFailures()
    {
        await using var rig = await Rig.StartAsync(SplitSettings.Create(SplitPolicy.Auto, SplitPolicy.Auto), Wrapped);
        var connector = rig.Connector(TunnelService.Game, deadLeg: 1);
        for (var i = 0; i < SplitConnector.MaxFailures + 2; i++)
        {
            await using var stream = await connector.OpenAsync(CancellationToken.None);
            Assert.IsNotType<SplitStream>(stream);
        }
        Assert.Equal(SplitConnector.MaxFailures + 2, rig.SingleCalls);
        // Only the first MaxFailures attempts touched the legs (two legs each).
        Assert.Equal(SplitConnector.MaxFailures * 2, rig.LegOpens);
        Assert.Equal(0, connector.SplitConnections);
    }

    [Fact]
    public async Task StrictFailsWhenALegFails()
    {
        await using var rig = await Rig.StartAsync(SplitSettings.Create(SplitPolicy.Strict, SplitPolicy.Strict), Wrapped);
        var connector = rig.Connector(TunnelService.Chat, deadLeg: 0);
        await Assert.ThrowsAsync<IOException>(() => connector.OpenAsync(CancellationToken.None));
        Assert.Equal(0, rig.SingleCalls);
    }

    [Fact]
    public async Task KickDropsAJoinedConnection()
    {
        await using var rig = await Rig.StartAsync(SplitSettings.Create(SplitPolicy.Auto, SplitPolicy.Auto), Wrapped);
        await using var stream = await rig.Connector(TunnelService.Game).OpenAsync(CancellationToken.None);
        await stream.WriteAsync(new byte[] { 1, 2, 3 });
        var back = new byte[3];
        Assert.True(await SecureStream.ReadExactAsync(stream, back, CancellationToken.None));
        await WaitUntilAsync(() => rig.Server.Accepted == 1);

        Assert.True(rig.Table.Remove(rig.GuestEntry));
        var split = Assert.IsType<SplitStream>(stream);
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            var buffer = new byte[16];
            while (await split.ReadAsync(buffer) > 0) { }
        });
        Assert.False(split.ClosedCleanly);
    }

    /// <summary>Remote control is never split: a leg asking for it is refused before pairing.</summary>
    [Fact]
    public async Task OnlyGameAndChatAreSplit()
    {
        await using var rig = await Rig.StartAsync(SplitSettings.Create(SplitPolicy.Auto, SplitPolicy.Auto), Wrapped);
        // The handshakes themselves succeed (Remote is a real service); the host drops each leg right after, without close records.
        await using var stream = await SplitLegs.ConnectAsync((i, ct) => TunnelClient.Raw("127.0.0.1", rig.Server.Ports[i], ct), 2, rig.Member.Epoch,
            (raw, leg, ct) => TunnelHandshake.ConnectAsync(raw, rig.Member, TransportMode.High, false, TunnelService.Remote, ct, leg));
        await Assert.ThrowsAnyAsync<Exception>(async () => await stream.ReadAsync(new byte[1]));
        Assert.False(stream.ClosedCleanly);
        await WaitUntilAsync(() => rig.Server.Rejected >= 2);
        Assert.Equal(0, rig.Server.Accepted);
        Assert.Equal(0, rig.Server.Registry.PendingCount);
    }

    /// <summary>A member connecting legs of an older epoch (credentials from before a rekey) fails every handshake.</summary>
    [Fact]
    public async Task LegsFromAnOlderEpochFail()
    {
        await using var rig = await Rig.StartAsync(SplitSettings.Create(SplitPolicy.Auto, SplitPolicy.Auto), Wrapped, hostEpochBump: true);
        var connector = rig.Connector(TunnelService.Game);
        await using var stream = await connector.OpenAsync(CancellationToken.None);
        Assert.IsNotType<SplitStream>(stream);
        Assert.Equal(0, connector.SplitConnections);
        await WaitUntilAsync(() => rig.Server.Rejected >= 2);
        Assert.Equal(0, rig.Server.Accepted);
    }

    // ---- helpers ----

    private static (RoomCredentials Credentials, MemberTable Table, RoomCode Host, MemberEntry Guest) NewRoom(SplitSettings split, TunnelSettings? tunnel = null)
    {
        using var host = X25519KeyPair.Generate();
        var credentials = RoomCredentials.Create(host.PublicKey, 25565, tunnel ?? Wrapped) with { Split = split };
        var table = new MemberTable();
        var hostRoom = credentials.ToHostRoom(table, "Host");
        var guest = table.Issue(RandomNumberGenerator.GetBytes(32), "Alice");
        return (credentials with { Member = new MemberKey(guest.Key.Id, guest.Key.CopyPsk()) }, table, hostRoom, guest);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException();
            await Task.Delay(20);
        }
    }

    /// <summary>
    /// A host with split listeners on free loopback ports that echo whatever arrives, and a member room. The single-path
    /// opener is a plain loopback echo connection that only counts its calls.
    /// </summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly List<TunnelService> _served = [];
        private readonly TcpListener _single = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();
        private Task _singleLoop = Task.CompletedTask;
        private int _singleCalls;
        private int _legOpens;

        public SplitTunnelServer Server { get; private set; } = null!;
        public MemberTable Table { get; private set; } = null!;
        public MemberEntry GuestEntry { get; private set; } = null!;
        public RoomCode Member { get; private set; } = null!;
        public int SingleCalls => Volatile.Read(ref _singleCalls);
        public int LegOpens => Volatile.Read(ref _legOpens);
        public IReadOnlyList<TunnelService> Served { get { lock (_served) return [.. _served]; } }

        public static Task<Rig> StartAsync(SplitSettings split, TunnelSettings tunnel, bool hostEpochBump = false)
        {
            var (credentials, table, host, guest) = NewRoom(split, tunnel);
            var rig = new Rig { Table = table, GuestEntry = guest, Member = credentials.ToRoom() };
            if (hostEpochBump) host = host with { Epoch = host.Epoch + 1 };
            rig.Server = new SplitTunnelServer(host, service => Echo(rig, service), ports: [.. Enumerable.Repeat(0, split.Paths)]);
            rig._single.Start();
            rig._singleLoop = Task.Run(rig.SingleLoopAsync);
            return Task.FromResult(rig);
        }

        private static Func<Stream, CancellationToken, Task> Echo(Rig rig, TunnelService service) => async (stream, ct) =>
        {
            lock (rig._served) rig._served.Add(service);
            var buffer = new byte[16 * 1024];
            int n;
            while ((n = await stream.ReadAsync(buffer, ct)) > 0)
            {
                await stream.WriteAsync(buffer.AsMemory(0, n), ct);
                await stream.FlushAsync(ct);
            }
        };

        private async Task SingleLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                try { using var _ = await _single.AcceptTcpClientAsync(_cts.Token); }
                catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException) { return; }
            }
        }

        /// <param name="legs">False: the member has no paths set up.</param>
        /// <param name="deadLeg">This leg's port refuses connections.</param>
        public SplitConnector Connector(TunnelService service, bool legs = true, int deadLeg = -1)
        {
            var port = ((IPEndPoint)_single.LocalEndpoint).Port;
            Func<CancellationToken, Task<Stream>> single = ct =>
            {
                Interlocked.Increment(ref _singleCalls);
                return TunnelClient.Raw("127.0.0.1", port, ct);
            };
            IReadOnlyList<Func<CancellationToken, Task<Stream>>>? openers = legs
                ? [.. Server.Ports.Select((p, i) => (Func<CancellationToken, Task<Stream>>)(ct =>
                {
                    Interlocked.Increment(ref _legOpens);
                    return i == deadLeg ? Task.FromException<Stream>(new SocketException((int)SocketError.ConnectionRefused)) : TunnelClient.Raw("127.0.0.1", p, ct);
                }))]
                : null;
            return new SplitConnector(Member, service, single, openers);
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _single.Stop();
            try { await _singleLoop; } catch (OperationCanceledException) { }
            await Server.DisposeAsync();
            _cts.Dispose();
        }
    }
}
