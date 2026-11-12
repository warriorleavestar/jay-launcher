using System.Collections.ObjectModel;
using Amber.App.Localization;
using Amber.Core.Game;
using Amber.Core.Launch;
using Amber.Core.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

/// <summary>One choice in "Start and go to…": the title screen, a world, a server or the joined Jay room.</summary>
public sealed record StartTarget(string Label, StartInto Into, string? Value, bool Chosen, string? Detail = null);

/// <summary>
/// A row of the start button's ▾ menu: a heading (disabled), a choice (Target, checked when remembered), or a
/// submenu (Children). Go starts with that choice.
/// </summary>
public sealed class StartMenuEntry(string header, StartTarget? target = null, IReadOnlyList<StartMenuEntry>? children = null, IRelayCommand<StartTarget?>? go = null)
{
    public string Header { get; } = header;
    public StartTarget? Target { get; } = target;
    public IReadOnlyList<StartMenuEntry>? Children { get; } = children;
    public bool IsEnabled => Target is not null || Children is { Count: > 0 };
    public bool IsChecked => Target?.Chosen == true || Children?.Any(c => c.IsChecked) == true;
    public IRelayCommand<StartTarget?>? Go { get; } = target is null ? null : go;
}

/// <summary>A world in the Settings tab's "Start and go to" list.</summary>
public sealed record StartWorldChoice(string Folder, string Label);

/// <summary>
/// "Start and go to…" (skip the title screen): a dropdown on the Settings tab, and the ▾ menu next to the start
/// button. The choice is remembered per instance; the start itself decides how (the game's own quick play, or the
/// universal loader) and says so when it cannot and opens on the title screen instead.
/// </summary>
public sealed partial class InstanceViewModel
{
    public const int StartMenuRows = 8;

    public OptionItem[] StartIntoChoices { get; } = OptionItem.Keys("quickplay.title", "quickplay.world", "quickplay.server", "quickplay.room");

    public ObservableCollection<StartWorldChoice> StartWorldChoices { get; } = [];

    /// <summary>The ▾ menu's rows, kept current (worlds, servers, the room and the remembered choice).</summary>
    public ObservableCollection<StartMenuEntry> StartMenuEntries { get; } = [];

