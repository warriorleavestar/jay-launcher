using System.Collections.ObjectModel;
using Amber.Core.Content;
using Amber.Core.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

/// <summary>A search result in "Add mods while you're at it".</summary>
public sealed partial class ExtraModItem(ContentProject project) : ObservableObject
{
    public ContentProject Project { get; } = project;
    public string Title => Project.Title;
    public string Summary => Project.Summary;
    [ObservableProperty] public partial bool IsPicked { get; set; }
}

/// <summary>
/// "Add mods while you're at it" (official design §6.11, journey J3): a small Modrinth search inside the new-instance
/// panel. Nothing is fetched until the player opens it; ticked mods are installed with their required dependencies
/// right after the instance, in the same activity task.
/// </summary>
public sealed partial class DownloadViewModel
{
    private const int ModResultLimit = 8;
    private CancellationTokenSource? _modSearchCts;

    public ObservableCollection<ExtraModItem> ModResults { get; } = [];

    /// <summary>The ticked mods, in the order they were ticked.</summary>
    public ObservableCollection<ContentProject> PickedMods { get; } = [];

    [ObservableProperty] public partial string ModQuery { get; set; } = "";

    private CancellationTokenSource? _modTypingCts;

    /// <summary>Typing searches by itself after a short pause (Enter searches at once).</summary>
    partial void OnModQueryChanged(string value)
    {
        _modTypingCts?.Cancel();
        if (!ModsOpen || !CanAddMods) return;
        var cts = _modTypingCts = new CancellationTokenSource();
        _ = Task.Delay(ModTypingPause, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (!cts.IsCancellationRequested) _ = SearchModsAsync(); });
        }, TaskScheduler.Default);
    }

    /// <summary>Self-check: no Modrinth requests; results come from <see cref="ShowModResults"/>.</summary>
    internal bool SearchModsOffline { get; set; }

    internal static TimeSpan ModTypingPause { get; set; } = TimeSpan.FromMilliseconds(400);
    [ObservableProperty] public partial bool IsSearchingMods { get; set; }
    [ObservableProperty] public partial string ModSearchError { get; set; } = "";

    /// <summary>Open or closed; remembered across sessions. Opening it is what allows the first Modrinth request.</summary>
    public bool ModsOpen
    {
        get => _service.Settings.NewInstanceModsOpen;
        set
        {
            if (_service.Settings.NewInstanceModsOpen == value) return;
            _service.Settings.NewInstanceModsOpen = value;
            try { _service.Settings.Save(); } catch (IOException) { }
            OnPropertyChanged();
            if (value && CanAddMods && ModResults.Count == 0) _ = SearchModsAsync();
        }
    }

    /// <summary>The loader name for the search, or null when the choice can't take mods.</summary>
    private string? ModLoader => NewInstanceMods.LoaderId(SelectedLoader.Kind);

    /// <summary>Mods need a version and a mod loader.</summary>
    public bool CanAddMods => Target is not null && ModLoader is not null;

    public bool ShowModsNeedLoader => !CanAddMods;


    private bool _modsSearched;

    /// <summary>A search came back empty.</summary>
    public bool ShowModsEmpty => _modsSearched && ModResults.Count == 0 && !IsSearchingMods && ModSearchError.Length == 0;

    partial void OnIsSearchingModsChanged(bool value) => OnPropertyChanged(nameof(ShowModsEmpty));
    partial void OnModSearchErrorChanged(string value) => OnPropertyChanged(nameof(ShowModsEmpty));

    /// <summary>"Add mods (optional)" or "3 picked: Sodium, Iris, JourneyMap".</summary>
    public string ModsHeader => PickedMods.Count == 0
        ? L.T("newinstance.mods.header")
        : L.P("newinstance.mods.header_picked", PickedMods.Count, PickedMods.Count, string.Join(L.T("common.list_separator"), PickedMods.Take(4).Select(p => p.Title)) + (PickedMods.Count > 4 ? "…" : ""));

    private void OnModsContextChanged()
    {
        // Another version or loader: the old results and picks may not fit any more.
        _modSearchCts?.Cancel();
        ModResults.Clear();
        PickedMods.Clear();
        _modsSearched = false;
        ModSearchError = "";
        NotifyMods();
        if (ModsOpen && CanAddMods) _ = SearchModsAsync();
    }

    private void NotifyMods()
    {
        OnPropertyChanged(nameof(CanAddMods));
        OnPropertyChanged(nameof(ShowModsNeedLoader));
        OnPropertyChanged(nameof(ShowModsEmpty));
        OnPropertyChanged(nameof(ModsHeader));
    }

    /// <summary>Searches Modrinth for mods that fit the chosen version and loader (empty text: the most relevant ones).</summary>
    [RelayCommand]
    private async Task SearchModsAsync()
    {
        if (Target is not { } target || ModLoader is not { } loader || SearchModsOffline) return;
        _modSearchCts?.Cancel();
        var cts = _modSearchCts = new CancellationTokenSource();
        IsSearchingMods = true;
        ModSearchError = "";
        try
        {
            var result = await _service.Modrinth.SearchAsync(new SearchQuery(ContentType.Mod, ModQuery.Trim(), target.Id, loader, 0, ModResultLimit), cts.Token);
            if (cts.IsCancellationRequested) return;
            ShowModResults(result.Items);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException or System.Text.Json.JsonException)
        {
            ModSearchError = L.F("newinstance.mods.error", ErrorText.Of(e));
        }
        finally
        {
            if (ReferenceEquals(_modSearchCts, cts)) IsSearchingMods = false;
        }
    }

    /// <summary>Shows search results; picked mods keep their tick.</summary>
    internal void ShowModResults(IEnumerable<ContentProject> projects)
    {
        _modsSearched = true;
        ModResults.Clear();
        foreach (var p in projects)
            ModResults.Add(new ExtraModItem(p) { IsPicked = PickedMods.Any(m => m.Source == p.Source && m.Id == p.Id) });
        NotifyMods();
    }

    /// <summary>Tick or untick a result.</summary>
    [RelayCommand]
    private void TogglePick(ExtraModItem? item)
    {
        if (item is null) return;
        var existing = PickedMods.FirstOrDefault(m => m.Source == item.Project.Source && m.Id == item.Project.Id);
        if (existing is not null) PickedMods.Remove(existing);
        else PickedMods.Add(item.Project);
        item.IsPicked = existing is null;
        OnPropertyChanged(nameof(ModsHeader));
    }

    /// <summary>After the instance is in: resolve and download the picks into its mods folder (same task, same cancel).</summary>
    private async Task InstallPickedModsAsync(IReadOnlyList<ContentProject> picks, Amber.App.Services.GameContext ctx, string id,
        Amber.Core.Activity.TaskHandle task, CancellationToken ct)
    {
        var instance = _service.GetInstance(ctx, id);
        SetStatus(StatusMessage.Info("newinstance.mods.resolving", picks.Count));
        task.Report(null, L.F("newinstance.mods.resolving", picks.Count));
        var plan = await NewInstanceMods.PlanAsync(_service.Modrinth, ctx.ContentInstaller, picks, instance, ct);
        await ctx.ContentInstaller.InstallAsync(plan, instance.DirectoryFor(ContentType.Mod), new Progress<Amber.Core.Net.DownloadProgress>(p =>
        {
            Work.Bytes(p.BytesDownloaded);
            Progress = 100.0 * p.CompletedFiles / Math.Max(1, plan.Files.Count);
            task.Report(Progress / 100, p.CurrentFile, p.BytesDownloaded);
        }), ct);
        _modProblems = plan.Problems;
        _modsInstalled = plan.Files.Count;
    }

    private IReadOnlyList<string> _modProblems = [];
    private int _modsInstalled;
}
