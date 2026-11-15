namespace Amber.Mobile;

/// <summary>What the system Back gesture does on the game screen.</summary>
public enum EmbeddedGameBackAction
{
    /// <summary>No game has run yet: leave the setup screen.</summary>
    CloseScreen,
    /// <summary>The game is running: send Esc so Back pauses or closes the in-game screen.</summary>
    SendEscape,
    /// <summary>The game has ended: leave and end the game process so the next game gets a fresh one.</summary>
    EndProcess
}

/// <summary>
/// The single game run a game process can host. An embedded JVM can be created only once per process,
/// so after the first run starts the game can never be started again in the same process.
/// </summary>
public sealed class EmbeddedGameRunState
{
    private const int NotStarted = 0, Running = 1, Ended = 2;
    private int _state;

    public bool HasStarted => Volatile.Read(ref _state) != NotStarted;
    public bool IsRunning => Volatile.Read(ref _state) == Running;

    public EmbeddedGameBackAction BackAction => Volatile.Read(ref _state) switch
    {
        Running => EmbeddedGameBackAction.SendEscape,
        Ended => EmbeddedGameBackAction.EndProcess,
        _ => EmbeddedGameBackAction.CloseScreen
    };

    /// <summary>Runs the game once; any later call fails without running it.</summary>
    public void Run(Action game)
    {
        if (Interlocked.CompareExchange(ref _state, Running, NotStarted) != NotStarted)
            throw new InvalidOperationException("The game already ran in this process.");
        try
        {
            game();
        }
        finally
        {
            Volatile.Write(ref _state, Ended);
        }
    }
}
