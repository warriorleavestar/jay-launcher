using Amber.Core.Identity;
using Amber.Core.Multiplayer;

namespace Amber.Core.Tests;

/// <summary>The "let them straight in" list: trusting one device versus a person with a Jay account.</summary>
public sealed class TrustedMembersTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jay-trusted-" + Guid.NewGuid().ToString("N"));

    public TrustedMembersTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); }
        catch (IOException) { }
    }

    private static byte[] Key(byte n)
    {
        var key = new byte[32];
        key[0] = n;
        key[31] = 0x40;
        return key;
    }

    [Fact]
    public void AddDeviceReplacesAnEntryAndDropsItsAccount()
    {
        using var bob = JayAccount.FromMnemonic(Bip39.Generate(MnemonicLanguage.English), null);
        var path = Path.Combine(_dir, "trusted.json");
        var list = new TrustedMembers(path);
        list.Add(Key(1), "Bob", bob.Address);
        Assert.True(list.IsTrustedJayAccount(bob.Address));

        list.AddDevice(Key(1), "Bob's PC");
        var entry = Assert.Single(new TrustedMembers(path).All);
        Assert.Equal("Bob's PC", entry.Name);
        Assert.Null(entry.JayAccount);
        Assert.False(list.IsTrustedJayAccount(bob.Address));
        Assert.True(list.IsTrusted(Key(1)));
    }

    [Fact]
    public void RemoveDeviceLeavesTheOtherDevicesOfTheSameAccount()
    {
        using var bob = JayAccount.FromMnemonic(Bip39.Generate(MnemonicLanguage.English), null);
        var list = new TrustedMembers(Path.Combine(_dir, "trusted.json"));
        list.Add(Key(1), "Bob", bob.Address);
        list.Add(Key(2), "Bob", bob.Address);
        list.AddDevice(Key(3), "Sam");

        list.RemoveDevice(Key(1));
        Assert.False(list.IsTrusted(Key(1)));
        Assert.True(list.IsTrusted(Key(2)));
        Assert.True(list.IsTrustedJayAccount(bob.Address));
        Assert.True(list.IsTrusted(Key(3)));
        // Removing a device that is not on the list changes nothing.
        list.RemoveDevice(Key(9));
        Assert.Equal(2, list.All.Count);

        // Remove takes the person: every device under their account, and nobody else.
        list.Add(Key(1), "Bob", bob.Address);
        list.Remove(Key(1));
        Assert.False(list.IsTrusted(Key(2)));
        Assert.True(list.IsTrusted(Key(3)));
    }
}
