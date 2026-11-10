using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Tests;

/// <summary>Long-lived invites: 30 days by default, revoke, expire, survive a restart, go into each new room's invite book.</summary>
public sealed class LongLivedInviteTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("jay-longinv-").FullName;
    private DateTimeOffset _now = new(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);

    private string FilePath => Path.Combine(_dir, "long-invites.json");

    private LongLivedInvites Store() => new(FilePath, () => _now);

    [Fact]
    public void DefaultsToThirtyDaysAndSurvivesARestart()
    {
        var store = Store();
        var invite = store.Create("Bob\n");
        Assert.Equal("Bob", invite.Label);
        Assert.Equal(_now + TimeSpan.FromDays(30), invite.Expires);
        var code = store.CodeOf(invite, TunnelSettings.Default).ToString();

        var again = Store();
        var loaded = Assert.Single(again.Active);
        Assert.Equal(invite.Id, loaded.Id);
        Assert.Equal(code, again.CodeOf(loaded, TunnelSettings.Default).ToString());
    }

    [Fact]
    public void SameCodeWorksInEveryRoomUntilItExpires()
    {
        var store = Store();
        var invite = store.Create("Bob");
        var code = store.CodeOf(invite, TunnelSettings.Default);
        for (var room = 0; room < 2; room++)
        {
            var book = new InviteBook(() => _now);
            Assert.Equal(1, store.SeedInto(book, TunnelSettings.Default));
            var pending = Assert.Single(book.Pending);
            Assert.Equal(code.Token, pending.Invite.Token);
            Assert.Equal(code.LobbyKey, pending.Invite.LobbyKey);
            Assert.False(pending.AutoApprove);
            // Other invites of the room share the lobby key.
            Assert.Equal(code.LobbyKey, book.Create(1, null, false, TunnelSettings.Default).Invite.LobbyKey);
        }
        _now += TimeSpan.FromDays(31);
        Assert.Empty(store.Active);
        Assert.Equal(0, store.SeedInto(new InviteBook(() => _now), TunnelSettings.Default));
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public void ExpiresInsideARunningRoom()
    {
        var store = Store();
        store.Create("Bob", TimeSpan.FromHours(1));
        var book = new InviteBook(() => _now);
        store.SeedInto(book, TunnelSettings.Default);
        Assert.True(book.HasPending);
        _now += TimeSpan.FromHours(2);
        Assert.True(book.Tick());
        Assert.False(book.HasPending);
        Assert.Null(book.Lobby);
    }

    [Fact]
    public void RevokingTheLastOneThrowsTheKeyAway()
    {
        var store = Store();
        var a = store.Create("A");
        var oldKey = store.CodeOf(a, TunnelSettings.Default).LobbyKey;
        var b = store.Create("B");
        store.Revoke(a.Id);
        Assert.Equal([b.Id], store.Active.Select(i => i.Id));
        store.Revoke(b.Id);
        Assert.Empty(store.Active);
        Assert.False(File.Exists(FilePath));
        var c = store.Create("C");
        Assert.NotEqual(oldKey, store.CodeOf(c, TunnelSettings.Default).LobbyKey);
    }

    [Fact]
    public void ARoomWithAnotherKeyRefusesIt()
    {
        var store = Store();
        store.Create("Bob");
        var book = new InviteBook(() => _now);
        book.Create(1, null, false, TunnelSettings.Default);
        Assert.Equal(0, store.SeedInto(book, TunnelSettings.Default));
    }

    [Fact]
    public void DamagedFileStartsEmpty()
    {
        File.WriteAllText(FilePath, "{ not json");
        Assert.Empty(Store().Active);
        Assert.Throws<ArgumentOutOfRangeException>(() => Store().Create("x", TimeSpan.FromDays(400)));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); }
        catch (IOException) { }
    }
}
