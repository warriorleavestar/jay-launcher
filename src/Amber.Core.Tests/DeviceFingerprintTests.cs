using System.Net;
using System.Net.Sockets;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Public;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Tests;

/// <summary>设备指纹：指纹码的格式和算法、本机保存、目录按码找房间、房主要求设备指纹 / 只让名单里的电脑进。</summary>
public sealed class DeviceFingerprintTests : IAsyncDisposable
{
    private sealed class FakeNetwork : IPublicNetwork
    {
        public readonly Dictionary<(string, int), int> Map = [];

        public async Task<Stream> ConnectAsync(string ip, int port, CancellationToken ct)
        {
            if (!Map.TryGetValue((ip, port), out var local)) throw new IOException("unreachable");
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, local, ct);
            return client.GetStream();
        }

        public Task<string?> MyIpAsync(CancellationToken ct) => Task.FromResult<string?>("10.235.0.7");
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jay-fp-" + Guid.NewGuid().ToString("N"));
    private readonly FakeNetwork _net = new();
    private readonly X25519KeyPair _host = X25519KeyPair.Generate();
    private readonly X25519KeyPair _guest = X25519KeyPair.Generate();
    private readonly List<IAsyncDisposable> _cleanup = [];

    private string Dir(string name) => Path.Combine(_dir, name);

    [Fact]
    public void CodesAreDifferentEachTimeButAllBelongToTheDevice()
    {
        var device = DeviceFingerprint.Enable(Dir("a"));
        var other = DeviceFingerprint.Enable(Dir("b"));
        var codes = Enumerable.Range(0, 50).Select(_ => device.NewCode()).ToList();
        Assert.Equal(codes.Count, codes.Distinct().Count());
        foreach (var code in codes)
        {
            Assert.Matches("^JAYF1(-[0-9A-HJKMNP-TV-Z]{4}){5}$", code);
            Assert.True(device.Owns(code));
            Assert.False(other.Owns(code));
        }
        // 抄写时的大小写、空格、O/0 混用都认。
        var sloppy = codes[0].ToLowerInvariant().Replace("-", " ").Replace('0', 'o');
        Assert.True(device.Owns(sloppy));
    }

    [Theory]
    [InlineData("")]
    [InlineData("JAYF1-XXXX")]
    [InlineData("JAYF9-0000-0000-0000-0000-0000")]
    [InlineData("JAY2-0000-0000-0000-0000-0000")]
    [InlineData("JAYF1-0000-0000-0000-0000-000U")]
    public void BadCodesAreRejected(string text) => Assert.False(FingerprintCode.TryParse(text, out _));

    [Fact]
    public void CodeWithWrongPaddingIsRejected()
    {
        var code = DeviceFingerprint.Enable(Dir("a")).NewCode();
        // 最后一个字符只有最高 1 位是数据，低 4 位是补的 0：改动低位就不认。
        const string crockford = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        var bumped = code[..^1] + crockford[crockford.IndexOf(code[^1]) ^ 1];
        Assert.True(FingerprintCode.TryParse(code, out _));
        Assert.False(FingerprintCode.TryParse(bumped, out _));
    }

    [Fact]
    public void FingerprintIsOffByDefaultAndRotateVoidsOldCodes()
    {
        var dir = Dir("a");
        Assert.False(DeviceFingerprint.IsEnabled(dir));
        Assert.Null(DeviceFingerprint.Load(dir));
        var device = DeviceFingerprint.Enable(dir);
        var code = device.NewCode();
        var again = DeviceFingerprint.Load(dir)!;
        Assert.Equal(device.DeviceId, again.DeviceId);
        Assert.True(again.Owns(code));
        var rotated = DeviceFingerprint.Rotate(dir);
        Assert.NotEqual(device.DeviceId, rotated.DeviceId);
        Assert.False(rotated.Owns(code));
        DeviceFingerprint.Disable(dir);
        Assert.Null(DeviceFingerprint.Load(dir));
    }

    [Fact]
    public void ProofIsBoundToHostAndGuest()
    {
        var device = DeviceFingerprint.Enable(Dir("a"));
        var proof = device.Prove(_host.PublicKey, _guest.PublicKey, []);
        Assert.True(proof.Verify(_host.PublicKey, _guest.PublicKey));
        Assert.False(proof.Verify(X25519KeyPair.Generate().PublicKey, _guest.PublicKey));
        Assert.False(proof.Verify(_host.PublicKey, X25519KeyPair.Generate().PublicKey));
    }

    // ---- 目录按指纹码找房间 ----

    private async Task<RoomDirectoryServer> StartDirectoryAsync()
    {
        var directory = new RoomDirectoryServer(IPAddress.Loopback, 0);
        _cleanup.Add(directory);
        _net.Map[(PublicNetworkProfile.DirectoryIp, PublicNetworkProfile.DirectoryPort)] = directory.Port;
        await Task.CompletedTask;
        return directory;
    }

    private RoomListing Hidden(string invite) => new()
    {
        Id = "",
        Name = "朋友房",
        Host = "Alex",
        Mode = RoomVisibility.Hidden,
        HostKey = _host.PublicKey,
        Ip = "10.235.0.7",
        Invite = invite,
    };

