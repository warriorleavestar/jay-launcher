namespace Amber.Core.Multiplayer.Files;

/// <summary>A file waiting for someone who is not reachable yet. The file stays on this computer; nothing goes to a server.</summary>
public sealed record QueuedFile(string Id, string Person, string Path, string Name, FileKind Kind, DateTimeOffset Queued);

/// <summary>
/// Files for people who are not online right now (design: "When they are offline"). It lives in memory for this run of
/// Jay, like the conversation it belongs to; each file starts by itself, oldest first, once that person is reachable.
/// </summary>
public sealed class FileSendQueue
{
    /// <summary>At most this many files wait at once (for everyone together).</summary>
    public const int MaxQueued = 50;

    private readonly List<QueuedFile> _items = [];
    private readonly object _lock = new();

    public int Count { get { lock (_lock) return _items.Count; } }

    public IReadOnlyList<QueuedFile> All { get { lock (_lock) return [.. _items]; } }

    /// <summary>False when the queue is full (the card then says it could not be sent).</summary>
    public bool Add(QueuedFile file)
    {
        lock (_lock)
        {
            if (_items.Count >= MaxQueued || _items.Any(i => i.Id == file.Id)) return false;
            _items.Add(file);
            return true;
        }
    }

    public bool Remove(string id)
    {
        lock (_lock) return _items.RemoveAll(i => i.Id == id) > 0;
    }

    /// <summary>Takes everything waiting for <paramref name="person"/>, oldest first.</summary>
    public IReadOnlyList<QueuedFile> TakeFor(string person)
    {
        lock (_lock)
        {
            var mine = _items.Where(i => i.Person == person).OrderBy(i => i.Queued).ToList();
            _items.RemoveAll(i => i.Person == person);
            return mine;
        }
    }

    public bool IsWaitingFor(string person)
    {
        lock (_lock) return _items.Any(i => i.Person == person);
    }

    public void Clear()
    {
        lock (_lock) _items.Clear();
    }
}
