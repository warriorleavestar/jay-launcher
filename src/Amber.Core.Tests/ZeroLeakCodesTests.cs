using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Contacts;
using Amber.Core.Multiplayer.Public;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Tests;

/// <summary>
/// Codes that are useless when leaked (docs/security/zero-leak-codes.md): contact codes carry only a public key,
/// invites travel as sealed mail bound to one recipient, and directories are trusted for routing only.
/// </summary>
public sealed class ZeroLeakCodesTests : IAsyncDisposable
{
    private const string Secret = "test-network-secret";
    private readonly string _dir = Directory.CreateTempSubdirectory("jay-zeroleak-").FullName;
    private readonly List<IAsyncDisposable> _cleanup = [];
    private DateTimeOffset _now = DateTimeOffset.UtcNow;

    private sealed class Loopback(int port, PublicNetworkProfile? profile = null) : IPublicNetwork
    {
        public int Port { get; set; } = port;

        public async Task<Stream> ConnectAsync(string ip, int p, CancellationToken ct)
        {
            var c = new TcpClient();
            await c.ConnectAsync(IPAddress.Loopback, Port, ct);
            return c.GetStream();
        }

        public Task<string?> MyIpAsync(CancellationToken ct) => Task.FromResult<string?>("10.235.0.7");
        public PublicNetworkProfile? Profile => profile ?? ZeroLeakCodesTests.Profile();
    }

    private static PublicNetworkProfile Profile(IReadOnlyList<byte[]>? pinned = null, IReadOnlyList<byte[]>? roots = null) =>
        new("test", "Test", "jay-net-test", Secret, ["tcp://127.0.0.1:1"]) { DirectoryKeys = pinned ?? [], DirectoryRoots = roots ?? [] };

    private RoomDirectoryServer StartDirectory(X25519KeyPair? identity = null, byte[]? certificate = null)
    {
        var server = new RoomDirectoryServer(IPAddress.Loopback, 0, clock: () => _now)
        {
            Identity = identity, NetworkSecret = Secret, Certificate = certificate,
        };
        _cleanup.Add(server);
        return server;
    }

    private sealed class Person : IDisposable
    {
        public required X25519KeyPair Key;
        public required ContactBook Book;
        public required ContactPost Post;
        public required string Name;
        public void Dispose() => Key.Dispose();
    }

    private Person NewPerson(string name, RoomDirectoryServer server)
    {
        var key = X25519KeyPair.Generate();
        var book = new ContactBook(Path.Combine(_dir, name, ContactBook.FileName), () => _now);
        var client = new RoomDirectoryClient(new Loopback(server.Port));
        return new Person
        {
            Key = key, Book = book, Name = name,
            Post = new ContactPost(() => key, book, client, Path.Combine(_dir, name, ContactPost.SeenFileName), () => _now),
        };
    }

    // ---- contact codes ----

    [Fact]
    public void ContactCodeRoundTripsAndToleratesPasting()
    {
        using var key = X25519KeyPair.Generate();
        var code = ContactCode.Format(key.PublicKey);
        Assert.StartsWith(ContactCode.Prefix, code);
        Assert.Equal(ContactCode.Prefix.Length + ContactCode.BodyLength, code.Length);
        Assert.True(ContactCode.TryParse("my code: " + code.ToLowerInvariant() + " thanks", out var parsed));
        Assert.Equal(key.PublicKey, parsed);
        var spaced = ContactCode.Prefix + string.Join(" ", code[ContactCode.Prefix.Length..].Chunk(5).Select(c => new string(c)));
        Assert.True(ContactCode.TryParse(spaced, out parsed));
        Assert.Equal(key.PublicKey, parsed);
        Assert.Equal(PastedCodeKind.ContactCode, PastedCode.Detect(code));
    }

    [Fact]
    public void MistypedContactCodeIsRefused()
    {
        using var key = X25519KeyPair.Generate();
        var code = ContactCode.Format(key.PublicKey).ToCharArray();
        var i = ContactCode.Prefix.Length + 20;
        code[i] = code[i] == 'A' ? 'B' : 'A';
        Assert.False(ContactCode.TryParse(new string(code), out _));
        Assert.False(ContactCode.TryParse(ContactCode.Format(key.PublicKey)[..^3], out _));
    }

