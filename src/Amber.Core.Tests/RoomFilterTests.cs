using Amber.Core.Multiplayer.Public;

namespace Amber.Core.Tests;

public class RoomFilterTests
{
    private static RoomListing Room(string name, string host, string game, int players, RoomVisibility mode = RoomVisibility.Public, bool approval = true, int ageMinutes = 0) => new()
    {
        Id = name, Name = name, Host = host, Game = game, Players = players, Mode = mode, Approval = approval,
        HostKey = new byte[32], Updated = DateTimeOffset.UnixEpoch.AddMinutes(1000 - ageMinutes),
    };

    private static readonly RoomListing[] Rooms =
    [
        Room("Sky island", "Alex", "1.20.1 Fabric", 5),
        Room("Build contest", "Bo", "1.21.4", 2, RoomVisibility.Password, ageMinutes: 30),
        Room("Open farm", "Cy", "1.20.1 Fabric", 9, approval: false, ageMinutes: 5),
        Room("alex's base", "Di", "1.21.4", 1, RoomVisibility.Direct, ageMinutes: 1),
    ];

    [Fact]
    public void MostPlayersFirstByDefault() =>
        Assert.Equal(["Open farm", "Sky island", "Build contest", "alex's base"], RoomFilter.Apply(Rooms, "", null, false, RoomSort.MostPlayers).Select(r => r.Name));

    [Fact]
    public void SearchMatchesNameOrHostIgnoringCase() =>
        Assert.Equal(["Sky island", "alex's base"], RoomFilter.Apply(Rooms, " ALEX ", null, false, RoomSort.MostPlayers).Select(r => r.Name));

    [Fact]
    public void GameFilterAndOpenOnly()
    {
        Assert.Equal(["Open farm", "Sky island"], RoomFilter.Apply(Rooms, null, "1.20.1 fabric", false, RoomSort.MostPlayers).Select(r => r.Name));
        Assert.Equal(["Open farm", "alex's base"], RoomFilter.Apply(Rooms, null, null, true, RoomSort.MostPlayers).Select(r => r.Name));
    }

    [Fact]
    public void SortByNameOrNewest()
    {
        Assert.Equal(["alex's base", "Build contest", "Open farm", "Sky island"], RoomFilter.Apply(Rooms, null, null, false, RoomSort.Name).Select(r => r.Name));
        Assert.Equal(["Sky island", "alex's base", "Open farm", "Build contest"], RoomFilter.Apply(Rooms, null, null, false, RoomSort.Newest).Select(r => r.Name));
    }

    [Fact]
    public void GamesAreDistinctAndSorted() => Assert.Equal(["1.20.1 Fabric", "1.21.4"], RoomFilter.Games(Rooms));
}
