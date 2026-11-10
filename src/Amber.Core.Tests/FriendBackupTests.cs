using Amber.Core.Identity;
using Amber.Core.Multiplayer.Contacts;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Tests;

/// <summary>Friend list backups under a Jay account, and getting friends back on a new computer without the emoji.</summary>
public sealed class FriendBackupTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jay-friends-" + Guid.NewGuid().ToString("N"));
    private readonly JayAccount _alice = JayAccount.FromMnemonic(Bip39.Generate(MnemonicLanguage.English), null);
    private readonly JayAccount _bob = JayAccount.FromMnemonic(Bip39.Generate(MnemonicLanguage.English), null);

    private ContactBook Book(string name, X25519KeyPair me, JayAccount? account, string device = "Windows") =>
        new(Path.Combine(_dir, name + ".json"), () => Now) { Me = me.PublicKey, MyCertificate = account?.CertifyDevice(me.PublicKey, device, 0, Now) };

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
        var backup = FriendBackup.Export(_alice, contacts, Now);
        Assert.True(FriendBackup.LooksLikeBackup(backup));

        // Restoring the same words on another computer opens it.
        using var again = JayAccount.FromSeed(SeedOf(_alice));
        var restored = Assert.Single(FriendBackup.Import(again, backup)!);
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
    public void RestoredFriendsReconnectWithoutEmoji()
    {
        using var aliceOld = X25519KeyPair.Generate();
        using var bobPc = X25519KeyPair.Generate();
        var a = Book("alice-old", aliceOld, _alice);
        var b = Book("bob", bobPc, _bob, "Linux");
        Assert.True(Exchange(a, aliceOld, b, bobPc));
        a.Rename(bobPc.PublicKey, "Bobby");
        var backup = FriendBackup.Export(_alice, a.All, Now);

        // New computer: restore the words, import the backup, say hello again.
        using var aliceNew = X25519KeyPair.Generate();
        var a2 = Book("alice-new", aliceNew, _alice, "macOS");
        Assert.Equal(1, a2.Import(FriendBackup.Import(_alice, backup)!));
        Assert.Equal(0, a2.Import(FriendBackup.Import(_alice, backup)!));
        Assert.True(Exchange(a2, aliceNew, b, bobPc, relink: true, confirmByHand: false));
        Assert.Empty(a2.Pending);
        Assert.Empty(b.Pending);
        // Bob's list: one person, two devices, still called by the name he had.
        var alice = b.FindVerifiedByJayAccount(_alice.Address)!;
        Assert.Equal(2, b.DevicesOf(alice).Count);
        Assert.All(b.DevicesOf(alice), c => Assert.Equal("A", c.Name));
        // Alice's new device keeps the name she gave Bob.
        Assert.Equal("Bobby", a2.Find(bobPc.PublicKey)!.Name);
    }

    [Fact]
    public void RelinkNeedsACertificateAndAKnownContact()
    {
        using var me = X25519KeyPair.Generate();
        using var friend = X25519KeyPair.Generate();
        var noAccount = Book("x", me, null);
        noAccount.Import([new Contact(friend.PublicKey, "F", true, Now)]);
        Assert.Null(noAccount.StartRelink(me.PublicKey, friend.PublicKey, "me"));
        var withAccount = Book("y", me, _alice);
        Assert.Null(withAccount.StartRelink(me.PublicKey, friend.PublicKey, "me"));
    }

    [Fact]
    public void FriendWithoutRecognitionStillComparesEmoji()
    {
        using var aliceNew = X25519KeyPair.Generate();
        using var stranger = X25519KeyPair.Generate();
        var a = Book("a", aliceNew, _alice);
        a.Import([new Contact(stranger.PublicKey, "S", true, Now)]);
        // The other side never knew this account: it is asked first, and nothing is confirmed without the emoji.
        var other = Book("s", stranger, null);
        var hello = a.StartRelink(aliceNew.PublicKey, stranger.PublicKey, "A")!;
        Assert.Null(other.Receive(aliceNew.PublicKey, hello, "S"));
        Assert.Equal(ContactStep.AskingMe, Assert.Single(other.Pending).Step);
        var nonce = a.Receive(stranger.PublicKey, other.Accept(aliceNew.PublicKey, "S")!, "A")!;
        other.Receive(aliceNew.PublicKey, nonce, "S");
        Assert.Equal(ContactStep.Compare, Assert.Single(other.Pending).Step);
        Assert.False(other.IsVerified(aliceNew.PublicKey));
    }

    [Fact]
    public void RemovingAPersonRemovesAllTheirDevices()
    {
        using var me = X25519KeyPair.Generate();
        var book = Book("p", me, null);
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
        book.Import([new Contact(two, "Bob", true, Now) { JayAccount = _bob.Address }]);
        book.RemovePerson(two);
        Assert.Equal("Sam", Assert.Single(book.All).Name);
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
