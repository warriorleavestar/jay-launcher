using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Amber.Core.Localization;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Transport;
using Xunit.Abstractions;

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

    [Fact]
    public void AnExtraNetworkSharingTheRoomSecretIsIgnored()
    {
        var (credentials, _, _, _) = NewRoom(SplitSettings.Create(SplitPolicy.Auto, SplitPolicy.Auto));
        var json = credentials.ToJson();
        json["split"]!["nets"]![0]!["secret"] = credentials.Secret;
        Assert.Same(SplitSettings.None, RoomCredentials.FromJson(JsonNode.Parse(json.ToJsonString())!).Split);
    }

    /// <summary>A later version may change the shape of the field: the rekey still works, it just carries no extra networks.</summary>
    [Theory]
    [InlineData("[]")]
    [InlineData("\"split\"")]
    [InlineData("{\"nets\":5}")]
    public void ARekeyWithAnUnknownSplitShapeStillRekeys(string split)
    {
        var (credentials, _, _, _) = NewRoom(SplitSettings.Create(SplitPolicy.Auto, SplitPolicy.Auto));
        var room = credentials.ToRoom();
        var next = credentials.Rotate();
        var json = next.ToRekey().ToJson();
        json["split"] = JsonNode.Parse(split);
        var rekey = RoomRekey.FromJson(JsonNode.Parse(json.ToJsonString())!);
        Assert.NotNull(rekey);
        Assert.Empty(rekey.SplitNets);
        var moved = room.Rekeyed(rekey);
        Assert.Equal(next.Epoch, moved.Epoch);
        Assert.False(moved.Split.Enabled);
    }

    /// <summary>
    /// Strict means split or nothing on every host. A full-LAN (TUN) host can't split in this version, so it refuses to
    /// start a Strict room with a reason instead of running one whose Strict service nobody can reach.
    /// </summary>
    [Fact]
    public void AFullLanHostWontRunAStrictRoom()
    {
        var (_, _, host, _) = NewRoom(SplitSettings.Create(SplitPolicy.Strict, SplitPolicy.Auto));
        var wrapped = new EasyTierOptions(host, true, [], 15000);
        Assert.True(RoomTransport.HostSplits(wrapped));
        Assert.Null(RoomTransport.HostProblem(wrapped));
        Assert.False(RoomTransport.ServesSinglePath(wrapped, TunnelService.Game));
        Assert.True(RoomTransport.ServesSinglePath(wrapped, TunnelService.Chat));
        Assert.True(RoomTransport.ServesSinglePath(wrapped, TunnelService.Remote));

        var fullLan = wrapped with { Tun = true };
        Assert.False(RoomTransport.HostSplits(fullLan));
        Assert.False(RoomTransport.ServesSinglePath(fullLan, TunnelService.Game));
        Assert.Equal(L.T("core.mp.split.full_lan_strict"), RoomTransport.HostProblem(fullLan));
        var e = Assert.Throws<InvalidOperationException>(() => RoomTransport.Start(fullLan));
        Assert.Equal(L.T("core.mp.split.full_lan_strict"), e.Message);

        // Auto in full-LAN mode is fine: nothing splits, every service stays on one path.
        var (_, _, autoHost, _) = NewRoom(SplitSettings.Create(SplitPolicy.Auto, SplitPolicy.Auto));
        var autoFullLan = new EasyTierOptions(autoHost, true, [], 15000) { Tun = true };
        Assert.Null(RoomTransport.HostProblem(autoFullLan));
        Assert.True(RoomTransport.ServesSinglePath(autoFullLan, TunnelService.Game));
    }

    /// <summary>Garbage on a leg (here in the Minecraft disguise) is a failed leg: Auto falls back to one path, Strict refuses.</summary>
    [Fact]
    public async Task GarbageOnALegFallsBackUnderAuto()
    {
        await using var rig = await Rig.StartAsync(SplitSettings.Create(SplitPolicy.Auto, SplitPolicy.Strict), new TunnelSettings(TransportMode.High, true));
        var singles = 0;
        Func<CancellationToken, Task<Stream>> single = _ =>
        {
            Interlocked.Increment(ref singles);
            return Task.FromResult<Stream>(new MemoryStream());
        };
        IReadOnlyList<Func<CancellationToken, Task<Stream>>> legs =
        [
            ct => TunnelClient.Raw("127.0.0.1", rig.Server.Ports[0], ct),
            _ => Task.FromResult<Stream>(new GarbageStream()),
        ];
        var game = new SplitConnector(rig.Member, TunnelService.Game, single, legs);
        await using (var stream = await game.OpenAsync(CancellationToken.None)) Assert.IsNotType<SplitStream>(stream);
        Assert.Equal(1, singles);
        Assert.NotNull(game.LastReason);
        var chat = new SplitConnector(rig.Member, TunnelService.Chat, single, legs);
        await Assert.ThrowsAsync<IOException>(() => chat.OpenAsync(CancellationToken.None));
        Assert.Equal(1, singles);
    }

    /// <summary>
    /// The places for legs in their handshake are shared across all ports: when stalled legs hold every one, the oldest
    /// makes room, so a member still gets a split connection.
    /// </summary>
    [Fact]
    public async Task StalledLegsAreCappedAcrossPorts()
    {
        await using var rig = await Rig.StartAsync(SplitSettings.Create(SplitPolicy.Auto, SplitPolicy.Auto), Wrapped);
        var ports = rig.Server.Ports;
        var idle = new List<TcpClient>();
        try
        {
            for (var i = 0; i < SplitTunnelServer.MaxPendingHandshakes; i++)
            {
                var c = new TcpClient();
                try { await c.ConnectAsync(IPAddress.Loopback, ports[i % ports.Count]); }
                catch (SocketException) { }
                idle.Add(c);
            }
            Assert.True(await WaitAsync(() => rig.Server.PendingHandshakes == SplitTunnelServer.MaxPendingHandshakes, 60));
            Assert.Equal(0, rig.Server.Evicted);
            var connector = rig.Connector(TunnelService.Game);
            await using (var stream = await connector.OpenAsync(CancellationToken.None))
            {
                Assert.IsType<SplitStream>(stream);
                await stream.WriteAsync(new byte[] { 5 });
                await stream.FlushAsync();
                var back = new byte[1];
                Assert.True(await SecureStream.ReadExactAsync(stream, back, CancellationToken.None));
                Assert.Equal(5, back[0]);
            }
            Assert.Equal(ports.Count, rig.Server.Evicted);
            Assert.True(rig.Server.PendingHandshakes <= SplitTunnelServer.MaxPendingHandshakes);
        }
        finally
        {
            foreach (var c in idle) c.Dispose();
        }
        Assert.True(await WaitAsync(() => rig.Server.PendingHandshakes == 0, 20));
    }

    /// <summary>A leg that says nothing and never closes is let go after the handshake deadline.</summary>
    [Fact]
    public async Task StalledLegsAreReleasedAtTheDeadline()
    {
        await using var rig = await Rig.StartAsync(SplitSettings.Create(SplitPolicy.Auto, SplitPolicy.Auto), Wrapped,
            handshakeTimeout: TimeSpan.FromMilliseconds(300));
        using var idle = new TcpClient();
        await idle.ConnectAsync(IPAddress.Loopback, rig.Server.Ports[1]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var closed = false;
        try { closed = await idle.GetStream().ReadAsync(new byte[1], timeout.Token) == 0; }
        catch (Exception e) when (e is IOException or SocketException) { closed = true; }
        Assert.True(closed);
        Assert.True(await WaitAsync(() => rig.Server.PendingHandshakes == 0));
        Assert.Equal(0, rig.Server.Accepted);
    }

    /// <summary>Legs that fail before their handshake starts give their place back, whatever the failure.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegsThatFailBeforeTheHandshakeFreeTheirPlace(bool unexpected)
    {
        const int failing = SplitTunnelServer.MaxPendingHandshakes + 8;
        var seen = 0;
        var log = new List<string>();
        await using var rig = await Rig.StartAsync(SplitSettings.Create(SplitPolicy.Auto, SplitPolicy.Auto), Wrapped, beforeServe: _ =>
        {
            if (Interlocked.Increment(ref seen) > failing) return;
            if (unexpected) throw new NotSupportedException("test");
            throw new SocketException((int)SocketError.ConnectionReset);
        }, log: line => { lock (log) log.Add(line); });
        var ports = rig.Server.Ports;
        for (var i = 0; i < failing; i++)
        {
            using var c = new TcpClient();
            try { await c.ConnectAsync(IPAddress.Loopback, ports[i % ports.Count]); }
            catch (SocketException) { }
            Assert.True(await WaitAsync(() => Volatile.Read(ref seen) > i && rig.Server.PendingHandshakes == 0, 10));
        }
        Assert.Equal(failing, rig.Server.Rejected);
        // The line is written before the leg is counted. Unexpected failures are failed handshakes, limited to one line a
        // minute; a dropped connection writes nothing.
        var failed = L.T("core.mp.transport.endpoint_handshake_failed");
        lock (log)
        {
            if (unexpected)
                Assert.Single(log, l => l.StartsWith(failed, StringComparison.Ordinal) && l.EndsWith(nameof(NotSupportedException), StringComparison.Ordinal));
            else
                Assert.DoesNotContain(log, l => l.StartsWith(failed, StringComparison.Ordinal));
            Assert.DoesNotContain(log, l => l.StartsWith(L.T("core.mp.transport.endpoint_ended"), StringComparison.Ordinal));
        }
        var connector = rig.Connector(TunnelService.Game);
        await using var stream = await connector.OpenAsync(CancellationToken.None);
        Assert.IsType<SplitStream>(stream);
    }

    private static async Task<bool> WaitAsync(Func<bool> condition, int seconds = 5)
    {
        for (var i = 0; i < seconds * 20; i++)
        {
            if (condition()) return true;
            await Task.Delay(50);
        }
        return condition();
    }

    /// <summary>Answers every read with random bytes and swallows writes.</summary>
    private sealed class GarbageStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            RandomNumberGenerator.Fill(buffer.AsSpan(offset, count));
            return count;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) { }
    }

    [Fact]
    public async Task AFullLanMemberIsToldWhyItCantSplit()
    {
        var (credentials, _, _, _) = NewRoom(SplitSettings.Create(SplitPolicy.Strict, SplitPolicy.Auto));
        var member = credentials.ToRoom();
        var reason = L.T("core.mp.split.full_lan");
        var singles = 0;
        Func<CancellationToken, Task<Stream>> single = _ =>
        {
            Interlocked.Increment(ref singles);
            return Task.FromResult<Stream>(new MemoryStream());
        };
        var game = new SplitConnector(member, TunnelService.Game, single, null, noLegsReason: reason);
        var e = await Assert.ThrowsAsync<IOException>(() => game.OpenAsync(CancellationToken.None));
        Assert.Equal(L.F("core.mp.split.strict_unavailable", reason), e.Message);
        Assert.DoesNotContain("。 ", L.F("core.mp.split.strict_unavailable", reason));
        var chat = new SplitConnector(member, TunnelService.Chat, single, null, noLegsReason: reason);
        await using (await chat.OpenAsync(CancellationToken.None)) { }
        Assert.Equal(reason, chat.LastReason);
        Assert.Equal(1, singles);
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
    public async Task AHandlerThatFailsAfterPairingIsNotARefusedLeg()
    {
        var log = new List<string>();
        await using var rig = await Rig.StartAsync(SplitSettings.Create(SplitPolicy.Auto, SplitPolicy.Auto), Wrapped,
            handler: (_, _) => throw new InvalidDataException("handler"), log: line => { lock (log) log.Add(line); });
        var connector = rig.Connector(TunnelService.Game);
        try
        {
            await using var stream = await connector.OpenAsync(CancellationToken.None);
            await stream.ReadAsync(new byte[1]);
        }
        catch (Exception e) when (e is IOException or SocketException or CryptographicException or InvalidDataException or EndOfStreamException) { }
        await WaitUntilAsync(() => rig.Server.Accepted == 1);
        await Task.Delay(200);
        Assert.Equal(0, rig.Server.Rejected);
        lock (log)
        {
            Assert.DoesNotContain(log, l => l.StartsWith(L.T("core.mp.transport.endpoint_handshake_failed"), StringComparison.Ordinal));
        }
    }

    /// <summary>A failure after a leg's handshake (here: picking the service) is a connection that ended, not a failed handshake.</summary>
    [Fact]
    public async Task ARouteThatFailsAfterTheHandshakeIsLoggedAsEnded()
    {
        var log = new List<string>();
        await using var rig = await Rig.StartAsync(SplitSettings.Create(SplitPolicy.Strict, SplitPolicy.Strict), Wrapped,
            log: line => { lock (log) log.Add(line); }, route: _ => throw new InvalidOperationException("route"));
        var connector = rig.Connector(TunnelService.Game);
        try
        {
            await using var stream = await connector.OpenAsync(CancellationToken.None);
            await stream.ReadAsync(new byte[1]);
        }
        catch (Exception e) when (e is IOException or SocketException or CryptographicException or InvalidDataException or EndOfStreamException
                                   or InvalidOperationException or OperationCanceledException) { }
        var ended = L.T("core.mp.transport.endpoint_ended");
        Assert.True(await WaitAsync(() => { lock (log) return log.Any(l => l.StartsWith(ended, StringComparison.Ordinal)); }));
        // One refusal per leg, each counted once.
        await WaitUntilAsync(() => rig.Server.Rejected >= 2);
        await Task.Delay(100);
        Assert.Equal(2, rig.Server.Rejected);
        Assert.Equal(0, rig.Server.Accepted);
        lock (log)
        {
            Assert.Contains(log, l => l.StartsWith(ended, StringComparison.Ordinal) && l.EndsWith(nameof(InvalidOperationException), StringComparison.Ordinal));
            Assert.DoesNotContain(log, l => l.StartsWith(L.T("core.mp.transport.endpoint_handshake_failed"), StringComparison.Ordinal));
        }
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

    /// <summary>A dead path: Auto falls back to one path each time, and after a few failures in a row pauses trying the legs.</summary>
    [Fact]
    public async Task AutoPausesAfterRepeatedFailures()
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
        Assert.Equal(L.T("core.mp.split.gave_up"), connector.LastReason);
    }

    /// <summary>The pause ends: paths that came up late (the extra instances start after the room) still get used.</summary>
    [Fact]
    public async Task AutoTriesAgainAfterThePause()
    {
        await using var rig = await Rig.StartAsync(SplitSettings.Create(SplitPolicy.Auto, SplitPolicy.Auto), Wrapped);
        var dead = 1;
        var connector = rig.Connector(TunnelService.Game, deadLeg: () => Volatile.Read(ref dead) == 1 ? 1 : -1, retryAfter: TimeSpan.FromMilliseconds(300));
        for (var i = 0; i < SplitConnector.MaxFailures + 1; i++)
        {
            await using var stream = await connector.OpenAsync(CancellationToken.None);
            Assert.IsNotType<SplitStream>(stream);
        }
        Assert.Equal(SplitConnector.MaxFailures * 2, rig.LegOpens);
        Volatile.Write(ref dead, 0);
        // Keeps connecting until the pause is over (on one path meanwhile), so a slow machine doesn't fail the test.
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (true)
        {
            await using var stream = await connector.OpenAsync(CancellationToken.None);
            if (stream is SplitStream) break;
            Assert.True(DateTime.UtcNow < until, "Auto never tried again after the pause");
            await Task.Delay(50);
        }
        Assert.Equal(1, connector.SplitConnections);
        Assert.Null(connector.LastReason);
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

    /// <summary>
    /// Auto never holds a connection up for long: a leg that never answers falls back to one path within the budget, in
    /// time for the caller's own deadline (chat gives a connection 5 seconds).
    /// </summary>
    [Fact]
    public async Task AutoDoesntWaitLongForASlowLeg()
    {
        await using var rig = await Rig.StartAsync(SplitSettings.Create(SplitPolicy.Auto, SplitPolicy.Auto), Wrapped);
        using var silent = new TcpListener(IPAddress.Loopback, 0);
        silent.Start();
        var held = new List<TcpClient>();
        var accepting = Task.Run(async () =>
        {
            try { while (true) { var c = await silent.AcceptTcpClientAsync(); lock (held) held.Add(c); } }
            catch (Exception e) when (e is SocketException or ObjectDisposedException or InvalidOperationException) { }
        });
        var silentPort = ((IPEndPoint)silent.LocalEndpoint).Port;
        var singlePort = rig.SinglePort;
        IReadOnlyList<Func<CancellationToken, Task<Stream>>> legs =
        [
            ct => TunnelClient.Raw("127.0.0.1", rig.Server.Ports[0], ct),
            ct => TunnelClient.Raw("127.0.0.1", silentPort, ct),
        ];
        var connector = new SplitConnector(rig.Member, TunnelService.Chat, ct => TunnelClient.Raw("127.0.0.1", singlePort, ct), legs)
        {
            AutoBudget = TimeSpan.FromMilliseconds(300),
        };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await using (var stream = await connector.OpenAsync(deadline.Token)) Assert.IsNotType<SplitStream>(stream);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"took {watch.Elapsed}");
        Assert.Equal(L.T("core.mp.split.too_slow"), connector.LastReason);
        silent.Stop();
        await accepting;
        lock (held) foreach (var c in held) c.Dispose();
    }

    [Fact]
    public void TheAutoBudgetIsPositiveOrInfinite()
    {
        var room = NewRoom(SplitSettings.Create(SplitPolicy.Auto, SplitPolicy.Auto)).Credentials.ToRoom();
        Func<CancellationToken, Task<Stream>> single = _ => throw new InvalidOperationException();
        Assert.Throws<ArgumentOutOfRangeException>(() => new SplitConnector(room, TunnelService.Chat, single, null) { AutoBudget = TimeSpan.Zero });
        Assert.Throws<ArgumentOutOfRangeException>(() => new SplitConnector(room, TunnelService.Chat, single, null) { AutoBudget = TimeSpan.FromSeconds(-2) });
        Assert.Equal(Timeout.InfiniteTimeSpan, new SplitConnector(room, TunnelService.Chat, single, null) { AutoBudget = Timeout.InfiniteTimeSpan }.AutoBudget);
    }

    /// <summary>A leg cancelled for its own reasons is a failure, not "too slow": only the budget running out is.</summary>
    [Fact]
    public async Task ALegsOwnCancellationIsNotTooSlow()
    {
        await using var rig = await Rig.StartAsync(SplitSettings.Create(SplitPolicy.Auto, SplitPolicy.Auto), Wrapped);
        var singlePort = rig.SinglePort;
        IReadOnlyList<Func<CancellationToken, Task<Stream>>> legs =
        [
            ct => TunnelClient.Raw("127.0.0.1", rig.Server.Ports[0], ct),
            _ => Task.FromException<Stream>(new OperationCanceledException()),
        ];
        var connector = new SplitConnector(rig.Member, TunnelService.Chat, ct => TunnelClient.Raw("127.0.0.1", singlePort, ct), legs);
        await using (var stream = await connector.OpenAsync(CancellationToken.None)) Assert.IsNotType<SplitStream>(stream);
        Assert.NotEqual(L.T("core.mp.split.too_slow"), connector.LastReason);
        Assert.StartsWith(L.T("core.mp.split.failed"), connector.LastReason);
    }

    /// <summary>
    /// Chat asks whether a connection opened now would split, to move a connection made before the paths were ready.
    /// </summary>
    [Fact]
    public async Task ACouldSplitNowOnlyAfterAnAutoConnectionWentOnOnePath()
    {
        await using var rig = await Rig.StartAsync(SplitSettings.Create(SplitPolicy.Auto, SplitPolicy.Auto), Wrapped);
        var dead = 1;
        var connector = rig.Connector(TunnelService.Chat, () => Volatile.Read(ref dead));
        await using (var s = await connector.OpenAsync(CancellationToken.None)) Assert.IsNotType<SplitStream>(s);
        Assert.False(connector.LastWasSplit);
        Assert.True(connector.CouldSplitNow);
        Volatile.Write(ref dead, -1);
        await using (var s = await connector.OpenAsync(CancellationToken.None)) Assert.IsType<SplitStream>(s);
        Assert.True(connector.LastWasSplit);
        Assert.False(connector.CouldSplitNow);

        // Not while the paths aren't ready, without legs, or for another policy.
        string? problem = "not ready";
        var waiting = new SplitConnector(rig.Member, TunnelService.Chat, _ => throw new InvalidOperationException(),
            [.. rig.Server.Ports.Select(p => (Func<CancellationToken, Task<Stream>>)(ct => TunnelClient.Raw("127.0.0.1", p, ct)))], pathProblem: () => problem);
        Assert.False(waiting.CouldSplitNow);
        problem = null;
        Assert.True(waiting.CouldSplitNow);
        Assert.False(rig.Connector(TunnelService.Chat, legs: false).CouldSplitNow);
        await using var strictRig = await Rig.StartAsync(SplitSettings.Create(SplitPolicy.Strict, SplitPolicy.Strict), Wrapped);
        Assert.False(strictRig.Connector(TunnelService.Chat).CouldSplitNow);
    }

    /// <summary>While Auto pauses after failing in a row, it doesn't offer to split either.</summary>
    [Fact]
    public async Task ACouldSplitNowWaitsOutThePause()
    {
        await using var rig = await Rig.StartAsync(SplitSettings.Create(SplitPolicy.Auto, SplitPolicy.Auto), Wrapped);
        var connector = rig.Connector(TunnelService.Chat, deadLeg: 1);
        for (var i = 0; i < SplitConnector.MaxFailures; i++)
            await using (var s = await connector.OpenAsync(CancellationToken.None)) Assert.IsNotType<SplitStream>(s);
        Assert.False(connector.CouldSplitNow);
    }

    /// <summary>
    /// The session's leg ports, lent to one transport after another, take split connections every time (only stopping and
    /// starting was covered before).
    /// </summary>
    [Fact]
    public async Task BorrowedLegPortsAcceptAgainAfterARestart()
    {
        var (credentials, _, host, _) = NewRoom(SplitSettings.Create(SplitPolicy.Strict, SplitPolicy.Strict));
        var member = credentials.ToRoom();
        using var reservation = SplitLegReservation.TryReserve(2, IPAddress.Loopback, [0, 0]);
        Assert.NotNull(reservation);
        var ports = reservation!.AllForTest.Select(l => ((IPEndPoint)l.LocalEndpoint).Port).ToArray();
        Func<Stream, CancellationToken, Task> echo = async (stream, ct) =>
        {
            var buffer = new byte[64];
            int n;
            while ((n = await stream.ReadAsync(buffer, ct)) > 0)
            {
                await stream.WriteAsync(buffer.AsMemory(0, n), ct);
                await stream.FlushAsync(ct);
            }
        };
        IReadOnlyList<Func<CancellationToken, Task<Stream>>> legs =
            [.. ports.Select(p => (Func<CancellationToken, Task<Stream>>)(ct => TunnelClient.Raw("127.0.0.1", p, ct)))];
        for (var round = 0; round < 2; round++)
        {
            await using var server = new SplitTunnelServer(host, _ => echo, listeners: reservation.TryBorrow(), giveBack: reservation.Return);
            var connector = new SplitConnector(member, TunnelService.Game, _ => throw new InvalidOperationException("Strict never uses one path"), legs);
            await using var stream = await connector.OpenAsync(CancellationToken.None);
            Assert.IsType<SplitStream>(stream);
            byte[] ping = [(byte)round, 7, 8];
            await stream.WriteAsync(ping);
            await stream.FlushAsync();
            var back = new byte[ping.Length];
            await stream.ReadExactlyAsync(back);
            Assert.Equal(ping, back);
            Assert.Equal(1, server.Accepted);
        }
        Assert.NotNull(reservation.TryBorrow());
        reservation.Return();
    }

    /// <summary>The "connected over N paths" line is written when a service goes from one path to split, not per connection.</summary>
    [Fact]
    public async Task TheSplitLineIsLoggedOnlyWhenSplittingStarts()
    {
        await using var rig = await Rig.StartAsync(SplitSettings.Create(SplitPolicy.Auto, SplitPolicy.Auto), Wrapped);
        var dead = -1;
        var log = new List<string>();
        var connector = rig.Connector(TunnelService.Game, () => Volatile.Read(ref dead), log: line => { lock (log) log.Add(line); });
        var line = L.F("core.mp.split.connected_game", 2);
        int Count() { lock (log) return log.Count(l => l == line); }
        for (var i = 0; i < 3; i++) await using (var s = await connector.OpenAsync(CancellationToken.None)) Assert.IsType<SplitStream>(s);
        Assert.Equal(1, Count());
        Volatile.Write(ref dead, 1);
        await using (var s = await connector.OpenAsync(CancellationToken.None)) Assert.IsNotType<SplitStream>(s);
        Volatile.Write(ref dead, -1);
        await using (var s = await connector.OpenAsync(CancellationToken.None)) Assert.IsType<SplitStream>(s);
        Assert.Equal(2, Count());
    }

    /// <summary>
    /// After a kick rekeys the room, a host and member rebuilt from the rekeyed room code split again, and a host left on
    /// the old room code can't pair the new legs (why the app rebuilds the transport on both sides).
    /// </summary>
    [Fact]
    public async Task SplittingWorksAgainAfterAKick()
    {
        var (credentials, _, host, _) = NewRoom(SplitSettings.Create(SplitPolicy.Strict, SplitPolicy.Auto));
        var member = credentials.ToRoom();
        var rekey = RoomRekey.FromJson(JsonNode.Parse(credentials.Rotate().ToRekey().ToJson().ToJsonString())!)!;
        var hostNext = host.Rekeyed(rekey);
        var memberNext = member.Rekeyed(rekey);
        Assert.True(memberNext.Epoch > member.Epoch);
        Func<Stream, CancellationToken, Task> echo = async (stream, ct) =>
        {
            var buffer = new byte[256];
            int n;
            while ((n = await stream.ReadAsync(buffer, ct)) > 0)
            {
                await stream.WriteAsync(buffer.AsMemory(0, n), ct);
                await stream.FlushAsync(ct);
            }
        };
        Func<CancellationToken, Task<Stream>> noSingle = _ => throw new InvalidOperationException("Strict never uses one path");
        static IReadOnlyList<Func<CancellationToken, Task<Stream>>> Legs(SplitTunnelServer server) =>
            [.. server.Ports.Select(p => (Func<CancellationToken, Task<Stream>>)(ct => TunnelClient.Raw("127.0.0.1", p, ct)))];

        await using (var stale = new SplitTunnelServer(host, _ => echo, ports: [0, 0]))
        {
            var connector = new SplitConnector(memberNext, TunnelService.Game, noSingle, Legs(stale));
            await Assert.ThrowsAsync<IOException>(() => connector.OpenAsync(CancellationToken.None));
            await WaitUntilAsync(() => stale.Rejected >= 2);
            Assert.Equal(0, stale.Accepted);
        }

        await using var rebuilt = new SplitTunnelServer(hostNext, _ => echo, ports: [0, 0]);
        var game = new SplitConnector(memberNext, TunnelService.Game, noSingle, Legs(rebuilt));
        await using var stream = await game.OpenAsync(CancellationToken.None);
        Assert.IsType<SplitStream>(stream);
        byte[] ping = [1, 2, 3, 4];
        await stream.WriteAsync(ping);
        await stream.FlushAsync();
        var back = new byte[ping.Length];
        await stream.ReadExactlyAsync(back);
        Assert.Equal(ping, back);
        Assert.Equal(1, rebuilt.Accepted);
    }

    // ---- helpers ----

    internal static (RoomCredentials Credentials, MemberTable Table, RoomCode Host, MemberEntry Guest) NewRoom(SplitSettings split, TunnelSettings? tunnel = null)
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
        public int SinglePort => ((IPEndPoint)_single.LocalEndpoint).Port;
        public int LegOpens => Volatile.Read(ref _legOpens);
        public IReadOnlyList<TunnelService> Served { get { lock (_served) return [.. _served]; } }

        public static Task<Rig> StartAsync(SplitSettings split, TunnelSettings tunnel, bool hostEpochBump = false, Action<TcpClient>? beforeServe = null,
            TimeSpan? handshakeTimeout = null, Func<Stream, CancellationToken, Task>? handler = null, Action<string>? log = null,
            Func<TunnelService, Func<Stream, CancellationToken, Task>?>? route = null)
        {
            var (credentials, table, host, guest) = NewRoom(split, tunnel);
            var rig = new Rig { Table = table, GuestEntry = guest, Member = credentials.ToRoom() };
            if (hostEpochBump) host = host with { Epoch = host.Epoch + 1 };
            rig.Server = new SplitTunnelServer(host, route ?? (service => handler ?? Echo(rig, service)), log, ports: [.. Enumerable.Repeat(0, split.Paths)]) { BeforeServeForTest = beforeServe, HandshakeTimeout = handshakeTimeout ?? TunnelHandshake.Timeout };
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
        public SplitConnector Connector(TunnelService service, bool legs = true, int deadLeg = -1) =>
            Connector(service, () => deadLeg, legs);

        public SplitConnector Connector(TunnelService service, Func<int> deadLeg, bool legs = true, TimeSpan? retryAfter = null, Action<string>? log = null)
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
                    return i == deadLeg() ? Task.FromException<Stream>(new SocketException((int)SocketError.ConnectionRefused)) : TunnelClient.Raw("127.0.0.1", p, ct);
                }))]
                : null;
            return new SplitConnector(Member, service, single, openers, log) { RetryAfter = retryAfter ?? TimeSpan.FromSeconds(30) };
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