    // ---- the emoji check ----

    [Fact]
    public void BothSidesSeeTheSameEmojiOnlyWithTheRealNonces()
    {
        using var a = X25519KeyPair.Generate();
        using var b = X25519KeyPair.Generate();
        var na = ContactHandshake.NewNonce();
        var nb = ContactHandshake.NewNonce();
        Assert.True(ContactHandshake.Opens(ContactHandshake.Commit(na), na));
        Assert.False(ContactHandshake.Opens(ContactHandshake.Commit(na), nb));
        var sas = ContactHandshake.Sas(a.PublicKey, b.PublicKey, na, nb);
        Assert.Equal(ContactHandshake.EmojiCount, sas.Length);
        Assert.All(sas, i => Assert.InRange(i, 0, 63));
        Assert.Equal(sas, ContactHandshake.Sas(a.PublicKey, b.PublicKey, na, nb));
        Assert.NotEqual(sas, ContactHandshake.Sas(b.PublicKey, a.PublicKey, na, nb));
        Assert.Equal(64, ContactHandshake.Emoji.Distinct().Count());
        Assert.Equal(64, ContactHandshake.EmojiNames.Distinct().Count());
    }

    [Fact]
    public void ContactBooksAgreeOnEmojiAndSaveVerifiedContacts()
    {
        using var a = X25519KeyPair.Generate();
        using var b = X25519KeyPair.Generate();
        var bookA = new ContactBook(Path.Combine(_dir, "a.json")) { Me = a.PublicKey };
        var bookB = new ContactBook(Path.Combine(_dir, "b.json")) { Me = b.PublicKey };
        var hello = bookA.StartAdd(a.PublicKey, b.PublicKey, "Alex")!;
        Assert.Null(bookB.Receive(a.PublicKey, hello, "Steve"));
        var asking = Assert.Single(bookB.Pending);
        Assert.Equal(ContactStep.AskingMe, asking.Step);
        Assert.Equal("Alex", asking.Name);
        var reply = bookB.Accept(a.PublicKey, "Steve")!;
        var nonce = bookA.Receive(b.PublicKey, reply, "Alex")!;
        Assert.Null(bookB.Receive(a.PublicKey, nonce, "Steve"));
        var sasA = Assert.Single(bookA.Pending).Sas;
        var sasB = Assert.Single(bookB.Pending).Sas;
        Assert.NotNull(sasA);
        Assert.Equal(sasA, sasB);
        Assert.True(bookA.Confirm(b.PublicKey)!.Verified);
        Assert.True(bookB.Confirm(a.PublicKey)!.Verified);
        // Saved and read back.
        var again = new ContactBook(Path.Combine(_dir, "a.json"));
        var saved = Assert.Single(again.All);
        Assert.Equal("Steve", saved.Name);
        Assert.True(again.IsVerified(b.PublicKey));
    }

    [Fact]
    public void NonceThatDoesNotOpenTheCommitmentEndsTheExchange()
    {
        using var a = X25519KeyPair.Generate();
        using var b = X25519KeyPair.Generate();
        var bookA = new ContactBook(Path.Combine(_dir, "a.json")) { Me = a.PublicKey };
        var bookB = new ContactBook(Path.Combine(_dir, "b.json")) { Me = b.PublicKey };
        bookB.Receive(a.PublicKey, bookA.StartAdd(a.PublicKey, b.PublicKey, "Alex")!, "Steve");
        bookB.Accept(a.PublicKey, "Steve");
        // Someone in the middle picks its nonce after seeing B's: it can't match the commitment it already sent.
        var forged = MailMessage.Create(MailKind.HelloNonce, b.PublicKey, new JsonObject { ["n"] = Convert.ToHexStringLower(ContactHandshake.NewNonce()) }, DateTimeOffset.UtcNow);
        bookB.Receive(a.PublicKey, forged, "Steve");
        Assert.Empty(bookB.Pending);
        Assert.False(bookB.IsVerified(a.PublicKey));
    }