    /// <summary>Rebuilds the ▾ menu's rows from the instance's files and settings.</summary>
    public void RefreshStartMenu()
    {
        var (title, worlds, servers, room) = StartMenu();
        StartMenuEntry Choice(StartTarget t) => new(t.Detail is { } d ? $"{t.Label}  ·  {d}" : t.Label, t, go: StartAndGoToCommand);
        StartMenuEntry Sub(string header, IReadOnlyList<StartTarget> list, string empty) =>
            new(header, children: list.Count > 0 ? [.. list.Select(Choice)] : [new StartMenuEntry(empty)]);
        StartMenuEntries.Clear();
        StartMenuEntries.Add(new StartMenuEntry(L.T("quickplay.menu.heading")));
        StartMenuEntries.Add(Choice(title));
        StartMenuEntries.Add(Sub(L.T("quickplay.world_menu"), worlds, L.T("quickplay.menu.no_worlds")));
        StartMenuEntries.Add(Sub(L.T("quickplay.server_menu"), servers, L.T("quickplay.menu.no_servers")));
        if (room is not null) StartMenuEntries.Add(Choice(room));
        OnPropertyChanged(nameof(StartIntoLabel));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowStartWorld), nameof(ShowStartServer), nameof(ShowStartRoomNote), nameof(StartIntoHint))]
    public partial int StartIntoIndex { get; set; }

    [ObservableProperty] public partial StartWorldChoice? SelectedStartWorld { get; set; }

    public bool ShowStartWorld => StartIntoIndex == (int)StartInto.World;
    public bool ShowStartServer => StartIntoIndex == (int)StartInto.Server;
    public bool ShowStartRoomNote => StartIntoIndex == (int)StartInto.Room;
    public bool HasNoStartWorlds => StartWorldChoices.Count == 0;

    /// <summary>One line under the dropdown: what happens, and what it needs.</summary>
    public string StartIntoHint => (StartInto)StartIntoIndex switch
    {
        StartInto.World => StartWorldChoices.Count == 0 ? L.T("quickplay.hint.no_worlds") : L.T("quickplay.hint.world"),
        StartInto.Server => L.T("quickplay.hint.server"),
        StartInto.Room => InRoom ? L.T("quickplay.hint.room") : L.T("quickplay.hint.room_none"),
        _ => L.T("quickplay.hint.title"),
    };

    /// <summary>This player has joined a Jay room (the menu offers it only then).</summary>
    public bool InRoom => _service.RoomGameAddress is not null;

    /// <summary>The start button's ▾ label for screen readers: where the next start goes.</summary>
    public string StartIntoLabel => L.F("quickplay.menu.name", CurrentStartTargetText());

    partial void OnStartIntoIndexChanged(int value) => AutoSave();
    partial void OnSelectedStartWorldChanged(StartWorldChoice? value) => AutoSave();

    private void LoadStartInto(InstanceSettings s)
    {
        StartWorldChoices.Clear();
        if (Current is { } c)
            foreach (var w in WorldManager.List(c.SavesDir).OrderByDescending(w => w.LastPlayed ?? DateTimeOffset.MinValue))
                StartWorldChoices.Add(new StartWorldChoice(w.FolderName, w.Name == w.FolderName ? w.Name : $"{w.Name} ({w.FolderName})"));
        OnPropertyChanged(nameof(HasNoStartWorlds));
        StartIntoIndex = (int)QuickPlay.Parse(s.StartInto, s.JoinServer);
        SelectedStartWorld = StartWorldChoices.FirstOrDefault(w => w.Folder == s.StartWorld)
            ?? (s.StartWorld is { Length: > 0 } gone ? AddMissingWorld(gone) : StartWorldChoices.FirstOrDefault());
        OnPropertyChanged(nameof(StartIntoHint));
        RefreshStartMenu();
    }

    /// <summary>The saved world is gone: keep it in the list (marked) rather than silently picking another.</summary>
    private StartWorldChoice AddMissingWorld(string folder)
    {
        var missing = new StartWorldChoice(folder, L.F("quickplay.world_missing", folder));
        StartWorldChoices.Add(missing);
        return missing;
    }

    private string? StartIntoKey => QuickPlay.Key((StartInto)StartIntoIndex);

    private void OnRoomGameAddressChanged()
    {
        OnPropertyChanged(nameof(InRoom));
        OnPropertyChanged(nameof(StartIntoHint));
        RefreshStartMenu();
    }

    private string CurrentStartTargetText()
    {
        if (Current is not { } c) return L.T("quickplay.title");
        var s = InstanceSettings.Load(_service.Directory, c.Id);
        return QuickPlay.Parse(s.StartInto, s.JoinServer) switch
        {
            StartInto.World when s.StartWorld is { Length: > 0 } w => L.F("quickplay.into_world", w),
            StartInto.Server when s.JoinServer is { Length: > 0 } server => L.F("quickplay.into_server", server),
            StartInto.Room => L.T("quickplay.room"),
            _ => L.T("quickplay.title"),
        };
    }

    /// <summary>
    /// The ▾ menu: the title screen, then worlds (most recently played first), servers (saved in the game, plus the
    /// one set here), and the joined room. The remembered choice is checked.
    /// </summary>
    public (StartTarget Title, IReadOnlyList<StartTarget> Worlds, IReadOnlyList<StartTarget> Servers, StartTarget? Room) StartMenu()
    {
        var s = Current is { } c ? InstanceSettings.Load(_service.Directory, c.Id) : new InstanceSettings();
        var into = QuickPlay.Parse(s.StartInto, s.JoinServer);
        var title = new StartTarget(L.T("quickplay.title"), StartInto.Title, null, into == StartInto.Title);
        var worlds = new List<StartTarget>();
        var servers = new List<StartTarget>();
        if (Current is { } inst)
        {
            foreach (var w in WorldManager.List(inst.SavesDir).OrderByDescending(w => w.LastPlayed ?? DateTimeOffset.MinValue).Take(StartMenuRows))
                worlds.Add(new StartTarget(w.Name, StartInto.World, w.FolderName, into == StartInto.World && s.StartWorld == w.FolderName,
                    w.Name == w.FolderName ? null : w.FolderName));
            var saved = new List<ServerEntry>();
            try { saved = ServerList.Read(inst.WorkDir).ToList(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { }
            if (s.JoinServer is { Length: > 0 } own && !saved.Any(e => e.Address == own)) saved.Insert(0, new ServerEntry(own, own));
            foreach (var e in saved.Take(StartMenuRows))
                servers.Add(new StartTarget(e.Name, StartInto.Server, e.Address, into == StartInto.Server && s.JoinServer == e.Address,
                    e.Name == e.Address ? null : e.Address));
        }
        var room = InRoom ? new StartTarget(L.T("quickplay.room"), StartInto.Room, null, into == StartInto.Room) : null;
        return (title, worlds, servers, room);
    }

    /// <summary>A choice in the ▾ menu: remember it for this instance, then start.</summary>
    [RelayCommand]
    private void StartAndGoTo(StartTarget? target)
    {
        if (target is null || Current is null) return;
        RememberStartInto(target);
        StartGame();
    }

    internal void RememberStartInto(StartTarget target)
    {
        if (Current is not { } c) return;
        var s = InstanceSettings.Load(_service.Directory, c.Id);
        s.StartInto = QuickPlay.Key(target.Into);
        if (target.Into == StartInto.World) s.StartWorld = target.Value;
        if (target.Into == StartInto.Server) s.JoinServer = target.Value;
        try { s.Save(_service.Directory, c.Id); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        if (TabIndex == (int)InstanceTab.Settings) LoadSettings();
        RefreshStartMenu();
    }
}
