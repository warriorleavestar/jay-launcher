using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Amber.Core.Identity;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Contacts;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Tests;

/// <summary>
/// Jay accounts in multiplayer: the knock carries a device certificate (both account signatures) and a live device
/// signature over the handshake, the host checks both against the proven key, trust can follow the account to a new
/// device, and contacts learn a friend's other devices from these live proofs (privacy plan item 7).
/// </summary>
public sealed class JayAccountMultiplayerTests : IAsyncDisposable, IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jay-mp-account-" + Guid.NewGuid().ToString("N"));
    private readonly InviteBook _book = new(() => Now);
    private readonly DeviceKeys _host = DeviceKeys.Generate();
    private readonly JayAccount _alice = JayAccount.FromMnemonic(Bip39.Generate(MnemonicLanguage.English), null);
    private readonly JayAccount _bob = JayAccount.FromMnemonic(Bip39.Generate(MnemonicLanguage.English), null);
    private readonly List<KnockRequest> _asked = [];
    private KnockServer? _server;

    private static readonly KnockLimits Fast = new() { HandshakeTimeout = TimeSpan.FromSeconds(3), DecisionTimeout = TimeSpan.FromSeconds(3) };

    private KnockServer Server(Func<string, bool>? trustedAccount = null, JayProof? hostProof = null)
    {
        var credentials = RoomCredentials.Create(_host.Dh.PublicKey, 25565, new TunnelSettings(TransportMode.High, true));
        return _server ??= new KnockServer(_book, _host.Dh, () => credentials, _ => false, (request, _) =>
        {
            lock (_asked) _asked.Add(request);
            return Task.FromResult(true);
        }, IPAddress.Loopback, 0, limits: Fast)
        {
            IsTrustedJayAccount = trustedAccount,
            JayCertificate = () => hostProof,
        };
    }

    private async Task<KnockOutcome> KnockAsync(DeviceKeys guest, JayProof? jay)
    {
        var server = Server();
        var invite = _book.Create(1, TimeSpan.FromMinutes(10), false, TunnelSettings.Default);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        return await KnockClient.KnockAsync(client.GetStream(), invite.Invite, guest.Dh, "Alice", "1.0", decisionTimeout: TimeSpan.FromSeconds(5), jay: jay);
    }

    private static JayProof Proof(JayAccount account, DeviceKeys device, string name = "Windows") =>
        new(account.CertifyDevice(device, name, 0, Now), device);

    [Fact]
    public async Task HostSeesTheGuestsVerifiedJayAccount()
    {
        using var guest = DeviceKeys.Generate();
        var outcome = await KnockAsync(guest, Proof(_alice, guest));
        Assert.True(outcome.Approved);
        var request = Assert.Single(_asked);
        Assert.Equal(_alice.Address, request.JayAccount);
        Assert.Equal(_alice.Address, request.JayDevice!.Account);
        Assert.False(request.Trusted);
        Assert.False(request.TrustedByAccount);
    }

    [Fact]
    public async Task CertificateForAnotherDeviceIsIgnored()
    {
        using var guest = DeviceKeys.Generate();
        using var other = DeviceKeys.Generate();
        // A copied certificate: signed for someone else's device keys, so it proves nothing about this guest.
        Assert.True((await KnockAsync(guest, new JayProof(_alice.CertifyDevice(other, "Windows", 0, Now), guest))).Approved);
        Assert.Null(Assert.Single(_asked).JayAccount);
    }

    [Fact]
    public async Task CertificateWithoutTheDevicesSigningKeysIsIgnored()
    {
        using var guest = DeviceKeys.Generate();
        using var other = DeviceKeys.Generate();
        // The certificate names this guest's handshake key, but the live proof is signed by keys it doesn't name.
        var cert = _alice.CertifyDevice(guest, "Windows", 0, Now);
        Assert.True((await KnockAsync(guest, new JayProof(cert, other))).Approved);
        Assert.Null(Assert.Single(_asked).JayAccount);
    }

    [Fact]
    public async Task TrustedAccountLetsANewDeviceInWithoutAsking()
    {
        Server(account => account == _alice.Address);
        using var newComputer = DeviceKeys.Generate();
        var outcome = await KnockAsync(newComputer, Proof(_alice, newComputer, "macOS"));
        Assert.True(outcome.Approved);
        Assert.Empty(_asked);
    }

    [Fact]
    public async Task UntrustedAccountIsStillAsked()
    {
        Server(account => account == _alice.Address);
        using var guest = DeviceKeys.Generate();
        Assert.True((await KnockAsync(guest, Proof(_bob, guest, "Linux"))).Approved);
        var request = Assert.Single(_asked);
        Assert.Equal(_bob.Address, request.JayAccount);
        Assert.False(request.Trusted);
    }

    [Fact]
    public async Task GuestLearnsTheHostsAccountOnlyAfterApproval()
    {
        Server(hostProof: Proof(_bob, _host));
        using var guest = DeviceKeys.Generate();
        var outcome = await KnockAsync(guest, null);
        Assert.True(outcome.Approved);
        Assert.Equal(_bob.Address, outcome.HostJayAccount);
        Assert.Equal(_bob.Address, outcome.HostJayDevice!.Account);
        Assert.Null(Assert.Single(_asked).JayAccount);
    }

    [Fact]
    public async Task HostCertificateForAnotherKeyIsNotSent()
    {
        using var other = DeviceKeys.Generate();
        Server(hostProof: Proof(_bob, other));
        using var guest = DeviceKeys.Generate();
        var outcome = await KnockAsync(guest, null);
        Assert.Null(outcome.HostJayAccount);
    }

    [Fact]
    public void LiveProofIsTiedToTheSession()
    {
        using var device = DeviceKeys.Generate();
        var proof = Proof(_alice, device);
        var json = proof.ToJson("session-1"u8);
        Assert.Equal(_alice.Address, JayProof.Verified(json, device.Dh.PublicKey, "session-1"u8)!.Account);
        // Replayed into another session, or with the certificate alone, it proves nothing.
        Assert.Null(JayProof.Verified(json, device.Dh.PublicKey, "session-2"u8));
        Assert.Null(JayProof.Verified(new JsonObject { ["cert"] = proof.Certificate.ToJson() }, device.Dh.PublicKey, "session-1"u8));
        Assert.Null(JayProof.Verified(null, device.Dh.PublicKey, "session-1"u8));
    }

    [Fact]
    public void CertificateIsSignedWithBothAccountKeys()
    {
        using var device = DeviceKeys.Generate();
        var cert = _alice.CertifyDevice(device, "Windows", 0, Now);
        Assert.True(DeviceCertificate.FromJson(cert.ToJson())!.Verify(device.Dh.PublicKey));
        Assert.True(cert.ToJson().ToJsonString().Length < 12 * 1024);
        // Either half of the account signature on its own isn't enough.
        var noMlDsa = cert with { Signature = cert.Signature! with { MlDsa = new byte[MlDsa65.SignatureSize] } };
        var noEd = cert with { Signature = cert.Signature! with { Ed = new byte[64] } };
        Assert.False(noMlDsa.Verify(device.Dh.PublicKey));
        Assert.False(noEd.Verify(device.Dh.PublicKey));
        // Someone else's ML-DSA key under Alice's Ed25519 key hashes to another address.
        var mixed = cert with { AccountMlDsa = _bob.MlDsaPublicKey };
        Assert.NotEqual(_alice.Address, mixed.Account);
        Assert.False(mixed.Verify(device.Dh.PublicKey));
        // Older single-signature certificates are not read at all.
        var v1 = cert.ToJson();
        v1["v"] = 1;
        Assert.Null(DeviceCertificate.FromJson(v1));
    }

    [Fact]
    public void TrustedMembersRememberTheAccountAndForgetEveryDevice()
    {
        var path = Path.Combine(_dir, "trusted.json");
        using var laptop = X25519KeyPair.Generate();
        using var desktop = X25519KeyPair.Generate();
        using var stranger = X25519KeyPair.Generate();
        var trusted = new TrustedMembers(path);
        trusted.Add(laptop.PublicKey, "Alice", _alice.Address);
        trusted.Add(desktop.PublicKey, "Alice", _alice.Address);
        trusted.Add(stranger.PublicKey, "Sam");
        Assert.True(trusted.IsTrustedJayAccount(_alice.Address));
        Assert.False(trusted.IsTrustedJayAccount(_bob.Address));
        Assert.False(trusted.IsTrustedJayAccount("not an address"));
        // Re-adding a device without an account keeps the account it had.
        trusted.Add(laptop.PublicKey, "Alice2");

        var again = new TrustedMembers(path);
        Assert.Equal(_alice.Address, again.All.Single(m => m.Name == "Alice2").JayAccount);
        Assert.True(again.IsTrustedJayAccount(_alice.Address));

        // Untrusting the person removes all of their devices, not just the one clicked.
        again.Remove(laptop.PublicKey);
        Assert.False(again.IsTrustedJayAccount(_alice.Address));
        Assert.Equal("Sam", Assert.Single(again.All).Name);
    }

    [Fact]
    public void ContactsLearnTheAccountLiveAndRecognizeAFriendsNewDevice()
    {
        using var aliceLaptop = DeviceKeys.Generate();
        using var bobPc = DeviceKeys.Generate();
        var bookA = new ContactBook(Path.Combine(_dir, "a.json"), () => Now) { Me = aliceLaptop.Dh.PublicKey.ToArray() };
        var bookB = new ContactBook(Path.Combine(_dir, "b.json"), () => Now) { Me = bobPc.Dh.PublicKey.ToArray() };
        byte[] alice = aliceLaptop.Dh.PublicKey.ToArray(), bob = bobPc.Dh.PublicKey.ToArray();

        // The usual exchange with emoji: mail carries no account (the certificate doesn't fit in mail).
        Assert.Null(bookB.Receive(alice, bookA.StartAdd(alice, bob, "Alice")!, "Bob"));
        var nonce = bookA.Receive(bob, bookB.Accept(alice, "Bob")!, "Alice")!;
        bookB.Receive(alice, nonce, "Bob");
        Assert.Null(bookA.Confirm(bob)!.JayAccount);
        Assert.Null(bookB.Confirm(alice)!.JayAccount);

        // Alice knocks on Bob's room: the knock proved her account live, so Bob's list groups her device under it.
        Assert.Null(bookB.SeenLive(alice, _alice.CertifyDevice(aliceLaptop, "Windows", 0, Now)));
        Assert.Equal(_alice.Address, new ContactBook(Path.Combine(_dir, "b.json")).All.Single().JayAccount);

        // Alice restores her words on a new computer and knocks again: the new device joins her, waiting for Bob's OK.
        using var aliceNew = DeviceKeys.Generate();
        var added = new List<Contact>();
        bookB.DeviceAdded += added.Add;
        var fresh = bookB.SeenLive(aliceNew.Dh.PublicKey, _alice.CertifyDevice(aliceNew, "macOS", 0, Now));
        Assert.NotNull(fresh);
        Assert.True(fresh.NewDevice);
        Assert.Equal("Alice", fresh.Name);
        Assert.True(Assert.Single(added).PublicKey.AsSpan().SequenceEqual(aliceNew.Dh.PublicKey));
        Assert.Equal(2, bookB.All.Count(c => c.Verified && c.JayAccount == _alice.Address));

        // A certificate for another key, or from an account Bob doesn't know, adds nothing.
        using var stranger = DeviceKeys.Generate();
        Assert.Null(bookB.SeenLive(stranger.Dh.PublicKey, _alice.CertifyDevice(aliceNew, "macOS", 0, Now)));
        Assert.Null(bookB.SeenLive(stranger.Dh.PublicKey, _bob.CertifyDevice(stranger, "Linux", 0, Now)));
        // An older epoch than one seen from that account doesn't either.
        using var oldDevice = DeviceKeys.Generate();
        bookB.SeenLive(aliceLaptop.Dh.PublicKey, _alice.CertifyDevice(aliceLaptop, "Windows", 2, Now));
        Assert.Null(bookB.SeenLive(oldDevice.Dh.PublicKey, _alice.CertifyDevice(oldDevice, "Old", 1, Now)));
        Assert.Single(added);
    }

    public async ValueTask DisposeAsync()
    {
        if (_server is not null) await _server.DisposeAsync();
        Dispose();
    }

    public void Dispose()
    {
        _alice.Dispose();
        _bob.Dispose();
        _host.Dispose();
        try { Directory.Delete(_dir, true); }
        catch (DirectoryNotFoundException) { }
    }
}
