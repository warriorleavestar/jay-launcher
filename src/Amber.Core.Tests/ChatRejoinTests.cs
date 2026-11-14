using System.Collections.Concurrent;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Tests;

/// <summary>
/// A member opening chat again (to move it onto the split paths once they are ready) takes over their old connection
/// without the room seeing them leave and join again.
/// </summary>
public sealed class ChatRejoinTests : IDisposable
{
    private readonly X25519KeyPair _host = X25519KeyPair.Generate();
    private readonly X25519KeyPair _alice = X25519KeyPair.Generate();
    private readonly X25519KeyPair _bob = X25519KeyPair.Generate();

    public void Dispose()
    {
        _host.Dispose();
        _alice.Dispose();
        _bob.Dispose();
    }

    private static async Task<bool> WaitAsync(Func<bool> condition, int seconds = 5)
    {
        for (var i = 0; i < seconds * 20; i++)
        {
            if (condition()) return true;
            await Task.Delay(50);
        }
        return condition();
    }

    [Fact]
    public async Task TheSameMemberReconnectingIsNotAnnouncedAgain()
    {
        var credentials = RoomCredentials.Create(_host.PublicKey.ToArray(), 25565, TunnelSettings.Default);
        var table = new MemberTable();
        var hostRoom = credentials.ToHostRoom(table, "Host");
        var alice = table.Issue(_alice.PublicKey, "Alice");
        var bob = table.Issue(_bob.PublicKey, "Bob");
        RoomCode RoomOf(MemberEntry e) => RoomCredentials.FromJson((credentials with { Member = e.Key }).ToJson()).ToRoom();

        await using var hub = new ChatHub(hostRoom, 0);
        await using var bobClient = new ChatClient(RoomOf(bob), "Bob");
        var seen = new ConcurrentQueue<ChatMessage>();
        bobClient.Received += seen.Enqueue;
        await bobClient.ConnectAsync("127.0.0.1", hub.Port);
        var first = new ChatClient(RoomOf(alice), "Alice");
        var firstDropped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.Disconnected += () => firstDropped.TrySetResult();
        await first.ConnectAsync("127.0.0.1", hub.Port);
        Assert.True(await WaitAsync(() => seen.Any(m => m.Kind == ChatKind.Join && m.SenderId == alice.Id)));

        // The second connection takes over: the first is dropped, and nobody hears Alice leave or join again.
        // A connection that gives up before joining leaves the current one alone.
        await using (var calledOff = new ChatClient(RoomOf(alice), "Alice"))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => calledOff.ConnectAsync("127.0.0.1", hub.Port, beforeJoin: () => false));
        Assert.True(await first.SendAsync("first still here"));
        Assert.True(await WaitAsync(() => seen.Any(m => m.Text == "first still here")));
        Assert.False(firstDropped.Task.IsCompleted);

        // The name it claims doesn't count: the room keeps showing the name from the host's member table.
        await using var second = new ChatClient(RoomOf(alice), "Host");
        await second.ConnectAsync("127.0.0.1", hub.Port);
        await firstDropped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await first.CloseQuietlyAsync();
        Assert.True(await second.SendAsync("still here"));
        Assert.True(await WaitAsync(() => seen.Any(m => m.Text == "still here")));
        Assert.Equal("Alice", seen.First(m => m.Text == "still here").Name);
        Assert.True(await WaitAsync(() => hub.MemberCount == 2));
        Assert.Single(seen, m => m.Kind == ChatKind.Join && m.SenderId == alice.Id);
        Assert.DoesNotContain(seen, m => m.Kind == ChatKind.Leave);

        // Leaving for real is still announced.
        await second.DisposeAsync();
        Assert.True(await WaitAsync(() => seen.Any(m => m.Kind == ChatKind.Leave && m.SenderId == alice.Id)));
    }
}

/// <summary>
/// Remote-help answers during a chat move, end to end with a real hub: whichever side of the move a request came in on,
/// the answer picked by <see cref="ChatRoute{T}.ReplyVia"/> reaches the host.
/// </summary>
public sealed class ChatMoveRemoteReplyTests : IDisposable
{
    private readonly X25519KeyPair _host = X25519KeyPair.Generate();
    private readonly X25519KeyPair _alice = X25519KeyPair.Generate();

    public void Dispose()
    {
        _host.Dispose();
        _alice.Dispose();
    }

