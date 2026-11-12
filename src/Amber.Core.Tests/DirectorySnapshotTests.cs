using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Public;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Tests;

/// <summary>Privacy item 12: everyone downloads the whole directory and looks rooms up locally.</summary>
public sealed class DirectorySnapshotTests : IAsyncDisposable
{
    /// <summary>Maps the directory address to the loopback server and counts connections and bytes received.</summary>
    private sealed class CountingNetwork(int port) : IPublicNetwork
    {
        public int Connections;
        public long BytesIn;

        public async Task<Stream> ConnectAsync(string ip, int p, CancellationToken ct)
        {
            Interlocked.Increment(ref Connections);
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, ct);
            return new Counting(client.GetStream(), this);
        }

        public Task<string?> MyIpAsync(CancellationToken ct) => Task.FromResult<string?>("10.235.0.7");
    }

    private sealed class Counting(NetworkStream inner, CountingNetwork owner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
        public override int Read(byte[] buffer, int offset, int count) => Add(inner.Read(buffer, offset, count));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => Add(await inner.ReadAsync(buffer, ct));
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => inner.WriteAsync(buffer, ct);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => inner.WriteAsync(buffer, offset, count, ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }

        private int Add(int n)
        {
            Interlocked.Add(ref owner.BytesIn, n);
            return n;
        }
    }

    private DateTimeOffset _now = DateTimeOffset.UtcNow;
    private readonly RoomDirectoryServer _directory;
    private readonly X25519KeyPair _host = X25519KeyPair.Generate();

    public DirectorySnapshotTests() => _directory = new RoomDirectoryServer(IPAddress.Loopback, 0, clock: () => _now);

    public async ValueTask DisposeAsync() => await _directory.DisposeAsync();

    private RoomListing Listing(RoomVisibility mode) => new()
    {
        Id = "",
        Name = "room",
        Host = "Alex",
        Mode = mode,
        HostKey = _host.PublicKey,
        Ip = "10.235.0.7",
        Game = "1.21.1",
        Players = 1,
        Invite = mode == RoomVisibility.Public ? new InviteBook().CreateStanding(false, TunnelSettings.Default).Invite.ToString() : null,
    };

    private Task HideAsync(RoomSigner signer, byte[] lobby) =>
        new RoomDirectoryClient(new CountingNetwork(_directory.Port)).AnnounceAsync(signer.Sign(Listing(RoomVisibility.Hidden), _now), false, [lobby], default);

    [Fact]
    public async Task ReadersLookingForDifferentRoomsSendTheSameRequests()
    {
        var a = new RoomSigner();
        var b = new RoomSigner();
        var lobbyA = RandomNumberGenerator.GetBytes(32);
        var lobbyB = RandomNumberGenerator.GetBytes(32);
        await HideAsync(a, lobbyA);
        await HideAsync(b, lobbyB);

        var log = new ConcurrentQueue<string>();
        _directory.OnRequest = r => log.Enqueue(r.ToJsonString());
        var nets = new[] { new CountingNetwork(_directory.Port), new CountingNetwork(_directory.Port), new CountingNetwork(_directory.Port) };
        var clients = nets.Select(n => new RoomDirectoryClient(n)).ToArray();
        var polls = clients.Select(c => c.Snapshots.StartPolling(() => c, TimeSpan.FromHours(1))).ToList();
        try
        {
            // Reading waits for the first scheduled refresh, then answers from the local copy.
            Assert.Equal(a.RoomId, (await clients[0].LookupAsync(lobbyA, default))!.Id);
            Assert.Equal(b.RoomId, (await clients[1].LookupAsync(lobbyB, default))!.Id);
            Assert.Null(await clients[1].LookupAsync(RandomNumberGenerator.GetBytes(32), default));
            await clients[2].Snapshots.ReadyAsync(clients[2], default);
            // More lookups touch nothing on the network.
            Assert.NotNull(await clients[0].LookupAsync(lobbyA, default));
            Assert.NotNull(await clients[0].LookupAsync(lobbyB, default));
        }
        finally
        {
            polls.ForEach(p => p.Dispose());
        }
        Assert.All(nets, n => Assert.Equal(1, n.Connections));
        // Same request from all three, same number of bytes back.
        Assert.Equal(3, log.Count);
        Assert.Single(log.Distinct());
        Assert.Contains("\"op\":\"snap\"", log.First());
        Assert.Single(nets.Select(n => n.BytesIn).Distinct());
    }

    [Fact]
    public async Task RepliesArePaddedAndLaterRefreshesAreDeltas()
    {
        var log = new List<JsonObject>();
        _directory.OnRequest = r => { lock (log) log.Add(r); };
        var net = new CountingNetwork(_directory.Port);
        var client = new RoomDirectoryClient(net);
        Assert.Empty(await client.ListAsync(default));
        Assert.Equal(0, (net.BytesIn - 2000) / RoomDirectoryProtocol.SnapshotPad);
        Assert.True(net.BytesIn > RoomDirectoryProtocol.SnapshotPad);

        var signer = new RoomSigner();
        var lobby = RandomNumberGenerator.GetBytes(16);
        await new RoomDirectoryClient(net).AnnounceAsync(signer.Sign(Listing(RoomVisibility.Public), _now), true, [lobby], default);
        Assert.Single(await client.ListAsync(default));
        Assert.NotNull(await client.LookupAsync(lobby, default));
        // The second refresh named the version it had, so the reply was a delta.
        var snaps = log.Where(r => r["op"]?.GetValue<string>() == "snap").ToList();
        Assert.Null(snaps[0]["have"]);
        Assert.NotNull(snaps[1]["have"]);
        var delta = _directory.Handle(snaps[1]);
        Assert.False(delta["full"]!.GetValue<bool>());

        await client.WithdrawAsync(signer, default);
        Assert.Empty(await client.ListAsync(default));
        Assert.Null(await client.LookupAsync(lobby, default));

        // A copy from another run (or an unknown version) gets everything again.
        Assert.True(_directory.Handle(new JsonObject { ["op"] = "snap", ["epoch"] = "00", ["have"] = 1L })["full"]!.GetValue<bool>());
    }

    [Fact]
    public async Task RenewalsWithTheSameDetailsDoNotChangeHiddenEntries()
    {
        var signer = new RoomSigner();
        var lobby = RandomNumberGenerator.GetBytes(16);
        var client = new RoomDirectoryClient(new CountingNetwork(_directory.Port));
        await client.AnnounceAsync(signer.Sign(Listing(RoomVisibility.Hidden), _now), false, [lobby], default);
        var before = _directory.Handle(new JsonObject { ["op"] = "snap" });
        _now += TimeSpan.FromSeconds(30);
        await client.AnnounceAsync(signer.Sign(Listing(RoomVisibility.Hidden), _now), false, [lobby], default);
        var after = _directory.Handle(new JsonObject { ["op"] = "snap", ["epoch"] = before["epoch"]!.GetValue<string>(), ["have"] = before["ver"]!.GetValue<long>() });
        Assert.Empty(after["hidden"]!.AsArray());
        Assert.Equal(before["ver"]!.GetValue<long>(), after["ver"]!.GetValue<long>());
        // Expiry removes the entry.
        _now += RoomDirectoryProtocol.Ttl + TimeSpan.FromSeconds(1);
        var gone = _directory.Handle(new JsonObject { ["op"] = "snap", ["epoch"] = before["epoch"]!.GetValue<string>(), ["have"] = before["ver"]!.GetValue<long>() });
        Assert.Contains(gone["gone"]!.AsArray().Select(g => g!.GetValue<string>()), k => k.StartsWith("h:" + Convert.ToHexStringLower(HiddenEntry.Index(lobby)) + ":", StringComparison.Ordinal));
    }

    [Fact]
    public void HiddenEntriesOpenOnlyWithTheirInviteAndAllHaveOneSize()
    {
        var lobby = RandomNumberGenerator.GetBytes(16);
        var hit = new DirectoryHit("room-id", "10.235.1.2", RandomNumberGenerator.GetBytes(32));
        var entry = HiddenEntry.Seal(lobby, hit);
        Assert.Equal(HiddenEntry.SealedLength, entry.Length);
        Assert.Equal(HiddenEntry.SealedLength, HiddenEntry.Seal(lobby, hit with { Id = new string('x', 120) }).Length);
        var opened = HiddenEntry.Open(lobby, entry)!;
        Assert.Equal((hit.Id, hit.Ip), (opened.Id, opened.Ip));
        Assert.Equal(hit.HostKey, opened.HostKey);
        Assert.Null(HiddenEntry.Open(RandomNumberGenerator.GetBytes(16), entry));
        entry[^1] ^= 1;
        Assert.Null(HiddenEntry.Open(lobby, entry));
        // An entry pointing outside the public network is ignored.
        Assert.Null(HiddenEntry.Open(lobby, HiddenEntry.Seal(lobby, hit with { Ip = "203.0.113.5" })));
        Assert.Throws<ArgumentException>(() => HiddenEntry.Index(new byte[15]));
        Assert.NotEqual(HiddenEntry.Index(lobby), Rendezvous.Id(lobby));
    }

    [Fact]
    public async Task MalformedHiddenEntriesAreRejected()
    {
        var request = new JsonObject
        {
            ["op"] = "announce",
            ["room"] = new RoomSigner().Sign(Listing(RoomVisibility.Hidden), _now).ToJson(),
            ["listed"] = false,
            ["hidden"] = new JsonArray(new JsonObject { ["k"] = new string('a', 64), ["b"] = Convert.ToBase64String(new byte[10]) }),
        };
        Assert.Equal("bad", _directory.Handle(request)["error"]!.GetValue<string>());
        // The old raw lookup keys are gone: a hidden room with nothing else is empty.
        request.Remove("hidden");
        request["lookup"] = new JsonArray((JsonNode)new string('c', 64));
        Assert.Equal("empty", _directory.Handle(request)["error"]!.GetValue<string>());
        Assert.Equal("op", _directory.Handle(new JsonObject { ["op"] = "lookup", ["key"] = new string('c', 64) })["error"]!.GetValue<string>());
        Assert.Equal("op", _directory.Handle(new JsonObject { ["op"] = "list" })["error"]!.GetValue<string>());
    }

    /// <summary>Publishes junk (not sealed with the invite) under <paramref name="lobby"/>'s index, as a stranger could.</summary>
    private JsonObject Squat(byte[] lobby) => _directory.Handle(new JsonObject
    {
        ["op"] = "announce",
        ["room"] = new RoomSigner().Sign(Listing(RoomVisibility.Hidden), _now).ToJson(),
        ["listed"] = false,
        ["hidden"] = new JsonArray(new JsonObject
        {
            ["k"] = Convert.ToHexStringLower(HiddenEntry.Index(lobby)),
            ["b"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(HiddenEntry.SealedLength)),
        }),
    });

    [Fact]
    public async Task StrangersCannotHoldAnInvitesIndexAgainstItsRoom()
    {
        // After a directory restart the index is free; a stranger who saw it in an old snapshot gets there first.
        var lobby = RandomNumberGenerator.GetBytes(16);
        Assert.True(Squat(lobby)["ok"]!.GetValue<bool>());
        var room = new RoomSigner();
        await HideAsync(room, lobby);
        var reader = new RoomDirectoryClient(new CountingNetwork(_directory.Port));
        Assert.Equal(room.RoomId, (await reader.LookupAsync(lobby, default))!.Id);
        // Only a few rooms can share one index; once the room is there it keeps its place when it renews.
        for (var i = 1; i < RoomDirectoryServer.MaxEntriesPerIndex - 1; i++) Assert.True(Squat(lobby)["ok"]!.GetValue<bool>());
        Assert.Equal("lookup", Squat(lobby)["error"]!.GetValue<string>());
        _now += TimeSpan.FromSeconds(30);
        await HideAsync(room, lobby);
        Assert.Equal(room.RoomId, (await reader.LookupAsync(lobby, default))!.Id);
    }

    [Fact]
    public async Task EntriesForOneInviteThatPointAtDifferentHostsAreNotTrusted()
    {
        // Someone else holding the invite publishes their own host under it: the reader can't tell which is real.
        var lobby = RandomNumberGenerator.GetBytes(16);
        await HideAsync(new RoomSigner(), lobby);
        var other = new RoomSigner().Sign(Listing(RoomVisibility.Hidden) with { HostKey = X25519KeyPair.Generate().PublicKey }, _now);
        await new RoomDirectoryClient(new CountingNetwork(_directory.Port)).AnnounceAsync(other, false, [lobby], default);
        Assert.Null(await new RoomDirectoryClient(new CountingNetwork(_directory.Port)).LookupAsync(lobby, default));
    }

    [Fact]
    public async Task PublicRenewalsThatChangeNothingVisibleAreNotSnapshotChanges()
    {
        var signer = new RoomSigner();
        var room = Listing(RoomVisibility.Public);
        var client = new RoomDirectoryClient(new CountingNetwork(_directory.Port));
        await client.AnnounceAsync(signer.Sign(room, _now), true, [], default);
        var before = _directory.Handle(new JsonObject { ["op"] = "snap" });
        JsonObject Since() => _directory.Handle(new JsonObject { ["op"] = "snap", ["epoch"] = before["epoch"]!.GetValue<string>(), ["have"] = before["ver"]!.GetValue<long>() });
        _now += TimeSpan.FromSeconds(30);
        await client.AnnounceAsync(signer.Sign(room, _now), true, [], default);
        Assert.Empty(Since()["rooms"]!.AsArray());
        Assert.Single(await client.ListAsync(default));
        // A visible change (another player joined) is.
        await client.AnnounceAsync(signer.Sign(room with { Players = 2 }, _now), true, [], default);
        Assert.Equal(2, Assert.Single(Since()["rooms"]!.AsArray())!["players"]!.GetValue<int>());
    }

    [Fact]
    public void MalformedSnapshotRepliesAreNotUsedAtAll()
    {
        JsonObject Good() => new()
        {
            ["ok"] = true, ["epoch"] = "ab", ["ver"] = 3L, ["full"] = true,
            ["rooms"] = new JsonArray(), ["hidden"] = new JsonArray(), ["gone"] = new JsonArray(),
        };
        Assert.True(DirectorySnapshots.IsUsable(Good()));
        var badVersion = Good();
        badVersion["ver"] = 1.5;
        var badGone = Good();
        badGone["gone"] = new JsonArray(5);
        var badIndex = Good();
        badIndex["hidden"] = new JsonArray(new JsonObject { ["k"] = 7, ["t"] = "00", ["b"] = "" });
        var badBlob = Good();
        badBlob["hidden"] = new JsonArray(new JsonObject { ["k"] = new string('a', 64), ["t"] = "00", ["b"] = "!!" });
        foreach (var reply in new[] { badVersion, badGone, badIndex, badBlob }) Assert.False(DirectorySnapshots.IsUsable(reply));
    }

    [Fact]
    public async Task CopiesOfADirectoryThatStoppedAnsweringAreDropped()
    {
        var clock = DateTimeOffset.UtcNow;
        var copy = new DirectorySnapshots(() => clock);
        var client = new RoomDirectoryClient(new CountingNetwork(_directory.Port));
        await client.AnnounceAsync(new RoomSigner().Sign(Listing(RoomVisibility.Public), _now), true, [], default);
        await copy.RefreshAsync(client, default);
        Assert.Single(copy.Rooms());
        // The directory goes away: nothing refreshes the copy, and after a while its rooms are no longer shown.
        await _directory.DisposeAsync();
        await Assert.ThrowsAnyAsync<IOException>(() => copy.RefreshAsync(client, default));
        Assert.Single(copy.Rooms());
        clock += DirectorySnapshots.StaleAfter + TimeSpan.FromSeconds(1);
        Assert.Empty(copy.Rooms());
    }

    [Fact]
    public async Task TheSnapshotNeverOutgrowsTheLargestReply()
    {
        Assert.True(new RoomDirectoryServer(IPAddress.Loopback, 0) is var fresh && fresh.SnapshotBudget + 2 * RoomDirectoryProtocol.SnapshotPad <= RoomDirectoryProtocol.MaxReply);
        await fresh.DisposeAsync();
        // A small budget stands in for a full directory: publishes that would pass it are refused, renewals still work.
        _directory.SnapshotBudget = 8 * 1024;
        var client = new RoomDirectoryClient(new CountingNetwork(_directory.Port));
        var signers = new List<RoomSigner>();
        DirectoryRejectedException? refused = null;
        for (var i = 0; i < 100 && refused is null; i++)
        {
            var signer = new RoomSigner();
            try
            {
                await client.AnnounceAsync(signer.Sign(Listing(RoomVisibility.Hidden), _now), false, [RandomNumberGenerator.GetBytes(16), RandomNumberGenerator.GetBytes(16)], default);
                signers.Add(signer);
            }
            catch (DirectoryRejectedException e) { refused = e; }
        }
        Assert.Equal("full", refused?.Code);
        Assert.NotEmpty(signers);
        var full = _directory.Handle(new JsonObject { ["op"] = "snap" });
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(full.ToJsonString()) <= _directory.SnapshotBudget + 512);
        // The rooms already there keep renewing.
        _now += TimeSpan.FromSeconds(30);
        await HideAsync(signers[0], RandomNumberGenerator.GetBytes(16));
    }
}
