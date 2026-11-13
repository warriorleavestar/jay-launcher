using Amber.Core.Multiplayer;

namespace Amber.Core.Tests;

/// <summary>Which chat connections are heard: both sides of a move, and nothing from a room session that is over.</summary>
public sealed class ChatRouteTests
{
    private sealed class Conn;

    [Fact]
    public void OnlyTheCurrentConnectionIsHeardOutsideAMove()
    {
        var route = new ChatRoute<Conn>();
        Conn current = new(), other = new();
        Assert.True(route.Accepts(current, current));
        Assert.False(route.Accepts(other, current));
    }

    [Fact]
    public void BothSidesOfAMoveAreHeardUntilTheOldOneCloses()
    {
        var route = new ChatRoute<Conn>();
        Conn old = new(), incoming = new();
        route.Moving(incoming);
        // Before the switch: the new connection may already receive.
        Assert.True(route.Accepts(incoming, old));
        Assert.True(route.Accepts(old, old));
        route.Switched(old);
        route.MoveEnded(incoming);
        // After the switch: what the old connection had queued still counts.
        Assert.True(route.Accepts(old, incoming));
        Assert.True(route.Accepts(incoming, incoming));
        route.Closed(old);
        Assert.False(route.Accepts(old, incoming));
    }

    [Fact]
    public void AMoveThatFailedStopsHearingTheNewConnection()
    {
        var route = new ChatRoute<Conn>();
        Conn old = new(), incoming = new();
        route.Moving(incoming);
        route.MoveEnded(incoming);
        Assert.False(route.Accepts(incoming, old));
        Assert.True(route.Accepts(old, old));
    }

    /// <summary>
    /// Leaving room A during a move and joining B: A's connections, on either side of the move, are no longer heard, and
    /// anything they queued before is dropped by generation.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NothingFromALeftRoomIsHeard(bool incomingSide)
    {
        var route = new ChatRoute<Conn>();
        Conn oldA = new(), newA = new(), chatB = new();
        var generationA = route.Generation;
        route.Moving(newA);
        if (!incomingSide)
        {
            route.Switched(oldA);
            route.MoveEnded(newA);
        }
        route.Retire();
        Assert.False(route.IsCurrent(generationA));
        Assert.False(route.Accepts(incomingSide ? newA : oldA, chatB));
        Assert.True(route.Accepts(chatB, chatB));
        // A late close from A doesn't touch anything of B's.
        route.Closed(oldA);
        route.MoveEnded(newA);
        Assert.True(route.IsCurrent(route.Generation));
    }
}
