using System.Collections.ObjectModel;
using Amber.Core.Localization;
using Amber.Core.Multiplayer;
using Amber.Core.Navigation;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Amber.App.ViewModels;

/// <summary>One row in the palette: the searchable item plus what Enter (and Ctrl+Enter) does.</summary>
public sealed class PaletteEntry(CommandItem item, Action run, Action? runAlt = null, string? pickFor = null)
{
    public CommandItem Item { get; } = item;
    public string Title => Item.Title;
    public string Subtitle => Item.Subtitle ?? "";
    public bool HasSubtitle => !string.IsNullOrEmpty(Item.Subtitle);
    public string KindText => L.T(Item.Kind switch
    {
        CommandKind.Invite => "palette.kind.invite",
        CommandKind.Instance => "palette.kind.instance",
        CommandKind.Action => "palette.kind.action",
        CommandKind.Setting => "palette.kind.setting",
        CommandKind.Help => "palette.kind.help",
        _ => "palette.kind.page",
    });
    public Action Run { get; } = run;
    public Action? RunAlt { get; } = runAlt;

    /// <summary>Set for a two-step action: Enter asks for an instance first, then runs this with its id.</summary>
    public string? PickFor { get; } = pickFor;
}

/// <summary>
/// Command palette (official design §6.23): Ctrl+K anywhere. Instances, actions, settings fields and pages by name,
/// pinyin initials, names in other languages and old page names; a pasted invite is recognised and offered first.
/// Two-step actions ("Start game…", "Add mods…") ask for the instance next. Nothing runs without Enter or a click.
/// </summary>
public sealed partial class CommandPaletteViewModel : ObservableObject
{
    private const int RecentMax = 5;
    private readonly List<string> _recent = [];
    private readonly Func<IEnumerable<(string Id, string Name, string Facts)>> _instances;
    private List<PaletteEntry> _entries = [];

    /// <summary>Routes a path such as <c>settings#GameDirectory</c>.</summary>
    public Action<string>? Navigate { get; set; }
    public Action<string>? OpenInstance { get; set; }
    public Action<string>? StartInstance { get; set; }
    public Action<string>? AddModsTo { get; set; }
    public Action? OpenActivity { get; set; }
    public Action<string>? JoinInvite { get; set; }
    public Action<string>? JoinJayServer { get; set; }

    public CommandPaletteViewModel(Func<IEnumerable<(string Id, string Name, string Facts)>> instances) => _instances = instances;

    public ObservableCollection<PaletteEntry> Results { get; } = [];

    [ObservableProperty] public partial bool IsOpen { get; private set; }
    [ObservableProperty] public partial string Query { get; set; } = "";
    [ObservableProperty] public partial int SelectedIndex { get; set; } = -1;

    /// <summary>The two-step action waiting for an instance, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPicking), nameof(PickingText))]
    public partial PaletteEntry? Pending { get; private set; }

    public bool IsPicking => Pending is not null;
    public string PickingText => Pending is { } p ? L.F("palette.pick_instance", p.Title.TrimEnd('\u2026', '.')) : "";

    [ObservableProperty] public partial bool IsEmpty { get; private set; }
    [ObservableProperty] public partial bool ShowsRecent { get; private set; }

    public string Placeholder => L.T("palette.placeholder");

    public void Toggle()
    {
        if (IsOpen) Close();
        else Open();
    }

    public void Open()
    {
        _entries = Build();
        Pending = null;
        Query = "";
        IsOpen = true;
        Refresh();
    }

    public void Close()
    {
        IsOpen = false;
        Pending = null;
    }

    /// <summary>Esc: leaves the instance step first, then closes.</summary>
    public void Back()
    {
        if (Pending is not null)
        {
            Pending = null;
            Query = "";
            Refresh();
        }
        else Close();
    }

    partial void OnQueryChanged(string value)
    {
        if (IsOpen) Refresh();
    }

