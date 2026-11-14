using System.Net;
using System.Net.Sockets;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Tests;

/// <summary>房主大厅只在有可用邀请时开着：发邀请就开，用完 / 撤销 / 过期就关（EasyTier 用假的代替）。</summary>
public sealed class HostLobbyTests
{
    private sealed class FakeLobby(List<LobbyOptions> started, LobbyOptions options) : IDisposable
    {
        public bool Disposed;
        public LobbyOptions Options => options;
        public void Dispose()
        {
            Disposed = true;
            lock (started) started.Remove(options);
        }
    }

    [Fact]
    public async Task OpensOnlyWhileInvitesArePending()
    {
        var now = DateTimeOffset.UtcNow;
        var running = new List<LobbyOptions>();
        using var host = X25519KeyPair.Generate();
        using var guest = X25519KeyPair.Generate();
        var credentials = RoomCredentials.Create(host.PublicKey, 25565, TunnelSettings.Default);
        var admitted = 0;
        KnockServer? knock = null;
        await using var lobby = new HostLobby(o =>
            {
                lock (running) running.Add(o);
                return new FakeLobby(running, o);
            }, () => ["tcp://127.0.0.1:1"],
            book => knock = new KnockServer(book, host, () => credentials, _ => false, (_, _) => Task.FromResult(true), IPAddress.Loopback, 0),
            book: new InviteBook(() => now));
        lobby.Admitted += _ => Interlocked.Increment(ref admitted);
        Assert.False(lobby.IsOpen);

        var invite = lobby.Book.Create(1, TimeSpan.FromMinutes(10), false, TunnelSettings.Default);
        Assert.True(await WaitAsync(() => lobby.IsOpen));
        var options = Assert.Single(running);
        Assert.True(options.IsHost);
        Assert.Equal(LobbyKeys.LobbyName(invite.Invite.LobbyKey), options.Name);
        Assert.Equal(LobbyKeys.LobbySecret(invite.Invite.LobbyKey), options.Secret);
        Assert.DoesNotContain(options.Secret, options.ToString());

        // 敲门用掉唯一一次：大厅关掉，大厅密钥丢掉。
        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, knock!.Port);
            var outcome = await KnockClient.KnockAsync(client.GetStream(), invite.Invite, guest, "Alex", "1");
            Assert.True(outcome.Approved);
        }
        Assert.True(await WaitAsync(() => admitted == 1));
        lobby.Book.Tick();
        Assert.True(await WaitAsync(() => !lobby.IsOpen));
        Assert.Empty(running);
        Assert.Null(lobby.Book.Lobby);

        // 过期：时间一到，下一次检查就关。
        var second = lobby.Book.Create(1, TimeSpan.FromMinutes(10), false, TunnelSettings.Default);
        Assert.True(await WaitAsync(() => lobby.IsOpen));
        Assert.NotEqual(invite.Invite.LobbyKey, second.Invite.LobbyKey);
        now += TimeSpan.FromMinutes(11);
        lobby.Book.Tick();
        Assert.True(await WaitAsync(() => !lobby.IsOpen));

        // 撤销。
        var third = lobby.Book.Create(1, null, false, TunnelSettings.Default);
        Assert.True(await WaitAsync(() => lobby.IsOpen));
        lobby.Book.Revoke(third);
        Assert.True(await WaitAsync(() => !lobby.IsOpen));
        Assert.Empty(running);
    }

    [Fact]
    public void LobbyArgsAreSeparateFromTheRoom()
    {
        var host = LobbyArgs.Build(new LobbyOptions("jay-l-0011223344556677", "secret", true, ["tcp://1.2.3.4:11010"], 15000));
        Assert.Contains("10.234.0.1", host);
        Assert.Equal("47660", host[host.IndexOf("--tcp-whitelist") + 1]);
        Assert.Contains("--no-tun", host);
        Assert.Contains("--no-listener", host);
        Assert.DoesNotContain("secret", host);
        Assert.DoesNotContain("--port-forward", host);
        var guest = LobbyArgs.Build(new LobbyOptions("jay-l-0011223344556677", "secret", false, ["tcp://1.2.3.4:11010"], 15001, 40000));
        Assert.Equal("1", guest[guest.IndexOf("--tcp-whitelist") + 1]);
        Assert.Contains("tcp://127.0.0.1:40000/10.234.0.1:47660", guest);
        Assert.StartsWith("10.234.0.", guest[guest.IndexOf("--ipv4") + 1]);
        Assert.NotEqual("10.234.0.1", guest[guest.IndexOf("--ipv4") + 1]);
        Assert.Equal("secret", LobbyArgs.Environment(new LobbyOptions("n", "secret", true, [], 1))[EasyTierArgs.SecretVariable]);
    }

    /// <summary>
    /// Nobody in a lobby opens a direct link: the knocker can't know yet whether the room is "relays only", and a
    /// direct link would show the host and every knocker each other's addresses.
    /// </summary>
    [Fact]
    public void ALobbyNeverOpensADirectLink()
    {
        LobbyOptions[] all =
        [
            new("jay-l-x", "s", true, ["tcp://relay.example:11010"], 15000),
            new("jay-l-x", "s", false, ["tcp://relay.example:11010"], 15001, 40000),
            // Direct rooms: the host listens on loopback for the bridge, the guest reaches it through the bridge.
            new("jay-l-x", "s", true, [], 15002, null, 40123),
            new("jay-l-x", "s", false, ["udp://127.0.0.1:40124"], 15003, 40001),
        ];
        foreach (var options in all)
        {
            var args = LobbyArgs.Build(options);
            Assert.Contains("--disable-p2p", args);
            for (var i = args.IndexOf("--listeners"); i >= 0; i = args.IndexOf("--listeners", i + 1))
                Assert.StartsWith("udp://127.0.0.1:", args[i + 1]);
        }
    }

    private static async Task<bool> WaitAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100; i++)
        {
            if (condition()) return true;
            await Task.Delay(50);
        }
        return condition();
    }
}