    [Fact]
    public void CrossedHellosEndInOneExchange()
    {
        using var a = X25519KeyPair.Generate();
        using var b = X25519KeyPair.Generate();
        var bookA = new ContactBook(Path.Combine(_dir, "a.json")) { Me = a.PublicKey };
        var bookB = new ContactBook(Path.Combine(_dir, "b.json")) { Me = b.PublicKey };
        var helloA = bookA.StartAdd(a.PublicKey, b.PublicKey, "Alex")!;
        var helloB = bookB.StartAdd(b.PublicKey, a.PublicKey, "Steve")!;
        var toB = bookB.Receive(a.PublicKey, helloA, "Steve");
        var toA = bookA.Receive(b.PublicKey, helloB, "Alex");
        // Exactly one side yields and answers.
        Assert.True(toA is null ^ toB is null);
        var (initiator, responder, initKey, respKey, answer) = toB is not null ? (bookA, bookB, a.PublicKey, b.PublicKey, toB) : (bookB, bookA, b.PublicKey, a.PublicKey, toA!);
        var nonce = initiator.Receive(respKey, answer, "x")!;
        responder.Receive(initKey, nonce, "y");
        Assert.Equal(Assert.Single(initiator.Pending).Sas, Assert.Single(responder.Pending).Sas);
    }

    // ---- sealed mail ----

    [Fact]
    public void SealedMailOpensOnlyForTheRecipientAndNamesTheSender()
    {
        using var a = X25519KeyPair.Generate();
        using var b = X25519KeyPair.Generate();
        using var c = X25519KeyPair.Generate();
        var message = MailMessage.Create(MailKind.Receipt, b.PublicKey, new JsonObject { ["for"] = "x" }, DateTimeOffset.UtcNow);
        var blob = SealedMail.Seal(a, b.PublicKey, message);
        // The sender's key isn't visible in the blob.
        Assert.DoesNotContain(Convert.ToHexString(a.PublicKey), Convert.ToHexString(blob));
        var opened = SealedMail.Open(b, blob);
        Assert.NotNull(opened);
        Assert.Equal(a.PublicKey, opened.Value.Sender);
        Assert.Equal(message.IdHex, opened.Value.Message.IdHex);
        Assert.Null(SealedMail.Open(c, blob));
        blob[^20] ^= 1;
        Assert.Null(SealedMail.Open(b, blob));
    }

    [Fact]
    public void MailboxIdsChangeDailyAndDoNotShowTheKey()
    {
        using var a = X25519KeyPair.Generate();
        var today = SealedMail.MailboxId(a.PublicKey, 20000);
        Assert.NotEqual(today, SealedMail.MailboxId(a.PublicKey, 20001));
        Assert.Equal(32, today.Length);
        var boxes = SealedMail.MailboxesToCheck(a.PublicKey, DateTimeOffset.FromUnixTimeSeconds(20000L * 86400 + 5));
        Assert.Equal([SealedMail.MailboxId(a.PublicKey, 19999), today, SealedMail.MailboxId(a.PublicKey, 20001)], boxes);
    }

    // ---- end to end through a directory ----

    private static async Task Exchange(Person a, Person b)
    {
        Assert.True(await a.Post.AddAsync(b.Key.PublicKey, a.Name, default));
        Assert.Empty(await b.Post.CheckAsync(b.Name, default));
        await b.Post.AcceptAsync(a.Key.PublicKey, b.Name, default);
        await a.Post.CheckAsync(a.Name, default);
        await b.Post.CheckAsync(b.Name, default);
        Assert.Equal(Assert.Single(a.Book.Pending).Sas, Assert.Single(b.Book.Pending).Sas);
        a.Book.Confirm(b.Key.PublicKey);
        b.Book.Confirm(a.Key.PublicKey);
    }