    [Fact]
    public async Task HiddenRoomIsFoundOnlyByTheHostsCodes()
    {
        await StartDirectoryAsync();
        var client = new RoomDirectoryClient(_net);
        var device = DeviceFingerprint.Enable(Dir("host"));
        var invite = new InviteBook().CreateStanding(false, TunnelSettings.Default).Invite.ToString();
        var signer = new RoomSigner();
        await client.AnnounceAsync(signer.Sign(Hidden(invite), DateTimeOffset.UtcNow), false, [], default, device.SearchKey);

        // 不在公开列表里。
        Assert.Empty(await client.ListAsync(default));
        // 每个码都找得到，带着那张邀请。
        for (var i = 0; i < 3; i++)
        {
            var found = await client.FindAsync(device.NewCode(), default);
            Assert.NotNull(found);
            Assert.Equal(signer.RoomId, found.Id);
            Assert.NotNull(found.ToInvite());
        }
        // 别的电脑的码找不到。
        Assert.Null(await client.FindAsync(DeviceFingerprint.Enable(Dir("other")).NewCode(), default));
        // 不带指纹密钥续期（房主关了指纹查找）：再也找不到。
        await client.AnnounceAsync(signer.Sign(Hidden(invite), DateTimeOffset.UtcNow.AddSeconds(1)), false, [new byte[16]], default);
        Assert.Null(await client.FindAsync(device.NewCode(), default));
    }

    [Fact]
    public async Task SearchKeyIsFirstComeFirstServed()
    {
        await StartDirectoryAsync();
        var client = new RoomDirectoryClient(_net);
        var device = DeviceFingerprint.Enable(Dir("host"));
        var invite = new InviteBook().CreateStanding(false, TunnelSettings.Default).Invite.ToString();
        await client.AnnounceAsync(new RoomSigner().Sign(Hidden(invite), DateTimeOffset.UtcNow), false, [], default, device.SearchKey);
        var e = await Assert.ThrowsAsync<DirectoryRejectedException>(() =>
            client.AnnounceAsync(new RoomSigner().Sign(Hidden(invite), DateTimeOffset.UtcNow), false, [], default, device.SearchKey));
        Assert.Equal("lookup", e.Code);
    }

    // ---- 房主的设备要求 ----

    private (InviteBook Book, JayInvite Invite) StartKnock(Func<DevicePolicy> policy, List<KnockRequest>? seen = null)
    {
        var book = new InviteBook();
        var credentials = RoomCredentials.Create(_host.PublicKey, 25565, TunnelSettings.Default);
        var server = new KnockServer(book, _host, () => credentials, _ => false, (r, _) =>
        {
            seen?.Add(r);
            return Task.FromResult(true);
        }, IPAddress.Loopback, 0) { DevicePolicy = policy };
        _cleanup.Add(server);
        _net.Map[("10.235.0.7", KnockProtocol.Port)] = server.Port;
        return (book, book.CreateStanding(false, TunnelSettings.Default).Invite);
    }

    [Fact]
    public async Task HostCanRequireADeviceFingerprint()
    {
        var seen = new List<KnockRequest>();
        var (_, invite) = StartKnock(() => new DevicePolicy(true, []), seen);
        var without = await PublicJoin.KnockAsync(_net, "10.235.0.7", invite, _guest, "Steve", "0.8.0", null, default);
        Assert.Equal("device", without.Reason);
        var device = DeviceFingerprint.Enable(Dir("guest"));
        var with = await PublicJoin.KnockAsync(_net, "10.235.0.7", invite, _guest, "Steve", "0.8.0", null, default, device);
        Assert.True(with.Approved);
        Assert.Equal(device.DeviceId, Assert.Single(seen).DeviceId);
    }

    [Fact]
    public async Task HostCanAllowOnlyListedComputers()
    {
        var friend = DeviceFingerprint.Enable(Dir("friend"));
        var stranger = DeviceFingerprint.Enable(Dir("stranger"));
        var codes = DevicePolicy.ParseCodes(DeviceFingerprint.Enable(Dir("x")).NewCode() + "\n" + friend.NewCode());
        Assert.Equal(2, codes.Count);
        var (_, invite) = StartKnock(() => new DevicePolicy(false, codes));
        Assert.Equal("device", (await PublicJoin.KnockAsync(_net, "10.235.0.7", invite, _guest, "S", "0.8.0", null, default, stranger)).Reason);
        Assert.Equal("device", (await PublicJoin.KnockAsync(_net, "10.235.0.7", invite, _guest, "S", "0.8.0", null, default)).Reason);
        Assert.True((await PublicJoin.KnockAsync(_net, "10.235.0.7", invite, _guest, "S", "0.8.0", null, default, friend)).Approved);
    }

    [Fact]
    public async Task DeviceProofIsNotSentWhenHostDoesNotAsk()
    {
        var seen = new List<KnockRequest>();
        var (_, invite) = StartKnock(() => DevicePolicy.None, seen);
        var outcome = await PublicJoin.KnockAsync(_net, "10.235.0.7", invite, _guest, "S", "0.8.0", null, default, DeviceFingerprint.Enable(Dir("guest")));
        Assert.True(outcome.Approved);
        Assert.Null(Assert.Single(seen).DeviceId);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var d in _cleanup) await d.DisposeAsync();
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
