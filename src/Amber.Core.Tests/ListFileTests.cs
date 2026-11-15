using System.Text.Json.Nodes;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Contacts;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Tests;

/// <summary>
/// The contact list and the trusted list survive a file that can't be read for a moment, a damaged entry, and a damaged
/// file: nothing is written over a list that couldn't be read, and a damaged file is kept as .broken.
/// </summary>
public sealed class ListFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jay-listfile-" + Guid.NewGuid().ToString("N"));

    public ListFileTests() => Directory.CreateDirectory(_dir);

    private static string Hex() => Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(X25519KeyPair.Length));

    private static Contact NewContact(string name) =>
        new(System.Security.Cryptography.RandomNumberGenerator.GetBytes(X25519KeyPair.Length), name, true, DateTimeOffset.UtcNow);

    [Fact]
    public void ADamagedContactEntryIsSkippedNotTheWholeList()
    {
        var path = Path.Combine(_dir, ContactBook.FileName);
        File.WriteAllText(path, new JsonArray(
            new JsonObject { ["pub"] = Hex(), ["name"] = "Alex", ["verified"] = true },
            new JsonObject { ["pub"] = Hex(), ["name"] = "Bo", ["verified"] = true, ["added"] = "not a number" },
            new JsonObject { ["pub"] = Hex(), ["name"] = "Cy", ["verified"] = true }).ToJsonString());
        var original = File.ReadAllText(path);
        var book = new ContactBook(path);
        Assert.Equal(["Alex", "Cy"], book.All.Select(c => c.Name));
        // Careful until the list is saved again: the skipped entry may be a friend.
        Assert.True(book.Unreadable);
        book.Import([NewContact("Di")]);
        Assert.Equal(original, File.ReadAllText(path + ".broken"));
        Assert.False(book.Unreadable);
    }

    [Fact]
    public void AContactFileThatCantBeReadIsNotWrittenOver()
    {
        var path = Path.Combine(_dir, ContactBook.FileName);
        var first = new ContactBook(path);
        first.Import([NewContact("Alex"), NewContact("Bo")]);
        var before = File.ReadAllText(path);

        var book = new ContactBook(path);
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Empty(book.All);
            Assert.True(book.Unreadable);
            book.Import([NewContact("Cy")]);
        }
        Assert.Equal(before, File.ReadAllText(path));
        // Readable again: the real list is back.
        Assert.Equal(["Alex", "Bo"], book.All.Select(c => c.Name).Order());
        Assert.False(book.Unreadable);
    }

    [Fact]
    public void ADamagedContactFileIsKeptBeforeItIsReplaced()
    {
        var path = Path.Combine(_dir, ContactBook.FileName);
        File.WriteAllText(path, "{ not json");
        var book = new ContactBook(path);
        Assert.Empty(book.All);
        Assert.True(book.Unreadable);
        book.Import([NewContact("Alex")]);
        Assert.Equal("{ not json", File.ReadAllText(path + ".broken"));
        Assert.Equal(["Alex"], new ContactBook(path).All.Select(c => c.Name));
        Assert.False(book.Unreadable);
    }

    [Fact]
    public void TheTrustedListSurvivesTheSame()
    {
        var path = Path.Combine(_dir, "trusted.json");
        File.WriteAllText(path, new JsonArray(
            new JsonObject { ["pub"] = Hex(), ["name"] = "Alex" },
            new JsonObject { ["pub"] = Hex(), ["name"] = 5 },
            new JsonObject { ["pub"] = Hex(), ["name"] = "Cy" }).ToJsonString());
        var damaged = new TrustedMembers(path);
        Assert.Equal(["Alex", "Cy"], damaged.All.Select(m => m.Name));
        var original = File.ReadAllText(path);
        damaged.Add(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32), "Di");
        Assert.Equal(original, File.ReadAllText(path + ".broken"));
        File.Delete(path + ".broken");
        File.WriteAllText(path, original);

        var before = File.ReadAllText(path);
        var trusted = new TrustedMembers(path);
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Empty(trusted.All);
            trusted.Add(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32), "Di");
        }
        Assert.Equal(before, File.ReadAllText(path));
        Assert.Equal(2, trusted.All.Count);

        File.WriteAllText(path, "[ broken");
        var fresh = new TrustedMembers(path);
        Assert.Empty(fresh.All);
        fresh.Add(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32), "Di");
        Assert.Equal("[ broken", File.ReadAllText(path + ".broken"));
        Assert.Single(new TrustedMembers(path).All);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); }
        catch (IOException) { }
    }
}
