namespace Amber.Core.Multiplayer;

/// <summary>
/// When a member moves chat onto the split paths. Chat connects before the direct path is up, so under Auto it starts on
/// one path; once a new connection would split, chat opens again and the hub drops the old connection. Moving also restarts
/// private chat, so it is rare and careful:
/// <list type="bullet">
/// <item>never while private work is going on (a file transfer, a file offer, a mod check or sync);</item>
/// <item>at most <see cref="MaxTries"/> times per room, <see cref="Spacing"/> apart;</item>
/// <item>never again in this room once a move ended split.</item>
/// </list>
/// A move whose new connection still went on one path counts as a try, so a room where splitting keeps failing stops
/// moving chat instead of interrupting it every half minute.
/// </summary>
public sealed class ChatSplitMove
{
    public const int MaxTries = 2;
    public static readonly TimeSpan Spacing = TimeSpan.FromSeconds(30);

    private object? _room;
    private int _tries;
    private DateTime _last = DateTime.MinValue;
    private bool _done;

    public int Tries => _tries;
    public bool Done => _done;

    /// <summary>Whether to move now; when it says yes, the try is counted.</summary>
    /// <param name="room">The current room: counts start again for another room (a rekeyed room counts as another).</param>
    /// <param name="couldSplit">A new chat connection would split now.</param>
    /// <param name="busy">Private work is going on that a move would cut off.</param>
    public bool ShouldTry(object room, DateTime now, bool couldSplit, bool busy)
    {
        if (!ReferenceEquals(room, _room))
        {
            _room = room;
            _tries = 0;
            _last = DateTime.MinValue;
            _done = false;
        }
        if (_done || _tries >= MaxTries || !couldSplit || busy || now - _last < Spacing) return false;
        _tries++;
        _last = now;
        return true;
    }

    /// <summary>The new connection is in: split ends the moving for this room.</summary>
    public void Moved(bool split)
    {
        if (split) _done = true;
    }
}
