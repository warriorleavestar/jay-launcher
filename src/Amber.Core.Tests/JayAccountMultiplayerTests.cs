using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Amber.Core.Identity;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Contacts;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Tests;

/// <summary>
/// Jay accounts in multiplayer (phase 2): the knock carries a device certificate the host checks against the proven key,
/// trust can follow the account to a new device, and contacts recognize a friend's other device.
/// </summary>
public sealed class JayAccountMultiplayerTests : IAsyncDisposable, IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jay-mp-account-" + Guid.NewGuid().ToString("N"));
    private readonly InviteBook _book = new(() => Now);
    private readonly X25519KeyPair _host = X25519KeyPair.Generate();
    private readonly JayAccount _alice = JayAccount.FromMnemonic(Bip39.Generate(MnemonicLanguage.English), null);
    private readonly JayAccount _bob = JayAccount.FromMnemonic(Bip39.Generate(MnemonicLanguage.English), null);
    private readonly List<KnockRequest> _asked = [];
    private KnockServer? _server;

    private static readonly KnockLimits Fast = new() { HandshakeTimeout = TimeSpan.FromSeconds(3), DecisionTimeout = TimeSpan.FromSeconds(3) };

    private KnockServer Server(Func<string, bool>? trustedAccount = null, DeviceCertificate? hostCert = null)
    {
        var credentials = RoomCredentials.Create(_host.PublicKey, 25565, new TunnelSettings(TransportMode.High, true));
        return _server ??= new KnockServer(_book, _host, () => credentials, _ => false, (request, _) =>
        {
            lock (_asked) _asked.Add(request);
            return Task.FromResult(true);
        }, IPAddress.Loopback, 0, limits: Fast)
        {
            IsTrustedJayAccount = trustedAccount,
            JayCertificate = () => hostCert,
        };
    }

    private async Task<KnockOutcome> KnockAsync(X25519KeyPair guest, DeviceCertificate? jay)
    {
        var server = Server();
        var invite = _book.Create(1, TimeSpan.FromMinutes(10), false, TunnelSettings.Default);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        return await KnockClient.KnockAsync(client.GetStream(), invite.Invite, guest, "Alice", "1.0", decisionTimeout: TimeSpan.FromSeconds(5), jay: jay);
    }

    [Fact]
    public async Task HostSeesTheGuestsVerifiedJayAccount()
    {
        using var guest = X25519KeyPair.Generate();
        var outcome = await KnockAsync(guest, _alice.CertifyDevice(guest.PublicKey, "Windows", 0, Now));
        Assert.True(outcome.Approved);
        var request = Assert.Single(_asked);
        Assert.Equal(_alice.Address, request.JayAccount);
        Assert.False(request.Trusted);
        Assert.False(request.TrustedByAccount);
    }

    [Fact]
    public async Task CertificateForAnotherDeviceIsIgnored()
    {
        using var guest = X25519KeyPair.Generate();
        using var other = X25519KeyPair.Generate();
        // A copied certificate: signed for someone else's device key, so it proves nothing about this guest.
        var stolen = _alice.CertifyDevice(other.PublicKey, "Windows", 0, Now);
        Assert.True((await KnockAsync(guest, stolen)).Approved);
        Assert.Null(Assert.Single(_asked).JayAccount);
    }

    [Fact]
    public async Task TrustedAccountLetsANewDeviceInWithoutAsking()
    {
        Server(account => account == _alice.Address);
        using var newComputer = X25519KeyPair.Generate();
        var outcome = await KnockAsync(newComputer, _alice.CertifyDevice(newComputer.PublicKey, "macOS", 0, Now));
        Assert.True(outcome.Approved);
        Assert.Empty(_asked);
    }

    [Fact]
    public async Task UntrustedAccountIsStillAsked()
    {
        Server(account => account == _alice.Address);
        using var guest = X25519KeyPair.Generate();
        Assert.True((await KnockAsync(guest, _bob.CertifyDevice(guest.PublicKey, "Linux", 0, Now))).Approved);
        var request = Assert.Single(_asked);
        Assert.Equal(_bob.Address, request.JayAccount);
        Assert.False(request.Trusted);
    }

    [Fact]
    public async Task GuestLearnsTheHostsAccountOnlyAfterApproval()
    {
        Server(hostCert: _bob.CertifyDevice(_host.PublicKey, "Windows", 0, Now));
        using var guest = X25519KeyPair.Generate();
        var outcome = await KnockAsync(guest, null);
        Assert.True(outcome.Approved);
        Assert.Equal(_bob.Address, outcome.HostJayAccount);
        Assert.Null(Assert.Single(_asked).JayAccount);
    }

    [Fact]
    public async Task HostCertificateForAnotherKeyIsNotSent()
    {
        using var other = X25519KeyPair.Generate();
        Server(hostCert: _bob.CertifyDevice(other.PublicKey, "Windows", 0, Now));
        using var guest = X25519KeyPair.Generate();
        var outcome = await KnockAsync(guest, null);
        Assert.Null(outcome.HostJayAccount);
    }

    [Fact]
    public void VerifiedAccountIsCanonical()
    {
        using var device = X25519KeyPair.Generate();
        var cert = _alice.CertifyDevice(device.PublicKey, "Windows", 0, Now);
        var json = cert.ToJson();
        json["account"] = cert.Account.ToUpperInvariant();
        // Upper case is valid Bech32m but the signature covers the key, so it still verifies; the result is lowercase.
        Assert.Equal(_alice.Address, DeviceCertificate.VerifiedAccount(json, device.PublicKey));
        Assert.Null(DeviceCertificate.VerifiedAccount(null, device.PublicKey));
        Assert.Null(DeviceCertificate.VerifiedAccount(new JsonObject { ["account"] = _alice.Address }, device.PublicKey));
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
    public void ContactsRecordTheAccountAndRecognizeAFriendsNewDevice()
    {
        using var aliceLaptop = X25519KeyPair.Generate();
        using var bobPc = X25519KeyPair.Generate();
        var bookA = new ContactBook(Path.Combine(_dir, "a.json"), () => Now)
            { Me = aliceLaptop.PublicKey, MyCertificate = _alice.CertifyDevice(aliceLaptop.PublicKey, "Windows", 0, Now) };
        var bookB = new ContactBook(Path.Combine(_dir, "b.json"), () => Now)
            { Me = bobPc.PublicKey, MyCertificate = _bob.CertifyDevice(bobPc.PublicKey, "Linux", 0, Now) };

        // First time: the usual exchange with emoji; both sides learn the other's account.
        Assert.Null(bookB.Receive(aliceLaptop.PublicKey, bookA.StartAdd(aliceLaptop.PublicKey, bobPc.PublicKey, "Alice")!, "Bob"));
        var asking = Assert.Single(bookB.Pending);
        Assert.Equal(_alice.Address, asking.JayAccount);
        Assert.False(asking.KnownFriend);
        var nonce = bookA.Receive(bobPc.PublicKey, bookB.Accept(aliceLaptop.PublicKey, "Bob")!, "Alice")!;
        Assert.Equal(_bob.Address, Assert.Single(bookA.Pending).JayAccount);
        bookB.Receive(aliceLaptop.PublicKey, nonce, "Bob");
        Assert.Equal(_bob.Address, bookA.Confirm(bobPc.PublicKey)!.JayAccount);
        Assert.Equal(_alice.Address, bookB.Confirm(aliceLaptop.PublicKey)!.JayAccount);
        Assert.Equal(_alice.Address, new ContactBook(Path.Combine(_dir, "b.json")).All.Single().JayAccount);

        // Alice restores her words on a new computer and adds Bob again: Bob's side recognizes her and answers without asking.
        using var aliceNew = X25519KeyPair.Generate();
        var bookA2 = new ContactBook(Path.Combine(_dir, "a2.json"), () => Now)
            { Me = aliceNew.PublicKey, MyCertificate = _alice.CertifyDevice(aliceNew.PublicKey, "macOS", 0, Now) };
        var added = new List<Contact>();
        bookB.DeviceAdded += added.Add;
        var reply = bookB.Receive(aliceNew.PublicKey, bookA2.StartAdd(aliceNew.PublicKey, bobPc.PublicKey, "Alice")!, "Bob");
        Assert.NotNull(reply);
        var known = bookB.Pending.Single(p => p.PublicKey.AsSpan().SequenceEqual(aliceNew.PublicKey));
        Assert.True(known.KnownFriend);
        Assert.Equal(ContactStep.WaitingForNonce, known.Step);
        Assert.Equal("Alice", bookB.FindVerifiedByJayAccount(_alice.Address)!.Name);
        // Bob's side finishes on Alice's nonce without the emoji, and says so once: the new device never slips in silently.
        Assert.Empty(added);
        var bobReply = bookA2.Receive(bobPc.PublicKey, reply!, "Alice")!;
        bookB.Receive(aliceNew.PublicKey, bobReply, "Bob");
        Assert.True(Assert.Single(added).PublicKey.AsSpan().SequenceEqual(aliceNew.PublicKey));
        Assert.Contains(bookB.All, c => c.Verified && c.PublicKey.AsSpan().SequenceEqual(aliceNew.PublicKey));

        // A stranger can't borrow Alice's certificate: it names her device key, not theirs.
        using var stranger = X25519KeyPair.Generate();
        var bookS = new ContactBook(Path.Combine(_dir, "s.json"), () => Now) { Me = stranger.PublicKey };
        var forged = bookS.StartAdd(stranger.PublicKey, bobPc.PublicKey, "Alice")!;
        forged.Body["jay"] = _alice.CertifyDevice(aliceNew.PublicKey, "macOS", 0, Now).ToJson();
        Assert.Null(bookB.Receive(stranger.PublicKey, forged, "Bob"));
        var asked = bookB.Pending.Single(p => p.PublicKey.AsSpan().SequenceEqual(stranger.PublicKey));
        Assert.False(asked.KnownFriend);
        Assert.Null(asked.JayAccount);
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
