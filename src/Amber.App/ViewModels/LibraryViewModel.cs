using System.Collections.ObjectModel;
using Amber.App.Localization;
using Amber.App.Services;
using Amber.Core.Content;
using Amber.Core.GameLibrary;
using Amber.Core.Launch;
using Amber.Core.Localization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

/// <summary>One instance card or row on the library home.</summary>
public sealed partial class LibraryCard(LibraryEntry entry, int modCount, double playMinutes, LibraryViewModel owner) : ObservableObject
{
    public LibraryEntry Entry { get; } = entry;
    public string Id => Entry.Id;
    public string Name => Entry.Name;
    public int ModCount { get; } = modCount;
    public string Initial => CoverArt.InitialOf(Entry.Name);
    public int CoverSlot => CoverArt.SlotFor(Entry.Id);
    /// <summary>Loader tag class (vanilla, fabric, forge, …).</summary>
    public string LoaderClass => Entry.Loader ?? "vanilla";
    public string LoaderName => LibraryViewModel.LoaderName(Entry.Loader);
    public string Facts => Entry.Loader is null
        ? Entry.McVersion
        : ModCount > 0 ? L.F("library.facts.mods", Entry.McVersion, LoaderName, L.P("library.mods", ModCount, ModCount)) : L.F("library.facts", Entry.McVersion, LoaderName);
    public string LastPlayedText => Entry.LastPlayed is { } t ? L.F("library.last_played", Fmt.Ago(t, DateTimeOffset.Now)) : L.T("library.never_played");
    public string PlayTimeText => playMinutes >= 1 ? L.F("library.play_time", Fmt.PlayTime(TimeSpan.FromMinutes(playMinutes))) : "";
    public bool HasPlayTime => playMinutes >= 1;
    public bool IsPinned => Entry.Pinned;
    public bool IsRunning => Entry.IsRunning;
    public bool HasProblem => Entry.HasProblem;
    public string PinTip => L.T(IsPinned ? "library.unpin" : "library.pin");
    public string LaunchTip => L.F("library.launch_tip", Entry.Name);
    public string OpenTip => L.F("library.open_tip", Entry.Name);

    [RelayCommand] private void Open() => owner.OpenInstance(Id);
    [RelayCommand] private void Launch() => owner.LaunchInstance(Id);
    [RelayCommand] private void TogglePin() => owner.SetPinned(Id, !IsPinned);
}

/// <summary>An entry under "Needs you": one sentence and one action.</summary>
public sealed partial class NeedItem(string text, string action, bool isError, Action run) : ObservableObject
{
    public string Text { get; } = text;
    public string Action { get; } = action;
    public bool IsError { get; } = isError;
    [RelayCommand] private void Run() => run();
}

/// <summary>
/// Library home (official design §6.2): continue playing, needs you, and all instances with search, filter chips,
/// sort and a grid or list view. Reads only local data; it never goes online.
/// </summary>
public sealed partial class LibraryViewModel : PageViewModel
{
    private readonly LauncherService _service;
    private readonly HomeViewModel _home;
    private readonly ActivityViewModel _activity;
    private List<LibraryCard> _all = [];
    private int _refreshing;
    private bool _refreshAgain;

    public override string Title => L.T("shell.nav.library");
    public override string Icon => "M4 19.5V5a2 2 0 0 1 2-2h12a2 2 0 0 1 2 2v14.5M4 19.5A2.5 2.5 0 0 0 6.5 22H20M8 7h8M8 11h8";

    /// <summary>Navigation (set by MainViewModel).</summary>
    public Action<string>? Navigate { get; set; }

    /// <summary>The launch engine: the continue card's Play button is its primary command.</summary>
    public HomeViewModel Home => _home;

