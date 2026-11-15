namespace Amber.Core.Multiplayer;

/// <summary>
/// Which chat connections the app listens to. Normally that is the current connection only. While chat moves to a new
/// connection, the hub sends each message to exactly one of the two: to the new one once it has joined (possibly before it
/// is in place), to the old one before that (possibly handled after the switch). So both sides of a move are heard.
/// <para>Everything is tied to the room session: <see cref="Retire"/> (leaving the room, or its network changing) starts a
/// new generation and forgets both sides of a move, and callbacks queued by a connection of an earlier generation are
/// dropped (<see cref="IsCurrent"/>). A late kick, breaker, rekey or remote request from a room that was left never acts on
/// the next one, and an answer held back during a move is never sent into the next one.</para>
/// </summary>
public sealed class ChatRoute<T> where T : class
{
    public int Generation { get; private set; }
    public T? Incoming { get; private set; }
    public T? Outgoing { get; private set; }

    public bool IsCurrent(int generation) => generation == Generation;

    /// <summary>
    /// For one connection, made when it is created: wraps a handler so that it only runs while this room session (this
    /// generation) is still the current one. The app queues every callback of the connection through it.
    /// </summary>
    public Func<Action, Action> Binder()
    {
        var generation = Generation;
        return handle => () =>
        {
            if (generation == Generation) handle();
        };
    }

    /// <summary>Whether what <paramref name="client"/> receives belongs to the chat whose connection is <paramref name="current"/>.</summary>
    public bool Accepts(T client, T? current) => client == current || client == Incoming || client == Outgoing;

    /// <summary>
    /// Which connection answers something that came in on <paramref name="client"/>: the same one while the hub still
    /// knows it (the current connection, or the new one of a move even before it is in place here), otherwise the one in
    /// use now (the request came in on the old side of a move, which the hub has dropped).
    /// </summary>
    public T ReplyVia(T client, T? current) =>
        client == current || client == Incoming || current is null ? client : current;

    private readonly List<(T Client, Func<T, Task> Send)> _waiting = [];

    /// <summary>
    /// Answers something that came in on <paramref name="client"/>. Outside a move it goes out now (<see cref="ReplyVia"/>).
    /// During a move it waits for the move to end: until then it isn't known here whether the hub has already taken the new
    /// connection in (and dropped the old one, the current one here) or not yet (the new one hasn't joined).
    /// </summary>
    public void Answer(T client, T? current, Func<T, Task> send)
    {
        if (Incoming is not null) _waiting.Add((client, send));
        else _ = send(ReplyVia(client, current));
    }

    /// <summary>A move started: <paramref name="incoming"/> is heard from now on.</summary>
    public void Moving(T incoming) => Incoming = incoming;

    /// <summary>
    /// The move ended (in place or not): the new connection is no longer heard as the incoming one, and answers held back
    /// during the move go out over <paramref name="current"/>, the connection in use now (none when chat dropped).
    /// </summary>
    public void MoveEnded(T incoming, T? current)
    {
        if (Incoming != incoming) return;
        Incoming = null;
        var waiting = _waiting.ToArray();
        _waiting.Clear();
        if (current is null) return;
        foreach (var (client, send) in waiting) _ = send(ReplyVia(client, current));
    }

    /// <summary>The move is in place: <paramref name="old"/> is still heard until <see cref="Closed"/>.</summary>
    public void Switched(T old) => Outgoing = old;

    public void Closed(T old)
    {
        if (Outgoing == old) Outgoing = null;
    }

    /// <summary>The room session ended or changed network: nothing of it is heard any more.</summary>
    public void Retire()
    {
        Generation++;
        Incoming = null;
        Outgoing = null;
        _waiting.Clear();
    }
}