    [Fact]
    public async Task InviteTravelsAsSealedMailAndComesBackWithAReceipt()
    {
        var server = StartDirectory();
        using var alex = NewPerson("alex", server);
        using var steve = NewPerson("steve", server);
        await Exchange(alex, steve);

        var invite = new InviteBook().Create(1, TimeSpan.FromMinutes(10), true, TunnelSettings.Default, boundTo: steve.Key.PublicKey).Invite;
        var confirmed = new List<SentMail>();
        alex.Post.Confirmed += confirmed.Add;
        await alex.Post.SendInviteAsync(alex.Book.Find(steve.Key.PublicKey)!, invite, alex.Key.PublicKey, "Survival", _now.AddMinutes(10), default);
        var got = Assert.Single(await steve.Post.CheckAsync(steve.Name, default));
        Assert.Equal(invite.ToString(), got.Invite.ToString());
        Assert.Equal(alex.Key.PublicKey, got.HostKey);
        Assert.Equal("Survival", got.Room);
        // Opened mail is deleted from the directory, and the same id isn't accepted twice.
        Assert.Empty(await steve.Post.CheckAsync(steve.Name, default));
        await alex.Post.CheckAsync(alex.Name, default);
        Assert.Single(confirmed);
        _now += ContactPost.ReceiptGrace + TimeSpan.FromSeconds(1);
        Assert.Empty(alex.Post.Unconfirmed());
    }

    [Fact]
    public async Task InviteWithoutReceiptIsReportedAsPossiblyHeldBack()
    {
        var server = StartDirectory();
        using var alex = NewPerson("alex", server);
        using var steve = NewPerson("steve", server);
        await Exchange(alex, steve);
        var invite = new InviteBook().Create(1, null, true, TunnelSettings.Default).Invite;
        await alex.Post.SendInviteAsync(alex.Book.Find(steve.Key.PublicKey)!, invite, alex.Key.PublicKey, "Survival", null, default);
        Assert.Empty(alex.Post.Unconfirmed());
        _now += ContactPost.ReceiptGrace + TimeSpan.FromSeconds(1);
        Assert.Single(alex.Post.Unconfirmed());
    }

    [Fact]
    public async Task InviteFromSomeoneNotVerifiedIsDropped()
    {
        var server = StartDirectory();
        using var stranger = NewPerson("stranger", server);
        using var steve = NewPerson("steve", server);
        var message = MailMessage.Create(MailKind.Invite, steve.Key.PublicKey, new JsonObject
        {
            ["code"] = new InviteBook().Create(1, null, true, TunnelSettings.Default).Invite.ToString(),
            ["host"] = Convert.ToHexStringLower(stranger.Key.PublicKey),
            ["room"] = "Free diamonds",
        }, _now);
        await stranger.Post.SendAsync(steve.Key.PublicKey, message, default);
        Assert.Empty(await steve.Post.CheckAsync(steve.Name, default));
    }

    [Fact]
    public async Task ReplayedMailIsIgnored()
    {
        var server = StartDirectory();
        using var alex = NewPerson("alex", server);
        using var steve = NewPerson("steve", server);
        await Exchange(alex, steve);
        var message = MailMessage.Create(MailKind.Invite, steve.Key.PublicKey, new JsonObject
        {
            ["code"] = new InviteBook().Create(1, null, true, TunnelSettings.Default).Invite.ToString(),
            ["host"] = Convert.ToHexStringLower(alex.Key.PublicKey),
            ["room"] = "Survival",
        }, _now);
        var blob = SealedMail.Seal(alex.Key, steve.Key.PublicKey, message);
        var directory = new RoomDirectoryClient(new Loopback(server.Port));
        var box = SealedMail.MailboxId(steve.Key.PublicKey, SealedMail.Day(_now));
        await directory.PostMailAsync(box, blob, SealedMail.DropHash(message.DropToken), default);
        Assert.Single(await steve.Post.CheckAsync(steve.Name, default));
        // The directory (or anyone who copied the blob) posts it again.
        await directory.PostMailAsync(box, blob, SealedMail.DropHash(message.DropToken), default);
        Assert.Empty(await steve.Post.CheckAsync(steve.Name, default));
    }

    [Fact]
    public async Task DirectoryDropsMailOnlyForTheRightTokenAndAfterItsLifetime()
    {
        var server = StartDirectory();
        var client = new RoomDirectoryClient(new Loopback(server.Port));
        var box = new string('a', 32);
        var token = new byte[MailMessage.TokenLength];
        token[0] = 1;
        await client.PostMailAsync(box, [1, 2, 3], SealedMail.DropHash(token), default);
        await client.DropMailAsync(box, new byte[MailMessage.TokenLength], default);
        Assert.Single(await client.FetchMailAsync([box], default));
        await client.DropMailAsync(box, token, default);
        Assert.Empty(await client.FetchMailAsync([box], default));
        await client.PostMailAsync(box, [4, 5, 6], SealedMail.DropHash(token), default);
        _now += RoomDirectoryProtocol.MailTtl + TimeSpan.FromSeconds(1);
        Assert.Empty(await client.FetchMailAsync([box], default));
    }

