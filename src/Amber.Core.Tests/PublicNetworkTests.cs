using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Public;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Tests;

/// <summary>
/// 公共网络（只在本机回环上，不经过 EasyTier）：房间签名、目录服务（发布、续期、列表、查找、撤下、重放、冒用）、
/// 公开 / 密码 / 隐藏房间经公共网络敲门、长期邀请踢人不作废、启动参数。
/// </summary>
public sealed class PublicNetworkTests : IAsyncDisposable
{
    /// <summary>把公共网络里的 ip:port 映射到本机回环端口。</summary>
    private sealed class FakeNetwork : IPublicNetwork
    {
        public readonly Dictionary<(string, int), int> Map = [];
        public string? Ip = "10.235.0.7";

        public async Task<Stream> ConnectAsync(string ip, int port, CancellationToken ct)
        {
            if (!Map.TryGetValue((ip, port), out var local)) throw new IOException("unreachable");
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, local, ct);
            return client.GetStream();
        }

        public Task<string?> MyIpAsync(CancellationToken ct) => Task.FromResult(Ip);
    }

    // Real time, truncated: the announcer signs with the wall clock and hidden entries are checked against the directory clock.
    private DateTimeOffset _now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    private readonly RoomDirectoryServer _directory;
    private readonly FakeNetwork _net = new();
    private readonly X25519KeyPair _host = X25519KeyPair.Generate();
    private readonly X25519KeyPair _guest = X25519KeyPair.Generate();
    private readonly List<IAsyncDisposable> _cleanup = [];

    public PublicNetworkTests()
    {
        _directory = new RoomDirectoryServer(IPAddress.Loopback, 0, clock: () => _now);
        _net.Map[(PublicNetworkProfile.DirectoryIp, PublicNetworkProfile.DirectoryPort)] = _directory.Port;
    }

    private RoomListing Listing(RoomVisibility mode = RoomVisibility.Public, string? invite = null) => new()
    {
        Id = "",
        Name = "生存服",
        Host = "Alex",
        Mode = mode,
        HostKey = _host.PublicKey,
        Ip = "10.235.0.7",
        Game = "1.21.1",
        Players = 2,
        Invite = invite ?? (mode is RoomVisibility.Public or RoomVisibility.Direct ? new InviteBook().CreateStanding(false, TunnelSettings.Default).Invite.ToString() : null),
        Lobby = mode == RoomVisibility.Password ? new string('a', 32) : null,
        Salt = mode == RoomVisibility.Password ? new string('b', 32) : null,
    };

    [Fact]
    public void SignedListingRoundTripsAndTamperingIsRejected()
    {
        var signer = new RoomSigner();
        var signed = signer.Sign(Listing(), _now);
        Assert.Equal(signer.RoomId, signed.Id);
        Assert.True(signed.Verify());
        var back = RoomListing.FromJson(JsonNode.Parse(signed.ToJson().ToJsonString()));
        Assert.NotNull(back);
        Assert.Equal(signed.Name, back.Name);
        Assert.Equal(_host.PublicKey, back.HostKey);

        // 改任何一个字段签名就对不上。
        var json = signed.ToJson();
        json["name"] = "别人的房间";
        Assert.Null(RoomListing.FromJson(json));
        json = signed.ToJson();
        json["ip"] = "10.235.0.9";
        Assert.Null(RoomListing.FromJson(json));
        // 编号必须是签名公钥派生的：换一把钥匙签同一个编号也不行。
        var other = new RoomSigner().Sign(Listing(), _now);
        json = other.ToJson();
        json["id"] = signed.Id;
        Assert.Null(RoomListing.FromJson(json));
        // 地址只能在公共网络网段里。
        Assert.Null(RoomListing.FromJson(signer.Sign(Listing() with { Ip = "192.168.1.2" }, _now).ToJson()));
    }

    [Fact]
    public void LongNamesAreSanitizedBeforeSigning()
    {
        var signed = new RoomSigner().Sign(Listing() with { Name = new string('房', 50) + "‮", Game = new string('9', 60) }, _now);
        Assert.NotNull(RoomListing.FromJson(signed.ToJson()));
        Assert.Equal(32, signed.Name.Length);
    }

    [Fact]
    public async Task AnnounceListLookupAndWithdraw()
    {
        var client = new RoomDirectoryClient(_net);
        var signer = new RoomSigner();
        var book = new InviteBook();
        var standing = book.CreateStanding(false, TunnelSettings.Default);
        var key = standing.Invite.LobbyKey;
        await client.AnnounceAsync(signer.Sign(Listing(invite: standing.Invite.ToString()), _now), true, [key], default);

        var rooms = await client.ListAsync(default);
        var room = Assert.Single(rooms);
        Assert.Equal(signer.RoomId, room.Id);
        Assert.Equal(RoomVisibility.Public, room.Mode);
        Assert.Equal(standing.Invite.ToString(), room.ToInvite()!.ToString());

        var hit = await client.LookupAsync(standing.Invite.LobbyKey, default);
        Assert.NotNull(hit);
        Assert.Equal("10.235.0.7", hit.Ip);
        Assert.Equal(_host.PublicKey, hit.HostKey);
        Assert.Null(await client.LookupAsync(new byte[16], default));

        await client.WithdrawAsync(signer, default);
        Assert.Empty(await client.ListAsync(default));
        Assert.Null(await client.LookupAsync(standing.Invite.LobbyKey, default));
    }

    [Fact]
    public async Task HiddenRoomsAreOnlyFoundByInvite()
    {
        var client = new RoomDirectoryClient(_net);
        var book = new InviteBook();
        var invite = book.Create(1, TimeSpan.FromMinutes(10), false, TunnelSettings.Default);
        await client.AnnounceAsync(new RoomSigner().Sign(Listing(RoomVisibility.Hidden, invite: ""), _now), false, [invite.Invite.LobbyKey], default);
        Assert.Empty(await client.ListAsync(default));
        Assert.NotNull(await client.LookupAsync(invite.Invite.LobbyKey, default));
    }

    [Fact]
    public async Task RoomsExpireWithoutRenewal()
    {
        var client = new RoomDirectoryClient(_net);
        await client.AnnounceAsync(new RoomSigner().Sign(Listing(), _now), true, [], default);
        Assert.Single(await client.ListAsync(default));
        _now += RoomDirectoryProtocol.Ttl + TimeSpan.FromSeconds(1);
        Assert.Empty(await client.ListAsync(default));
    }

    [Fact]
    public async Task ReplayAndForgedWithdrawAreRejected()
    {
        var client = new RoomDirectoryClient(_net);
        var signer = new RoomSigner();
        var first = signer.Sign(Listing() with { Players = 1 }, _now);
        var second = signer.Sign(Listing() with { Players = 5 }, _now);
        await client.AnnounceAsync(second, true, [], default);
        // 旧版本（序号小）重放回去不认。
        var replay = await Assert.ThrowsAsync<DirectoryRejectedException>(() => client.AnnounceAsync(first, true, [], default));
        Assert.Equal("stale", replay.Code);
        Assert.Equal(5, Assert.Single(await client.ListAsync(default)).Players);
        // 别人的钥匙签的撤下请求不认。
        var forged = _directory.Handle(new JsonObject { ["op"] = "withdraw", ["id"] = signer.RoomId, ["seq"] = long.MaxValue, ["sig"] = new string('0', 128) });
        Assert.False(forged["ok"]!.GetValue<bool>());
        Assert.Single(await client.ListAsync(default));
        // 撤下以后拿旧签名重新发布也不认。
        await client.WithdrawAsync(signer, default);
        await Assert.ThrowsAsync<DirectoryRejectedException>(() => client.AnnounceAsync(second, true, [], default));
        Assert.Empty(await client.ListAsync(default));
    }

    [Fact]
    public async Task OneInvitesIndexHoldsOnlyAFewRooms()
    {
        var client = new RoomDirectoryClient(_net);
        var key = new byte[16];
        for (var i = 0; i < RoomDirectoryServer.MaxEntriesPerIndex; i++)
            await client.AnnounceAsync(new RoomSigner().Sign(Listing(), _now), true, [key], default);
        // One more listed room still gets in, without an entry there; a hidden one with nothing else is refused.
        var listed = new RoomSigner();
        await client.AnnounceAsync(listed.Sign(Listing(), _now), true, [key], default);
        Assert.Contains(await client.ListAsync(default), r => r.Id == listed.RoomId);
        var more = await Assert.ThrowsAsync<DirectoryRejectedException>(() => client.AnnounceAsync(new RoomSigner().Sign(Listing(), _now), false, [key], default));
        Assert.Equal("lookup", more.Code);
    }


    [Fact]
    public async Task BadRequestsGetErrorsNotCrashes()
    {
        Assert.False(_directory.Handle(new JsonObject { ["op"] = "nope" })["ok"]!.GetValue<bool>());
        Assert.False(_directory.Handle(new JsonObject { ["op"] = "announce", ["room"] = "x" })["ok"]!.GetValue<bool>());
        Assert.False(_directory.Handle(new JsonObject { ["op"] = "lookup", ["key"] = 5 })["ok"]!.GetValue<bool>());
        // 超长的一行：直接断开，服务还活着。
        using (var tcp = new TcpClient())
        {
            await tcp.ConnectAsync(IPAddress.Loopback, _directory.Port);
            var junk = new byte[RoomDirectoryProtocol.MaxLine + 10];
            Array.Fill(junk, (byte)'a');
            try { await tcp.GetStream().WriteAsync(junk); } catch (IOException) { }
        }
        Assert.Empty(await new RoomDirectoryClient(_net).ListAsync(default));
    }

    // ---- 经公共网络敲门 ----

    private KnockServer StartKnock(InviteBook book, MemberTable members, Func<KnockRequest, bool>? approve = null)
    {
        var credentials = RoomCredentials.Create(_host.PublicKey, 25565, TunnelSettings.Default);
        var server = new KnockServer(book, _host, () => credentials, _ => false, (r, _) => Task.FromResult(approve?.Invoke(r) ?? true), IPAddress.Loopback, 0, members: members);
        _cleanup.Add(server);
        _net.Map[("10.235.0.7", KnockProtocol.Port)] = server.Port;
        return server;
    }

    [Fact]
    public async Task PublicRoomKnockFromListing()
    {
        var book = new InviteBook();
        var members = new MemberTable();
        StartKnock(book, members);
        var standing = book.CreateStanding(false, TunnelSettings.Default);
        var listing = new RoomSigner().Sign(Listing(invite: standing.Invite.ToString()), _now);
        var outcome = await PublicJoin.KnockAsync(_net, listing.Ip, listing.ToInvite()!, _guest, "Steve", "0.8.0", listing.HostKey, default);
        Assert.True(outcome.Approved);
        // 长期邀请次数不减。
        Assert.True(standing.IsUsable(DateTimeOffset.UtcNow));
        var again = await PublicJoin.KnockAsync(_net, listing.Ip, listing.ToInvite()!, X25519KeyPair.Generate(), "Bob", "0.8.0", listing.HostKey, default);
        Assert.True(again.Approved);
    }

    [Fact]
    public async Task KickDoesNotRevokeStandingInvite()
    {
        var book = new InviteBook();
        var members = new MemberTable();
        StartKnock(book, members);
        var standing = book.CreateStanding(true, TunnelSettings.Default);
        var outcome = await PublicJoin.KnockAsync(_net, "10.235.0.7", standing.Invite, _guest, "Steve", "0.8.0", null, default);
        Assert.True(outcome.Approved);
        Assert.True(members.TryGet(outcome.Credentials!.Member!.Id, out var entry));
        Assert.False(members.Kick(entry, book));
        Assert.True(standing.IsUsable(DateTimeOffset.UtcNow));
        // 被踢的人（同一把公钥）再敲门被拒。
        var back = await PublicJoin.KnockAsync(_net, "10.235.0.7", standing.Invite, _guest, "Steve", "0.8.0", null, default);
        Assert.False(back.Approved);
    }

    [Fact]
    public async Task PasswordRoomNeedsTheRightPassword()
    {
        var book = new InviteBook();
        var members = new MemberTable();
        StartKnock(book, members);
        var salt = RoomPassword.NewSalt();
        var standing = book.CreateStanding(false, TunnelSettings.Default, RoomPassword.Token("苦力怕123", salt));
        var listing = new RoomSigner().Sign(Listing(RoomVisibility.Password) with
        {
            Lobby = Convert.ToHexStringLower(standing.Invite.LobbyKey),
            Salt = Convert.ToHexStringLower(salt),
        }, _now);
        var parsed = RoomListing.FromJson(listing.ToJson())!;
        Assert.Null(parsed.ToInvite());
        var wrong = await PublicJoin.KnockAsync(_net, parsed.Ip, parsed.ToInvite("creeper")!, _guest, "Steve", "0.8.0", parsed.HostKey, default);
        Assert.False(wrong.Approved);
        var right = await PublicJoin.KnockAsync(_net, parsed.Ip, parsed.ToInvite("苦力怕123")!, _guest, "Steve", "0.8.0", parsed.HostKey, default);
        Assert.True(right.Approved);
        // 列表里没有密码，也没有令牌。
        Assert.DoesNotContain(Convert.ToHexStringLower(standing.Invite.Token), listing.ToJson().ToJsonString());
    }

    [Fact]
    public async Task HostKeyMismatchIsRejected()
    {
        var book = new InviteBook();
        StartKnock(book, new MemberTable());
        var standing = book.CreateStanding(true, TunnelSettings.Default);
        var outcome = await PublicJoin.KnockAsync(_net, "10.235.0.7", standing.Invite, _guest, "Steve", "0.8.0", X25519KeyPair.Generate().PublicKey, default);
        Assert.False(outcome.Approved);
        Assert.Equal("mismatch", outcome.Reason);
    }

    [Fact]
    public async Task InviteCodeFindsHostThroughDirectory()
    {
        var book = new InviteBook();
        StartKnock(book, new MemberTable());
        var invite = book.Create(1, TimeSpan.FromMinutes(10), true, TunnelSettings.Default);
        await using (var announcer = new RoomAnnouncer(_net, () => Listing(RoomVisibility.Hidden, invite: ""), () => [invite.Invite.LobbyKey]))
        {
            await announcer.AnnounceOnceAsync(default);
            Assert.True(announcer.Published, announcer.LastError);
            var outcome = await PublicJoin.TryKnockByInviteAsync(_net, invite.Invite, _guest, "Steve", "0.8.0", default);
            Assert.NotNull(outcome);
            Assert.True(outcome.Approved);
        }
        // 关房间时撤下：再找就找不到。
        Assert.Null(await PublicJoin.TryKnockByInviteAsync(_net, book.Create(1, null, true, TunnelSettings.Default).Invite, _guest, "Steve", "0.8.0", default));
    }

    [Fact]
    public async Task AnnouncerReportsMissingAddress()
    {
        _net.Ip = null;
        await using var announcer = new RoomAnnouncer(_net, () => Listing(), () => [], every: TimeSpan.FromHours(1));
        await announcer.AnnounceOnceAsync(default);
        Assert.False(announcer.Published);
        Assert.NotNull(announcer.LastError);
    }

    [Fact]
    public void ArgsKeepPublicNetworkLockedDown()
    {
        var profile = PublicNetworkProfile.Jay(["tcp://seed.example:11010"]);
        var member = PublicNetworkArgs.Build(new PublicNetworkOptions(profile, 15000, 15001));
        Assert.Contains("--disable-p2p", member);
        Assert.Contains("--no-tun", member);
        Assert.Contains("--no-listener", member);
        Assert.Equal(KnockProtocol.Port.ToString(), member[member.IndexOf("--tcp-whitelist") + 1]);
        Assert.Equal("1", member[member.IndexOf("--udp-whitelist") + 1]);
        Assert.Equal("jay", member[member.IndexOf("--hostname") + 1]);
        Assert.Equal("15001", member[member.IndexOf("--socks5") + 1]);
        Assert.Contains("tcp://seed.example:11010", member);
        // 密钥只经环境变量传。
        Assert.DoesNotContain(PublicNetworkProfile.JaySecret, member);
        Assert.Equal(PublicNetworkProfile.JaySecret, PublicNetworkArgs.Environment(new PublicNetworkOptions(profile, 1, 2))[EasyTierArgs.SecretVariable]);

        var directory = PublicNetworkArgs.Build(new PublicNetworkOptions(profile, 15000, 15001, Directory: true, Listeners: ["tcp://0.0.0.0:11010"]));
        Assert.Equal(PublicNetworkArgs.DirectoryIpWithMask, directory[directory.IndexOf("--ipv4") + 1]);
        Assert.Equal(PublicNetworkProfile.DirectoryPort.ToString(), directory[directory.IndexOf("--tcp-whitelist") + 1]);
        Assert.DoesNotContain("--no-listener", directory);
        Assert.False(PublicNetworkProfile.Jay().IsUsable == (PublicNetworkProfile.JayNodes.Length == 0));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var c in _cleanup) await c.DisposeAsync();
        await _directory.DisposeAsync();
    }
}