[CollectionDefinition(nameof(RealRoomPorts), DisableParallelization = true)]
public sealed class RealRoomPorts;

/// <summary>Split room tests that need the real room ports.</summary>
[Collection(nameof(RealRoomPorts))]
public sealed class SplitRoomPortTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    /// <summary>
    /// A leg port taken by another program: an Auto room starts without splitting (and says so), a Strict room doesn't start.
    /// Uses the real room ports (it occupies 47657 itself, and the Auto room listens on 47656), so it runs on its own,
    /// never alongside another test.
    /// </summary>
    [Fact]
    public async Task ABusyLegPortStopsOnlyAStrictRoom()
    {
        using var squatter = new TcpListener(IPAddress.Loopback, SplitSettings.LegPort(0));
        try { squatter.Start(); }
        catch (SocketException) { /* already taken by something else: the same situation for the leg port */ }
        // The Auto room listens on the real single-path port. When Jay is running a room on this machine it's taken, and
        // there is nothing to test here.
        // xunit 2 has no runtime skip, so the test says why it did nothing.
        try
        {
            using var probe = new TcpListener(IPAddress.Loopback, TunnelSettings.Port);
            probe.Start();
        }
        catch (SocketException)
        {
            _output.WriteLine($"Skipped: port {TunnelSettings.Port} is in use on this machine.");
            return;
        }

        var (_, _, autoHost, _) = SplitRoomTests.NewRoom(SplitSettings.Create(SplitPolicy.Auto, SplitPolicy.Auto));
        var log = new List<string>();
        await using (var transport = RoomTransport.Start(new EasyTierOptions(autoHost, true, [], 15000), line => { lock (log) log.Add(line); }))
        {
            Assert.Null(transport.SplitServer);
            Assert.NotNull(transport.Server);
            lock (log) Assert.Contains(L.T("core.mp.split.ports_busy"), log);
        }

        var (_, _, strictHost, _) = SplitRoomTests.NewRoom(SplitSettings.Create(SplitPolicy.Strict, SplitPolicy.Auto));
        var e = Assert.Throws<IOException>(() => RoomTransport.Start(new EasyTierOptions(strictHost, true, [], 15000)));
        Assert.Equal(L.F("core.mp.split.ports_busy_strict", SplitSettings.LegPort(0), SplitSettings.LegPort(1)), e.Message);
    }
}