    public LibraryViewModel(LauncherService service, HomeViewModel home, ActivityViewModel activity)
    {
        _service = service;
        _home = home;
        _activity = activity;
        service.InstalledVersionsChanged += () => Dispatcher.UIThread.Post(Refresh);
        service.CurrentInstanceChanged += () => Dispatcher.UIThread.Post(Refresh);
        service.LibraryChanged += () => Dispatcher.UIThread.Post(Refresh);
        service.ContentChanged += () => Dispatcher.UIThread.Post(Refresh);
        _activity.Runs.CollectionChanged += (_, _) => Dispatcher.UIThread.Post(Refresh);
        home.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(HomeViewModel.HasFailure) or nameof(HomeViewModel.HasCrash) or nameof(HomeViewModel.PrimaryLabel))
                Dispatcher.UIThread.Post(RefreshNeeds);
        };
    }

    protected override void OnFirstActivated() => Refresh();
    protected override void OnBecameCurrent() => Refresh();
    protected override void OnLanguageChanged() => Refresh();

    // ---- data ----

    [ObservableProperty] public partial bool IsLoaded { get; set; }

    /// <summary>All cards after search, filter and sort.</summary>
    public ObservableCollection<LibraryCard> Cards { get; } = [];
    public ObservableCollection<NeedItem> Needs { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasContinue))]
    public partial LibraryCard? Continue { get; set; }

    public bool HasContinue => Continue is not null;
    public bool HasNeeds => Needs.Count > 0;
    /// <summary>Every instance in the library, unfiltered (command palette).</summary>
    public IReadOnlyList<LibraryCard> AllCards => _all;

    public int TotalCount => _all.Count;
    public bool IsEmpty => IsLoaded && _all.Count == 0;
    public bool HasInstances => _all.Count > 0;
    public bool NoMatches => _all.Count > 0 && Cards.Count == 0;
    public string CountText => L.P("library.count", _all.Count, _all.Count);

    /// <summary>Reloads the instance list in the background (version files and mods folders are read off the UI thread).</summary>
    public void Refresh()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) == 1)
        {
            _refreshAgain = true;
            return;
        }
        var ids = _service.InstalledVersions();
        var running = _activity.Runs.Where(r => r.Session.IsActive).Select(r => r.Session.InstanceId).ToHashSet();
        var crashed = _activity.Runs.Where(r => r.Session.State == LaunchState.Crashed).Select(r => r.Session.InstanceId).ToHashSet();
        // Read the settings' instance table here on the UI thread: it is a plain Dictionary that the UI thread changes
        // (pin, play time, mod checks), and reading it from the worker while it changes can throw.
        var metas = new Dictionary<string, InstanceMeta?>(StringComparer.Ordinal);
        foreach (var id in ids) metas[id] = _service.MetaOf(id);
        _ = Task.Run(() =>
        {
            try { return BuildRows(ids, metas, running, crashed); }
            catch (Exception e)
            {
                // Never leave the refresh flag set: the library would stop updating for the rest of the session.
                System.Diagnostics.Trace.WriteLine("Jay: library refresh failed: " + e);
                return null;
            }
        }).ContinueWith(t =>
        {
            var rows = t.Result;
            Dispatcher.UIThread.Post(() =>
            {
                Interlocked.Exchange(ref _refreshing, 0);
                if (rows is not null) Apply(rows);
                if (_refreshAgain)
                {
                    _refreshAgain = false;
                    Refresh();
                }
            });
        }, TaskScheduler.Default);
    }

    private List<(LibraryEntry Entry, int Mods, double Minutes)> BuildRows(IEnumerable<string> ids, Dictionary<string, InstanceMeta?> metas,
        HashSet<string> running, HashSet<string> crashed)
    {
        var rows = new List<(LibraryEntry Entry, int Mods, double Minutes)>();
        foreach (var id in ids)
        {
            var meta = metas.GetValueOrDefault(id);
            GameInstance? instance = null;
            try { instance = _service.GetInstance(id); }
            catch (Exception e) when (e is InvalidOperationException || Amber.Core.Game.GameDirectory.IsUnreadable(e)) { }
            var mods = 0;
            if (instance is { Loader: not null })
            {
                try { mods = Directory.Exists(instance.ModsDir) ? Directory.EnumerateFiles(instance.ModsDir, "*.jar").Count() : 0; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            var entry = new LibraryEntry(id, meta?.DisplayName ?? id, instance?.McVersion ?? id, instance?.Loader, meta?.Pinned ?? false, meta?.LastPlayed,
                HasProblem: instance is null || meta?.State is not null || crashed.Contains(id), IsRunning: running.Contains(id), Unreadable: instance is null);
            rows.Add((entry, mods, meta?.PlayMinutes ?? 0));
        }
        return rows;
    }

    private void Apply(List<(LibraryEntry Entry, int Mods, double Minutes)> rows)
    {
        _all = rows.Select(r => new LibraryCard(r.Entry, r.Mods, r.Minutes, this)).ToList();
        var current = _service.CurrentInstance;
        var pick = LibraryQuery.ContinueWith(_all.Select(c => c.Entry).ToList(),
            current is null ? _service.Settings.RecentInstances : [current, .. _service.Settings.RecentInstances ?? []], null);
        Continue = pick is null ? null : _all.First(c => c.Id == pick.Id);
        IsLoaded = true;
        ApplyQuery();
        RefreshNeeds();
    }

    // ---- search, filter, sort, view ----

    [ObservableProperty] public partial string Search { get; set; } = "";
    [ObservableProperty] public partial int FilterIndex { get; set; }
    [ObservableProperty] public partial int SortIndex { get; set; }

    /// <summary>Grid of covers (true) or a list of rows (false). Compact width always uses rows.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsListView))]
    public partial bool IsGridView { get; set; } = true;

    public bool IsListView => !IsGridView;

    /// <summary>Set by the page from its width: compact always lists rows (§6.2).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowGrid), nameof(ShowRows))]
    public partial bool IsCompact { get; set; }

    public bool ShowGrid => IsGridView && !IsCompact;
    public bool ShowRows => !ShowGrid;

    partial void OnIsGridViewChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowGrid));
        OnPropertyChanged(nameof(ShowRows));
    }

    public OptionItem[] FilterOptions { get; } = OptionItem.Keys("library.filter.all", "library.filter.pinned", "library.filter.problems", "library.filter.running");
    public OptionItem[] SortOptions { get; } = OptionItem.Keys("library.sort.recent", "library.sort.name", "library.sort.version");

    partial void OnSearchChanged(string value) => ApplyQuery();
    partial void OnFilterIndexChanged(int value) => ApplyQuery();
    partial void OnSortIndexChanged(int value) => ApplyQuery();

    [RelayCommand] private void UseGrid() => IsGridView = true;
    [RelayCommand] private void UseList() => IsGridView = false;

    [RelayCommand]
    private void ClearFilters()
    {
        Search = "";
        FilterIndex = 0;
    }

    private void ApplyQuery()
    {
        var byId = _all.ToDictionary(c => c.Id);
        var result = LibraryQuery.Apply(_all.Select(c => c.Entry), Search, (LibraryFilter)Math.Max(0, FilterIndex), (LibrarySort)Math.Max(0, SortIndex));
        Cards.Clear();
        foreach (var e in result) Cards.Add(byId[e.Id]);
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasInstances));
        OnPropertyChanged(nameof(NoMatches));
    }

    // ---- needs you ----

    /// <summary>The page of this instance, or of the one in focus when it is not installed (any more).</summary>
    private string InstanceRoute(string? id) =>
        id is not null && _service.InstalledVersions().Contains(id) ? "library/" + id : Amber.Core.Navigation.LegacyRoutes.CurrentInstance;

    private void RefreshNeeds()
    {
        Needs.Clear();
        // Both lead to the instance itself: its page shows the failure card or the last-session panel.
        if (_home.Failure is { } failure)
        {
            var failed = failure.Request.VersionId;
            Needs.Add(new NeedItem(L.F("library.need.failed", failure.Title), L.T("library.need.view"), true, () => Navigate?.Invoke(InstanceRoute(failed))));
        }
        if (_home.HasCrash)
        {
            var crashed = _home.CrashInstance;
            Needs.Add(new NeedItem(L.T("library.need.crashed"), L.T("library.need.view"), true, () => Navigate?.Invoke(InstanceRoute(crashed))));
        }
        foreach (var card in _all)
        {
            // An instance whose version files cannot be read: its page offers Repair, Open folder and Delete.
            var state = card.Entry.Unreadable ? "broken" : _service.MetaOf(card.Id)?.State;
            if (state is null) continue;
            var key = state == "incomplete" ? "library.need.incomplete" : "library.need.broken";
            Needs.Add(new NeedItem(L.F(key, card.Name), L.T("library.need.view"), state != "incomplete", () => OpenInstance(card.Id)));
        }
        OnPropertyChanged(nameof(HasNeeds));
    }

    // ---- actions ----

    public void OpenInstance(string id)
    {
        _service.SetCurrentInstance(id);
        Navigate?.Invoke("library/" + id);
    }

    /// <summary>The ▶ on a card: starts that instance without leaving the library (progress shows in the pill).</summary>
    public void LaunchInstance(string id)
    {
        _service.SetCurrentInstance(id);
        _service.RequestLaunch(new LaunchRequest(id));
    }

    public void SetPinned(string id, bool pinned) => _service.SetPinned(id, pinned);

    [RelayCommand] private void NewInstance() => Navigate?.Invoke("library/new");
    [RelayCommand] private void ImportPack() => Navigate?.Invoke("discover");

    /// <summary>Ctrl+Enter / Enter on the continue card: the start page's primary action for the continue instance.</summary>
    [RelayCommand]
    private void PlayContinue()
    {
        if (Continue is { } c && _service.CurrentInstance != c.Id) _service.SetCurrentInstance(c.Id);
        if (_home.PrimaryCommand.CanExecute(null)) _home.PrimaryCommand.Execute(null);
    }

    internal static string LoaderName(string? loader) => loader switch
    {
        null => L.T("library.loader.vanilla"),
        "fabric" => "Fabric",
        "forge" => "Forge",
        "neoforge" => "NeoForge",
        "quilt" => "Quilt",
        "optifine" => "OptiFine",
        _ => loader,
    };
}
