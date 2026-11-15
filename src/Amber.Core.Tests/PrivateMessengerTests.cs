using System.Security.Cryptography;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Tests;

/// <summary>成员之间的端到端私聊和传文件：加密格式、房主转交（名单、按指纹转、盖发方）、传文件的确认 / 拒绝 / 校验。</summary>
public sealed class PrivateMessengerTests : IDisposable
{
    private readonly X25519KeyPair _host = X25519KeyPair.Generate();
    private readonly X25519KeyPair _alice = X25519KeyPair.Generate();
    private readonly X25519KeyPair _bob = X25519KeyPair.Generate();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jay-pm-" + Guid.NewGuid().ToString("N"));

    private static string Fp(X25519KeyPair key) => MemberIdentity.FingerprintOf(key.PublicKey);

    [Fact]
    public void SealOpensOnlyForTheRecipientFromTheRealSender()
    {
        var plain = "只给 Bob 看"u8.ToArray();
        var sealedData = PrivateSeal.Seal(_alice, _bob.PublicKey, plain);
        Assert.Equal(plain, PrivateSeal.Open(_bob, _alice.PublicKey, sealedData));
        // 第三个人（包括房主）解不开；说成别的发方也解不开。
        Assert.Null(PrivateSeal.Open(_host, _alice.PublicKey, sealedData));
        Assert.Null(PrivateSeal.Open(_bob, _host.PublicKey, sealedData));
        // 改一个字节就不认。
        sealedData[^1] ^= 1;
        Assert.Null(PrivateSeal.Open(_bob, _alice.PublicKey, sealedData));
        // 同样的内容每次加密都不一样。
        Assert.NotEqual(PrivateSeal.Seal(_alice, _bob.PublicKey, plain), PrivateSeal.Seal(_alice, _bob.PublicKey, plain));
    }

