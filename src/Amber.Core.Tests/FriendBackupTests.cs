using Amber.Core.Identity;
using Amber.Core.Multiplayer.Contacts;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Tests;

/// <summary>Friend list backups under a Jay account, getting friends back on a new computer, and devices learned from live proofs.</summary>
public sealed class FriendBackupTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jay-friends-" + Guid.NewGuid().ToString("N"));
    private readonly JayAccount _alice = JayAccount.FromMnemonic(Bip39.Generate(MnemonicLanguage.English), null);
    private readonly JayAccount _bob = JayAccount.FromMnemonic(Bip39.Generate(MnemonicLanguage.English), null);

    private ContactBook Book(string name, X25519KeyPair me) => new(Path.Combine(_dir, name + ".json"), () => Now) { Me = me.PublicKey.ToArray() };

    /// <summary>Runs the whole add-contact exchange from <paramref name="a"/> to <paramref name="b"/> and returns whether both ended verified.</summary>
    private static bool Exchange(ContactBook a, X25519KeyPair aKey, ContactBook b, X25519KeyPair bKey, bool relink = false, bool confirmByHand = true)
    {
        var hello = relink ? a.StartRelink(aKey.PublicKey, bKey.PublicKey, "A") : a.StartAdd(aKey.PublicKey, bKey.PublicKey, "A");
        if (hello is null) return false;
        var reply = b.Receive(aKey.PublicKey, hello, "B") ?? b.Accept(aKey.PublicKey, "B");
        if (reply is null) return false;
        var nonce = a.Receive(bKey.PublicKey, reply, "A");
        if (nonce is null) return false;
        b.Receive(aKey.PublicKey, nonce, "B");
        if (confirmByHand)
        {
            a.Confirm(bKey.PublicKey);
            b.Confirm(aKey.PublicKey);
        }
        return a.IsVerified(bKey.PublicKey) && b.IsVerified(aKey.PublicKey);
    }

    [Fact]
    public void BackupOpensOnlyWithTheSameAccountAndUnchanged()
    {
        using var friend = X25519KeyPair.Generate();
        var contacts = new[]
        {
            new Contact(friend.PublicKey, "Bob", true, Now) { JayAccount = _bob.Address },
            new Contact(new byte[32], "half-added", false, Now),
        };
        var backup = FriendBackup.Export(_alice, contacts, Now)!;
        Assert.True(FriendBackup.LooksLikeBackup(backup));

        // Restoring the same words on another computer opens it.
        using var again = JayAccount.FromSeed(SeedOf(_alice));
        var restored = Assert.Single(FriendBackup.Import(again, backup)!.Contacts);
        Assert.Equal("Bob", restored.Name);
        Assert.Equal(_bob.Address, restored.JayAccount);
        Assert.True(restored.Verified);
        Assert.Equal(friend.PublicKey, restored.PublicKey);

        Assert.Null(FriendBackup.Import(_bob, backup));
        var tampered = (byte[])backup.Clone();
        tampered[^20] ^= 1;
        Assert.Null(FriendBackup.Import(_alice, tampered));
        Assert.Null(FriendBackup.Import(_alice, "not a backup"u8));
        // Same contacts, fresh nonce: two backups never look alike.
        Assert.NotEqual(backup, FriendBackup.Export(_alice, contacts, Now));
    }

    [Fact]
    public void RestoredFriendsReconnectThroughTheEmoji()
    {
        using var aliceOld = X25519KeyPair.Generate();
        using var bobPc = X25519KeyPair.Generate();
        var a = Book("alice-old", aliceOld);
        var b = Book("bob", bobPc);
        Assert.True(Exchange(a, aliceOld, b, bobPc));
        a.Rename(bobPc.PublicKey, "Bobby");
        var backup = FriendBackup.Export(_alice, a.All, Now)!;

        // New computer: restore the words, import the backup, say hello again. Mail can't carry the post-quantum
        // certificate, so Bob checks the new device with the emoji like any other.
        using var aliceNew = X25519KeyPair.Generate();
        var a2 = Book("alice-new", aliceNew);
        Assert.Equal(1, a2.Import(FriendBackup.Import(_alice, backup)!.Contacts));
        Assert.Equal(0, a2.Import(FriendBackup.Import(_alice, backup)!.Contacts));
        Assert.False(Exchange(a2, aliceNew, b, bobPc, relink: true, confirmByHand: false));
        Assert.Equal(ContactStep.Compare, Assert.Single(b.Pending).Step);
        Assert.True(Exchange(a2, aliceNew, b, bobPc, relink: true));
        Assert.Empty(a2.Pending);
        Assert.Empty(b.Pending);
        // Alice's new device keeps the name she gave Bob.
        Assert.Equal("Bobby", a2.Find(bobPc.PublicKey)!.Name);
    }

    [Fact]
    public void RelinkNeedsAKnownContact()
    {
        using var me = X25519KeyPair.Generate();
        using var friend = X25519KeyPair.Generate();
        using var stranger = X25519KeyPair.Generate();
        var book = Book("x", me);
        book.Import([new Contact(friend.PublicKey, "F", true, Now)]);
        Assert.NotNull(book.StartRelink(me.PublicKey, friend.PublicKey, "me"));
        Assert.Null(book.StartRelink(me.PublicKey, stranger.PublicKey, "me"));
    }

    /// <summary>
    /// Bob knows Alice on her laptop (emoji); a knock from each of her devices proved her account live, so the laptop is
    /// grouped under it and her phone was added as her new device.
    /// </summary>
    private (ContactBook bob, X25519KeyPair bobKey, DeviceKeys laptop, DeviceKeys phone) FriendWithTwoDevices(int phoneEpoch = 0)
    {
        var laptop = DeviceKeys.Generate();
        var phone = DeviceKeys.Generate();
        var bobKey = X25519KeyPair.Generate();
        var b = Book("bob", bobKey);
        Assert.True(Exchange(Book("laptop", laptop.Dh), laptop.Dh, b, bobKey));
        b.SeenLive(laptop.Dh.PublicKey, _alice.CertifyDevice(laptop, "Laptop", 0, Now));
        Assert.NotNull(b.SeenLive(phone.Dh.PublicKey, _alice.CertifyDevice(phone, "Phone", phoneEpoch, Now)));
        Assert.True(b.IsVerified(phone.Dh.PublicKey));
        return (b, bobKey, laptop, phone);
    }

    [Fact]
    public void RemovedDeviceComesBackOnlyThroughTheEmoji()
    {
        var (b, bobKey, laptop, phone) = FriendWithTwoDevices();
        using (laptop) using (bobKey) using (phone)
        {
            // Bob removes the phone ("it was stolen"). The thief still has its keys and a valid certificate.
            b.Remove(phone.Dh.PublicKey);
            var reloaded = new ContactBook(Path.Combine(_dir, "bob.json"), () => Now) { Me = bobKey.PublicKey.ToArray() };
            Assert.Contains(reloaded.RemovedDevices, r => r.PublicKey.AsSpan().SequenceEqual(phone.Dh.PublicKey) && r.JayAccount == _alice.Address);
            Assert.Null(reloaded.SeenLive(phone.Dh.PublicKey, _alice.CertifyDevice(phone, "Phone", 0, Now)));
            var thief = new ContactBook(Path.Combine(_dir, "thief.json"), () => Now) { Me = phone.Dh.PublicKey.ToArray() };

            // The hello is not answered on its own: Bob is asked, like for a stranger.
            Assert.Null(reloaded.Receive(phone.Dh.PublicKey, thief.StartAdd(phone.Dh.PublicKey, bobKey.PublicKey, "Alice")!, "Bob"));
            Assert.Equal(ContactStep.AskingMe, Assert.Single(reloaded.Pending).Step);
            // Even if Bob accepts, it stops at the emoji.
            var nonce = thief.Receive(bobKey.PublicKey, reloaded.Accept(phone.Dh.PublicKey, "Bob")!, "Alice")!;
            reloaded.Receive(phone.Dh.PublicKey, nonce, "Bob");
            Assert.Equal(ContactStep.Compare, Assert.Single(reloaded.Pending).Step);
            Assert.False(reloaded.IsVerified(phone.Dh.PublicKey));

            // A backup restore can't sneak it back either.
            Assert.Equal(0, reloaded.Import([new Contact(phone.Dh.PublicKey.ToArray(), "Alice", true, Now) { JayAccount = _alice.Address }]));

            // Bob compares the emoji and they match: now it's back, and off the removed list.
            Assert.NotNull(reloaded.Confirm(phone.Dh.PublicKey));
            Assert.True(reloaded.IsVerified(phone.Dh.PublicKey));
            Assert.False(reloaded.Find(phone.Dh.PublicKey)!.NewDevice);
            Assert.Empty(reloaded.RemovedDevices);
        }
    }

    [Fact]
    public void NewDeviceNoticeStaysUntilAnswered()
    {
        var (b, bobKey, laptop, phone) = FriendWithTwoDevices();
        using (laptop) using (bobKey) using (phone)
        {
            Assert.True(b.Find(phone.Dh.PublicKey)!.NewDevice);
            Assert.Equal("Phone", b.Find(phone.Dh.PublicKey)!.DeviceName);
            Assert.False(b.Find(laptop.Dh.PublicKey)!.NewDevice);
            var path = Path.Combine(_dir, "bob.json");
            Assert.True(new ContactBook(path).Find(phone.Dh.PublicKey)!.NewDevice);
            // Until I confirm it, a room invite to that device doesn't let it straight in.
            Assert.False(b.Find(phone.Dh.PublicKey)!.MayBeAutoAdmitted);
            Assert.True(b.Find(laptop.Dh.PublicKey)!.MayBeAutoAdmitted);
            b.AcknowledgeDevice(phone.Dh.PublicKey);
            Assert.False(new ContactBook(path).Find(phone.Dh.PublicKey)!.NewDevice);
            Assert.True(b.Find(phone.Dh.PublicKey)!.MayBeAutoAdmitted);
        }
    }

    [Fact]
    public void OlderCertificateEpochIsNotRecognized()
    {
        var (b, bobKey, laptop, phone) = FriendWithTwoDevices(phoneEpoch: 2);
        using (laptop) using (bobKey) using (phone)
        {
            Assert.Equal(2, b.Find(phone.Dh.PublicKey)!.Epoch);
            // A certificate from before Alice's account moved to epoch 2 isn't enough any more.
            using var old = DeviceKeys.Generate();
            Assert.Null(b.SeenLive(old.Dh.PublicKey, _alice.CertifyDevice(old, "Old", 1, Now)));
            Assert.False(b.IsVerified(old.Dh.PublicKey));
        }
    }

    [Fact]
    public void BackupCarriesRemovedDevicesAndHidesTheCount()
    {
        using var gone = X25519KeyPair.Generate();
        var one = new[] { new Contact(new byte[32].Select((_, i) => (byte)(i + 1)).ToArray(), "Bob", true, Now) { JayAccount = _bob.Address } };
        var many = Enumerable.Range(1, 12).Select(n => new Contact(Enumerable.Repeat((byte)n, 32).ToArray(), "Friend " + n, true, Now) { JayAccount = _bob.Address }).ToArray();
        var small = FriendBackup.Export(_alice, one, Now, [new RemovedDevice(gone.PublicKey, _bob.Address)])!;
        var big = FriendBackup.Export(_alice, many, Now)!;
        // Padded: both fit the same 4 KiB block, so the size doesn't give the number of friends away.
        Assert.Equal(small.Length, big.Length);
        var contents = FriendBackup.Import(_alice, small)!;
        Assert.Equal(Now, contents.Created);
        var removed = Assert.Single(contents.Removed);
        Assert.Equal(gone.PublicKey, removed.PublicKey);

        using var me = X25519KeyPair.Generate();
        var book = Book("restored", me);
        Assert.Equal(1, book.Import(contents.Contacts, contents.Removed));
        Assert.Single(book.RemovedDevices);
        Assert.Single(new ContactBook(Path.Combine(_dir, "restored.json")).RemovedDevices);
    }

    [Fact]
    public void RemovingAPersonRemovesAllTheirDevices()
    {
        using var me = X25519KeyPair.Generate();
        var book = Book("p", me);
        var one = new byte[32];
        one[0] = 1;
        var two = new byte[32];
        two[0] = 2;
        var solo = new byte[32];
        solo[0] = 3;
        book.Import([
            new Contact(one, "Bob", true, Now) { JayAccount = _bob.Address },
            new Contact(two, "Bob", true, Now) { JayAccount = _bob.Address },
            new Contact(solo, "Sam", true, Now),
        ]);
        Assert.Equal(2, book.DevicesOf(book.Find(one)!).Count);
        Assert.Single(book.DevicesOf(book.Find(solo)!));
        // One lost device only.
        book.Remove(two);
        Assert.Single(book.DevicesOf(book.Find(one)!));
        // A removed device can't come back through an import (only through the emoji).
        Assert.Equal(0, book.Import([new Contact(two, "Bob", true, Now) { JayAccount = _bob.Address }]));
        book.RemovePerson(one);
        Assert.Equal("Sam", Assert.Single(book.All).Name);
    }

    /// <summary>A backup made before version 1 addresses (bound to the Ed25519 account key, naming the old address) still opens.</summary>
    [Fact]
    public void BackupFromBeforeNewAddressesStillOpens()
    {
        using var friend = X25519KeyPair.Generate();
        var plain = System.Text.Encoding.UTF8.GetBytes(new System.Text.Json.Nodes.JsonObject
        {
            ["v"] = 1,
            ["account"] = JayAddress.EncodeVersion0(_alice.PublicKey),
            ["created"] = Now.ToUnixTimeSeconds(),
            ["contacts"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
            {
                ["pub"] = Convert.ToHexStringLower(friend.PublicKey),
                ["name"] = "Bob",
                ["added"] = Now.ToUnixTimeSeconds(),
                ["jay"] = JayAddress.EncodeVersion0(_bob.PublicKey),
            }),
        }.ToJsonString());
        var nonce = new byte[12];
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        var key = _alice.DeriveFriendBackupKey();
        using (var aes = new System.Security.Cryptography.AesGcm(key, 16))
            aes.Encrypt(nonce, plain, cipher, tag, (byte[])[.. "JAYFB1\n"u8, .. _alice.PublicKey]);
        byte[] old = [.. "JAYFB1\n"u8, .. nonce, .. cipher, .. tag];

        var restored = Assert.Single(FriendBackup.Import(_alice, old)!.Contacts);
        Assert.Equal("Bob", restored.Name);
        // Someone else's old address can't be turned into a new one: it is learned again from their next knock.
        Assert.Null(restored.JayAccount);
        Assert.Null(FriendBackup.Import(_bob, old));
    }

    private static byte[] SeedOf(JayAccount account) => account.ExportSeed();

    public void Dispose()
    {
        _alice.Dispose();
        _bob.Dispose();
        try { Directory.Delete(_dir, true); }
        catch (DirectoryNotFoundException) { }
    }
}
