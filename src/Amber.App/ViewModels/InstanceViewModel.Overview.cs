using System.Collections.ObjectModel;
using Amber.App.Services;
using Amber.Core.Content;
using Amber.Core.Game;
using Amber.Core.Launch;
using Amber.Core.Localization;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

/// <summary>A fact row on the overview's "this instance" card, with an optional jump to where it is changed.</summary>
public sealed record OverviewFact(string Label, string Value, string? ActionLabel = null, IRelayCommand? Action = null)
{
    public bool HasAction => Action is not null;
}

/// <summary>
/// Instance › Overview (official design §6.4): recent worlds, recent screenshots, saved servers and the instance's
/// facts. It never goes online by itself: servers show the saved list without pinging, and mod updates only show
/// the result of the last check you started.
/// </summary>
public sealed partial class InstanceViewModel
{
    public const int OverviewRows = 3;
    public const int OverviewShots = 4;

    public ObservableCollection<WorldItem> RecentWorlds { get; } = [];
    public ObservableCollection<ScreenshotItem> RecentShots { get; } = [];
    public ObservableCollection<ServerEntry> SavedServers { get; } = [];
    public ObservableCollection<OverviewFact> OverviewFacts { get; } = [];

    [ObservableProperty] public partial bool HasRecentWorlds { get; set; }
    [ObservableProperty] public partial bool HasRecentShots { get; set; }
    [ObservableProperty] public partial bool HasSavedServers { get; set; }
    [ObservableProperty] public partial bool IsVanilla { get; set; }

    private int _overviewGeneration;

    private void LoadOverview()
    {
        if (Current is not { } c) return;
        var generation = ++_overviewGeneration;
        IsVanilla = c.Loader is null;

        RecentWorlds.Clear();
        foreach (var w in WorldManager.List(c.SavesDir).OrderByDescending(w => w.LastPlayed ?? DateTimeOffset.MinValue).Take(OverviewRows))
            RecentWorlds.Add(new WorldItem(w));
        HasRecentWorlds = RecentWorlds.Count > 0;

        SavedServers.Clear();
        foreach (var s in ServerList.Read(c.WorkDir).Take(OverviewRows)) SavedServers.Add(s);
        HasSavedServers = SavedServers.Count > 0;

        BuildOverviewFacts(c);
        _ = LoadRecentShotsAsync(c, generation);
    }

    private async Task LoadRecentShotsAsync(GameInstance c, int generation)
    {
        RecentShots.Clear();
        var files = await Task.Run(() => System.IO.Directory.Exists(c.ScreenshotsDir)
            ? System.IO.Directory.EnumerateFiles(c.ScreenshotsDir, "*.png").OrderByDescending(File.GetLastWriteTimeUtc).Take(OverviewShots).ToList()
            : []);
        if (generation != _overviewGeneration || Current != c) return;
        foreach (var file in files) RecentShots.Add(new ScreenshotItem(file));
        HasRecentShots = RecentShots.Count > 0;
        foreach (var item in RecentShots.ToList())
        {
            try
            {
                item.Thumbnail = await Task.Run(() =>
                {
                    using var stream = File.OpenRead(item.Path);
                    return Bitmap.DecodeToWidth(stream, 240);
                });
            }
            catch (Exception e) when (e is IOException or ArgumentException or InvalidOperationException or NullReferenceException) { }
        }
    }

    private void BuildOverviewFacts(GameInstance c)
    {
        OverviewFacts.Clear();
        var effective = _service.EffectiveSettings(c.Id);
        var version = c.Loader is null ? c.McVersion : $"{c.McVersion} · {LibraryViewModel.LoaderName(c.Loader)} {c.LoaderVersion}".TrimEnd();
        OverviewFacts.Add(new(L.T("overview.fact.version"), version));
        var java = string.IsNullOrWhiteSpace(effective.JavaPath) ? L.T("overview.fact.java_auto") : effective.JavaPath!;
        OverviewFacts.Add(new(L.T("overview.fact.java"), java, L.T("overview.change"), GoToSettingsCommand));
        var memory = effective.AutoMemory == true ? L.T("overview.fact.memory_auto") : L.F("overview.fact.memory_mb", effective.MaxMemoryMb ?? 4096);
        OverviewFacts.Add(new(L.T("overview.fact.memory"), memory, L.T("overview.change"), GoToSettingsCommand));
        OverviewFacts.Add(new(L.T("overview.fact.location"), c.WorkDir, L.T("common.open_folder"), OpenFolderCommand));
        var minutes = _service.MetaOf(c.Id)?.PlayMinutes ?? 0;
        OverviewFacts.Add(new(L.T("overview.fact.played"), minutes >= 1 ? Fmt.PlayTime(TimeSpan.FromMinutes(minutes)) : L.T("library.never_played")));
    }

    [RelayCommand]
    private void GoToSettings() => TabIndex = (int)InstanceTab.Settings;

    /// <summary>"All ›" on each overview block: switch to that tab.</summary>
    [RelayCommand]
    private void GoToTab(string? tab)
    {
        if (Enum.TryParse<InstanceTab>(tab, out var t)) TabIndex = (int)t;
    }

    /// <summary>[Enter] on a recent world. Starts in place: progress is in the hero and the playing pill.</summary>
    [RelayCommand]
    private void EnterWorld(WorldItem? item)
    {
        if (Current is not { } c || item is null) return;
        _service.SetCurrentInstance(c.Id);
        _service.RequestLaunch(new LaunchRequest(c.Id, JoinWorld: item.World.FolderName));
    }

    /// <summary>[Connect] on a saved server. Starts in place.</summary>
    [RelayCommand]
    private void ConnectServer(ServerEntry? entry)
    {
        if (Current is not { } c || entry is null) return;
        _service.SetCurrentInstance(c.Id);
        _service.RequestLaunch(new LaunchRequest(c.Id, JoinServer: entry.Address));
    }

    /// <summary>[Check for updates] on the overview: go to the mods tab and run the check there.</summary>
    [RelayCommand]
    private void CheckModUpdatesFromOverview()
    {
        TabIndex = (int)InstanceTab.Mods;
        Dispatcher.UIThread.Post(() =>
        {
            if (CheckModUpdatesCommand.CanExecute(null)) CheckModUpdatesCommand.Execute(null);
        }, DispatcherPriority.Background);
    }
}
