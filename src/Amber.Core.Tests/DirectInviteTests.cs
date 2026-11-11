using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Tests;

/// <summary>
/// Direct links: the room hands out a plain JAY2 invite (no address in any code); the two sides' addresses travel only
/// inside the encrypted knock, and only after approval. 桥钥匙来自邀请里的大厅密钥，批准前直连桥只能到大厅，
/// 批准后证明成员身份才接到房间；踢出断开桥；换网络时桥不动；旧的 JAY2D / JAY2R 码不认。
/// </summary>
public sealed class DirectInviteTests
{
    private static readonly DirectOptions Options = new([], false, [], false, false, false, true);
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    private sealed class FakeEasyTier(EasyTierOptions options) : IEasyTierSession
    {
        public EasyTierOptions Options { get; } = options;
        public bool HasExited { get; private set; }
        public IReadOnlyList<string> RecentOutput => [];
        public Task<List<PeerInfo>> PeersAsync(CancellationToken ct = default) => Task.FromResult(new List<PeerInfo>());
        public void Dispose() => HasExited = true;
    }

    private static readonly IReadOnlyList<Candidate> SomeAddresses =
    [
        new(PathKind.IPv6, IPEndPoint.Parse("[2408:8000::1]:40000")),
        new(PathKind.Punch, IPEndPoint.Parse("203.0.113.5:51234")),
        new(PathKind.Lan, IPEndPoint.Parse("192.168.1.5:40000")),
    ];

    // ---- Addresses: only inside the encrypted knock, never in a code ----

    [Fact]
    public void AddressesRoundTripThroughTheKnockJson()
    {
        var addresses = new DirectAddresses("abcdef0123456789", NatKind.Cone, SomeAddresses);
        var json = addresses.ToJson();
        Assert.Equal(["c", "n", "s"], json.Select(p => p.Key).Order(StringComparer.Ordinal));
        Assert.True(DirectAddresses.TryParse(JsonNode.Parse(json.ToJsonString())!.AsObject(), out var parsed));
        Assert.Equal("abcdef0123456789", parsed.Session);
        Assert.Equal(NatKind.Cone, parsed.Nat);
        Assert.Equal(SomeAddresses, parsed.Candidates);
        Assert.True(parsed.DirectlyReachable);
        Assert.False(new DirectAddresses("abcdef0123456789", NatKind.Symmetric, [SomeAddresses[1]]).DirectlyReachable);
        // ToString ends up in logs and exceptions: no address in it.
        Assert.DoesNotContain("203.0.113.5", addresses.ToString());
        Assert.DoesNotContain("2408:8000", addresses.ToString());
    }

    [Fact]
    public void BadAddressesAreRefusedOrSkipped()
    {
        Assert.False(DirectAddresses.TryParse(null, out _));
        Assert.False(DirectAddresses.TryParse(new JsonObject { ["s"] = "short", ["n"] = "Cone", ["c"] = new JsonArray() }, out _));
        Assert.False(DirectAddresses.TryParse(new JsonObject { ["s"] = "abc/def0123456789", ["c"] = new JsonArray() }, out _));
        Assert.False(DirectAddresses.TryParse(new JsonObject { ["s"] = 5 }, out _));
        // Unknown kinds, broken endpoints, port 0 and wrong shapes are skipped; the count is capped.
        var items = new JsonArray(
            new JsonArray("Nope", "1.2.3.4:5"), new JsonArray("Punch", "not-an-address"), new JsonArray("Punch", "1.2.3.4:0"),
            new JsonArray(1, 2), JsonValue.Create("x"), new JsonArray("Punch", "198.51.100.7:40001"));
        for (var i = 0; i < 40; i++) items.Add(new JsonArray("Lan", $"192.168.1.{i + 1}:40000"));
        Assert.True(DirectAddresses.TryParse(new JsonObject { ["s"] = "abcdef0123456789", ["n"] = "Weird", ["c"] = items }, out var parsed));
        Assert.Equal(NatKind.Unknown, parsed.Nat);
        Assert.Equal(DirectAddresses.MaxCandidates, parsed.Candidates.Count);
        Assert.Equal(new Candidate(PathKind.Punch, IPEndPoint.Parse("198.51.100.7:40001")), parsed.Candidates[0]);
    }

