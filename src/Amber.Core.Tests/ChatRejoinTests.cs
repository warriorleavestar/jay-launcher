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
