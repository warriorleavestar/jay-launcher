namespace Amber.Core.Content;

/// <summary>
/// One entry of Discover's "to install" bar (official design §6.12): a chosen file of a project, where it goes, and the
/// optional dependencies the player ticked. Required dependencies are resolved when the queue is installed.
/// </summary>
public sealed record QueuedContent(
    ContentType Type,
    string InstanceId,
    ContentFile File,
    string Title,
    string? WorldPath = null,
    IReadOnlySet<string>? OptionalProjects = null,
    IReadOnlySet<string>? OptionalFiles = null,
    bool ViaConnector = false)
{
    public string Key => InstallQueue.KeyOf(File.Source, File.ProjectId);
}

/// <summary>
/// The "to install" list: one entry per project (adding a project again replaces its entry, so changing the version in
/// the detail pane doesn't queue it twice), installed together as one task.
/// </summary>
public sealed class InstallQueue
{
    private readonly List<QueuedContent> _items = [];

    public IReadOnlyList<QueuedContent> Items => _items;
    public int Count => _items.Count;

    public event Action? Changed;

    public static string KeyOf(ContentSource source, string projectId) => $"{source}:{projectId}";

    public bool Contains(ContentSource source, string projectId) => _items.Any(i => i.Key == KeyOf(source, projectId));

    public void Add(QueuedContent item)
    {
        var index = _items.FindIndex(i => i.Key == item.Key);
        if (index >= 0) _items[index] = item;
        else _items.Add(item);
        Changed?.Invoke();
    }

    public bool Remove(ContentSource source, string projectId)
    {
        var removed = _items.RemoveAll(i => i.Key == KeyOf(source, projectId)) > 0;
        if (removed) Changed?.Invoke();
        return removed;
    }

    /// <summary>Space on a result row: queue it, or take it out again. Returns whether it is queued now.</summary>
    public bool Toggle(QueuedContent item)
    {
        if (Remove(item.File.Source, item.File.ProjectId)) return false;
        Add(item);
        return true;
    }

    public void Clear()
    {
        if (_items.Count == 0) return;
        _items.Clear();
        Changed?.Invoke();
    }

    /// <summary>
    /// Several resolved plans going into the same folder become one: a dependency two mods share (Fabric API) is
    /// downloaded once, and a problem reported by both is listed once.
    /// </summary>
    public static InstallPlan Merge(IEnumerable<InstallPlan> plans)
    {
        var files = new List<ContentFile>();
        var seenIds = new HashSet<(ContentSource, string)>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var problems = new List<string>();
        foreach (var plan in plans)
        {
            foreach (var file in plan.Files)
                if (seenIds.Add((file.Source, file.FileId)) & seenNames.Add(file.FileName))
                    files.Add(file);
            foreach (var p in plan.Problems)
                if (!problems.Contains(p)) problems.Add(p);
        }
        return new InstallPlan(files, problems);
    }
}
