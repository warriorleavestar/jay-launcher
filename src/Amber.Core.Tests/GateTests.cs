using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Amber.Core.Localization;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Gate;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Tests;

/// <summary>Jay 服务器网关：邀请码格式、入服、进服、用户名核对、设备上限、扫描器拿不到任何回应。</summary>
public sealed class GateTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jay-gate-test-" + Guid.NewGuid().ToString("N"));
    private GateStore _store = null!;
    private GateServer _server = null!;
    private X25519KeyPair _serverKey = null!;
    private TcpListener _backend = null!;
    private readonly List<string> _log = [];

    public async Task InitializeAsync()
    {
        _backend = new TcpListener(IPAddress.Loopback, 0);
        _backend.Start();
        _store = GateStore.Initialize(Path.Combine(_root, "gate"), "127.0.0.1", 1, 0);
        var config = _store.LoadConfig();
        config.ListenAddress = "127.0.0.1";
        config.BackendPort = ((IPEndPoint)_backend.LocalEndpoint).Port;
        _serverKey = _store.LoadServerKey();
        _server = new GateServer(_store, config, _serverKey, line => { lock (_log) _log.Add(line); });
        _server.Start();
        config.PublicPort = _server.Port;
        _store.SaveConfig(config);
        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        _backend.Stop();
        _serverKey.Dispose();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private MemberIdentity Identity(string name) => MemberIdentity.LoadOrCreate(Path.Combine(_root, "client-" + name));

    private GateInvite Invite(string name, int uses = 1) => _store.CreateInvite(name, TimeSpan.FromHours(1), uses, _serverKey.PublicKey);

    /// <summary>A server with a domain: the plain code carries the domain, and never the player's name.</summary>
    [Fact]
    public void InviteRoundTripsAndToleratesCopyPaste()
    {
        var invite = Invite("Steve") with { Host = "mc.example.com" };
        var code = invite.Encode();
        Assert.StartsWith(GateInvite.Prefix, code);
        Assert.Equal((code, (string?)null), invite.ToCode());
        var parsed = GateInvite.TryParse($"来玩吧：\n{code.ToLowerInvariant()}  （24 小时内有效）", out var error);
        Assert.Null(error);
        Assert.NotNull(parsed);
        Assert.Equal("mc.example.com", parsed.Host);
        Assert.Equal(invite.Port, parsed.Port);
        Assert.Equal(invite.Token, parsed.Token);
        Assert.Equal(invite.ServerKeyHash, parsed.ServerKeyHash);
        var raw = GateInvite.Base32Decode(code[GateInvite.Prefix.Length..])!;
        Assert.DoesNotContain("Steve", System.Text.Encoding.ASCII.GetString(raw), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Convert.ToHexString(invite.Token), invite.ToString());
        Assert.DoesNotContain("mc.example.com", invite.ToString());
    }

    /// <summary>A server with only an IP: no plain code at all; the sealed code shows no address and opens with the passphrase.</summary>
    [Fact]
    public void IpOnlyServerGetsASealedInvite()
    {
        var invite = Invite("Steve");
        Assert.True(invite.NeedsSeal);
        Assert.Throws<InvalidOperationException>(() => invite.Encode());
        var (code, passphrase) = invite.ToCode();
        Assert.NotNull(passphrase);
        Assert.StartsWith(GateInvite.SealedPrefix, code);
        Assert.True(GateInvite.IsSealed(code));
        Assert.DoesNotMatch(@"\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}", code);
        Assert.Equal(PastedCodeKind.JayServer, PastedCode.Detect(code));

        // Without the passphrase it only says it needs one.
        Assert.Null(GateInvite.TryParse(code, out var error));
        Assert.Equal(L.T("core.gate.invite.sealed"), error);
        Assert.Null(GateInvite.TryOpen(code, "0000 0000 0000 0000", out error));
        Assert.Equal(L.T("core.mp.sealed.wrong_passphrase"), error);
        Assert.Null(GateInvite.TryOpen(code, "123", out error));
        Assert.Equal(L.T("core.mp.sealed.passphrase_format"), error);

        var opened = GateInvite.TryOpen("加入：" + code + " 口令另说", passphrase.Replace(" ", ""), out error);
        Assert.Null(error);
        Assert.NotNull(opened);
        Assert.Equal("127.0.0.1", opened.Host);
        Assert.Equal(invite.Port, opened.Port);
        Assert.Equal(invite.Token, opened.Token);
        Assert.Equal(invite.ServerKeyHash, opened.ServerKeyHash);
        // A plain code is opened by TryOpen too, passphrase or not.
        var domain = invite with { Host = "mc.example.com" };
        Assert.Equal("mc.example.com", GateInvite.TryOpen(domain.Encode(), null, out _)!.Host);
    }

    /// <summary>A hand-made plain code with an IP in it, and an old JAYS1 code, are refused with a reason.</summary>
    [Fact]
    public void PlainCodeWithAnIpAndOldCodesAreRefused()
    {
        var invite = Invite("Steve");
        var body = new List<byte> { 1, (byte)invite.Host.Length };
        body.AddRange(System.Text.Encoding.ASCII.GetBytes(invite.Host));
        body.AddRange(new byte[] { (byte)(invite.Port >> 8), (byte)invite.Port });
        body.AddRange(invite.ServerKeyHash);
        body.AddRange(invite.Token);
        var bytes = body.ToArray();
        var checksum = System.Security.Cryptography.SHA256.HashData([.. "jay-invite-v2"u8, .. bytes])[..4];
        Assert.Null(GateInvite.TryParse(GateInvite.Prefix + GateInvite.Base32Encode([.. bytes, .. checksum]), out var error));
        Assert.Equal(L.T("core.gate.invite.ip_needs_seal"), error);

        Assert.Null(GateInvite.TryParse("请进：JAYS1-04632E9J5RRKCE1E64Q34C", out error));
        Assert.Equal(L.T("core.gate.invite.old"), error);
        Assert.Equal(PastedCodeKind.JayServer, PastedCode.Detect("JAYS1-04632E9J5RRKCE1E64Q34C"));
    }

    [Fact]
    public void DiagnosticsRedactServerInvites()
    {
        var text = NetDiagnostics.Redact("a JAYS2-04632E9J5RRKCE1E64Q34C b jayss1-QUJDREVGR0hJSktMTU5PUA c JAYS1-04632E9J5RRKCE1E64Q34D", [], includePublicIps: true);
        Assert.DoesNotContain("04632E9J5RRKCE1E64Q34", text);
        Assert.DoesNotContain("QUJDREVGR0hJSktMTU5PUA", text);
    }

    [Theory]
    [InlineData("mc.example.com", false)]
    [InlineData("localhost", false)]
    [InlineData("203.0.113.5", true)]
    [InlineData("2408:8000::1", true)]
    [InlineData("[2408:8000::1]", true)]
    public void IpHostsNeedASeal(string host, bool ip) => Assert.Equal(ip, GateInvite.IsIpLiteral(host));

    [Fact]
    public void TamperedInviteIsRejected()
    {
        var code = (Invite("Steve") with { Host = "mc.example.com" }).Encode();
        for (var i = GateInvite.Prefix.Length; i < code.Length; i += 7)
        {
            var c = code[i] == 'Z' ? 'Y' : 'Z';
            var tampered = code[..i] + c + code[(i + 1)..];
            Assert.Null(GateInvite.TryParse(tampered, out var error));
            Assert.NotNull(error);
        }
        Assert.Null(GateInvite.TryParse(code[..^3], out _));
        Assert.Null(GateInvite.TryParse("JAY2-ABC", out _));
    }

    [Fact]
    public async Task EnrollThenJoinForwardsLoginToBackend()
    {
        using var identity = Identity("a");
        var membership = await GateClient.EnrollAsync(Invite("Steve"), identity);
        Assert.Equal("Steve", membership.Username);
        Assert.Equal(_serverKey.PublicKey, membership.ServerPublicKey);
        Assert.Equal(_store.AccessKey(), membership.AccessKey);

        await using var local = new TunnelClient(0, GateClient.Opener(membership, identity));
        using var game = new TcpClient();
        await game.ConnectAsync(IPAddress.Loopback, local.Port);
        var hello = LoginPackets("steve");
        await game.GetStream().WriteAsync(hello);

        using var server = await _backend.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var received = new byte[hello.Length];
        await server.GetStream().ReadExactlyAsync(received).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(hello, received);

        await server.GetStream().WriteAsync("pong"u8.ToArray());
        var reply = new byte[4];
        await game.GetStream().ReadExactlyAsync(reply).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("pong"u8.ToArray(), reply);
        Assert.NotNull(_store.Read().Member("Steve")!.Devices.Single().LastSeen);
    }

    [Theory]
    [InlineData("revoke")]
    [InlineData("disable")]
    [InlineData("remove")]
    public async Task RevokedDeviceIsDroppedWhileOnline(string how)
    {
        using var identity = Identity("a");
        var membership = await GateClient.EnrollAsync(Invite("Steve"), identity);
        await using var local = new TunnelClient(0, GateClient.Opener(membership, identity));
        using var game = new TcpClient();
        await game.ConnectAsync(IPAddress.Loopback, local.Port);
        await game.GetStream().WriteAsync(LoginPackets("Steve"));
        using var server = await _backend.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await server.GetStream().WriteAsync("pong"u8.ToArray());
        var reply = new byte[4];
        await game.GetStream().ReadExactlyAsync(reply).AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        switch (how)
        {
            case "revoke": Assert.Equal(1, _store.RevokeDevices("Steve", null)); break;
            case "disable": Assert.True(_store.SetDisabled("Steve", true)); break;
            default: Assert.True(_store.RemoveMember("Steve")); break;
        }

        // 网关每秒看一次名单：在线的连接要被断开（读到结尾或连接被重置），不用等玩家重连。
        var dropped = false;
        try { dropped = await game.GetStream().ReadAsync(new byte[16]).AsTask().WaitAsync(TimeSpan.FromSeconds(10)) == 0; }
        catch (IOException) { dropped = true; }
        Assert.True(dropped);
        lock (_log) Assert.Contains(_log, l => l.Contains("Steve"));
    }

    /// <summary>A device taken off the list is told so and gets no address book (relays, rotation seeds) with the answer.</summary>
    [Fact]
    public async Task ARevokedDeviceGetsNoAddressBook()
    {
        using var identity = Identity("a");
        var membership = await GateClient.EnrollAsync(Invite("Steve"), identity);
        _store.RevokeDevices("Steve", null);
        GateNetwork? seen = null;
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, membership.Port);
        var refused = await Assert.ThrowsAsync<GateRejectedException>(() =>
            GateProtocol.JoinAsync(tcp.GetStream(), membership, identity.KeyPair, JoinRequest.Direct, n => seen = n, default));
        Assert.Equal(GateStatus.NotMember, refused.Status);
        Assert.Null(seen);
    }

    /// <summary>A disabled member who tries to join is told "disabled", as on enrollment; enabled again, the same device joins.</summary>
    [Fact]
    public async Task ADisabledMemberIsToldSo()
    {
        using var identity = Identity("a");
        var membership = await GateClient.EnrollAsync(Invite("Steve"), identity);
        Assert.True(_store.SetDisabled("Steve", true));
        var refused = await Assert.ThrowsAsync<GateRejectedException>(() => JoinRaw(membership, identity));
        Assert.Equal(GateStatus.Disabled, refused.Status);
        Assert.True(_store.SetDisabled("Steve", false));
        await using var joined = await JoinRaw(membership, identity);
    }

    [Fact]
    public async Task OtherMembersStayOnlineWhenOneIsRevoked()
    {
        using var a = Identity("a");
        using var b = Identity("b");
        var steve = await GateClient.EnrollAsync(Invite("Steve"), a);
        var alex = await GateClient.EnrollAsync(Invite("Alex"), b);
        await using var tunnel = new TunnelClient(0, GateClient.Opener(alex, b));
        using var game = new TcpClient();
        await game.ConnectAsync(IPAddress.Loopback, tunnel.Port);
        await game.GetStream().WriteAsync(LoginPackets("Alex"));
        using var server = await _backend.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(10));
        _store.RevokeDevices("Steve", null);
        await Task.Delay(2500);
        await server.GetStream().WriteAsync("pong"u8.ToArray());
        var reply = new byte[4];
        await game.GetStream().ReadExactlyAsync(reply).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("pong"u8.ToArray(), reply);
        Assert.Equal("Steve", steve.Username);
    }

    [Fact]
    public async Task InviteIsSingleUseAndGateStaysSilent()
    {
        using var identity = Identity("a");
        var invite = Invite("Steve");
        await GateClient.EnrollAsync(invite, identity);
        using var other = Identity("b");
        var e = await Assert.ThrowsAsync<IOException>(() => GateClient.EnrollAsync(invite, other));
        Assert.Equal(Amber.Core.Localization.L.T("core.gate.protocol.silent"), e.Message);
    }

    [Fact]
    public async Task ConnectionResetDuringInitialHandshakeReportsSilentGate()
    {
        using var identity = Identity("reset");
        await using var stream = new ResetOnReadStream();
        var error = await Assert.ThrowsAsync<IOException>(() => GateProtocol.EnrollAsync(stream, Invite("Steve"), identity.KeyPair));
        Assert.Equal(L.T("core.gate.protocol.silent"), error.Message);
        Assert.IsType<SocketException>(error.InnerException?.InnerException);
    }

    private sealed class ResetOnReadStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("Connection reset", new SocketException((int)SocketError.ConnectionReset)));
    }

    [Fact]
    public async Task WrongUsernameGetsDisconnectAndNeverReachesBackend()
    {
        using var identity = Identity("a");
        var membership = await GateClient.EnrollAsync(Invite("Steve"), identity);
        await using var secure = await JoinRaw(membership, identity);
        await secure.WriteAsync(LoginPackets("Notch"));
        await secure.FlushAsync();
        var buffer = new byte[512];
        var n = await secure.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("Notch", Encoding.UTF8.GetString(buffer, 0, n));
        var pending = _backend.AcceptTcpClientAsync();
        await Task.Delay(300);
        Assert.False(pending.IsCompleted);
    }

    [Fact]
    public async Task StolenAccessKeyOnAnotherDeviceIsRefused()
    {
        using var identity = Identity("a");
        var membership = await GateClient.EnrollAsync(Invite("Steve"), identity);
        using var thief = Identity("thief");
        var e = await Assert.ThrowsAsync<GateRejectedException>(() => JoinRaw(membership, thief));
        Assert.Equal(GateStatus.NotMember, e.Status);
    }

    [Fact]
    public async Task ImpostorServerIsDetectedBeforeDeviceKeyIsSent()
    {
        using var identity = Identity("a");
        var membership = await GateClient.EnrollAsync(Invite("Steve"), identity);
        using var fake = X25519KeyPair.Generate();
        var pinnedWrong = membership with { ServerPublicKey = fake.PublicKey };
        await Assert.ThrowsAsync<System.Security.Cryptography.CryptographicException>(() => JoinRaw(pinnedWrong, identity));
    }

    [Fact]
    public async Task DeviceLimitPerUsername()
    {
        var invite = Invite("Steve", uses: 3);
        using var a = Identity("a");
        using var b = Identity("b");
        using var c = Identity("c");
        await GateClient.EnrollAsync(invite, a);
        await GateClient.EnrollAsync(invite, b);
        var e = await Assert.ThrowsAsync<GateRejectedException>(() => GateClient.EnrollAsync(invite, c));
        Assert.Equal(GateStatus.DeviceLimit, e.Status);
        Assert.Equal(2, _store.Read().Member("steve")!.Devices.Count);
    }

    [Fact]
    public async Task ScannerGetsNoBytes()
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, _server.Port);
        var stream = tcp.GetStream();
        // 像 MC 状态查询那样的探测，以及一条长度合法但内容随机的“握手”。
        await stream.WriteAsync((byte[])[0x00, 0x30, .. RandomNumberGenerator.GetBytes(0x30)]);
        var buffer = new byte[64];
        int n;
        try { n = await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (IOException) { n = 0; }
        Assert.Equal(0, n);
    }

    [Fact]
    public async Task StatusPingIsForwardedWithoutUsername()
    {
        using var identity = Identity("a");
        var membership = await GateClient.EnrollAsync(Invite("Steve"), identity);
        await using var secure = await JoinRaw(membership, identity);
        var handshake = Handshake(1);
        await secure.WriteAsync(handshake);
        await secure.FlushAsync();
        using var server = await _backend.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var received = new byte[handshake.Length];
        await server.GetStream().ReadExactlyAsync(received).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(handshake, received);
    }

    [Fact]
    public async Task LegacyPingIsRejected()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => McLogin.ReadAsync(new MemoryStream([0xFE, 0x01]), CancellationToken.None));
    }

    private async Task<SecureStream> JoinRaw(GateMembership membership, MemberIdentity identity)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, membership.Port);
        return await GateProtocol.JoinAsync(tcp.GetStream(), membership, identity.KeyPair);
    }

    private static byte[] LoginPackets(string name) => [.. Handshake(2), .. Packet([0x00, .. String(name), .. new byte[16]])];

    private static byte[] Handshake(int next) =>
        Packet([0x00, 0xFF, 0x05, .. String("mc.example.com"), 0x63, 0xDD, (byte)next]);

    private static byte[] String(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return [(byte)bytes.Length, .. bytes];
    }

    private static byte[] Packet(byte[] body) => [(byte)body.Length, .. body];
}

/// <summary>JAYS1- / JAYS2- / JAYSS1- 贴错输入框时能认出来，不和 JAY2 系列的前缀混淆。</summary>
public sealed class GatePastedCodeTests
{
    [Theory]
    [InlineData("JAYS1-04632E9J5RRKCE1E64Q34C", PastedCodeKind.JayServer)]
    [InlineData("JAYS2-04632E9J5RRKCE1E64Q34C", PastedCodeKind.JayServer)]
    [InlineData("JAYSS1-QUJDREVGR0hJSktMTU5PUA", PastedCodeKind.JayServer)]
    [InlineData("来玩：jays1-04632e9j5rrkce", PastedCodeKind.JayServer)]
    [InlineData("JAY2-ABCDEF", PastedCodeKind.Invite)]
    [InlineData("JAY2D-ABCDEF", PastedCodeKind.OldDirect)]
    public void Detects(string text, PastedCodeKind kind) => Assert.Equal(kind, PastedCode.Detect(text));
}