    public void Move(int delta)
    {
        if (Results.Count == 0) return;
        SelectedIndex = SelectedIndex < 0 ? (delta > 0 ? 0 : Results.Count - 1) : Math.Clamp(SelectedIndex + delta, 0, Results.Count - 1);
    }

    public PaletteEntry? Selected => SelectedIndex >= 0 && SelectedIndex < Results.Count ? Results[SelectedIndex] : null;

    /// <summary>Enter (alt = Ctrl+Enter on an instance: start it).</summary>
    public void Run(PaletteEntry? entry = null, bool alt = false)
    {
        entry ??= Selected;
        if (entry is null) return;
        if (entry.PickFor is not null && Pending is null)
        {
            // Two-step action: pick the instance next.
            Pending = entry;
            Query = "";
            Refresh();
            return;
        }
        Remember(Pending ?? entry);
        Close();
        if (alt && entry.RunAlt is { } a) a();
        else entry.Run();
    }

    /// <summary>Tab: a two-step action goes to its instance step; otherwise the title goes into the box.</summary>
    public void Fill()
    {
        if (Selected is not { } entry) return;
        if (entry.PickFor is not null && Pending is null) Run(entry);
        else Query = entry.Title;
    }

    private void Remember(PaletteEntry entry)
    {
        _recent.Remove(entry.Item.Id);
        _recent.Insert(0, entry.Item.Id);
        if (_recent.Count > RecentMax) _recent.RemoveAt(RecentMax);
    }

    internal IReadOnlyList<string> Recent => _recent;

    private void Refresh()
    {
        Results.Clear();
        var text = Query ?? "";
        var trimmed = text.Trim();
        IEnumerable<PaletteEntry> rows;
        if (Pending is { } pending)
        {
            var instances = _entries.Where(e => e.Item.Kind == CommandKind.Instance).ToList();
            var matches = CommandIndex.Search(trimmed, instances.Select(e => e.Item));
            rows = matches.Select(m => ForPick(pending, instances.First(e => e.Item.Id == m.Item.Id)));
            ShowsRecent = false;
        }
        else if (trimmed.Length == 0)
        {
            // Empty: the recent ones, then the common actions.
            var byId = _entries.ToDictionary(e => e.Item.Id);
            var recent = _recent.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
            rows = recent.Concat(_entries.Where(e => e.Item.Kind == CommandKind.Action && !recent.Contains(e)));
            ShowsRecent = recent.Count > 0;
        }
        else
        {
            var all = _entries.ToList();
            if (InviteEntry(trimmed) is { } invite) all.Insert(0, invite);
            var byId = all.ToDictionary(e => e.Item.Id);
            rows = CommandIndex.Search(text, all.Select(e => e.Item)).Select(m => byId[m.Item.Id]);
            ShowsRecent = false;
        }
        foreach (var row in rows) Results.Add(row);
        IsEmpty = Results.Count == 0;
        SelectedIndex = Results.Count > 0 ? 0 : -1;
    }

    private PaletteEntry ForPick(PaletteEntry pending, PaletteEntry instance)
    {
        var id = instance.Item.Id["instance:".Length..];
        Action run = pending.PickFor switch
        {
            "start" => () => StartInstance?.Invoke(id),
            _ => () => AddModsTo?.Invoke(id),
        };
        return new PaletteEntry(instance.Item, run);
    }

    /// <summary>A pasted invite goes first, whatever else matches.</summary>
    private PaletteEntry? InviteEntry(string text)
    {
        switch (PastedCode.Detect(text))
        {
            case PastedCodeKind.Invite:
                return new PaletteEntry(new CommandItem("invite:room", CommandKind.Invite, L.T("palette.invite.join"), L.T("palette.invite.join_hint"), [text]),
                    () => JoinInvite?.Invoke(text));
            case PastedCodeKind.JayServer:
                return new PaletteEntry(new CommandItem("invite:server", CommandKind.Invite, L.T("palette.invite.server"), L.T("palette.invite.server_hint"), [text]),
                    () => JoinJayServer?.Invoke(text));
            default:
                return null;
        }
    }

