using Amber.App.Localization;
using Amber.App.Services;
using Amber.Core.Activity;
using Amber.Core.Bake;
using Amber.Core.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

/// <summary>
/// World maintenance (universal loader, interface 1.3.3): a world row's ⋯ → 升级存档 runs at once, 预生成… opens a
/// drawer with the radius and an estimate. Either runs as an Activity task; the world row says 准备中 meanwhile.
/// </summary>
public sealed partial class InstanceViewModel
{
    /// <summary>Running maintenance by instance and world folder (kept across world list reloads).</summary>
    private readonly Dictionary<(string Instance, string World), MaintenanceRun> _maintenance = [];

    public OptionItem[] PregenRadiusChoices { get; } = [.. WorldMaintenance.Radii.Select(r => OptionItem.Plural("maintenance.radius", r))];

    [ObservableProperty] public partial bool PregenOpen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PregenTitle), nameof(PregenEstimate), nameof(PregenResumeNote), nameof(HasPregenResume))]
    public partial WorldItem? PregenWorld { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PregenEstimate), nameof(PregenResumeNote), nameof(HasPregenResume), nameof(PregenRadius))]
    public partial int PregenRadiusIndex { get; set; }

    public int PregenRadius => WorldMaintenance.Radii[Math.Clamp(PregenRadiusIndex, 0, WorldMaintenance.Radii.Count - 1)];

    public string PregenTitle => PregenWorld is { } w ? L.F("maintenance.pregen.title", w.Name) : "";

    /// <summary>What the chosen radius means: distance, chunks, time and disk, as rough ranges.</summary>
    public string PregenEstimate
    {
        get
        {
            var done = PregenState()?.DoneFor(PregenRadius) ?? 0;
            var (low, high) = WorldMaintenance.Duration(PregenRadius, done);
            return L.F("maintenance.pregen.estimate", WorldMaintenance.Blocks(PregenRadius).ToString("N0", L.Culture),
                WorldMaintenance.Chunks(PregenRadius).ToString("N0", L.Culture), Span(low), Span(high),
                Fmt.Size(WorldMaintenance.DiskBytes(PregenRadius, done)));
        }
    }

    /// <summary>"7 分钟", or hours and minutes rounded to 10 minutes past an hour: an estimate, not a clock.</summary>
    private static string Span(TimeSpan t)
    {
        if (t.TotalMinutes < 60) return Fmt.Minutes(TimeSpan.FromMinutes(Math.Max(1, Math.Ceiling(t.TotalMinutes))));
        var minutes = (long)Math.Ceiling(t.TotalMinutes / 10) * 10;
        return L.F("format.duration.hours_minutes", minutes / 60, minutes % 60);
    }

    /// <summary>An earlier run of this world stopped part way: this one continues from there.</summary>
    public string? PregenResumeNote => PregenState() is { Complete: false, Done: > 0 } state
        ? L.F("maintenance.pregen.resume", state.DoneFor(PregenRadius).ToString("N0", L.Culture))
        : PregenState() is { Complete: true } done && done.Radius >= PregenRadius ? L.T("maintenance.pregen.already") : null;

    public bool HasPregenResume => PregenResumeNote is not null;

    /// <summary>Why maintenance cannot run on this instance now; null when it can.</summary>
    public string? MaintenanceBlocker =>
        Current is not { } c ? "" :
        !_service.UsesUniversalLoader(c.Id) ? L.T("maintenance.needs_loader") :
        _home?.IsBusy == true ? L.T("maintenance.game_running") : null;

    public bool CanMaintainWorlds => MaintenanceBlocker is null;

    private PregenState? PregenState() => PregenWorld is { } w ? Amber.Core.Bake.PregenState.Read(w.World.Path) : null;

    [RelayCommand]
    private void OpenPregen(WorldItem? item)
    {
        if (item is null) return;
        PregenWorld = item;
        OnPropertyChanged(nameof(MaintenanceBlocker));
        OnPropertyChanged(nameof(CanMaintainWorlds));
        PregenOpen = true;
    }

    [RelayCommand]
    private void ClosePregen() => PregenOpen = false;

    [RelayCommand]
    private void StartPregen()
    {
        if (PregenWorld is not { } item) return;
        if (StartMaintenance(item, WorldMaintenance.Pregen(item.World.FolderName, PregenRadius))) PregenOpen = false;
    }

    [RelayCommand]
    private void UpgradeWorld(WorldItem? item)
    {
        if (item is not null) StartMaintenance(item, WorldMaintenance.UpgradeOnly(item.World.FolderName));
    }

    /// <summary>Starts the run as an Activity task. False (with a toast) when it cannot run now.</summary>
    internal bool StartMaintenance(WorldItem item, WorldMaintenance spec) => StartMaintenance(item, spec, null) is not null;

    /// <summary>The run, or null when it cannot start. <paramref name="launch"/> replaces the real start (self-check only).</summary>
    internal MaintenanceRun? StartMaintenance(WorldItem item, WorldMaintenance spec, Action<LaunchRequest>? launch)
    {
        if (Current is not { } c) return null;
        if (MaintenanceBlocker is { Length: > 0 } why)
        {
            Toasts.Show(why);
            return null;
        }
        var key = (c.Id, item.World.FolderName);
        if (_maintenance.ContainsKey(key)) return null;
        var run = new MaintenanceRun(spec) { Name = item.Name };
        var title = spec.PregenRadius > 0 ? L.F("maintenance.task.pregen", item.Name, spec.PregenRadius) : L.F("maintenance.task.upgrade", item.Name);
        _maintenance[key] = run;
        SetWorldBusy(key, L.T("maintenance.row.preparing"));
        _service.Tasks.Start("world-maintenance", title, async (ctx, ct) =>
        {
            run.Progressed += p => ctx.Report(p.Fraction, StepText(p));
            using var stop = ct.Register(run.Cancel);
            ctx.Report(null, L.T("maintenance.step.preparing"));
            (launch ?? _service.RequestLaunch)(new LaunchRequest(c.Id) { Maintenance = run });
            try { return await run.Done; }
            finally
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    _maintenance.Remove(key);
                    SetWorldBusy(key, null);
                    if (Current?.Id == key.Item1) LoadWorlds();
                });
            }
        }, dedupeKey: $"world-maintenance:{c.Id}:{item.World.FolderName}");
        run.Progressed += p => Avalonia.Threading.Dispatcher.UIThread.Post(() => SetWorldBusy(key, RowText(p)));
        return run;
    }

    /// <summary>The Activity line for a progress step.</summary>
    internal static string StepText(MaintenanceProgress p) => p.Step switch
    {
        "upgrade" when p.Total > 0 => L.F("maintenance.step.upgrade_count", p.Done.ToString("N0", L.Culture), p.Total.ToString("N0", L.Culture)),
        "upgrade" => L.T("maintenance.step.upgrade"),
        "open" => L.T("maintenance.step.open"),
        "resume" => L.T("maintenance.step.resume"),
        "spawn" or "pregen" when p.Total > 0 => L.F("maintenance.step.chunks", p.Done.ToString("N0", L.Culture), p.Total.ToString("N0", L.Culture)),
        "done" or "quit" => L.T("maintenance.step.saving"),
        "cancelled" => L.T("maintenance.cancelled"),
        "failed" => L.T("maintenance.failed"),
        _ => L.T("maintenance.step.preparing"),
    };

    /// <summary>The world row's short state: 准备中, or how far it is.</summary>
    internal static string RowText(MaintenanceProgress p) => p.Fraction is { } f && p.Step is "spawn" or "pregen"
        ? L.F("maintenance.row.progress", (int)Math.Floor(f * 100))
        : p.Step == "upgrade" ? L.T("maintenance.row.upgrading") : L.T("maintenance.row.preparing");

    private void SetWorldBusy((string Instance, string World) key, string? text)
    {
        if (Current?.Id != key.Instance) return;
        foreach (var w in Worlds)
            if (w.World.FolderName == key.World) w.Busy = text;
    }

    /// <summary>World rows reloaded: carry the running state over.</summary>
    private void RestoreWorldBusy()
    {
        if (Current is not { } c) return;
        foreach (var w in Worlds)
            if (_maintenance.TryGetValue((c.Id, w.World.FolderName), out var run))
                w.Busy = run.Last is { } p ? RowText(p) : L.T("maintenance.row.preparing");
    }
}