    /// <summary>A direct room hands out a plain JAY2 invite: no IP, no port, no room secret, no member key.</summary>
    [Fact]
    public void DirectRoomInviteCarriesNoAddressOrSecret()
    {
        using var host = X25519KeyPair.Generate();
        var credentials = RoomCredentials.Create(host.PublicKey, 25565, TunnelSettings.Default);
        var members = new MemberTable();
        var room = credentials.ToHostRoom(members, "Host");
        var guest = members.Issue(RandomNumberGenerator.GetBytes(32), "Steve");
        var book = new InviteBook { KeepLobbyKey = true };
        var text = book.Create(1, TimeSpan.FromMinutes(10), false, credentials.Tunnel).Invite.ToString();
        Assert.StartsWith(JayInvite.Prefix, text);
        Assert.False(OldDirectCodes.Contains(text));
        Assert.DoesNotMatch(@"\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}", text);
        Assert.DoesNotContain("25565", text);
        foreach (var secret in room.Secrets) Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(credentials.Secret, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(credentials.Network, text, StringComparison.OrdinalIgnoreCase);
        foreach (var key in new[] { room.Member!, guest.Key })
        {
            Assert.DoesNotContain(key.PskBase64, text);
            Assert.DoesNotContain(key.IdHex, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>The removed direct codes (JAY2D / JAY2R carried both sides' public IPs; AMBDIRECT / AMBREPLY older still) are refused.</summary>
    [Theory]
    [InlineData("JAY2D-eyJ2IjoxfQ")]
    [InlineData("来直连：jay2d-eyJ2IjoxfQ 快来")]
    [InlineData("JAY2R-eyJ2IjoxfQ")]
    [InlineData("AMBDIRECT1-eyJhIjoxfQ")]
    [InlineData("来直连：ambdirect1-eyJyIjoiQU1CLUs3UTJNOVhELTRIWlA4UlcyTjZUQy0yNTU2NSJ9")]
    [InlineData("AMBREPLY1-eyJhIjoxfQ")]
    public void OldDirectCodesAreRefused(string text)
    {
        Assert.True(OldDirectCodes.Contains(text));
        Assert.Equal(PastedCodeKind.OldDirect, PastedCode.Detect(text));
        Assert.False(JayInvite.TryParse(text, out _));
    }

    [Fact]
    public void DiagnosticsStillRedactOldDirectCodes()
    {
        var text = NetDiagnostics.Redact("invite JAY2D-eyJjIjpbWyJQdW5jaCIsIjIwMy4wLjExMy41OjUxMjM0Il1dfQ reply jay2r-eyJ2IjoxfQ", [], includePublicIps: true);
        Assert.DoesNotContain("eyJjIjpbWyJQdW5jaCIsIjIwMy4wLjExMy41OjUxMjM0Il1dfQ", text);
        Assert.DoesNotContain("eyJ2IjoxfQ", text);
    }

    // ---- 桥钥匙 ----

    /// <summary>桥钥匙 = HKDF-SHA256(L, salt "jay-direct-v1", info "bridge", 32)：和房间密钥无关，两个房间同一个 L 就是同一把。</summary>
    [Fact]
    public void BridgeKeyComesFromTheLobbyKeyNotTheRoomSecret()
    {
        var expected = new byte[32];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, DirectTestKeys.LobbyKey, expected, "jay-direct-v1"u8, "bridge"u8);
        Assert.Equal(expected, DirectKeys.Bridge(DirectTestKeys.LobbyKey));
        var room = new RoomCode("K7Q2M9XD", "4HZP8RW2N6TC", 25565);
        var old = HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(room.Secret), 32, Encoding.UTF8.GetBytes(room.NetworkName), "amber-direct-v1"u8.ToArray());
        Assert.NotEqual(old, DirectKeys.Bridge(DirectTestKeys.LobbyKey));
        Assert.NotEqual(DirectKeys.Bridge(DirectTestKeys.LobbyKey), DirectKeys.Bridge(new byte[16]));
    }

    // ---- 批准前后的直连桥 ----

    private sealed class DirectRoom : IAsyncDisposable
    {
        public readonly X25519KeyPair HostKey = X25519KeyPair.Generate();
        public readonly RoomCredentials Credentials;
        public readonly MemberTable Members = new();
        public readonly RoomCode HostRoom;
        public readonly DirectSession Host;
        public readonly UdpClient Lobby;

        public DirectRoom(Func<bool>? acceptNewPeers = null)
        {
            Credentials = RoomCredentials.Create(HostKey.PublicKey, 25565, TunnelSettings.Default);
            HostRoom = Credentials.ToHostRoom(Members, "Host");
            Host = new DirectSession(null!, Path.Combine(Path.GetTempPath(), "amber-direct-" + Guid.NewGuid().ToString("N")), HostRoom, true, Options, new DiagnosticLog())
            {
                BridgeKey = DirectTestKeys.Bridge,
                StartForTest = (o, _) => Task.FromResult<IEasyTierSession>(new FakeEasyTier(o)),
                SpeedTestPortForTest = 0,
                AcceptNewPeers = acceptNewPeers,
            };
            // 代替大厅的 EasyTier：在大厅端口上收（房间的那个由各个测试在房间端口上开）。
            Lobby = new UdpClient(new IPEndPoint(IPAddress.Loopback, Host.LobbyUdpPort!.Value));
        }

        public Task<DirectAddresses> PrepareAsync() => Host.PrepareAsync();

        public RoomCode Admit(byte[] guestPub, string name) =>
            (Credentials with { Member = Members.Issue(guestPub, name).Key }).ToRoom();

        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            Lobby.Dispose();
            HostKey.Dispose();
        }
    }

    private static DirectSession Guest() => new(null!, Path.Combine(Path.GetTempPath(), "amber-direct-" + Guid.NewGuid().ToString("N")), null, false, Options, new DiagnosticLog())
    {
        BridgeKey = DirectTestKeys.Bridge,
        StartForTest = (o, _) => Task.FromResult<IEasyTierSession>(new FakeEasyTier(o)),
        SpeedTestPortForTest = 0,
    };

    /// <summary>从加入方本机“EasyTier”往直连桥发一个包，看它到了房主那边的哪个实例。</summary>
    private static async Task<string?> ArrivesAtAsync(DirectSession guest, UdpClient lobby, UdpClient? room, string text)
    {
        using var et = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var data = Encoding.UTF8.GetBytes(text);
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var fromLobby = lobby.ReceiveAsync(wait.Token).AsTask();
        var fromRoom = room?.ReceiveAsync(wait.Token).AsTask() ?? Task.Delay(Timeout.Infinite, wait.Token).ContinueWith(_ => default(UdpReceiveResult));
        for (var i = 0; i < 10 && !fromLobby.IsCompleted && !fromRoom.IsCompleted; i++)
        {
            await et.SendAsync(data, new IPEndPoint(IPAddress.Loopback, guest.Bridge!.LocalPort));
            await Task.WhenAny(fromLobby, fromRoom, Task.Delay(150));
        }
        if (fromLobby.IsCompletedSuccessfully && Encoding.UTF8.GetString(fromLobby.Result.Buffer) == text) return "lobby";
        if (fromRoom.IsCompletedSuccessfully && Encoding.UTF8.GetString(fromRoom.Result.Buffer) == text) return "room";
        return null;
    }

    /// <summary>
    /// The knock exchange, without the knock: the guest answers the host's offer, the host punches towards the guest's
    /// addresses, then the guest completes the join with the room the host approved it into.
    /// </summary>
    private static async Task<bool> JoinAsync(DirectRoom host, DirectSession guest, RoomCode room, Func<Task>? beforeJoin = null)
    {
        var mine = await guest.AnswerAsync(host.Host.Offer()!);
        Assert.NotNull(mine);
        Assert.Equal(host.Host.LocalAddresses!.Session, mine!.Session);
        var hostPunch = host.Host.AddGuestAsync(mine);
        if (beforeJoin is not null) await beforeJoin();
        var joined = await guest.CompleteJoinAsync(room);
        Assert.True(await hostPunch.WaitAsync(Deadline));
        return joined;
    }

    /// <summary>批准之前，加入方经直连桥的数据只到大厅；证明了成员身份之后才到房间。</summary>
    [Fact]
    public async Task BridgeReachesOnlyTheLobbyUntilTheGuestProvesMembership()
    {
        await using var host = new DirectRoom();
        var offer = await host.PrepareAsync();
        using var room = new UdpClient(new IPEndPoint(IPAddress.Loopback, host.Host.Bridge!.LocalPort));
        await using var guest = Guest();
        string? beforeAdmit = null;
        var joined = await JoinAsync(host, guest, host.Admit(RandomNumberGenerator.GetBytes(32), "Steve"), async () =>
        {
            Assert.NotNull(await guest.Bridge!.PunchAsync(offer.Candidates, offer.Nat, Deadline));
            beforeAdmit = await ArrivesAtAsync(guest, host.Lobby, room, "knock");
            Assert.False(host.Host.Bridge.Peers.Single().Admitted);
        });
        Assert.True(joined);
        Assert.Equal("lobby", beforeAdmit);
        var peer = host.Host.Bridge.Peers.Single();
        Assert.True(peer.Admitted);
        Assert.Same(host.Members.FindGuest(peer.Member!.PublicKey), peer.Member);
        Assert.Equal("room", await ArrivesAtAsync(guest, host.Lobby, room, "play"));
        Assert.True(guest.HasRoom && guest.Room.FromInvite);
    }

    /// <summary>
    /// Sealed direct code (no server): the guest opens the offer, the host opens the sealed reply and punches, the guest
    /// knocks over the bridge (which reaches only the lobby) and, once approved, the bridge reaches the room.
    /// </summary>
    [Fact]
    public async Task SealedCodeJoinsWithoutAnyServer()
    {
        await using var host = new DirectRoom(() => true);
        var offerAddresses = await host.PrepareAsync();
        using var room = new UdpClient(new IPEndPoint(IPAddress.Loopback, host.Host.Bridge!.LocalPort));
        using var offer = SealedDirect.Seal(offerAddresses, DirectTestKeys.Invite, DateTimeOffset.UtcNow);
        using var opened = SealedDirect.Open(offer.Code, offer.Passphrase, DateTimeOffset.UtcNow);
        await using var guest = Guest();
        var mine = await guest.AnswerAsync(opened.Host);
        var reply = SealedDirect.SealReply(opened, mine!, DateTimeOffset.UtcNow);
        var hostPunch = host.Host.AddGuestAsync(offer.OpenReply(reply, DateTimeOffset.UtcNow));
        string? beforeApproval = null;
        var joined = await guest.JoinThroughBridgeAsync(async (peers, _) =>
        {
            Assert.Equal([$"udp://127.0.0.1:{guest.Bridge!.LocalPort}"], peers);
            beforeApproval = await ArrivesAtAsync(guest, host.Lobby, room, "knock");
            return host.Admit(RandomNumberGenerator.GetBytes(32), "Steve");
        });
        Assert.True(joined);
        Assert.True(await hostPunch.WaitAsync(Deadline));
        Assert.Equal("lobby", beforeApproval);
        Assert.True(host.Host.Bridge.Peers.Single().Admitted);
        Assert.Equal("room", await ArrivesAtAsync(guest, host.Lobby, room, "play"));
        Assert.False(guest.UsingFallbackNode);
    }

    /// <summary>Sealed code, host turns the knock down: no room, nothing admitted.</summary>
    [Fact]
    public async Task SealedCodeTurnedDownLeavesWithoutARoom()
    {
        await using var host = new DirectRoom(() => true);
        var offerAddresses = await host.PrepareAsync();
        using var offer = SealedDirect.Seal(offerAddresses, DirectTestKeys.Invite, DateTimeOffset.UtcNow);
        using var opened = SealedDirect.Open(offer.Code, offer.Passphrase, DateTimeOffset.UtcNow);
        await using var guest = Guest();
        var mine = await guest.AnswerAsync(opened.Host);
        _ = host.Host.AddGuestAsync(offer.OpenReply(SealedDirect.SealReply(opened, mine!, DateTimeOffset.UtcNow), DateTimeOffset.UtcNow));
        Assert.False(await guest.JoinThroughBridgeAsync((_, _) => Task.FromResult<RoomCode?>(null)));
        Assert.False(guest.HasRoom);
        Assert.False(host.Host.Bridge!.Peers.Single().Admitted);
    }

    /// <summary>成员钥匙不在房主的成员表里（没批准、伪造的）：房主不认，桥还是只能到大厅。</summary>
    [Fact]
    public async Task UnknownMemberKeyIsRefusedAndTheBridgeStaysOnTheLobby()
    {
        await using var host = new DirectRoom();
        await host.PrepareAsync();
        using var room = new UdpClient(new IPEndPoint(IPAddress.Loopback, host.Host.Bridge!.LocalPort));
        await using var guest = Guest();
        var forged = (host.Credentials with { Member = MemberKey.Create() }).ToRoom();
        await Assert.ThrowsAsync<DirectFailedException>(() => JoinAsync(host, guest, forged));
        Assert.False(host.Host.Bridge.Peers.Single().Admitted);
        Assert.Equal("lobby", await ArrivesAtAsync(guest, host.Lobby, room, "sneak"));
    }

    /// <summary>挑战每条桥一个：别人的证明（换了挑战）不对。</summary>
    [Fact]
    public void ProofIsBoundToTheChallengeAndSession()
    {
        var key = MemberKey.Create();
        var a = DirectKeys.AdmitProof(key, "abcdef0123456789", new byte[16]);
        Assert.Equal(a, DirectKeys.AdmitProof(key, "abcdef0123456789", new byte[16]));
        Assert.NotEqual(a, DirectKeys.AdmitProof(key, "abcdef0123456789", Enumerable.Repeat((byte)1, 16).ToArray()));
        Assert.NotEqual(a, DirectKeys.AdmitProof(key, "0123456789abcdef", new byte[16]));
        Assert.NotEqual(a, DirectKeys.AdmitProof(MemberKey.Create(), "abcdef0123456789", new byte[16]));
    }

    /// <summary>The host only takes addresses for its own session: an answer for another room is refused.</summary>
    [Fact]
    public async Task HostRefusesAnAnswerForAnotherSession()
    {
        await using var host = new DirectRoom();
        var offer = await host.PrepareAsync();
        var other = new DirectAddresses(new string('a', 16) == offer.Session ? new string('b', 16) : new string('a', 16), NatKind.Cone, SomeAddresses);
        await Assert.ThrowsAsync<DirectFailedException>(() => host.Host.AddGuestAsync(other));
    }

    /// <summary>踢出：这个成员的直连桥马上断开，之后他的探测不再接。</summary>
    [Fact]
    public async Task KickDropsTheMembersBridgePeer()
    {
        await using var host = new DirectRoom();
        await host.PrepareAsync();
        using var room = new UdpClient(new IPEndPoint(IPAddress.Loopback, host.Host.Bridge!.LocalPort));
        await using var guest = Guest();
        Assert.True(await JoinAsync(host, guest, host.Admit(RandomNumberGenerator.GetBytes(32), "Steve")));
        var peer = host.Host.Bridge.Peers.Single();
        host.Members.Kick(peer.Member!, null);
        Assert.Empty(host.Host.Bridge.Peers);
        // 加入方的心跳停了会补发探测：踢出的地址不再接。
        await Task.Delay(DirectBridge.ReprobeAfter + TimeSpan.FromSeconds(1.5));
        Assert.Empty(host.Host.Bridge.Peers);
        Assert.Null(await ArrivesAtAsync(guest, host.Lobby, room, "after-kick"));
    }

    /// <summary>
    /// 没有待用邀请时房主的直连桥不接新人：桥钥匙（由 L 派生）整个房间都不换，拿着旧邀请的人连大厅都到不了。
    /// 一个刚批准的加入方经加密敲门交了地址之后，短时间内能接（他的一次性邀请那时已经用掉了）。
    /// </summary>
    [Fact]
    public async Task ClosedRoomTakesOnlyTheGuestItJustApproved()
    {
        await using var host = new DirectRoom(() => false);
        var offer = await host.PrepareAsync();
        await using var guest = Guest();
        var mine = await guest.AnswerAsync(offer);
        Assert.Null(await guest.Bridge!.PunchAsync(offer.Candidates, offer.Nat, TimeSpan.FromSeconds(3)));
        Assert.Empty(host.Host.Bridge!.Peers);
        var hostPunch = host.Host.AddGuestAsync(mine!);
        Assert.NotNull(await guest.Bridge.PunchAsync(offer.Candidates, offer.Nat, TimeSpan.FromSeconds(5)));
        Assert.True(await hostPunch.WaitAsync(Deadline));
        Assert.Single(host.Host.Bridge.Peers);
    }

    /// <summary>邀请用完了（不再接新人），已经证明过身份的直连成员照常：还连着房间。</summary>
    [Fact]
    public async Task AdmittedMembersKeepWorkingAfterInvitesRunOut()
    {
        var open = true;
        await using var host = new DirectRoom(() => Volatile.Read(ref open));
        await host.PrepareAsync();
        using var room = new UdpClient(new IPEndPoint(IPAddress.Loopback, host.Host.Bridge!.LocalPort));
        await using var guest = Guest();
        Assert.True(await JoinAsync(host, guest, host.Admit(RandomNumberGenerator.GetBytes(32), "Steve")));
        Volatile.Write(ref open, false);
        Assert.True(host.Host.Bridge.Peers.Single().Admitted);
        Assert.Equal("room", await ArrivesAtAsync(guest, host.Lobby, room, "still-here"));
    }

    /// <summary>踢人换网络：留下的直连成员换到新房间，直连桥不动（还是接在房间上）。</summary>
    [Fact]
    public async Task RekeyKeepsTheBridgeAndSwitchesTheRoom()
    {
        await using var host = new DirectRoom();
        await host.PrepareAsync();
        await using var guest = Guest();
        Assert.True(await JoinAsync(host, guest, host.Admit(RandomNumberGenerator.GetBytes(32), "Steve")));
        var next = guest.Room.Rekeyed(host.Credentials.Rotate().ToRekey());
        await guest.SwitchRoomAsync(next);
        Assert.Equal(next.Secret, guest.EasyTier!.Options.Room.Secret);
        Assert.Equal(2, guest.Room.Epoch);
        var hostNext = host.HostRoom.Rekeyed(new RoomRekey(next.NetworkName, next.Secret, 2));
        await host.Host.SwitchRoomAsync(hostNext);
        Assert.Equal(hostNext.Secret, host.Host.EasyTier!.Options.Room.Secret);
        Assert.True(host.Host.Bridge!.Peers.Single().Admitted);
        Assert.Single(guest.Bridge!.Peers);
    }

    // ---- 直连房间的邀请就是普通 JAY2 邀请：令牌、一次、过期、批准；地址只在批准后的加密回复里 ----

    private static readonly DirectAddresses HostOffer = new("0123456789abcdef", NatKind.Cone, SomeAddresses);

    private sealed class Knocking : IAsyncDisposable
    {
        public DateTimeOffset Now = new(2026, 11, 7, 0, 0, 0, TimeSpan.Zero);
        public readonly InviteBook Book;
        public readonly X25519KeyPair Host = X25519KeyPair.Generate();
        public readonly MemberTable Members = new();
        public readonly KnockServer Server;
        public readonly RoomCredentials Credentials;
        public readonly TaskCompletionSource<DirectAddresses> Answered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Knocking(Func<KnockRequest, CancellationToken, Task<bool>>? approve = null)
        {
            Book = new InviteBook(() => Now) { KeepLobbyKey = true };
            Credentials = RoomCredentials.Create(Host.PublicKey, 25565, TunnelSettings.Default);
            Server = new KnockServer(Book, Host, () => Credentials, _ => false, approve ?? ((_, _) => Task.FromResult(true)), IPAddress.Loopback, 0,
                limits: new KnockLimits { HandshakeTimeout = TimeSpan.FromSeconds(3), DecisionTimeout = TimeSpan.FromMilliseconds(400) }, members: Members)
            {
                DirectOffer = () => HostOffer,
            };
            Server.DirectAnswered += (_, _, addresses) => Answered.TrySetResult(addresses);
        }

        /// <summary>按邀请敲门（邀请先编码再解析，和真的一样）。<paramref name="seen"/> 记下加入方从回复里拿到的房主地址。</summary>
        public async Task<KnockOutcome> KnockAsync(JayInvite invite, X25519KeyPair who, List<DirectAddresses>? seen = null)
        {
            Assert.True(JayInvite.TryParse(invite.ToString(), out var parsed));
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, Server.Port);
            return await KnockClient.KnockAsync(client.GetStream(), parsed, who, "Steve", "0.6.0", decisionTimeout: TimeSpan.FromSeconds(5),
                direct: (host, _) =>
                {
                    seen?.Add(host);
                    return Task.FromResult<DirectAddresses?>(new DirectAddresses("ffffffffffffffff", NatKind.Symmetric, [new Candidate(PathKind.Punch, IPEndPoint.Parse("198.51.100.7:40001"))]));
                });
        }

        public async ValueTask DisposeAsync()
        {
            await Server.DisposeAsync();
            Host.Dispose();
        }
    }

    private static async Task<bool> RejectedAsync(Func<Task<KnockOutcome>> knock)
    {
        try { return !(await knock()).Approved; }
        catch (IOException) { return true; }
    }

    /// <summary>批准后：房主的地址在加密回复里给加入方，加入方的地址（带房主的会话）在加密确认里回给房主。</summary>
    [Fact]
    public async Task ApprovedKnockExchangesAddressesInsideTheEncryptedChannel()
    {
        await using var k = new Knocking();
        var invite = k.Book.Create(1, TimeSpan.FromMinutes(10), true, TunnelSettings.Default).Invite;
        using var a = X25519KeyPair.Generate();
        var seen = new List<DirectAddresses>();
        var outcome = await k.KnockAsync(invite, a, seen);
        Assert.True(outcome.Approved);
        var host = Assert.Single(seen);
        Assert.Equal(HostOffer.Session, host.Session);
        Assert.Equal(HostOffer.Candidates, host.Candidates);
        Assert.Equal(HostOffer.Candidates, outcome.HostAddresses!.Candidates);
        var guest = await k.Answered.Task.WaitAsync(Deadline);
        Assert.Equal(HostOffer.Session, guest.Session);
        Assert.Equal(NatKind.Symmetric, guest.Nat);
        Assert.Equal(IPEndPoint.Parse("198.51.100.7:40001"), Assert.Single(guest.Candidates).EndPoint);
    }

    /// <summary>没批准的人拿不到房主的任何地址，房主也收不到他的。</summary>
    [Fact]
    public async Task RejectedKnockLearnsNoAddress()
    {
        await using var k = new Knocking((_, _) => Task.FromResult(false));
        var invite = k.Book.Create(1, TimeSpan.FromMinutes(10), false, TunnelSettings.Default).Invite;
        using var a = X25519KeyPair.Generate();
        var seen = new List<DirectAddresses>();
        var outcome = await k.KnockAsync(invite, a, seen);
        Assert.False(outcome.Approved);
        Assert.Null(outcome.Credentials);
        Assert.Null(outcome.HostAddresses);
        Assert.Empty(seen);
        Assert.Empty(k.Members.All);
        await Task.Delay(200);
        Assert.False(k.Answered.Task.IsCompleted);
    }

    [Fact]
    public async Task DirectInviteIsSingleUse()
    {
        await using var k = new Knocking();
        var invite = k.Book.Create(1, TimeSpan.FromMinutes(10), true, TunnelSettings.Default).Invite;
        using var a = X25519KeyPair.Generate();
        using var b = X25519KeyPair.Generate();
        Assert.True((await k.KnockAsync(invite, a)).Approved);
        var seen = new List<DirectAddresses>();
        Assert.True(await RejectedAsync(() => k.KnockAsync(invite, b, seen)));
        Assert.Empty(seen);
        Assert.Null(k.Members.FindGuest(b.PublicKey));
    }

    [Fact]
    public async Task ExpiredDirectInviteFails()
    {
        await using var k = new Knocking();
        var invite = k.Book.Create(1, TimeSpan.FromMinutes(10), true, TunnelSettings.Default).Invite;
        k.Now += TimeSpan.FromMinutes(11);
        using var a = X25519KeyPair.Generate();
        Assert.True(await RejectedAsync(() => k.KnockAsync(invite, a)));
        Assert.Empty(k.Members.All);
    }

    [Fact]
    public async Task WrongTokenFails()
    {
        await using var k = new Knocking();
        var pending = k.Book.Create(1, TimeSpan.FromMinutes(10), true, TunnelSettings.Default);
        var token = (byte[])pending.Invite.Token.Clone();
        token[0] ^= 1;
        using var a = X25519KeyPair.Generate();
        var seen = new List<DirectAddresses>();
        Assert.True(await RejectedAsync(() => k.KnockAsync(pending.Invite with { Token = token }, a, seen)));
        Assert.Empty(seen);
        Assert.Empty(k.Members.All);
        Assert.Equal(1, pending.UsesLeft);
    }

    [Fact]
    public async Task BannedGuestCannotUseADirectInvite()
    {
        await using var k = new Knocking();
        using var a = X25519KeyPair.Generate();
        k.Members.Ban(a.PublicKey, "Steve");
        var invite = k.Book.Create(1, TimeSpan.FromMinutes(10), true, TunnelSettings.Default).Invite;
        var seen = new List<DirectAddresses>();
        Assert.True(await RejectedAsync(() => k.KnockAsync(invite, a, seen)));
        Assert.Empty(seen);
        Assert.Empty(k.Members.All);
    }

    /// <summary>直连房间的邀请簿：邀请都用完后大厅密钥不换（桥钥匙由它派生）。</summary>
    [Fact]
    public void DirectBookKeepsItsLobbyKey()
    {
        var book = new InviteBook { KeepLobbyKey = true };
        var key = book.LobbyKeyCopy();
        var invite = book.Create(1, TimeSpan.FromMinutes(10), false, TunnelSettings.Default);
        Assert.Equal(key, invite.Invite.LobbyKey);
        book.Revoke(invite);
        Assert.Equal(key, book.LobbyKeyCopy());
        Assert.Equal(key, book.Create(1, null, false, TunnelSettings.Default).Invite.LobbyKey);
        // 普通房间的邀请簿照旧：用完就换。
        var normal = new InviteBook();
        var first = normal.Create(1, null, false, TunnelSettings.Default);
        normal.Revoke(first);
        Assert.NotEqual(first.Invite.LobbyKey, normal.Create(1, null, false, TunnelSettings.Default).Invite.LobbyKey);
    }

    /// <summary>直连房主的大厅 EasyTier 只在本机回环上监听（给直连桥用）；普通大厅不监听。</summary>
    [Fact]
    public void DirectLobbyListensOnLoopbackOnly()
    {
        var direct = LobbyArgs.Build(new LobbyOptions("jay-l-x", "s", true, [], 15888, null, 40123));
        Assert.Contains("udp://127.0.0.1:40123", direct);
        Assert.DoesNotContain("--no-listener", direct);
        var normal = LobbyArgs.Build(new LobbyOptions("jay-l-x", "s", true, [], 15888));
        Assert.Contains("--no-listener", normal);
        Assert.DoesNotContain("--listeners", normal);
    }
}
