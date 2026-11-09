using System.Net;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Public;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Tests;

/// <summary>
/// 用真的 EasyTier 跑公共网络：一个种子节点 + 目录、一个房主、一个加入方，都在本机。
/// 只在设置了 JAY_ET_DIR（放 easytier-core 和 easytier-cli 的文件夹）时运行，CI 里默认跳过。
/// </summary>
public sealed class PublicNetworkEasyTierTests
{
    private static string? EtDir => Environment.GetEnvironmentVariable("JAY_ET_DIR");

    [Fact]
    public async Task HostPublishesAndGuestJoinsThroughRealEasyTier()
    {
        if (EtDir is not { } dir) return;
        var exe = OperatingSystem.IsWindows() ? ".exe" : "";
        var core = Path.Combine(dir, "easytier-core" + exe);
        var cli = Path.Combine(dir, "easytier-cli" + exe);
        var work = Path.Combine(Path.GetTempPath(), "jay-public-e2e-" + Guid.NewGuid().ToString("N")[..8]);
        var nodePort = EasyTierArgs.FreeTcpPort();
        var profile = PublicNetworkProfile.Jay([$"tcp://127.0.0.1:{nodePort}"]);

        // 目录服务监听 127.0.0.1:47668（和真的部署一样）。
        await using var directory = new RoomDirectoryServer(IPAddress.Loopback, PublicNetworkProfile.DirectoryPort);
        using var seed = PublicNetworkSession.Start(core, cli, new PublicNetworkOptions(profile with { Nodes = [] }, EasyTierArgs.FreeTcpPort(), EasyTierArgs.FreeTcpPort(),
            Directory: true, Listeners: [$"tcp://127.0.0.1:{nodePort}"]), Path.Combine(work, "seed"));
        using var host = PublicNetworkSession.Start(core, cli, new PublicNetworkOptions(profile, EasyTierArgs.FreeTcpPort(), EasyTierArgs.FreeTcpPort()), Path.Combine(work, "host"));
        using var guest = PublicNetworkSession.Start(core, cli, new PublicNetworkOptions(profile, EasyTierArgs.FreeTcpPort(), EasyTierArgs.FreeTcpPort()), Path.Combine(work, "guest"));
        await host.WaitForDirectoryAsync(TimeSpan.FromSeconds(60), default);
        await guest.WaitForDirectoryAsync(TimeSpan.FromSeconds(60), default);

        // 房主：敲门服务在 127.0.0.1:47660，公开房间（长期邀请，自动批准）。
        var hostKey = X25519KeyPair.Generate();
        var book = new InviteBook();
        var credentials = RoomCredentials.Create(hostKey.PublicKey, 25565, TunnelSettings.Default);
        await using var knock = new KnockServer(book, hostKey, () => credentials, _ => false, (_, _) => Task.FromResult(true), IPAddress.Loopback, KnockProtocol.Port, members: new MemberTable());
        var standing = book.CreateStanding(true, TunnelSettings.Default);
        await using var announcer = new RoomAnnouncer(host, () => new RoomListing
        {
            Id = "", Name = "e2e", Host = "host", Mode = RoomVisibility.Public, HostKey = hostKey.PublicKey, Invite = standing.Invite.ToString(), Players = 1,
        }, () => [RoomDirectoryProtocol.LookupKey(standing.Invite.LobbyKey)]);
        await announcer.AnnounceOnceAsync(default);
        Assert.True(announcer.Published, announcer.LastError);

        // 加入方：看列表、敲门。
        var rooms = await new RoomDirectoryClient(guest).ListAsync(default);
        var room = Assert.Single(rooms);
        Assert.Equal(await host.MyIpAsync(default), room.Ip);
        var outcome = await PublicJoin.KnockAsync(guest, room.Ip, room.ToInvite()!, X25519KeyPair.Generate(), "guest", "e2e", room.HostKey, default);
        Assert.True(outcome.Approved, outcome.Reason);
        Assert.Equal(credentials.Network, outcome.Credentials!.Network);

        // 公共网络里别的端口都连不上（只放行敲门端口）。
        await Assert.ThrowsAnyAsync<IOException>(() => guest.ConnectAsync(room.Ip, 22, default));
        // 房主关房间时撤下。
        await announcer.DisposeAsync();
        Assert.Empty(await new RoomDirectoryClient(guest).ListAsync(default));
        try { Directory.Delete(work, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// 网络服务端框架 + 真 EasyTier：一个目录节点、一个只当种子的地区节点（连到目录节点）。房主经地区节点、加入方经目录节点，
    /// 照样在同一个目录里碰面；屏蔽词经真网络把原因带回房主；封禁后房间马上消失。
    /// </summary>
    [Fact]
    public async Task FrameworkDirectoryAndSeedOnlyNodeWorkThroughRealEasyTier()
    {
        if (EtDir is not { } dir) return;
        var exe = OperatingSystem.IsWindows() ? ".exe" : "";
        var core = Path.Combine(dir, "easytier-core" + exe);
        var cli = Path.Combine(dir, "easytier-cli" + exe);
        var work = Path.Combine(Path.GetTempPath(), "jay-netserver-e2e-" + Guid.NewGuid().ToString("N")[..8]);
        var mainPort = EasyTierArgs.FreeTcpPort();
        var regionPort = EasyTierArgs.FreeTcpPort();

        await using var main = new Amber.NetworkServer.JayNetworkServerBuilder().UseDataDir(Path.Combine(work, "main")).UseEasyTier(core, cli)
            .UseListenPort(mainPort).UseAdmin(0).UseBlockedWords(["广告"]).Build();
        await main.StartAsync();
        await using var region = new Amber.NetworkServer.JayNetworkServerBuilder().UseDataDir(Path.Combine(work, "region")).UseEasyTier(core, cli)
            .UseListenPort(regionPort).UseSeeds([$"tcp://127.0.0.1:{mainPort}"]).SeedOnly().WithoutAdmin().Build();
        await region.StartAsync();

        var profile = PublicNetworkProfile.Jay();
        using var host = PublicNetworkSession.Start(core, cli, new PublicNetworkOptions(profile with { Nodes = [$"tcp://127.0.0.1:{regionPort}"] },
            EasyTierArgs.FreeTcpPort(), EasyTierArgs.FreeTcpPort()), Path.Combine(work, "host"));
        using var guest = PublicNetworkSession.Start(core, cli, new PublicNetworkOptions(profile with { Nodes = [$"tcp://127.0.0.1:{mainPort}"] },
            EasyTierArgs.FreeTcpPort(), EasyTierArgs.FreeTcpPort()), Path.Combine(work, "guest"));
        await host.WaitForDirectoryAsync(TimeSpan.FromSeconds(60), default);
        await guest.WaitForDirectoryAsync(TimeSpan.FromSeconds(60), default);

        var hostKey = X25519KeyPair.Generate();
        var standing = new InviteBook().CreateStanding(true, TunnelSettings.Default);
        // 地址是 EasyTier 自动分配的，刚连上时可能还没分到。
        string? myIp = null;
        for (var i = 0; i < 60 && (myIp = await host.MyIpAsync(default)) is null; i++) await Task.Delay(500);
        Assert.StartsWith(PublicNetworkProfile.Subnet, myIp);
        Assert.NotNull(myIp);
        RoomListing Room(string name) => new()
        {
            Id = "", Name = name, Host = "host", Mode = RoomVisibility.Public, HostKey = hostKey.PublicKey, Invite = standing.Invite.ToString(), Players = 1, Ip = myIp!,
        };
        var client = new RoomDirectoryClient(host);
        var rejected = await Assert.ThrowsAsync<DirectoryRejectedException>(() => client.AnnounceAsync(new RoomSigner().Sign(Room("广告房"), DateTimeOffset.UtcNow), true, [], default));
        Assert.Equal("name", rejected.Code);
        Assert.Contains("不允许的词", rejected.Message);

        var signer = new RoomSigner();
        await client.AnnounceAsync(signer.Sign(Room("好房间"), DateTimeOffset.UtcNow), true, [], default);
        var room = Assert.Single(await new RoomDirectoryClient(guest).ListAsync(default));
        Assert.Equal("好房间", room.Name);
        Assert.True(main.Health()["ok"]!.GetValue<bool>());
        Assert.True(region.Health()["ok"]!.GetValue<bool>());

        using var http = new HttpClient();
        var admin = Amber.NetworkServer.AdminClient.ForDataDir(http, main.DataDir, main.AdminPort!.Value);
        await admin.CallAsync("POST", "/ban", new System.Text.Json.Nodes.JsonObject { ["kind"] = "host", ["value"] = MemberIdentity.FingerprintOf(hostKey.PublicKey) });
        Assert.Empty(await new RoomDirectoryClient(guest).ListAsync(default));
        var banned = await Assert.ThrowsAsync<DirectoryRejectedException>(() => client.AnnounceAsync(signer.Sign(Room("好房间"), DateTimeOffset.UtcNow.AddSeconds(1)), true, [], default));
        Assert.Equal("banned", banned.Code);
        try { Directory.Delete(work, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