    [Fact]
    public async Task FullMailboxRefusesMore()
    {
        var server = StartDirectory();
        var client = new RoomDirectoryClient(new Loopback(server.Port));
        var box = new string('b', 32);
        for (var i = 0; i < RoomDirectoryProtocol.MaxMailPerBox; i++)
            await client.PostMailAsync(box, [(byte)i, 1], SealedMail.DropHash(new byte[16]), default);
        var e = await Assert.ThrowsAsync<DirectoryRejectedException>(() => client.PostMailAsync(box, [255, 255], SealedMail.DropHash(new byte[16]), default));
        Assert.Equal("full", e.Code);
    }

    [Fact]
    public async Task MailPostsHaveADirectoryWideBudget()
    {
        var server = StartDirectory();
        var accepted = 0;
        for (var i = 0; i < RoomDirectoryServer.MaxMailPostsPerMinute; i++)
        {
            var post = new System.Text.Json.Nodes.JsonObject
            {
                ["op"] = "post", ["box"] = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)),
                ["blob"] = Convert.ToBase64String([(byte)i, 1]), ["drop"] = SealedMail.DropHash(new byte[16]),
            };
            if (server.Handle(post)["ok"]?.GetValue<bool>() == true) accepted++;
        }
        Assert.Equal(RoomDirectoryServer.MaxMailPostsPerMinute, accepted);
        var client = new RoomDirectoryClient(new Loopback(server.Port));
        var e = await Assert.ThrowsAsync<DirectoryRejectedException>(() => client.PostMailAsync(new string('d', 32), [9, 9], SealedMail.DropHash(new byte[16]), default));
        Assert.Equal("rate", e.Code);
        _now += TimeSpan.FromMinutes(1.1);
        await client.PostMailAsync(new string('d', 32), [9, 9], SealedMail.DropHash(new byte[16]), default);
    }

    [Fact]
    public void MailPostsAreLimitedPerSource()
    {
        var server = StartDirectory();
        System.Text.Json.Nodes.JsonObject Post(int i) => new()
        {
            ["op"] = "post", ["box"] = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)),
            ["blob"] = Convert.ToBase64String([(byte)i, 2]), ["drop"] = SealedMail.DropHash(new byte[16]),
        };
        for (var i = 0; i < RoomDirectoryServer.MaxMailPostsPerSource; i++)
            Assert.True(server.Handle(Post(i), "127.0.9.1")["ok"]?.GetValue<bool>());
        Assert.Equal("rate", server.Handle(Post(99), "127.0.9.1")["error"]?.GetValue<string>());
        // Another source isn't held back by the first one.
        Assert.True(server.Handle(Post(100), "127.0.9.2")["ok"]?.GetValue<bool>());
        _now += RoomDirectoryServer.MailPostWindow + TimeSpan.FromSeconds(1);
        Assert.True(server.Handle(Post(101), "127.0.9.1")["ok"]?.GetValue<bool>());
    }

    [Fact]
    public async Task FetchPagesThroughAFullMailbox()
    {
        var server = StartDirectory();
        var client = new RoomDirectoryClient(new Loopback(server.Port));
        var box = new string('c', 32);
        for (var i = 0; i < RoomDirectoryProtocol.MaxMailPerBox; i++)
        {
            var blob = new byte[3000];
            blob[0] = (byte)i;
            await client.PostMailAsync(box, blob, SealedMail.DropHash(new byte[16]), default);
        }
        // 32 × 4 KB of base64 doesn't fit one reply line; the client reads it page by page.
        Assert.Equal(RoomDirectoryProtocol.MaxMailPerBox, (await client.FetchMailAsync([box], default)).Count);
    }

    // ---- invites bound to one person, and the host named in the invite ----

    private (InviteBook Book, KnockServer Server, X25519KeyPair Host) StartKnock(List<KnockRequest> seen)
    {
        var host = X25519KeyPair.Generate();
        var book = new InviteBook();
        var credentials = RoomCredentials.Create(host.PublicKey, 25565, TunnelSettings.Default);
        var server = new KnockServer(book, host, () => credentials, _ => false, (r, _) =>
        {
            seen.Add(r);
            return Task.FromResult(true);
        }, IPAddress.Loopback, 0);
        _cleanup.Add(server);
        return (book, server, host);
    }

    private static async Task<KnockOutcome> Knock(KnockServer server, JayInvite invite, X25519KeyPair me, byte[]? expectedHost = null)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, server.Port);
        return await KnockClient.KnockAsync(tcp.GetStream(), invite, me, "Steve", "0.9.0", expectedHost: expectedHost);
    }

    [Fact]
    public async Task BoundInviteTurnsAwayAnyoneElseWithoutUsingItUp()
    {
        var seen = new List<KnockRequest>();
        var (book, server, host) = StartKnock(seen);
        using var _ = host;
        using var steve = X25519KeyPair.Generate();
        using var thief = X25519KeyPair.Generate();
        var pending = book.Create(1, TimeSpan.FromMinutes(10), true, TunnelSettings.Default, boundTo: steve.PublicKey);
        var stolen = await Knock(server, pending.Invite, thief);
        Assert.Equal("denied", stolen.Reason);
        Assert.Empty(seen);
        Assert.Equal(1, pending.UsesLeft);
        var real = await Knock(server, pending.Invite, steve, host.PublicKey);
        Assert.NotNull(real.Credentials);
        Assert.Equal(0, pending.UsesLeft);
    }

    [Fact]
    public async Task KnockStopsWhenTheHostIsNotTheOneNamedInTheInvite()
    {
        var seen = new List<KnockRequest>();
        var (book, server, host) = StartKnock(seen);
        using var _ = host;
        using var steve = X25519KeyPair.Generate();
        using var someoneElse = X25519KeyPair.Generate();
        var pending = book.Create(1, TimeSpan.FromMinutes(10), true, TunnelSettings.Default);
        await Assert.ThrowsAnyAsync<System.Security.Cryptography.CryptographicException>(() => Knock(server, pending.Invite, steve, someoneElse.PublicKey));
        // Nothing about the guest reached the host, and the invite is still there.
        Assert.Empty(seen);
        Assert.Equal(1, pending.UsesLeft);
    }

    // ---- directory trust: root certificates and first use ----

    [Fact]
    public async Task DirectoryCertifiedByTheRootIsAccepted()
    {
        var root = TrustSigner.Generate(out _);
        using var identity = X25519KeyPair.Generate();
        var cert = DirectoryCertificate.Issue(root, identity.PublicKey, "jay-net-test", DateTimeOffset.UtcNow.AddDays(30));
        var server = StartDirectory(identity, cert);
        var client = new RoomDirectoryClient(new Loopback(server.Port, Profile(roots: [root.Id])));
        Assert.Empty(await client.ListAsync(default));
    }

    [Theory]
    [InlineData("other-root")]
    [InlineData("other-network")]
    [InlineData("expired")]
    [InlineData("other-key")]
    [InlineData("none")]
    public async Task DirectoryWithoutAGoodCertificateGetsNothing(string flaw)
    {
        var root = TrustSigner.Generate(out _);
        var other = TrustSigner.Generate(out _);
        using var identity = X25519KeyPair.Generate();
        using var otherKey = X25519KeyPair.Generate();
        byte[]? cert = flaw switch
        {
            "other-root" => DirectoryCertificate.Issue(other, identity.PublicKey, "jay-net-test", DateTimeOffset.UtcNow.AddDays(30)),
            "other-network" => DirectoryCertificate.Issue(root, identity.PublicKey, "someone-else", DateTimeOffset.UtcNow.AddDays(30)),
            "expired" => DirectoryCertificate.Issue(root, identity.PublicKey, "jay-net-test", DateTimeOffset.UtcNow.AddDays(-1)),
            "other-key" => DirectoryCertificate.Issue(root, otherKey.PublicKey, "jay-net-test", DateTimeOffset.UtcNow.AddDays(30)),
            _ => null,
        };
        var server = StartDirectory(identity, cert);
        var client = new RoomDirectoryClient(new Loopback(server.Port, Profile(roots: [root.Id])));
        await Assert.ThrowsAsync<DirectoryImpostorException>(() => client.ListAsync(default));
    }

    [Fact]
    public async Task UnpinnedNetworkRemembersTheFirstDirectoryKey()
    {
        using var real = X25519KeyPair.Generate();
        using var impostor = X25519KeyPair.Generate();
        var memory = new DirectoryKeyMemory(Path.Combine(_dir, DirectoryKeyMemory.FileName));
        var first = StartDirectory(real);
        var net = new Loopback(first.Port, Profile());
        var client = new RoomDirectoryClient(net) { KeyMemory = memory };
        Assert.Empty(await client.ListAsync(default));
        Assert.Equal(real.PublicKey, memory.Get("jay-net-test", PublicNetworkProfile.DirectoryIp));
        // Someone else takes the address with another key, or with a throwaway one: refused either way.
        net.Port = StartDirectory(impostor).Port;
        await Assert.ThrowsAsync<DirectoryKeyChangedException>(() => client.ListAsync(default));
        net.Port = StartDirectory(null).Port;
        await Assert.ThrowsAnyAsync<IOException>(() => client.ListAsync(default));
    }

    [Fact]
    public async Task ForgettingTheNetworkAcceptsADirectoryThatWasReKeyed()
    {
        // A directory that lost its key file (or was re-keyed by its owner) comes back with another key. Jay refuses it
        // until the user accepts the change (removing and re-adding the network, or saving the built-in network's nodes
        // again), which forgets the remembered key; the next key seen is remembered instead.
        using var before = X25519KeyPair.Generate();
        using var after = X25519KeyPair.Generate();
        var memory = new DirectoryKeyMemory(Path.Combine(_dir, DirectoryKeyMemory.FileName));
        var net = new Loopback(StartDirectory(before).Port, Profile());
        var client = new RoomDirectoryClient(net) { KeyMemory = memory };
        Assert.Empty(await client.ListAsync(default));
        net.Port = StartDirectory(after).Port;
        await Assert.ThrowsAsync<DirectoryKeyChangedException>(() => client.ListAsync(default));
        // Forgetting another network changes nothing.
        memory.Forget("some-other-net");
        await Assert.ThrowsAsync<DirectoryKeyChangedException>(() => client.ListAsync(default));
        memory.Forget("jay-net-test");
        Assert.Empty(await client.ListAsync(default));
        Assert.Equal(after.PublicKey, memory.Get("jay-net-test", PublicNetworkProfile.DirectoryIp));
        // The forgetting is saved: a fresh memory over the same file (Jay restarted) holds the new key.
        Assert.Equal(after.PublicKey, new DirectoryKeyMemory(Path.Combine(_dir, DirectoryKeyMemory.FileName)).Get("jay-net-test", PublicNetworkProfile.DirectoryIp));
    }

    [Fact]
    public async Task DirectoryWithoutAConfiguredKeyIsStillEncrypted()
    {
        var memory = new DirectoryKeyMemory(Path.Combine(_dir, DirectoryKeyMemory.FileName));
        var throwaway = StartDirectory(null);
        var client = new RoomDirectoryClient(new Loopback(throwaway.Port, Profile())) { KeyMemory = memory };
        Assert.Empty(await client.ListAsync(default));
        Assert.NotNull(memory.Get("jay-net-test", PublicNetworkProfile.DirectoryIp));
    }

    [Fact]
    public void NetworkStringCarriesDirectoryRoots()
    {
        var signer = TrustSigner.Generate(out _);
        var root = TrustSigner.Generate(out _);
        var info = new NetworkInfo("test-net", "Test", "jay-net-test", Secret, ["tcp://example.com:11010"], DirectoryRoots: [root.Id]);
        var signed = NetworkString.Parse(NetworkString.Create(info, signer));
        Assert.Equal(root.Id, Assert.Single(signed.ToProfile().DirectoryRoots));
        var tooMany = info with { DirectoryRoots = [.. Enumerable.Range(0, NetworkString.MaxDirectoryRoots + 1).Select(_ => root.Id)] };
        Assert.Throws<InvalidDataException>(() => NetworkString.Create(tooMany, signer));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var d in _cleanup) await d.DisposeAsync();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }
}
