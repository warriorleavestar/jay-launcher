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

    // Every test reader comes from loopback; the per-source limit has its own test.
    public DirectorySnapshotTests() => _directory = new RoomDirectoryServer(IPAddress.Loopback, 0, clock: () => _now) { UncountedLoopback = true };

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
        var room = Listing(RoomVisibility.Public);
        Assert.True(JayInvite.TryParse(room.Invite, out var invite));
        var lobby = invite.LobbyKey;
        await new RoomDirectoryClient(net).AnnounceAsync(signer.Sign(room, _now), true, [lobby], default);
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
        Assert.NotEqual(HiddenEntry.Index(lobby), SignedRecord.Lookup(lobby));
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

    /// <summary>
    /// Publishes junk under <paramref name="index"/>'s index, signed with <paramref name="signWith"/>'s entry key: a
    /// stranger signs with a key of their own, someone who holds the invite with the invite's.
    /// </summary>
    private JsonObject Publish(byte[] index, byte[] signWith, RoomListing? signedFor = null, RoomListing? sentWith = null, bool listed = false)
    {
        var listing = signedFor ?? new RoomSigner().Sign(Listing(RoomVisibility.Hidden), _now);
        var junk = RandomNumberGenerator.GetBytes(HiddenEntry.SealedLength);
        return _directory.Handle(new JsonObject
        {
            ["op"] = "announce",
            ["room"] = (sentWith ?? listing).ToJson(),
            ["listed"] = listed,
            ["hidden"] = new JsonArray(new JsonObject
            {
                ["k"] = Convert.ToHexStringLower(HiddenEntry.Index(index)),
                ["b"] = Convert.ToBase64String(junk),
                ["p"] = Convert.ToHexStringLower(HiddenEntry.PublicKey(signWith)),
                ["s"] = Convert.ToHexStringLower(HiddenEntry.SignPublish(signWith, junk, listing.Id, listing.Seq)),
            }),
        });
    }

    private static string? ErrorOf(JsonObject reply) => reply["error"]?.GetValue<string>();

    [Fact]
    public async Task StrangersCannotPublishUnderAnInvitesIndex()
    {
        // Someone who saw the index (say in an old snapshot) but doesn't hold the invite can't sign for it.
        var lobby = RandomNumberGenerator.GetBytes(16);
        Assert.Equal("lookup", ErrorOf(Publish(lobby, RandomNumberGenerator.GetBytes(16))));
        var room = new RoomSigner();
        await HideAsync(room, lobby);
        var reader = new RoomDirectoryClient(new CountingNetwork(_directory.Port));
        Assert.Equal(room.RoomId, (await reader.LookupAsync(lobby, default))!.Id);
        // Only a few rooms can share one index, and only holders of the invite can take those places;
        // once the room is there it keeps its place when it renews.
        for (var i = 1; i < RoomDirectoryServer.MaxEntriesPerIndex; i++) Assert.True(Publish(lobby, lobby)["ok"]!.GetValue<bool>());
        Assert.Equal("lookup", ErrorOf(Publish(lobby, lobby)));
        _now += TimeSpan.FromSeconds(30);
        await HideAsync(room, lobby);
        Assert.Equal(room.RoomId, (await reader.LookupAsync(lobby, default, _host.PublicKey))!.Id);
        // A full index isn't trusted by a reader that has no host to check against.
        Assert.Null(await reader.LookupAsync(lobby, default));
    }

    [Fact]
    public void AHiddenEntrysSignatureOnlyCoversItsOwnRoomAndTime()
    {
        var lobby = RandomNumberGenerator.GetBytes(16);
        var signed = new RoomSigner().Sign(Listing(RoomVisibility.Hidden), _now);
        // Replayed with another room, or with a later version of the same room: refused.
        Assert.Equal("lookup", ErrorOf(Publish(lobby, lobby, signed, new RoomSigner().Sign(Listing(RoomVisibility.Hidden), _now))));
        Assert.Equal("lookup", ErrorOf(Publish(lobby, lobby, signed, new RoomSigner().Sign(Listing(RoomVisibility.Hidden), _now + TimeSpan.FromSeconds(1)))));
        // A listing whose time is far from the directory's: refused with a reason the host can act on.
        var old = new RoomSigner().Sign(Listing(RoomVisibility.Hidden), _now - HiddenEntry.MaxClockSkew - TimeSpan.FromMinutes(1));
        Assert.Equal("clock", ErrorOf(Publish(lobby, lobby, old)));
        Assert.True(Publish(lobby, lobby, signed)["ok"]!.GetValue<bool>());
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
        // When the invite says which host it is for, the entry for that host is the answer.
        var hit = await new RoomDirectoryClient(new CountingNetwork(_directory.Port)).LookupAsync(lobby, default, _host.PublicKey);
        Assert.Equal(_host.PublicKey, hit!.HostKey);
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
        // A room already there still renews while the directory is full, even with a hidden key it didn't have before
        // (as after a new invite).
        _now += TimeSpan.FromSeconds(30);
        Assert.Null(await Record.ExceptionAsync(() => HideAsync(signers[0], RandomNumberGenerator.GetBytes(16))));
    }

    [Fact]
    public async Task AFailedFirstRefreshReachesTheReaderQuickly()
    {
        // Nothing listens on this port: the first poll fails at once, and a reader hears so without waiting for the schedule.
        var closed = new TcpListener(IPAddress.Loopback, 0);
        closed.Start();
        var port = ((IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();
        var copy = new DirectorySnapshots();
        var client = new RoomDirectoryClient(new CountingNetwork(port)) { Snapshots = copy };
        using var poll = copy.StartPolling(() => client, TimeSpan.FromHours(1));
        var started = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<Exception>(() => client.LookupAsync(RandomNumberGenerator.GetBytes(16), default));
        // Far below the hour-long schedule; loose so a slow test machine doesn't fail it.
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(30), started.Elapsed.ToString());
    }

    [Fact]
    public async Task ACopyThatWentStaleIsNotReadyEvenWhilePolling()
    {
        var clock = DateTimeOffset.UtcNow;
        var copy = new DirectorySnapshots(() => clock);
        var client = new RoomDirectoryClient(new CountingNetwork(_directory.Port)) { Snapshots = copy };
        await client.AnnounceAsync(new RoomSigner().Sign(Listing(RoomVisibility.Public), _now), true, [], default);
        using (copy.StartPolling(() => client, TimeSpan.FromHours(1)))
        {
            await copy.ReadyAsync(client, default);
            await _directory.DisposeAsync();
            clock += DirectorySnapshots.StaleAfter + TimeSpan.FromSeconds(1);
            await Assert.ThrowsAnyAsync<Exception>(() => copy.ReadyAsync(client, default));
        }
    }

    [Fact]
    public async Task StoppingThePollerTwiceIsHarmless()
    {
        var copy = new DirectorySnapshots();
        var client = new RoomDirectoryClient(new CountingNetwork(_directory.Port)) { Snapshots = copy };
        var poll = copy.StartPolling(() => client, TimeSpan.FromHours(1));
        await copy.ReadyAsync(client, default);
        poll.Dispose();
        poll.Dispose();
        Assert.False(copy.IsPolling);
        await _directory.DisposeAsync();
        await _directory.DisposeAsync();
    }

    /// <summary>A listing signed with any seq, as a hand-made publisher could send.</summary>
    private RoomListing SignedWithSeq(RoomVisibility mode, long seq)
    {
        var signer = new RoomSigner();
        var listing = signer.Sign(Listing(mode), _now) with { Seq = seq, Signature = [] };
        return listing with { Signature = signer.SignBytes(listing.SignedBytes()) };
    }

    [Fact]
    public void AWrongClockOnlyLeavesOutTheHiddenEntries()
    {
        var lobby = RandomNumberGenerator.GetBytes(16);
        // A public room from a host whose clock is hours off: listed, its invite entry left out, the host told.
        var skewed = new RoomSigner().Sign(Listing(RoomVisibility.Public), _now - TimeSpan.FromHours(3));
        var reply = Publish(lobby, lobby, skewed, null, listed: true);
        Assert.True(reply["ok"]!.GetValue<bool>());
        Assert.Equal("clock", reply["warn"]!.GetValue<string>());
        var snap = _directory.Handle(new JsonObject { ["op"] = "snap" });
        Assert.Single(snap["rooms"]!.AsArray());
        Assert.Empty(snap["hidden"]!.AsArray());
        // Seqs at the very ends of the range are refused cleanly, not with an overflow.
        foreach (var seq in new[] { long.MinValue, long.MinValue + _now.ToUnixTimeMilliseconds(), long.MaxValue, 0L })
            Assert.Equal("clock", ErrorOf(Publish(lobby, lobby, SignedWithSeq(RoomVisibility.Hidden, seq))));
    }

    [Fact]
    public async Task TheReaderLearnsASnapshotsLengthBeforeReadingIt()
    {
        // The reader resets its deadline from the length, using the directory's own rule.
        await HideAsync(new RoomSigner(), RandomNumberGenerator.GetBytes(16));
        var lengths = new List<int>();
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _directory.Port);
        await using (var stream = client.GetStream())
        {
            var trust = new DirectoryTrust { Network = "", Pinned = [] };
            await DirectoryChannel.CallAsync(stream, trust, "", "", new JsonObject { ["op"] = "snap", ["jay"] = "9.9.9" }, default, lengths.Add);
        }
        Assert.Equal(RoomDirectoryProtocol.SnapshotPad, Assert.Single(lengths));
        // A full-size snapshot at the slowest rate the directory accepts fits the reader's wait.
        Assert.True(RoomDirectoryProtocol.SnapshotDeadline(RoomDirectoryProtocol.MaxReply) > RoomDirectoryProtocol.SnapshotTimeout);
        Assert.Equal(RoomDirectoryProtocol.RequestTimeout + TimeSpan.FromSeconds(1), RoomDirectoryProtocol.SnapshotDeadline(RoomDirectoryServer.MinSnapshotRate));
    }

    [Fact]
    public void RetriesAreSpreadOut()
    {
        var waits = Enumerable.Range(0, 200).Select(_ => DirectorySnapshots.Jitter(TimeSpan.FromSeconds(10))).ToList();
        Assert.All(waits, w => Assert.InRange(w, TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(12)));
        Assert.True(waits.Distinct().Count() > 100);
    }

    [Fact]
    public async Task AFullIndexDoesNotKeepARoomOffTheDirectory()
    {
        // The invite of a public room is in the open, so anyone can sign entries for its index and fill it.
        var lobby = RandomNumberGenerator.GetBytes(16);
        for (var i = 0; i < RoomDirectoryServer.MaxEntriesPerIndex; i++) Assert.True(Publish(lobby, lobby)["ok"]!.GetValue<bool>());
        var host = new RoomSigner();
        var client = new RoomDirectoryClient(new CountingNetwork(_directory.Port));
        // The host's publish and renewals still go through; only its hidden entry is left out.
        await client.AnnounceAsync(host.Sign(Listing(RoomVisibility.Public), _now), true, [lobby], default);
        _now += TimeSpan.FromSeconds(30);
        await client.AnnounceAsync(host.Sign(Listing(RoomVisibility.Public), _now), true, [lobby], default);
        Assert.Contains(await client.ListAsync(default), r => r.Id == host.RoomId);
        var renewal = host.Sign(Listing(RoomVisibility.Public), _now + TimeSpan.FromSeconds(1));
        var reply = _directory.Handle(new JsonObject
        {
            ["op"] = "announce", ["room"] = renewal.ToJson(), ["listed"] = true, ["hidden"] = new JsonArray(HiddenItem(lobby, renewal)),
        });
        Assert.True(reply["ok"]!.GetValue<bool>());
        Assert.Equal("lookup", reply["warn"]!.GetValue<string>());
        // A hidden room with nothing else to register is refused, so it knows.
        Assert.Equal("lookup", ErrorOf(Publish(lobby, lobby)));
    }

    private static JsonObject HiddenItem(byte[] lobby, RoomListing signed)
    {
        var entry = HiddenEntry.Seal(lobby, new DirectoryHit(signed.Id, signed.Ip, signed.HostKey));
        return new JsonObject
        {
            ["k"] = Convert.ToHexStringLower(HiddenEntry.Index(lobby)),
            ["b"] = Convert.ToBase64String(entry),
            ["p"] = Convert.ToHexStringLower(HiddenEntry.PublicKey(lobby)),
            ["s"] = Convert.ToHexStringLower(HiddenEntry.SignPublish(lobby, entry, signed.Id, signed.Seq)),
        };
    }

    [Fact]
    public async Task AnInviteForAListedRoomIsFoundFromTheListing()
    {
        var standing = new InviteBook().CreateStanding(false, TunnelSettings.Default).Invite;
        var room = new RoomSigner();
        var client = new RoomDirectoryClient(new CountingNetwork(_directory.Port));
        // Announced without any hidden entry: the invite in its listing is enough to find it.
        await client.AnnounceAsync(room.Sign(Listing(RoomVisibility.Public) with { Invite = standing.ToString() }, _now), true, [], default);
        Assert.Equal(room.RoomId, (await client.LookupAsync(standing.LobbyKey, default))!.Id);
        // Someone else lists the same invite with their own host: without the host's key nothing is trusted, with it
        // the right room is found.
        var other = new RoomSigner().Sign(Listing(RoomVisibility.Public) with { Invite = standing.ToString(), HostKey = X25519KeyPair.Generate().PublicKey }, _now);
        await client.AnnounceAsync(other, true, [], default);
        await client.Snapshots.RefreshAsync(client, default);
        Assert.Null(await client.LookupAsync(standing.LobbyKey, default));
        Assert.Equal(room.RoomId, (await client.LookupAsync(standing.LobbyKey, default, _host.PublicKey))!.Id);
    }

    private static readonly System.Text.Json.JsonSerializerOptions Raw = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private async Task<JsonObject> SnapOverChannelAsync(JsonObject request)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _directory.Port);
        await using var stream = client.GetStream();
        request["jay"] = "9.9.9";
        return await DirectoryChannel.CallAsync(stream, new DirectoryTrust { Network = "" }, "", "", request, default, snapshotLength: _ => { });
    }

    /// <summary>Fills the directory with public rooms until a full snapshot is a large reply.</summary>
    private void FillPastSmall()
    {
        var bytes = 0;
        while (bytes < RoomDirectoryServer.SmallSnapshot * 3 / 2)
        {
            var listing = new RoomSigner().Sign(Listing(RoomVisibility.Public) with { Name = new string('x', 40) }, _now);
            Assert.True(_directory.Handle(new JsonObject { ["op"] = "announce", ["room"] = listing.ToJson(), ["listed"] = true })["ok"]!.GetValue<bool>());
            bytes += listing.ToJson().ToJsonString().Length;
        }
    }

    [Fact]
    public async Task ManyDifferentVersionsCostNoMoreThanTheFullSnapshot()
    {
        FillPastSmall();
        var first = _directory.Handle(new JsonObject { ["op"] = "snap" });
        var epoch = first["epoch"]!.GetValue<string>();
        var version = first["ver"]!.GetValue<long>();
        var built = _directory.SnapshotsBuilt;
        // Asking from old versions all over the range (within the large-reply bucket): deltas nearly as big as
        // everything are answered with the one cached full reply.
        var step = Math.Max(1, version / (RoomDirectoryServer.LargeSnapshotBurst - 4));
        for (var have = 0L; have < version; have += step)
        {
            var reply = await SnapOverChannelAsync(new JsonObject { ["op"] = "snap", ["epoch"] = epoch, ["have"] = have });
            Assert.True(reply["ok"]!.GetValue<bool>());
            if (reply["full"]!.GetValue<bool>() == false)
                Assert.True(System.Text.Encoding.UTF8.GetByteCount(reply.ToJsonString(Raw)) <= RoomDirectoryServer.SmallSnapshot + 1024);
        }
        Assert.True(_directory.CachedDeltaBytes <= RoomDirectoryServer.MaxCachedDeltaBytes);
        // Only small deltas were serialized one by one; the big answers all came from the cache.
        Assert.True(_directory.SnapshotsBuilt - built <= RoomDirectoryServer.LargeSnapshotBurst);
        var recent = await SnapOverChannelAsync(new JsonObject { ["op"] = "snap", ["epoch"] = epoch, ["have"] = version - 1 });
        Assert.False(recent["full"]!.GetValue<bool>());
    }

    [Fact]
    public async Task NothingBigIsSerializedForAReaderTurnedAway()
    {
        FillPastSmall();
        var slots = _directory.LargeSnapshotSlots;
        for (var i = 0; i < RoomDirectoryServer.MaxConcurrentSnapshots; i++) Assert.True(slots.Wait(0));
        try
        {
            var built = _directory.SnapshotsBuilt;
            var reply = await SnapOverChannelAsync(new JsonObject { ["op"] = "snap" });
            Assert.Equal("busy", reply["error"]!.GetValue<string>());
            Assert.Equal(built, _directory.SnapshotsBuilt);
        }
        finally { slots.Release(RoomDirectoryServer.MaxConcurrentSnapshots); }
        Assert.True((await SnapOverChannelAsync(new JsonObject { ["op"] = "snap" }))["full"]!.GetValue<bool>());
    }

    [Fact]
    public async Task AnotherAddressForTheSameHostKeyIsAConflict()
    {
        var client = new RoomDirectoryClient(new CountingNetwork(_directory.Port));
        // A public room, and someone listing its invite with the real (public) host key but their own address.
        var standing = new InviteBook().CreateStanding(false, TunnelSettings.Default).Invite;
        var real = new RoomSigner();
        await client.AnnounceAsync(real.Sign(Listing(RoomVisibility.Public) with { Invite = standing.ToString() }, _now), true, [], default);
        await client.AnnounceAsync(new RoomSigner().Sign(Listing(RoomVisibility.Public) with { Invite = standing.ToString(), Ip = "10.235.0.66" }, _now), true, [], default);
        Assert.Null(await client.LookupAsync(standing.LobbyKey, default));
        Assert.Empty(await client.LookupAllAsync(standing.LobbyKey, default));
        // Knowing the host key doesn't pick one: both addresses come back, for the knock handshake to tell apart.
        var both = await client.LookupAllAsync(standing.LobbyKey, default, _host.PublicKey);
        Assert.Equal(["10.235.0.66", "10.235.0.7"], both.Select(h => h.Ip).Order());

        // A hidden room, and someone who once held one of its invites publishing an entry for its host at another address.
        var lobby = RandomNumberGenerator.GetBytes(16);
        await HideAsync(new RoomSigner(), lobby);
        Assert.Single(await client.LookupAllAsync(lobby, default));
        var other = new RoomSigner().Sign(Listing(RoomVisibility.Hidden) with { Ip = "10.235.0.66" }, _now);
        await client.AnnounceAsync(other, false, [lobby], default);
        await client.Snapshots.RefreshAsync(client, default);
        Assert.Null(await client.LookupAsync(lobby, default));
        Assert.Equal(["10.235.0.66", "10.235.0.7"], (await client.LookupAllAsync(lobby, default, _host.PublicKey)).Select(h => h.Ip).Order());
    }
    [Fact]
    public async Task ForgedListingsDoNotCrowdOutTheHiddenEntry()
    {
        var client = new RoomDirectoryClient(new CountingNetwork(_directory.Port));
        // A hidden room, and someone holding its invite listing it in the open under the real (public) host key at
        // more addresses than are ever tried.
        var invite = new InviteBook().CreateStanding(false, TunnelSettings.Default).Invite;
        for (var i = 0; i < DirectorySnapshots.MaxAddressesToTry + 2; i++)
            await client.AnnounceAsync(new RoomSigner().Sign(Listing(RoomVisibility.Public) with { Invite = invite.ToString(), Ip = $"10.235.0.{60 + i}" }, _now), true, [], default);
        await HideAsync(new RoomSigner(), invite.LobbyKey);
        await client.Snapshots.RefreshAsync(client, default);
        var hits = await client.LookupAllAsync(invite.LobbyKey, default, _host.PublicKey);
        // The host's own entry is tried first; a few of the listed addresses fill the rest, never more in all.
        Assert.Equal("10.235.0.7", hits[0].Ip);
        Assert.Equal(DirectorySnapshots.MaxAddressesToTry, hits.Count);
        Assert.Equal(hits.Count, hits.DistinctBy(h => h.Ip).Count());
    }

    public enum Trap { Refuse, HangConnect, SilentHandshake }

    /// <summary>
    /// Directory requests go to the test directory; knocks are counted and refused, never connected, or connected to
    /// something that never says a word.
    /// </summary>
    private sealed class KnockTrap(int directoryPort, Trap trap) : IPublicNetwork
    {
        public readonly ConcurrentBag<string> Knocked = [];

        public async Task<Stream> ConnectAsync(string ip, int port, CancellationToken ct)
        {
            if (port == KnockProtocol.Port)
            {
                Knocked.Add(ip);
                if (trap == Trap.SilentHandshake) return new Silent();
                if (trap == Trap.HangConnect) await Task.Delay(Timeout.Infinite, ct);
                throw new SocketException((int)SocketError.ConnectionRefused);
            }
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, directoryPort, ct);
            return client.GetStream();
        }

        public Task<string?> MyIpAsync(CancellationToken ct) => Task.FromResult<string?>("10.235.0.9");
    }

    /// <summary>Takes whatever is written and never answers.</summary>
    private sealed class Silent : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) { }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => ValueTask.CompletedTask;
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => Task.CompletedTask;
    }

    [Theory]
    [InlineData(Trap.Refuse)]
    [InlineData(Trap.HangConnect)]
    [InlineData(Trap.SilentHandshake)]
    public async Task HiddenEntriesForgedForTheHostCostABoundedNumberOfKnocks(Trap trap)
    {
        // Someone holding the invite fills its index with entries under the host's (public) key, each at another address.
        var invite = new InviteBook().CreateStanding(false, TunnelSettings.Default).Invite;
        var writer = new RoomDirectoryClient(new CountingNetwork(_directory.Port));
        for (var i = 0; i < RoomDirectoryServer.MaxEntriesPerIndex; i++)
            await writer.AnnounceAsync(new RoomSigner().Sign(Listing(RoomVisibility.Hidden) with { Ip = $"10.235.1.{10 + i}" }, _now), false, [invite.LobbyKey], default);
        var network = new KnockTrap(_directory.Port, trap);
        var client = new RoomDirectoryClient(network);
        Assert.Equal(DirectorySnapshots.MaxAddressesToTry, (await client.LookupAllAsync(invite.LobbyKey, default, _host.PublicKey)).Count);
        var budget = TimeSpan.FromSeconds(1);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var outcome = await PublicJoin.TryKnockByInviteAsync(network, invite, X25519KeyPair.Generate(), "Steve", "0.8.0", default, client: client,
            expectedHost: _host.PublicKey, budget: budget);
        // Nobody answered: the caller goes on to the invite's lobby, having knocked at a few addresses at most, quickly.
        Assert.Null(outcome);
        Assert.InRange(network.Knocked.Count, 1, DirectorySnapshots.MaxAddressesToTry);
        Assert.Equal(network.Knocked.Count, network.Knocked.Distinct().Count());
        if (trap == Trap.Refuse) Assert.Equal(DirectorySnapshots.MaxAddressesToTry, network.Knocked.Count);
        // The budget covers the handshake too, not only connecting: well under one handshake timeout.
        Assert.True(watch.Elapsed < budget + TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
    }
    [Fact]
    public async Task AnEarlierGuestIsNotTheAnswerForLaterInvites()
    {
        var client = new RoomDirectoryClient(new CountingNetwork(_directory.Port));
        var book = new InviteBook();
        var first = book.Create(1, null, false, TunnelSettings.Default).Invite;
        // The host publishes its entry for as long as the room's lobby key lasts.
        await HideAsync(new RoomSigner(), book.CurrentLobbyKey()!);
        // Whoever got the first invite publishes an entry of their own under its key, pointing at themselves.
        var guest = X25519KeyPair.Generate();
        await client.AnnounceAsync(new RoomSigner().Sign(Listing(RoomVisibility.Hidden) with { HostKey = guest.PublicKey, Ip = "10.235.0.66" }, _now),
            false, [first.LobbyKey], default);
        var later = book.Create(1, null, false, TunnelSettings.Default).Invite;
        Assert.Equal(first.LobbyKey, later.LobbyKey);
        await client.Snapshots.RefreshAsync(client, default);
        // Without the host to check against, the two entries clash and give nothing (the joiner falls back to the lobby).
        Assert.Empty(await client.LookupAllAsync(later.LobbyKey, default));
        // With it, only the host's address is tried.
        var hits = await client.LookupAllAsync(later.LobbyKey, default, _host.PublicKey);
        Assert.Equal(["10.235.0.7"], hits.Select(h => h.Ip));
    }

    [Fact]
    public async Task AFullIndexIsNotTrustedWithoutTheHost()
    {
        var client = new RoomDirectoryClient(new CountingNetwork(_directory.Port));
        var lobby = RandomNumberGenerator.GetBytes(16);
        // Someone holding the invite fills its index before the host gets there, one entry of it readable.
        for (var i = 0; i < RoomDirectoryServer.MaxEntriesPerIndex - 1; i++) Assert.True(Publish(lobby, lobby)["ok"]!.GetValue<bool>());
        var guest = X25519KeyPair.Generate();
        await client.AnnounceAsync(new RoomSigner().Sign(Listing(RoomVisibility.Hidden) with { HostKey = guest.PublicKey, Ip = "10.235.0.66" }, _now),
            false, [lobby], default);
        // The host's entry is turned away.
        await Assert.ThrowsAsync<DirectoryRejectedException>(() => HideAsync(new RoomSigner(), lobby));
        await client.Snapshots.RefreshAsync(client, default);
        Assert.Empty(await client.LookupAllAsync(lobby, default));
    }

    [Fact]
    public async Task ListedRoomsPublishTheirEntryToo()
    {
        var client = new RoomDirectoryClient(new CountingNetwork(_directory.Port));
        var invite = new InviteBook().CreateStanding(false, TunnelSettings.Default).Invite;
        await client.AnnounceAsync(new RoomSigner().Sign(Listing(RoomVisibility.Public) with { Invite = invite.ToString() }, _now), true, [invite.LobbyKey], default);
        var snapshot = _directory.Handle(new JsonObject { ["op"] = "snap" });
        Assert.NotEmpty(snapshot["hidden"]!.AsArray());
        await client.Snapshots.RefreshAsync(client, default);
        // The entry and the listing give the same place, so they agree.
        Assert.Equal("10.235.0.7", Assert.Single(await client.LookupAllAsync(invite.LobbyKey, default)).Ip);
    }

    private sealed class TwoDirectories(int directory, int silent) : IPublicNetwork
    {
        public async Task<Stream> ConnectAsync(string ip, int p, CancellationToken ct)
        {
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, ip == "10.235.0.3" ? silent : directory, ct);
            return client.GetStream();
        }

        public Task<string?> MyIpAsync(CancellationToken ct) => Task.FromResult<string?>("10.235.0.7");
    }

    [Fact]
    public async Task ADirectoryThatNeverAnswersDoesNotHoldReadersUp()
    {
        var lobby = RandomNumberGenerator.GetBytes(16);
        await HideAsync(new RoomSigner(), lobby);
        // The second directory takes the connection and never says a word.
        var silent = new TcpListener(IPAddress.Loopback, 0);
        silent.Start();
        var held = new ConcurrentBag<TcpClient>();
        using var stop = new CancellationTokenSource();
        var accepting = Task.Run(async () =>
        {
            try { while (true) held.Add(await silent.AcceptTcpClientAsync(stop.Token)); }
            catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException) { }
        });
        try
        {
            var client = new RoomDirectoryClient(new TwoDirectories(_directory.Port, ((IPEndPoint)silent.LocalEndpoint).Port), ["10.235.0.2", "10.235.0.3"]);
            using var poll = client.Snapshots.StartPolling(() => client, TimeSpan.FromMinutes(5));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            // The reader goes on once the directory that answers has given its copy.
            Assert.Single(await client.LookupAllAsync(lobby, default));
            // ...while the silent one's connection is still open: the reader didn't wait for that directory to give up.
            Assert.All(held, c => Assert.False(c.Client.Poll(0, SelectMode.SelectRead) && c.Available == 0, "the reader waited for the silent directory"));
            Assert.True(watch.Elapsed < RoomDirectoryProtocol.SnapshotTimeout, $"took {watch.Elapsed}");
        }
        finally
        {
            stop.Cancel();
            silent.Stop();
            await accepting;
            foreach (var c in held) c.Dispose();
        }
    }

    [Fact]
    public async Task ARoomOpenedJustNowIsFoundAtTheNextRefresh()
    {
        var client = new RoomDirectoryClient(new CountingNetwork(_directory.Port));
        // The schedule is far off: the test makes the "next scheduled refresh" happen itself, so timing can't decide it.
        using var poll = client.Snapshots.StartPolling(() => client, TimeSpan.FromMinutes(10));
        await client.Snapshots.ReadyAsync(client, default);
        // The room is announced after the reader's copy was taken: the lookup waits for the next refresh.
        var lobby = RandomNumberGenerator.GetBytes(16);
        await HideAsync(new RoomSigner(), lobby);
        var searching = 0;
        // A first look answers at once from the copy as it is (the caller tries the invite's own lobby next).
        var lookedAt = client.Snapshots.RefreshedAt!.Value + TimeSpan.FromTicks(1);
        Assert.Empty(await client.LookupAllAsync(lobby, default, _host.PublicKey, waiting: () => Interlocked.Increment(ref searching)));
        Assert.Equal(0, searching);
        // A second try asks for a copy refreshed since: it waits for the next refresh and finds the room.
        var second = client.LookupAllAsync(lobby, default, _host.PublicKey, lookedAt, () => Interlocked.Increment(ref searching));
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (!second.IsCompleted && DateTime.UtcNow < deadline)
        {
            _now += TimeSpan.FromSeconds(1);
            if (Volatile.Read(ref searching) == 1) await client.Snapshots.RefreshAsync(client, default);
            await Task.WhenAny(second, Task.Delay(20));
        }
        var hits = await second;
        Assert.Equal("10.235.0.7", Assert.Single(hits).Ip);
        Assert.Equal(1, searching);
        // A copy refreshed since then is used as it is.
        Assert.Single(await client.LookupAllAsync(lobby, default, _host.PublicKey, lookedAt, () => Interlocked.Increment(ref searching)));
        Assert.Equal(1, searching);
    }

    [Fact]
    public async Task NonStringFieldsGetAnAnswerNotACrash()
    {
        _directory.MinClientVersion = new Version(0, 8, 0);
        async Task<JsonObject> CallAsync(JsonObject request)
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, _directory.Port);
            await using var stream = tcp.GetStream();
            return await DirectoryChannel.CallAsync(stream, new DirectoryTrust { Network = "" }, "", "", request, default, snapshotLength: _ => { });
        }
        Assert.Equal("version", (await CallAsync(new JsonObject { ["op"] = "snap", ["jay"] = 5 }))["error"]!.GetValue<string>());
        Assert.Equal("bad", (await CallAsync(new JsonObject { ["op"] = 5, ["jay"] = "9.9.9" }))["error"]!.GetValue<string>());
        Assert.True((await SnapOverChannelAsync(new JsonObject { ["op"] = "snap" }))["ok"]!.GetValue<bool>());
    }
    [Fact]
    public async Task LoopbackReadersCountPerSourceByDefault()
    {
        _directory.UncountedLoopback = false;
        for (var i = 0; i < RoomDirectoryServer.MaxSnapshotsPerSourcePerMinute; i++)
            Assert.True((await SnapOverChannelAsync(new JsonObject { ["op"] = "snap" }))["ok"]!.GetValue<bool>());
        Assert.Equal("rate", (await SnapOverChannelAsync(new JsonObject { ["op"] = "snap" }))["error"]!.GetValue<string>());
    }
    [Fact]
    public async Task DeltasForAnotherCopyDropIt()
    {
        var client = new RoomDirectoryClient(new CountingNetwork(_directory.Port));
        await client.Snapshots.RefreshAsync(client, default);
        await HideAsync(new RoomSigner(), RandomNumberGenerator.GetBytes(16));
        // A reply that is a delta from some other version can't be applied: the copy goes, and the next round asks
        // for the full snapshot.
        var ip = client.Snapshots.CopiesForTests().Single();
        var stray = _directory.Handle(new JsonObject { ["op"] = "snap" });
        stray["full"] = false;
        stray["from"] = stray["ver"]!.GetValue<long>() + 5;
        Assert.False(client.Snapshots.ApplyForTests(ip, stray));
        Assert.Empty(client.Snapshots.CopiesForTests());
    }

    [Fact]
    public async Task ACancelledFirstRefreshDoesNotKeepReadersWaiting()
    {
        var client = new RoomDirectoryClient(new StallingNetwork());
        var poll = client.Snapshots.StartPolling(() => client, TimeSpan.FromHours(1));
        var read = client.Snapshots.ReadyAsync(client, default);
        await Task.Delay(200);
        Assert.False(read.IsCompleted);
        // Stopping the poller cancels its first round: the reader gets an answer now, not after the long backstop.
        poll.Dispose();
        // Well under the backstop (over two minutes); loose so a slow test machine doesn't fail it.
        Assert.Same(read, await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(30))));
        await Assert.ThrowsAnyAsync<IOException>(() => read);
    }

    /// <summary>Connects, then never answers.</summary>
    private sealed class StallingNetwork : IPublicNetwork
    {
        public async Task<Stream> ConnectAsync(string ip, int port, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new OperationCanceledException(ct);
        }

        public Task<string?> MyIpAsync(CancellationToken ct) => Task.FromResult<string?>("10.235.0.9");
    }

    [Fact]
    public async Task BehindAForwardBigSnapshotsHaveAByteBudget()
    {
        // Behind the forward every reader is loopback and per-source counting is off: large replies still stop once
        // their byte budget is spent, and a small delta still gets through.
        _directory.UncountedLoopback = true;
        _directory.LargeSnapshotBytesPerSecond = 0;
        FillPastSmall();
        var first = await SnapOverChannelAsync(new JsonObject { ["op"] = "snap" });
        Assert.True(first["full"]!.GetValue<bool>());
        var size = System.Text.Encoding.UTF8.GetByteCount(first.ToJsonString(Raw));
        // Time moves on so starts are never short; only the bytes run out (they don't refill here).
        var served = 1;
        string? error = null;
        while (error is null && served < 200)
        {
            _now += TimeSpan.FromSeconds(1);
            var reply = await SnapOverChannelAsync(new JsonObject { ["op"] = "snap" });
            error = reply["error"]?.GetValue<string>();
            if (error is null) served++;
        }
        Assert.Equal("busy", error);
        // Each is charged what is sent, which is padded.
        var charged = RoomDirectoryProtocol.Padded(size);
        Assert.InRange(served * charged, _directory.LargeSnapshotByteBurst - 2L * charged, _directory.LargeSnapshotByteBurst + 2L * charged);
        var delta = await SnapOverChannelAsync(new JsonObject { ["op"] = "snap", ["epoch"] = first["epoch"]!.DeepClone(), ["have"] = first["ver"]!.DeepClone() });
        Assert.False(delta["full"]!.GetValue<bool>());
    }

    [Fact]
    public async Task AClockThatGoesBackDoesNotEmptyTheBuckets()
    {
        _directory.UncountedLoopback = true;
        FillPastSmall();
        Assert.True((await SnapOverChannelAsync(new JsonObject { ["op"] = "snap" }))["full"]!.GetValue<bool>());
        // The server's clock goes back an hour: big snapshots are served at once, not after the hour has passed again.
        _now -= TimeSpan.FromHours(1);
        Assert.Null((await SnapOverChannelAsync(new JsonObject { ["op"] = "snap" }))["error"]);
        _now += TimeSpan.FromSeconds(1);
        Assert.Null((await SnapOverChannelAsync(new JsonObject { ["op"] = "snap" }))["error"]);
    }

    [Fact]
    public async Task AFullIndexOnlyWarnsTheHostOfAHiddenRoom()
    {
        var lobby = RandomNumberGenerator.GetBytes(16);
        for (var i = 0; i < RoomDirectoryServer.MaxEntriesPerIndex; i++) Assert.True(Publish(lobby, lobby)["ok"]!.GetValue<bool>());
        var net = new CountingNetwork(_directory.Port);
        // A public room carries its invite in its listing: invites still find it, so there is nothing to warn about.
        await using (var listed = new RoomAnnouncer(net, () => Listing(RoomVisibility.Public), () => [lobby], every: TimeSpan.FromHours(1)))
        {
            await listed.AnnounceOnceAsync(default);
            Assert.True(listed.Published);
            Assert.Null(listed.Warning);
        }
        // A hidden room found by its friend code only: invites can't find it, and the host is told.
        var search = RandomNumberGenerator.GetBytes(32);
        await using var hidden = new RoomAnnouncer(net, () => Listing(RoomVisibility.Hidden), () => [lobby], every: TimeSpan.FromHours(1), searchKey: () => search);
        await hidden.AnnounceOnceAsync(default);
        Assert.True(hidden.Published);
        Assert.Equal(AnnounceWarning.Lookup, hidden.Warning);
    }
}