    [Fact]
    public async Task AnswersReachTheHostFromEitherSideOfAMove()
    {
        var credentials = RoomCredentials.Create(_host.PublicKey.ToArray(), 25565, TunnelSettings.Default);
        var table = new MemberTable();
        var hostRoom = credentials.ToHostRoom(table, "Host");
        var alice = table.Issue(_alice.PublicKey, "Alice");
        var aliceRoom = RoomCredentials.FromJson((credentials with { Member = alice.Key }).ToJson()).ToRoom();
        await using var hub = new ChatHub(hostRoom, 0);
        var declined = new System.Collections.Concurrent.ConcurrentQueue<string>();
        hub.RemoteDeclined += (_, sid) => declined.Enqueue(sid);
        var route = new ChatRoute<ChatClient>();
        string NewSid() => Convert.ToHexStringLower(Amber.Core.RemoteControl.RemoteWire.NewSessionId());
        string beforeMove = NewSid(), duringMove = NewSid();

        var old = new ChatClient(aliceRoom, "Alice");
        var onOld = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        old.RemoteRequested += sid => onOld.TrySetResult(sid);
        var oldDropped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        old.Disconnected += () => oldDropped.TrySetResult();
        await old.ConnectAsync("127.0.0.1", hub.Port);
        Assert.True(await WaitAsync(() => hub.MemberCount == 1));
        // A request comes in on the old connection, and the person is still deciding when chat moves.
        Assert.True(hub.SendRemoteRequest(alice, beforeMove));
        var first = await onOld.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // The move: the new connection joins (the hub drops the old one) before it is in place here.
        await using var moved = new ChatClient(aliceRoom, "Alice");
        var onNew = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        moved.RemoteRequested += sid => onNew.TrySetResult(sid);
        // The hub sends the roster once it has let the new connection in.
        var movedIn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        moved.RosterChanged += _ => movedIn.TrySetResult();
        route.Moving(moved);
        await moved.ConnectAsync("127.0.0.1", hub.Port);
        // The hub has taken over: the new connection is in and the old one is gone.
        await movedIn.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await oldDropped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(await WaitAsync(() => hub.MemberCount == 1));
        Assert.True(hub.SendRemoteRequest(alice, duringMove));
        var second = await onNew.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // Answered before the switch here: over the new connection, which the hub sent it on.
        await route.ReplyVia(moved, old).DeclineRemoteAsync(second);
        Assert.True(await WaitAsync(() => declined.Contains(duringMove)));

        // The switch, then the answer to the request that came in on the old connection: over the new one.
        route.Switched(old);
        route.MoveEnded(moved);
        Assert.Same(moved, route.ReplyVia(old, moved));
        await route.ReplyVia(old, moved).DeclineRemoteAsync(first);
        Assert.True(await WaitAsync(() => declined.Contains(beforeMove)));
        await old.CloseQuietlyAsync();
    }

    private static async Task<bool> WaitAsync(Func<bool> condition, int seconds = 5)
    {
        for (var i = 0; i < seconds * 20; i++)
        {
            if (condition()) return true;
            await Task.Delay(50);
        }
        return condition();
    }
}

public sealed class ChatSplitMoveTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void MovesOnlyWhenASplitIsPossibleAndNothingIsGoingOn()
    {
        var move = new ChatSplitMove();
        var room = new object();
        Assert.False(move.ShouldTry(room, T0, couldSplit: () => false, busy: () => false));
        Assert.False(move.ShouldTry(room, T0, couldSplit: () => true, busy: () => true));
        // Waiting doesn't use up tries.
        Assert.Equal(0, move.Tries);
        Assert.True(move.ShouldTry(room, T0, couldSplit: () => true, busy: () => false));
        Assert.Equal(1, move.Tries);
    }

    [Fact]
    public void AMoveCalledOffBeforeJoiningDoesntCount()
    {
        var move = new ChatSplitMove();
        var room = new object();
        Assert.True(move.ShouldTry(room, T0, () => true, () => false));
        move.NotJoined();
        Assert.Equal(0, move.Tries);
        // Still spaced out from the last try.
        Assert.False(move.ShouldTry(room, T0 + TimeSpan.FromSeconds(1), () => true, () => false));
        Assert.True(move.ShouldTry(room, T0 + ChatSplitMove.Spacing, () => true, () => false));
    }

    [Fact]
    public void NothingIsAskedOnceMovingIsOver()
    {
        var move = new ChatSplitMove();
        var room = new object();
        Assert.True(move.ShouldTry(room, T0, () => true, () => false));
        move.Moved(split: true);
        var asked = 0;
        Assert.False(move.ShouldTry(room, T0 + TimeSpan.FromHours(1), () => { asked++; return true; }, () => { asked++; return false; }));
        Assert.Equal(0, asked);
    }

    [Fact]
    public void ASplitMoveEndsMovingInThisRoom()
    {
        var move = new ChatSplitMove();
        var room = new object();
        Assert.True(move.ShouldTry(room, T0, () => true, () => false));
        move.Moved(split: true);
        Assert.False(move.ShouldTry(room, T0 + TimeSpan.FromHours(1), () => true, () => false));
    }

    [Fact]
    public void FailingMovesStopAfterTwoTriesSpacedOut()
    {
        var move = new ChatSplitMove();
        var room = new object();
        Assert.True(move.ShouldTry(room, T0, () => true, () => false));
        move.Moved(split: false);
        Assert.False(move.ShouldTry(room, T0 + ChatSplitMove.Spacing - TimeSpan.FromSeconds(1), () => true, () => false));
        Assert.True(move.ShouldTry(room, T0 + ChatSplitMove.Spacing, () => true, () => false));
        move.Moved(split: false);
        Assert.False(move.ShouldTry(room, T0 + TimeSpan.FromHours(1), () => true, () => false));
        Assert.Equal(ChatSplitMove.MaxTries, move.Tries);
        // Another room (or the same one rekeyed) starts again.
        Assert.True(move.ShouldTry(new object(), T0 + TimeSpan.FromHours(1), () => true, () => false));
    }
}
