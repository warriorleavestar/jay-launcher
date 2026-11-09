using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Tests;

/// <summary>
/// 设备验证：证明只有握着设备私钥的一方算得出；成员通道上互相证明（冒充成员、冒充房主都会被发现）；
/// 敲门时协商方案；私有方案索引；安全策略只能收紧。
/// </summary>
public sealed class DeviceAttestationTests
{
    private static readonly AttestationScheme Rotated = new(7, RandomNumberGenerator.GetBytes(32), 20);

    [Fact]
    public void BothSidesDeriveTheSameProofAndOnlyTheRightKeyVerifies()
    {
        using var member = X25519KeyPair.Generate();
        using var host = X25519KeyPair.Generate();
        using var impostor = X25519KeyPair.Generate();
        var binding = RandomNumberGenerator.GetBytes(32);
        using var mine = new DeviceAttestor(member, host.PublicKey, AttestationScheme.Default);
        using var theirs = new DeviceAttestor(host, member.PublicKey, AttestationScheme.Default);
        using var fake = new DeviceAttestor(impostor, host.PublicKey, AttestationScheme.Default);

        var proof = mine.Proof(binding, DeviceAttestor.Role.Initiator, 0);
        Assert.Equal(AttestationScheme.Default.ProofLength, proof.Length);
        Assert.True(theirs.Verify(binding, DeviceAttestor.Role.Initiator, 0, proof));
        // 冒充者有房主公钥，但没有成员的设备私钥。
        Assert.False(theirs.Verify(binding, DeviceAttestor.Role.Initiator, 0, fake.Proof(binding, DeviceAttestor.Role.Initiator, 0)));
        // 反射（把对方的证明原样送回去）、换一条连接、换一轮都不行。
        Assert.False(theirs.Verify(binding, DeviceAttestor.Role.Responder, 0, proof));
        Assert.False(theirs.Verify(RandomNumberGenerator.GetBytes(32), DeviceAttestor.Role.Initiator, 0, proof));
        Assert.False(theirs.Verify(binding, DeviceAttestor.Role.Initiator, 1, proof));
        Assert.False(theirs.Verify(binding, DeviceAttestor.Role.Initiator, 0, proof[..^1]));
    }

    [Fact]
    public void ChangingTheSchemeInvalidatesOldProofs()
    {
        using var member = X25519KeyPair.Generate();
        using var host = X25519KeyPair.Generate();
        var binding = RandomNumberGenerator.GetBytes(32);
        using var old = new DeviceAttestor(member, host.PublicKey, AttestationScheme.Default);
        using var rotated = new DeviceAttestor(host, member.PublicKey, Rotated);
        Assert.False(rotated.Verify(binding, DeviceAttestor.Role.Initiator, 0, old.Proof(binding, DeviceAttestor.Role.Initiator, 0)));
    }

    [Fact]
    public void DisposedAttestorNeverVerifies()
    {
        using var member = X25519KeyPair.Generate();
        using var host = X25519KeyPair.Generate();
        var binding = RandomNumberGenerator.GetBytes(32);
        var a = new DeviceAttestor(member, host.PublicKey, AttestationScheme.Default);
        var proof = a.Proof(binding, DeviceAttestor.Role.Initiator, 0);
        a.Dispose();
        Assert.False(a.Verify(binding, DeviceAttestor.Role.Initiator, 0, proof));
        Assert.Throws<ObjectDisposedException>(() => a.Proof(binding, DeviceAttestor.Role.Initiator, 0));
    }

    [Fact]
    public void PrivateIndexPicksTheActiveSchemeAndFallsBackSafely()
    {
        var salt = Convert.ToBase64String(Rotated.Salt);
        var picked = AttestationIndex.Parse($$"""{"active":7,"schemes":[{"id":3,"salt":"{{salt}}","len":24},{"id":7,"salt":"{{salt}}","len":20}]}""");
        Assert.Equal(7, picked.Id);
        Assert.Equal(20, picked.ProofLength);
        Assert.Equal(Rotated.Salt, picked.Salt);
        Assert.Same(AttestationScheme.Default, AttestationIndex.Parse("not json"));
        Assert.Same(AttestationScheme.Default, AttestationIndex.Parse($$"""{"active":9,"schemes":[{"id":7,"salt":"{{salt}}","len":20}]}"""));
        // 证明太短、盐太短：不接受，退回默认。
        Assert.Same(AttestationScheme.Default, AttestationIndex.Parse($$"""{"active":7,"schemes":[{"id":7,"salt":"{{salt}}","len":4}]}"""));
        Assert.Same(AttestationScheme.Default, AttestationIndex.Parse("""{"active":7,"schemes":[{"id":7,"salt":"AAA=","len":20}]}"""));
    }

