namespace Amber.Core.Multiplayer;

/// <summary>
/// Which chat connections the app listens to. Normally that is the current connection only. While chat moves to a new
/// connection, the hub sends each message to exactly one of the two: to the new one once it has joined (possibly before it
/// is in place), to the old one before that (possibly handled after the switch). So both sides of a move are heard.
/// <para>Everything is tied to the room session: <see cref="Retire"/> (leaving the room, or its network changing) starts a
/// new generation and forgets both sides of a move, and callbacks queued by a connection of an earlier generation are
/// dropped (<see cref="IsCurrent"/>). A late kick, breaker, rekey or remote request from a room that was left never acts on
/// the next one.</para>
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

    /// <summary>A move started: <paramref name="incoming"/> is heard from now on.</summary>
    public void Moving(T incoming) => Incoming = incoming;

    /// <summary>The move ended (in place or not): the new connection is no longer heard as the incoming one.</summary>
    public void MoveEnded(T incoming)
    {
        if (Incoming == incoming) Incoming = null;
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
    }
}
