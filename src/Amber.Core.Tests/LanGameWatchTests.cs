using Amber.Core.Multiplayer;

namespace Amber.Core.Tests;

/// <summary>The room's game list: worlds open to LAN, kept from their broadcasts, and which one a room follows by itself.</summary>
public sealed class LanGameWatchTests
{
    private static readonly DateTime T0 = new(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ListsWorldsFromTheirBroadcastsNewestFirst()
    {
        var watch = new LanGameWatch();
        Assert.True(watch.Saw(new LanGame("A", 25565), T0));
        Assert.False(watch.Saw(new LanGame("A", 25565), T0.AddSeconds(1.5)));
        Assert.True(watch.Saw(new LanGame("B", 25600), T0.AddSeconds(2)));
        Assert.Equal([25600, 25565], watch.Current(T0.AddSeconds(3)).Select(g => g.Port));
        // A new name on a known port is a change too.
        Assert.True(watch.Saw(new LanGame("A2", 25565), T0.AddSeconds(3)));
        Assert.Equal("A2", watch.Current(T0.AddSeconds(3)).Single(g => g.Port == 25565).Motd);
    }

    [Fact]
    public void AWorldNotHeardForSixSecondsDropsOff()
    {
        var watch = new LanGameWatch();
        watch.Saw(new LanGame("A", 25565), T0);
        watch.Saw(new LanGame("B", 25600), T0);
        watch.Saw(new LanGame("B", 25600), T0.AddSeconds(5));
        Assert.False(watch.Expire(T0.AddSeconds(6)));
        Assert.Equal(2, watch.Current(T0.AddSeconds(6)).Count);
        Assert.True(watch.Expire(T0.AddSeconds(6.5)));
        Assert.Equal([25600], watch.Current(T0.AddSeconds(6.5)).Select(g => g.Port));
        // Heard again later: it counts as newly opened.
        Assert.True(watch.Saw(new LanGame("A", 25565), T0.AddSeconds(20)));
        Assert.Equal(25565, watch.Current(T0.AddSeconds(20))[0].Port);
    }

    [Fact]
    public void AQuietWorldIsLeftOutBeforeItIsExpired()
    {
        var watch = new LanGameWatch();
        watch.Saw(new LanGame("A", 25565), T0);
        Assert.Empty(watch.Current(T0.AddSeconds(7)));
        watch.Clear();
        Assert.Empty(watch.Current(T0));
    }

    [Fact]
    public void AutoPickTakesTheNewestWorldOnlyWhenTheRoomHasNone()
    {
        LanGame[] current = [new("New", 25600), new("Old", 25565)];
        // Waiting for the game: the newest world.
        Assert.Equal(25600, LanGameWatch.AutoPick(current, null, typed: false, relayPort: 40000));
        // Linked to a world that is still open: stay, a second world is only listed.
        Assert.Null(LanGameWatch.AutoPick(current, 25565, typed: false, relayPort: 40000));
        // The linked world went quiet (reopened on a new port): follow it.
        Assert.Equal(25600, LanGameWatch.AutoPick([new("New", 25600)], 25565, typed: false, relayPort: 40000));
        // ...unless it was never heard (linked from the game's log before its first broadcast).
        Assert.Null(LanGameWatch.AutoPick([new("New", 25600)], 25565, typed: false, relayPort: 40000, linkedWasHeard: false));
        // A typed port is the player's to change.
        Assert.Null(LanGameWatch.AutoPick(current, null, typed: true, relayPort: 40000));
        Assert.Null(LanGameWatch.AutoPick([new("New", 25600)], 25565, typed: true, relayPort: 40000));
        // The room's own port is never a game.
        Assert.Null(LanGameWatch.AutoPick([new("Room", 40000)], null, typed: false, relayPort: 40000));
        Assert.Null(LanGameWatch.AutoPick([], null, typed: false, relayPort: 40000));
    }

    [Fact]
    public void ABroadcastIsBoundedInLengthAndName()
    {
        var name = new string('w', 5000);
        // Too long to be a game's broadcast: not read at all.
        Assert.Null(LanGames.Parse(LanGames.Format(name, 25565)));
        Assert.Null(LanGames.Read(new byte[LanGames.MaxPacketBytes + 1]));
        // A long name that still fits is cut.
        var fits = LanGames.Parse(LanGames.Format(new string('w', 500), 25565))!;
        Assert.Equal(LanGames.MaxMotdChars, fits.Motd.Length);
        Assert.Equal(25565, fits.Port);
        Assert.Equal(new LanGame("Survival", 25565), LanGames.Read(System.Text.Encoding.UTF8.GetBytes(LanGames.Format("Survival", 25565))));
    }

    [Fact]
    public void ManyPortsNeverGrowTheListPastItsLimitAndKeepTheOnesAlreadyThere()
    {
        var watch = new LanGameWatch();
        Assert.True(watch.Saw(new LanGame("Real", 25565), T0));
        for (var port = 30000; port < 31000; port++) watch.Saw(new LanGame($"x{port}", port), T0.AddSeconds(1));
        Assert.Equal(LanGameWatch.MaxGames, watch.Count);
        Assert.True(watch.Has(25565));
        // Repeats and new names on a kept port are still taken.
        Assert.True(watch.Saw(new LanGame("Real 2", 25565), T0.AddSeconds(2)));
        // Once the flood goes quiet, a new world gets in again.
        Assert.False(watch.Saw(new LanGame("Late", 26000), T0.AddSeconds(3)));
        Assert.True(watch.Saw(new LanGame("Later", 26001), T0.AddSeconds(8)));
        Assert.True(watch.Has(26001));
        Assert.True(watch.Count <= LanGameWatch.MaxGames);
    }

    [Fact]
    public void TheInboxGathersAFloodIntoOneUpdatePerPort()
    {
        var inbox = new LanGameInbox();
        var schedules = 0;
        for (var i = 0; i < 10_000; i++)
            if (inbox.Add(new LanGame($"n{i}", 25565 + i % 100))) schedules++;
        Assert.Equal(1, schedules);
        Assert.Equal(LanGameWatch.MaxGames, inbox.Count);
        var taken = inbox.Take();
        Assert.Equal(LanGameWatch.MaxGames, taken.Count);
        Assert.Equal(taken.Count, taken.Select(g => g.Port).Distinct().Count());
        // The newest name per port is the one kept.
        Assert.Equal("n9900", taken.Single(g => g.Port == 25565).Motd);
        Assert.Equal(0, inbox.Count);
        // After a take, the next broadcast schedules again.
        Assert.True(inbox.Add(new LanGame("A", 25565)));
        Assert.False(inbox.Add(new LanGame("B", 25566)));
    }
}