    [Fact]
    public void MissingIndexFileMeansDefault()
    {
        var dir = Directory.CreateTempSubdirectory("jay-attest-").FullName;
        try
        {
            Assert.Same(AttestationScheme.Default, AttestationIndex.Active(dir));
            File.WriteAllText(Path.Combine(dir, AttestationIndex.FileName), $$"""{"active":7,"schemes":[{"id":7,"salt":"{{Convert.ToBase64String(Rotated.Salt)}}","len":20}]}""");
            Assert.Equal(7, AttestationIndex.Active(dir).Id);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task MemberChannelPassesWhenBothDevicesAreReal()
    {
        using var hostDevice = X25519KeyPair.Generate();
        using var memberDevice = X25519KeyPair.Generate();
        var (table, entry) = HostTable(hostDevice, memberDevice.PublicKey);
        var key = MemberKeyFor(entry, memberDevice, hostDevice.PublicKey);
        var impersonations = 0;
        table.ImpersonationDetected += _ => impersonations++;

        var (client, server, a, b) = await PairAsync();
        using (a)
        using (b)
        {
            var profile = MemberChannel.ChatProfile(TransportMode.High);
            var connect = MemberChannel.ConnectAsync(client, key, profile, "chat", []);
            var (hostSide, member, _) = await MemberChannel.AcceptAsync(server, table, profile, "chat");
            var memberSide = await connect;
            Assert.Same(entry, member);
            await memberSide.WriteAsync("hi"u8.ToArray());
            await memberSide.FlushAsync();
            var buffer = new byte[2];
            Assert.True(await SecureStream.ReadExactAsync(hostSide, buffer, CancellationToken.None));
            Assert.Equal("hi"u8.ToArray(), buffer);
        }
        Assert.Equal(0, impersonations);
    }

    [Fact]
    public async Task StolenMemberKeyWithoutTheDeviceIsCaughtAndReported()
    {
        using var hostDevice = X25519KeyPair.Generate();
        using var memberDevice = X25519KeyPair.Generate();
        using var thief = X25519KeyPair.Generate();
        var (table, entry) = HostTable(hostDevice, memberDevice.PublicKey);
        // 小偷拿到了这个成员的编号和 psk（比如凭据泄露），但用的是自己电脑的设备密钥。
        var stolen = MemberKeyFor(entry, thief, hostDevice.PublicKey);
        MemberEntry? reported = null;
        table.ImpersonationDetected += e => reported = e;

        var (client, server, a, b) = await PairAsync();
        using (a)
        using (b)
        {
            var profile = MemberChannel.ChatProfile(TransportMode.High);
            var connect = MemberChannel.ConnectAsync(client, stolen, profile, "chat", []);
            await Assert.ThrowsAsync<DeviceAttestationException>(() => MemberChannel.AcceptAsync(server, table, profile, "chat"));
            await Assert.ThrowsAnyAsync<Exception>(() => connect);
        }
        Assert.Same(entry, reported);
    }

    [Fact]
    public async Task FakeHostWithTheRoomCredentialsIsCaughtByTheMember()
    {
        using var hostDevice = X25519KeyPair.Generate();
        using var fakeHost = X25519KeyPair.Generate();
        using var memberDevice = X25519KeyPair.Generate();
        // 冒充的房主拿到了成员表（编号 + psk），但没有真房主的设备私钥。
        var (table, entry) = HostTable(fakeHost, memberDevice.PublicKey);
        var key = MemberKeyFor(entry, memberDevice, hostDevice.PublicKey);
        var impersonations = 0;
        table.ImpersonationDetected += _ => impersonations++;

        var (client, server, a, b) = await PairAsync();
        using (a)
        using (b)
        {
            var profile = MemberChannel.ChatProfile(TransportMode.High);
            var connect = MemberChannel.ConnectAsync(client, key, profile, "chat", []);
            var accept = MemberChannel.AcceptAsync(server, table, profile, "chat");
            await Assert.ThrowsAsync<DeviceAttestationException>(() => connect);
            // 房主这边：成员的证明对不上（它是对着真房主的公钥算的），同样断开。
            await Assert.ThrowsAnyAsync<Exception>(() => accept);
        }
        // 真成员不会被当成冒充者上报给真房主（这里的“房主”就是冒充者自己）。
        Assert.True(impersonations <= 1);
    }

    [Fact]
    public async Task OldClientsWithoutAttestationStillConnect()
    {
        using var hostDevice = X25519KeyPair.Generate();
        using var memberDevice = X25519KeyPair.Generate();
        var table = new MemberTable { Attestation = (hostDevice, AttestationScheme.Default) };
        var entry = table.Issue(memberDevice.PublicKey, "Old"); // Attests = false：敲门时没说自己支持
        var (client, server, a, b) = await PairAsync();
        using (a)
        using (b)
        {
            var profile = MemberChannel.ChatProfile(TransportMode.High);
            var connect = MemberChannel.ConnectAsync(client, entry.Key, profile, "chat", []);
            var (_, member, _) = await MemberChannel.AcceptAsync(server, table, profile, "chat");
            await connect;
            Assert.Same(entry, member);
        }
    }

    [Fact]
    public async Task KnockNegotiatesTheHostsSchemeAndRequiredModeRejectsWithoutIt()
    {
        var now = new DateTimeOffset(2026, 11, 9, 0, 0, 0, TimeSpan.Zero);
        var book = new InviteBook(() => now);
        using var host = X25519KeyPair.Generate();
        using var guest = X25519KeyPair.Generate();
        var credentials = RoomCredentials.Create(host.PublicKey, 25565, new TunnelSettings(TransportMode.High, false));
        var members = new MemberTable { Attestation = (host, Rotated) };
        await using var server = new KnockServer(book, host, () => credentials, _ => false, (_, _) => Task.FromResult(true), IPAddress.Loopback, 0, members: members);
        var invite = book.Create(1, TimeSpan.FromMinutes(10), true, TunnelSettings.Default);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        var outcome = await KnockClient.KnockAsync(client.GetStream(), invite.Invite, guest, "Steve", "0.8.0", decisionTimeout: TimeSpan.FromSeconds(5));
        Assert.True(outcome.Approved);
        Assert.Equal(7, outcome.Credentials!.Scheme!.Id);
        Assert.NotNull(outcome.Credentials.Member!.Attestor);
        Assert.True(members.FindGuest(guest.PublicKey)!.Attests);
    }

    [Fact]
    public void SchemeTravelsInTheEncryptedReplyAndOldRepliesHaveNone()
    {
        using var host = X25519KeyPair.Generate();
        var credentials = RoomCredentials.Create(host.PublicKey, 25565, TunnelSettings.Default) with { Member = MemberKey.Create(), Scheme = Rotated };
        var json = credentials.ToJson();
        var back = RoomCredentials.FromJson(JsonNode.Parse(json.ToJsonString())!);
        Assert.Equal(Rotated.Id, back.Scheme!.Id);
        Assert.Equal(Rotated.Salt, back.Scheme.Salt);
        json.Remove("attest");
        Assert.Null(RoomCredentials.FromJson(JsonNode.Parse(json.ToJsonString())!).Scheme);
    }

    [Fact]
    public void NetworkPolicyCanOnlyTighten()
    {
        var local = new SecurityPolicy(AttestationMode.On, false, 3, true);
        var network = SecurityPolicy.Parse(JsonNode.Parse("""{"attestation":"required","breaker":"on","restartDelayMin":10}"""));
        var merged = local.Tighten(network);
        Assert.Equal(AttestationMode.Required, merged.Attestation);
        Assert.True(merged.Breaker);
        Assert.Equal(10, merged.RestartDelaySeconds);
        // 网络要求了最短倒计时：本机的“跳过等待”不再生效。
        Assert.False(merged.SkipRestartDelay);
        Assert.Equal(10, merged.EffectiveRestartDelay);

        // 网络写得更松，本机更严的设置照旧。
        var loose = SecurityPolicy.Parse(JsonNode.Parse("""{"attestation":"off","breaker":"off"}"""));
        var strict = new SecurityPolicy(AttestationMode.Required, true, 7, false);
        Assert.Equal(strict, strict.Tighten(loose));
        Assert.Equal(strict, strict.Tighten(null));
        Assert.Null(SecurityPolicy.Parse(null));
    }

    [Theory]
    [InlineData("""{"attestation":"maybe"}""")]
    [InlineData("""{"breaker":true}""")]
    [InlineData("""{"restartDelayMin":-1}""")]
    [InlineData("""{"restartDelayMin":100000}""")]
    [InlineData("""[1]""")]
    public void BadNetworkPolicyIsRefused(string json) =>
        Assert.Throws<InvalidDataException>(() => SecurityPolicy.Parse(JsonNode.Parse(json)));

    private static (MemberTable Table, MemberEntry Entry) HostTable(X25519KeyPair hostDevice, byte[] memberPublic)
    {
        var table = new MemberTable { Attestation = (hostDevice, AttestationScheme.Default) };
        var entry = table.Issue(memberPublic, "Alice");
        entry.Attests = true;
        return (table, entry);
    }

    /// <summary>成员手里的那把钥匙（编号和 psk 跟房主表里一样），配上用 localDevice 算证明的对象。</summary>
    private static MemberKey MemberKeyFor(MemberEntry entry, X25519KeyPair localDevice, byte[] hostPublic)
    {
        var psk = entry.Key.CopyPsk();
        try
        {
            return new MemberKey(entry.Key.Id, psk) { Attestor = new DeviceAttestor(localDevice, hostPublic, AttestationScheme.Default) };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(psk);
        }
    }

    private static async Task<(NetworkStream Client, NetworkStream Server, TcpClient A, TcpClient B)> PairAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var a = new TcpClient();
        var accept = listener.AcceptTcpClientAsync();
        await a.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        var b = await accept;
        listener.Stop();
        return (a.GetStream(), b.GetStream(), a, b);
    }
}