    [Theory]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("C:\\Windows\\evil.exe", "evil.exe")]
    [InlineData("", "file")]
    [InlineData("..", "file")]
    [InlineData("CON.txt", "_CON.txt")]
    [InlineData("a<b>c?.png", "abc.png")]
    [InlineData("photo\u202Egnp.exe", "photognp.exe")]
    [InlineData("in\u200Bvoice\u2066.pdf\u2069.exe", "invoice.pdf.exe")]
    public void FileNamesFromOthersAreMadeSafe(string name, string expected) => Assert.Equal(expected, PrivateMessenger.SafeName(name));

    /// <summary>A link that delivers by fingerprint, for tests with more than two people.</summary>
    private sealed class RoutedLink(string me, byte[] myKey, string name, Dictionary<string, RoutedLink> all) : IPrivateLink
    {
        public event Action<PrivateFrame>? PrivateReceived;

        public Task<bool> SendPrivateAsync(string to, byte[] sealedData, CancellationToken ct)
        {
            if (!all.TryGetValue(to, out var peer)) return Task.FromResult(false);
            peer.PrivateReceived?.Invoke(new PrivateFrame(me, myKey, name, sealedData));
            return Task.FromResult(true);
        }
    }

    /// <summary>One member's unanswered offers take at most their own share of places; another member's offer still arrives, and over-cap offers are declined.</summary>
    [Fact]
    public async Task OneSendersOffersDoNotBlockOthers()
    {
        using var carol = X25519KeyPair.Generate();
        var all = new Dictionary<string, RoutedLink>();
        foreach (var (key, name) in new[] { (_alice, "Alice"), (_bob, "Bob"), (carol, "Carol") }) all[Fp(key)] = new RoutedLink(Fp(key), key.PublicKey, name, all);
        var keys = new Dictionary<string, byte[]> { [Fp(_alice)] = _alice.PublicKey, [Fp(_bob)] = _bob.PublicKey, [Fp(carol)] = carol.PublicKey };
        await using var bob = new PrivateMessenger(all[Fp(_bob)], _bob, f => keys.GetValueOrDefault(f), Path.Combine(_dir, "bob"));
        var offered = new List<FileOffer>();
        bob.FileOffered += o => { lock (offered) offered.Add(o); };
        var declined = new List<string>();
        all[Fp(_alice)].PrivateReceived += frame =>
        {
            if (System.Text.Json.Nodes.JsonNode.Parse(PrivateSeal.Open(_alice, frame.FromPublicKey, frame.Sealed)!) is { } body && body["t"]?.GetValue<string>() == "decline")
                lock (declined) declined.Add(body["id"]!.GetValue<string>());
        };
        async Task Offer(X25519KeyPair from, string id)
        {
            var body = new System.Text.Json.Nodes.JsonObject { ["t"] = "offer", ["id"] = id, ["name"] = "a.txt", ["size"] = 10, ["sha"] = new string('a', 64) };
            await all[Fp(from)].SendPrivateAsync(Fp(_bob), PrivateSeal.Seal(from, _bob.PublicKey, System.Text.Encoding.UTF8.GetBytes(body.ToJsonString())), default);
        }

        var ids = Enumerable.Range(0, PrivateMessenger.MaxIncomingPerSender + 1).Select(_ => PrivateMessenger.NewTransferId()).ToList();
        foreach (var id in ids) await Offer(_alice, id);
        var fromCarol = PrivateMessenger.NewTransferId();
        await Offer(carol, fromCarol);

        Assert.True(await WaitUntil(() => { lock (offered) return offered.Count == PrivateMessenger.MaxIncomingPerSender + 1; }));
        lock (offered) Assert.Contains(offered, o => o.Id == fromCarol);
        Assert.True(await WaitUntil(() => { lock (declined) return declined.Count == 1; }));
        Assert.Equal(ids[^1], declined[0]);
    }

    /// <summary>The relaying host delivering the same sealed message again, or an old message later, shows nothing new.</summary>
    [Fact]
    public async Task ReplayedPrivateMessagesAreDropped()
    {
        var all = new Dictionary<string, RoutedLink>();
        foreach (var (key, name) in new[] { (_alice, "Alice"), (_bob, "Bob") }) all[Fp(key)] = new RoutedLink(Fp(key), key.PublicKey, name, all);
        var keys = new Dictionary<string, byte[]> { [Fp(_alice)] = _alice.PublicKey, [Fp(_bob)] = _bob.PublicKey };
        await using var bob = new PrivateMessenger(all[Fp(_bob)], _bob, f => keys.GetValueOrDefault(f), Path.Combine(_dir, "bob"));
        var got = new List<string>();
        bob.MessageReceived += m => { lock (got) got.Add(m.Text); };
        byte[] Sealed(string text, DateTimeOffset at) => PrivateSeal.Seal(_alice, _bob.PublicKey, System.Text.Encoding.UTF8.GetBytes(
            new System.Text.Json.Nodes.JsonObject { ["t"] = "msg", ["text"] = text, ["ts"] = at.ToUnixTimeMilliseconds() }.ToJsonString()));
        var now = DateTimeOffset.UtcNow;
        var first = Sealed("old news", now);
        await all[Fp(_alice)].SendPrivateAsync(Fp(_bob), first, default);
        await all[Fp(_alice)].SendPrivateAsync(Fp(_bob), first, default);
        await all[Fp(_alice)].SendPrivateAsync(Fp(_bob), Sealed("later", now + TimeSpan.FromMinutes(30)), default);
        // Sealed long ago (a new frame, so not caught by the frame memory) and delivered now.
        await all[Fp(_alice)].SendPrivateAsync(Fp(_bob), Sealed("from before", now), default);
        await all[Fp(_alice)].SendPrivateAsync(Fp(_bob), Sealed("a bit out of order", now + TimeSpan.FromMinutes(29)), default);
        Assert.True(await WaitUntil(() => { lock (got) return got.Count >= 3; }));
        await Task.Delay(200);
        lock (got) Assert.Equal(["old news", "later", "a bit out of order"], got);
    }

    /// <summary>
    /// The host's queue towards one person is small and drops frames when full, and nothing is resent: two files to the same
    /// person together keep no more than one window of chunks in flight.
    /// </summary>
    [Fact]
    public async Task TransfersToOnePersonShareOneWindow()
    {
        var inFlight = 0;
        var most = 0;
        var all = new Dictionary<string, RoutedLink>();
        foreach (var (key, name) in new[] { (_alice, "Alice"), (_bob, "Bob") }) all[Fp(key)] = new RoutedLink(Fp(key), key.PublicKey, name, all);
        var keys = new Dictionary<string, byte[]> { [Fp(_alice)] = _alice.PublicKey, [Fp(_bob)] = _bob.PublicKey };
        // Watch what goes over the link: chunks towards Bob, acks back to Alice.
        all[Fp(_bob)].PrivateReceived += frame =>
        {
            if (System.Text.Json.Nodes.JsonNode.Parse(PrivateSeal.Open(_bob, frame.FromPublicKey, frame.Sealed)!)?["t"]?.GetValue<string>() == "chunk")
                Volatile.Write(ref most, Math.Max(Volatile.Read(ref most), Interlocked.Increment(ref inFlight)));
        };
        all[Fp(_alice)].PrivateReceived += frame =>
        {
            if (System.Text.Json.Nodes.JsonNode.Parse(PrivateSeal.Open(_alice, frame.FromPublicKey, frame.Sealed)!)?["t"]?.GetValue<string>() == "ack")
                Interlocked.Decrement(ref inFlight);
        };
        await using var alice = new PrivateMessenger(all[Fp(_alice)], _alice, f => keys.GetValueOrDefault(f), Path.Combine(_dir, "alice"));
        await using var bob = new PrivateMessenger(all[Fp(_bob)], _bob, f => keys.GetValueOrDefault(f), Path.Combine(_dir, "bob"));
        bob.FileOffered += o => _ = Task.Run(() => bob.AcceptAsync(o.Id));
        Directory.CreateDirectory(_dir);
        var files = Enumerable.Range(0, 2).Select(i =>
        {
            var path = Path.Combine(_dir, $"big-{i}.bin");
            File.WriteAllBytes(path, RandomNumberGenerator.GetBytes(PrivateMessenger.ChunkSize * 200));
            return path;
        }).ToList();
        var sends = files.Select(f => alice.SendFileAsync(Fp(_bob), f)).ToList();
        Assert.All(await Task.WhenAll(sends).WaitAsync(TimeSpan.FromSeconds(60)), Assert.True);
        Assert.InRange(Volatile.Read(ref most), 1, PrivateMessenger.Window);
    }

    private static async Task<bool> WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(50);
        return condition();
    }

    // ---- 不经网络：两头直接连 ----

    private sealed class Link(string me, byte[] myKey, string name) : IPrivateLink
    {
        public Link? Peer;
        public bool Drop;
        public event Action<PrivateFrame>? PrivateReceived;

        public Task<bool> SendPrivateAsync(string to, byte[] sealedData, CancellationToken ct)
        {
            if (Peer is null || Drop) return Task.FromResult(false);
            Peer.PrivateReceived?.Invoke(new PrivateFrame(me, myKey, name, sealedData));
            return Task.FromResult(true);
        }
    }

    private (PrivateMessenger Alice, PrivateMessenger Bob) Pair()
    {
        var (alice, bob, _) = PairWithLink();
        return (alice, bob);
    }

    /// <summary>Like <see cref="Pair"/>, plus Alice's side of the link so a test can cut her off.</summary>
    private (PrivateMessenger Alice, PrivateMessenger Bob, Link AliceLink) PairWithLink()
    {
        var a = new Link(Fp(_alice), _alice.PublicKey, "Alice");
        var b = new Link(Fp(_bob), _bob.PublicKey, "Bob");
        a.Peer = b;
        b.Peer = a;
        var keys = new Dictionary<string, byte[]> { [Fp(_alice)] = _alice.PublicKey, [Fp(_bob)] = _bob.PublicKey };
        return (new PrivateMessenger(a, _alice, f => keys.GetValueOrDefault(f), Path.Combine(_dir, "alice")),
            new PrivateMessenger(b, _bob, f => keys.GetValueOrDefault(f), Path.Combine(_dir, "bob")), a);
    }

    [Fact]
    public async Task AFrameWhoseFingerprintIsNotItsKeysIsIgnored()
    {
        // Alice's link claims the host's fingerprint while sealing with her own key.
        var a = new Link(Fp(_host), _alice.PublicKey, "Host");
        var b = new Link(Fp(_bob), _bob.PublicKey, "Bob");
        a.Peer = b;
        b.Peer = a;
        var keys = new Dictionary<string, byte[]> { [Fp(_alice)] = _alice.PublicKey, [Fp(_bob)] = _bob.PublicKey, [Fp(_host)] = _host.PublicKey };
        await using var alice = new PrivateMessenger(a, _alice, f => keys.GetValueOrDefault(f), Path.Combine(_dir, "alice"));
        await using var bob = new PrivateMessenger(b, _bob, f => keys.GetValueOrDefault(f), Path.Combine(_dir, "bob"));
        var got = new TaskCompletionSource<PrivateMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        bob.MessageReceived += m => got.TrySetResult(m);
        Assert.True(await alice.SendTextAsync(Fp(_bob), "from the host, honest"));
        await Task.Delay(300);
        Assert.False(got.Task.IsCompleted);

        // The same text with a matching fingerprint arrives, and names the key that sealed it.
        var honest = new Link(Fp(_alice), _alice.PublicKey, "Alice") { Peer = b };
        await using var alice2 = new PrivateMessenger(honest, _alice, f => keys.GetValueOrDefault(f), Path.Combine(_dir, "alice2"));
        Assert.True(await alice2.SendTextAsync(Fp(_bob), "hi"));
        var m = await got.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(Fp(_alice), m.FromFingerprint);
        Assert.Equal(_alice.PublicKey.ToArray(), m.FromPublicKey);
    }

    [Fact]
    public void AWireFrameThatPairsAKeyWithAnotherFingerprintIsDropped()
    {
        var sealedData = Convert.ToBase64String(PrivateSeal.Seal(_alice, _bob.PublicKey, "x"u8));
        System.Text.Json.Nodes.JsonObject Frame(string from) => new() { ["from"] = from, ["pk"] = Convert.ToHexString(_alice.PublicKey), ["b"] = sealedData };
        Assert.Null(ChatWire.ParsePrivateFrom(Frame(Fp(_host))));
        Assert.Equal(Fp(_alice), ChatWire.ParsePrivateFrom(Frame(Fp(_alice)))!.FromFingerprint);
    }

    [Fact]
    public async Task UnansweredOfferExpiresAndFreesItsSlot()
    {
        var (alice, bob) = Pair();
        await using var _a = alice;
        await using var _b = bob;
        Directory.CreateDirectory(_dir);
        var source = Path.Combine(_dir, "a.txt");
        await File.WriteAllTextAsync(source, "hello");
        var offered = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var expired = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        bob.FileOffered += offer => offered.TrySetResult(offer.Id);
        bob.FileOfferExpired += id => expired.TrySetResult(id);
        var sending = alice.SendFileAsync(Fp(_bob), source);
        var id = await offered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await bob.SweepIdle();
        Assert.False(expired.Task.IsCompleted);

        bob.Clock = () => DateTime.UtcNow + TimeSpan.FromMinutes(6);
        await bob.SweepIdle();
        Assert.Equal(id, await expired.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        // The sender hears about it instead of waiting out its own timeout.
        Assert.False(await sending.WaitAsync(TimeSpan.FromSeconds(10)));
        // Accepting afterwards does nothing.
        await bob.AcceptAsync(id);
        Assert.False(Directory.Exists(Path.Combine(_dir, "bob")) && Directory.EnumerateFiles(Path.Combine(_dir, "bob")).Any());
    }

    [Fact]
    public async Task StalledReceiveIsGivenUp()
    {
        var (alice, bob, aliceLink) = PairWithLink();
        await using var _a = alice;
        await using var _b = bob;
        Directory.CreateDirectory(_dir);
        var source = Path.Combine(_dir, "big.bin");
        await File.WriteAllBytesAsync(source, RandomNumberGenerator.GetBytes(PrivateMessenger.ChunkSize * 200));
        bob.FileOffered += offer => _ = bob.AcceptAsync(offer.Id);
        var cut = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lastChunk = DateTime.UtcNow.Ticks;
        bob.Progress += p =>
        {
            Interlocked.Exchange(ref lastChunk, DateTime.UtcNow.Ticks);
            if (!p.Finished && p.Done >= PrivateMessenger.ChunkSize * 5)
            {
                // Alice goes silent mid-transfer.
                aliceLink.Drop = true;
                cut.TrySetResult();
            }
            if (p.Finished) ended.TrySetResult(p.Error);
        };
        _ = alice.SendFileAsync(Fp(_bob), source);
        await cut.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // Let the chunks already on their way land before moving the clock: a chunk landing after the clock moves sets
        // the job's last activity to the moved clock, and the sweep then never sees it as idle. A fixed delay was too
        // short on a busy Windows runner, so wait until no chunk has arrived for half a second.
        var settle = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow - new DateTime(Interlocked.Read(ref lastChunk), DateTimeKind.Utc) < TimeSpan.FromMilliseconds(500)
            && DateTime.UtcNow < settle)
        {
            await Task.Delay(100);
        }

        await bob.SweepIdle();
        Assert.False(ended.Task.IsCompleted);

        bob.Clock = () => DateTime.UtcNow + PrivateMessenger.ReceiveIdleTimeout + TimeSpan.FromSeconds(1);
        await bob.SweepIdle();
        Assert.Equal(Amber.Core.Localization.L.T("core.mp.private.stalled"), await ended.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        // The partial file stays so that sending again resumes.
        Assert.Single(Directory.GetFiles(Path.Combine(_dir, "bob"), "*.jaypart"));
    }

    [Fact]
    public async Task TextMessageArrives()
    {
        var (alice, bob) = Pair();
        await using var _a = alice;
        await using var _b = bob;
        var got = new TaskCompletionSource<PrivateMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        bob.MessageReceived += m => got.TrySetResult(m);
        Assert.True(await alice.SendTextAsync(Fp(_bob), "晚上一起挖矿？"));
        var message = await got.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("晚上一起挖矿？", message.Text);
        Assert.Equal(Fp(_alice), message.FromFingerprint);
        Assert.False(await alice.SendTextAsync("NOT-ONLINE", "hi"));
    }

    [Fact]
    public async Task FileArrivesIntactAfterAccepting()
    {
        var (alice, bob) = Pair();
        await using var _a = alice;
        await using var _b = bob;
        Directory.CreateDirectory(_dir);
        var source = Path.Combine(_dir, "截图.png");
        var data = RandomNumberGenerator.GetBytes(PrivateMessenger.ChunkSize * 40 + 123);
        await File.WriteAllBytesAsync(source, data);
        bob.FileOffered += offer => _ = bob.AcceptAsync(offer.Id);
        var saved = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        bob.Progress += p =>
        {
            if (p.Finished) saved.TrySetResult(p.SavedPath ?? p.Error ?? "");
        };
        Assert.True(await alice.SendFileAsync(Fp(_bob), source).WaitAsync(TimeSpan.FromSeconds(30)));
        var path = await saved.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(Path.Combine(_dir, "bob", "截图.png"), path);
        Assert.Equal(data, await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "bob"), "*.jaypart"));
    }

    [Fact]
    public async Task DeclinedFileIsNotSent()
    {
        var (alice, bob) = Pair();
        await using var _a = alice;
        await using var _b = bob;
        Directory.CreateDirectory(_dir);
        var source = Path.Combine(_dir, "a.txt");
        await File.WriteAllTextAsync(source, "hello");
        bob.FileOffered += offer => _ = bob.DeclineAsync(offer.Id);
        Assert.False(await alice.SendFileAsync(Fp(_bob), source).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.False(Directory.Exists(Path.Combine(_dir, "bob")) && Directory.EnumerateFiles(Path.Combine(_dir, "bob")).Any());
    }

    [Fact]
    public async Task InterruptedFileResumesWhereItStopped()
    {
        var (alice, bob) = Pair();
        await using var _a = alice;
        await using var _b = bob;
        Directory.CreateDirectory(_dir);
        var source = Path.Combine(_dir, "存档.zip");
        var data = RandomNumberGenerator.GetBytes(PrivateMessenger.ChunkSize * 60 + 77);
        await File.WriteAllBytesAsync(source, data);
        bob.FileOffered += offer => _ = bob.AcceptAsync(offer.Id);
        // 第一次：收到 20 块以后收方取消。没收完的部分留着。
        var round = 1;
        long resumedFrom = -1;
        var saved = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        bob.Progress += p =>
        {
            if (round == 1 && !p.Finished && p.Done >= PrivateMessenger.ChunkSize * 20) _ = bob.CancelAsync(p.Id);
            if (round == 2 && resumedFrom < 0) resumedFrom = p.Done;
            if (round == 2 && p.Finished) saved.TrySetResult(p.SavedPath ?? p.Error ?? "");
        };
        Assert.False(await alice.SendFileAsync(Fp(_bob), source).WaitAsync(TimeSpan.FromSeconds(30)));
        var part = Assert.Single(Directory.GetFiles(Path.Combine(_dir, "bob"), "*.jaypart"));
        Assert.True(new FileInfo(part).Length >= PrivateMessenger.ChunkSize * 20);
        // 第二次发同一个文件：从断开的地方接着收，结果和原文件一样。
        round = 2;
        Assert.True(await alice.SendFileAsync(Fp(_bob), source).WaitAsync(TimeSpan.FromSeconds(30)));
        var path = await saved.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(resumedFrom >= PrivateMessenger.ChunkSize * 20, $"resumed from {resumedFrom}");
        Assert.Equal(Path.Combine(_dir, "bob", "存档.zip"), path);
        Assert.Equal(data, await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "bob"), "*.jaypart"));
    }

    [Fact]
    public async Task NetworkPolicyCanLimitFiles()
    {
        var (alice, bob) = Pair();
        await using var _a = alice;
        await using var _b = bob;
        Directory.CreateDirectory(_dir);
        var source = Path.Combine(_dir, "big.bin");
        await File.WriteAllBytesAsync(source, new byte[5000]);
        // 发方的网络不让传：直接报错，不发 offer。
        alice.AllowsFile = size => size <= 1000;
        await Assert.ThrowsAsync<InvalidDataException>(() => alice.SendFileAsync(Fp(_bob), source));
        // 发方不管，收方的网络不让收：自动拒绝，不弹给用户。
        alice.AllowsFile = _ => true;
        bob.AllowsFile = size => size <= 1000;
        var offered = false;
        bob.FileOffered += _ => offered = true;
        Assert.False(await alice.SendFileAsync(Fp(_bob), source).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.False(offered);
    }

    [Fact]
    public async Task FileIsDeclinedWhenTheDiskIsTooFull()
    {
        var (alice, bob) = Pair();
        await using var _a = alice;
        await using var _b = bob;
        Directory.CreateDirectory(_dir);
        var source = Path.Combine(_dir, "big.bin");
        await File.WriteAllBytesAsync(source, new byte[10_000]);
        // 不限文件大小，只看收方磁盘够不够：这里假装只剩 1 MB（不到预留空间）。
        bob.FreeSpaceOf = _ => 1024 * 1024;
        var refused = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        bob.FileOffered += offer => _ = Task.Run(async () =>
        {
            try { await bob.AcceptAsync(offer.Id); refused.TrySetResult(null); }
            catch (IOException e) { refused.TrySetResult(e); }
        });
        Assert.False(await alice.SendFileAsync(Fp(_bob), source).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.IsType<IOException>(await refused.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(Directory.Exists(Path.Combine(_dir, "bob")) && Directory.EnumerateFiles(Path.Combine(_dir, "bob")).Any());
    }

    // ---- 经真的聊天转发（邀请房间） ----

    private sealed record Room(RoomCredentials Credentials, MemberTable Table, RoomCode Host);

    private Room HostRoom()
    {
        var credentials = RoomCredentials.Create(_host.PublicKey.ToArray(), 25565, TunnelSettings.Default);
        var table = new MemberTable();
        return new Room(credentials, table, credentials.ToHostRoom(table, "Host"));
    }

    private static RoomCode GuestRoom(Room room, MemberEntry entry) =>
        RoomCredentials.FromJson((room.Credentials with { Member = entry.Key }).ToJson()).ToRoom();

    private static async Task<bool> WaitAsync(Func<bool> condition, int seconds = 5)
    {
        for (var i = 0; i < seconds * 20; i++)
        {
            if (condition()) return true;
            await Task.Delay(50);
        }
        return condition();
    }

    [Fact]
    public async Task HubForwardsOnlyToTheRecipientAndStampsTheSender()
    {
        var room = HostRoom();
        var alice = room.Table.Issue(_alice.PublicKey, "Alice");
        var bob = room.Table.Issue(_bob.PublicKey, "Bob");
        var carol = room.Table.Issue(X25519KeyPair.Generate().PublicKey, "Carol");
        await using var hub = new ChatHub(room.Host, 0);
        await using var aliceChat = new ChatClient(GuestRoom(room, alice), "Alice");
        await using var bobChat = new ChatClient(GuestRoom(room, bob), "Bob");
        var carolChat = new ChatClient(GuestRoom(room, carol), "Carol");
        var carolGot = 0;
        carolChat.PrivateReceived += _ => Interlocked.Increment(ref carolGot);
        await aliceChat.ConnectAsync("127.0.0.1", hub.Port);
        await bobChat.ConnectAsync("127.0.0.1", hub.Port);
        await carolChat.ConnectAsync("127.0.0.1", hub.Port);
        // 名单：三个人都在，公钥和指纹对得上。
        Assert.True(await WaitAsync(() => aliceChat.Roster.Count == 3));
        Assert.Contains(aliceChat.Roster, r => r.Fingerprint == Fp(_bob) && r.PublicKey.SequenceEqual(_bob.PublicKey) && r.Name == "Bob");

        byte[]? KeyOf(ChatClient chat, string fp) => chat.Roster.FirstOrDefault(r => r.Fingerprint == fp)?.PublicKey;
        await using var aliceMessenger = new PrivateMessenger(aliceChat, _alice, fp => KeyOf(aliceChat, fp), Path.Combine(_dir, "alice"));
        await using var bobMessenger = new PrivateMessenger(bobChat, _bob, fp => KeyOf(bobChat, fp), Path.Combine(_dir, "bob"));
        var got = new TaskCompletionSource<PrivateMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        bobMessenger.MessageReceived += m => got.TrySetResult(m);
        Assert.True(await aliceMessenger.SendTextAsync(Fp(_bob), "悄悄话"));
        var message = await got.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("悄悄话", message.Text);
        Assert.Equal(Fp(_alice), message.FromFingerprint);
        Assert.Equal("Alice", message.FromName);

        // 经转发传一个小文件。
        Directory.CreateDirectory(_dir);
        var source = Path.Combine(_dir, "world.zip");
        var data = RandomNumberGenerator.GetBytes(50_000);
        await File.WriteAllBytesAsync(source, data);
        bobMessenger.FileOffered += offer => _ = bobMessenger.AcceptAsync(offer.Id);
        var saved = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        bobMessenger.Progress += p =>
        {
            if (p.Finished) saved.TrySetResult(p.SavedPath);
        };
        Assert.True(await aliceMessenger.SendFileAsync(Fp(_bob), source).WaitAsync(TimeSpan.FromSeconds(30)));
        var path = await saved.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(data, await File.ReadAllBytesAsync(path!));
        Assert.Equal(0, carolGot);

        // Carol 离开后名单更新。
        await carolChat.DisposeAsync();
        Assert.True(await WaitAsync(() => aliceChat.Roster.Count == 2));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
