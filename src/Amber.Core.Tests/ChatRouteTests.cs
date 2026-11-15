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
        route.MoveEnded(incoming, incoming);
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
        route.MoveEnded(incoming, old);
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
            route.MoveEnded(newA, newA);
        }
        route.Retire();
        Assert.False(route.IsCurrent(generationA));
        Assert.False(route.Accepts(incomingSide ? newA : oldA, chatB));
        Assert.True(route.Accepts(chatB, chatB));
        // A late close from A doesn't touch anything of B's.
        route.Closed(oldA);
        route.MoveEnded(newA, chatB);
        Assert.True(route.IsCurrent(route.Generation));
    }

    /// <summary>
    /// The callbacks themselves: queued by room A's connections (either side of a move), run after leaving A and joining
    /// B. None of them runs; B's own do.
    /// </summary>
    [Fact]
    public void QueuedCallbacksFromALeftRoomDontRun()
    {
        var route = new ChatRoute<Conn>();
        var queue = new List<Action>();
        var ran = new List<string>();
        var bindA = route.Binder();
        route.Moving(new Conn());
        foreach (var what in new[] { "kick", "breaker", "rekey", "remote", "message" })
            queue.Add(bindA(() => ran.Add("A " + what)));
        route.Retire();
        var bindB = route.Binder();
        queue.Add(bindB(() => ran.Add("B message")));
        foreach (var callback in queue) callback();
        Assert.Equal(["B message"], ran);
    }

    [Fact]
    public void CallbacksOfTheSameSessionRunAcrossAMove()
    {
        var route = new ChatRoute<Conn>();
        var ran = 0;
        var bindOld = route.Binder();
        Conn old = new(), incoming = new();
        route.Moving(incoming);
        var bindNew = route.Binder();
        route.Switched(old);
        route.MoveEnded(incoming, incoming);
        bindOld(() => ran++)();
        bindNew(() => ran++)();
        Assert.Equal(2, ran);
    }

    /// <summary>An answer goes over a connection the hub still knows, on whichever side of a move the request came in.</summary>
    [Fact]
    public void RepliesGoOverAConnectionTheHubStillKnows()
    {
        var route = new ChatRoute<Conn>();
        Conn old = new(), incoming = new();
        Assert.Same(old, route.ReplyVia(old, old));
        route.Moving(incoming);
        // Came in on the new connection before it is in place here: the hub sent it there, so answer there.
        Assert.Same(incoming, route.ReplyVia(incoming, old));
        route.Switched(old);
        route.MoveEnded(incoming, incoming);
        // Came in on the old connection, handled after the switch: the hub has dropped it, so answer on the new one.
        Assert.Same(incoming, route.ReplyVia(old, incoming));
        route.Closed(old);
        Assert.Same(incoming, route.ReplyVia(old, incoming));
        // No chat connection at all: the one it came in on is all there is.
        Assert.Same(old, route.ReplyVia(old, null));
    }

    /// <summary>
    /// During a move it isn't known here whether the hub has taken the new connection in yet, so an answer waits for the
    /// move to end and then goes over the connection in use: the new one after a switch, the old one after a failed move.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnswersDuringAMoveWaitForItToEnd(bool switched)
    {
        var route = new ChatRoute<Conn>();
        Conn old = new(), incoming = new();
        var sent = new List<Conn>();
        Task Send(Conn via)
        {
            sent.Add(via);
            return Task.CompletedTask;
        }
        route.Answer(old, old, Send);
        Assert.Equal([old], sent);
        sent.Clear();
        route.Moving(incoming);
        // On the old side (the hub may have dropped it already) and on the new side (it may not have joined yet).
        route.Answer(old, old, Send);
        route.Answer(incoming, old, Send);
        Assert.Empty(sent);
        if (switched) route.Switched(old);
        var current = switched ? incoming : old;
        route.MoveEnded(incoming, current);
        Assert.Equal([current, current], sent);
        // Sent once only.
        route.MoveEnded(incoming, current);
        Assert.Equal(2, sent.Count);
    }

    [Fact]
    public void AnswersHeldBackAreDroppedWhenChatDropsOrTheRoomIsLeft()
    {
        var route = new ChatRoute<Conn>();
        Conn old = new(), incoming = new(), next = new();
        var sent = 0;
        Task Send(Conn _)
        {
            sent++;
            return Task.CompletedTask;
        }
        route.Moving(incoming);
        route.Answer(old, old, Send);
        // The move failed and the hub had dropped the old connection: no chat to answer over.
        route.MoveEnded(incoming, null);
        Assert.Equal(0, sent);
        route.Moving(incoming);
        route.Answer(old, old, Send);
        route.Retire();
        // The next room session's move doesn't send the last one's answers.
        route.Moving(next);
        route.MoveEnded(next, next);
        route.MoveEnded(incoming, next);
        Assert.Equal(0, sent);
    }
}