    /// <summary>Every name a key has, in every UI language (so a Japanese name finds the page in Chinese UI too).</summary>
    internal static List<string> NamesOf(params string[] keys)
    {
        var names = new List<string>();
        foreach (var key in keys)
            foreach (var (code, _) in L.Languages)
                if (L.Load(code).TryGetValue(key, out var name) && !names.Contains(name, StringComparer.OrdinalIgnoreCase))
                    names.Add(name);
        return names;
    }

    private List<PaletteEntry> Build()
    {
        var list = new List<PaletteEntry>();

        foreach (var (id, name, facts) in _instances())
            list.Add(new PaletteEntry(new CommandItem("instance:" + id, CommandKind.Instance, name, facts, id == name ? null : [id]),
                () => OpenInstance?.Invoke(id), () => StartInstance?.Invoke(id)));

        PaletteEntry Act(string id, string key, Action run, string? pickFor = null, params string[] extra) =>
            new(new CommandItem("action:" + id, CommandKind.Action, L.T(key), null, [.. NamesOf(key), .. extra]), run, null, pickFor);
        list.Add(Act("start", "palette.action.start", () => { }, "start", "play", "launch", "run"));
        list.Add(Act("add-mods", "palette.action.add_mods", () => { }, "mods", "mod", "mods"));
        list.Add(Act("new-instance", "palette.action.new_instance", () => Navigate?.Invoke(LegacyRoutes.NewInstance), null,
            [.. NamesOf("download.title"), "downloads", "install"]));
        list.Add(Act("activity", "palette.action.activity", () => OpenActivity?.Invoke(), null, [.. NamesOf("activity.title"), "Ctrl+J"]));

        // Settings fields: "settings#Field" scrolls to the control and flashes it.
        (string Field, string Key)[] settings =
        [
            ("GameDirectory", "settings.directory.label"),
            ("Concurrency", "settings.download.concurrency"),
            ("Motion", "settings.motion.label"),
            ("Sidebar", "settings.sidebar.label"),
            ("ThemePack", "settings.themepack.label"),
            ("WindowWidth", "instance.settings.window_size"),
            ("JvmArgs", "instance.settings.jvm_args"),
            ("NoTrace", "settings.notrace.label"),
            ("Attestation", "settings.security.attest.label"),
            ("Breaker", "settings.security.breaker.label"),
            ("RecycleKeep", "recycle.keep.label"),
        ];
        var settingsName = L.T("shell.settings");
        foreach (var (field, key) in settings)
            list.Add(new PaletteEntry(new CommandItem("setting:" + field, CommandKind.Setting, L.T(key), settingsName, NamesOf(key)),
                () => Navigate?.Invoke("settings#" + field)));

        // Pages, with the old page names ("Downloads", "Toolbox"…) as other names.
        PaletteEntry Page(string route, string key, params string[] extra) =>
            new(new CommandItem("page:" + route, CommandKind.Page, L.T(key), null, [.. NamesOf(key), .. extra]), () => Navigate?.Invoke(route));
        list.Add(Page("library", "shell.nav.library", [.. NamesOf("home.title", "instance.title"), "home"]));
        list.Add(Page("discover", "shell.nav.discover", [.. NamesOf("content.title"), "content"]));
        list.Add(Page("play", "shell.nav.multiplayer", [.. NamesOf("multiplayer.title"), "multiplayer"]));
        list.Add(Page("settings", "shell.settings"));
        list.Add(Page(LegacyRoutes.Tools, "tools.title", "tools", "toolbox"));
        list.Add(Page(LegacyRoutes.Logs, "log.title", "log"));
        list.Add(Page("accounts", "accounts.title"));
        list.Add(Page("settings#RecycleKeep", "recycle.title", "trash", "recycle"));
        return list;
    }
}
