using System.Collections.ObjectModel;
using Amber.App.Services;
using Amber.Core.Bake;
using Amber.Core.Content;
using Amber.Core.Localization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

/// <summary>
/// The instance's "Universal loader" card (redesign batch 10): the switch for this instance, whether the loader runs
/// it, how many of its mods go through it, and [Prepare again] [Start without it this time] [See what it changed].
/// The chip next to the start button shows the same state and opens the card.
/// </summary>
public sealed partial class InstanceViewModel
{
    private bool _loadingUniversalLoader;
    private int _universalLoaderGeneration;

    /// <summary>This instance launches through the universal loader (its own choice, else the default in Settings › Game).</summary>
    [ObservableProperty] public partial bool UniversalLoaderOn { get; set; }

    /// <summary>The instance has not chosen: it follows the default.</summary>
    [ObservableProperty] public partial bool UniversalLoaderInherited { get; set; }

    /// <summary>Why the loader does not run this instance (vanilla, another game version…); empty when it does.</summary>
    [ObservableProperty] public partial string UniversalLoaderUnsupported { get; set; } = "";

    [ObservableProperty] public partial bool UniversalLoaderInstalled { get; set; }

    /// <summary>A preparation is on record for this instance (the last start or [Prepare again] made one).</summary>
    [ObservableProperty] public partial bool UniversalLoaderPrepared { get; set; }

    /// <summary>"3 mods run through it" / "No mod here needs it yet".</summary>
    [ObservableProperty] public partial string UniversalLoaderMods { get; set; } = "";

    [ObservableProperty] public partial bool ShowUniversalLoaderChanges { get; set; }

    /// <summary>What the loader changed in the last game (from its problems file), in plain words.</summary>
    public ObservableCollection<string> UniversalLoaderChanges { get; } = [];

    [ObservableProperty] public partial string UniversalLoaderChangesNote { get; set; } = "";

    public bool UniversalLoaderSupported => UniversalLoaderUnsupported.Length == 0;

    /// <summary>The switch can be turned on only where the loader runs; it can always be turned off.</summary>
    public bool CanToggleUniversalLoader => UniversalLoaderSupported || UniversalLoaderOn;

    public string UniversalLoaderState =>
        !UniversalLoaderOn ? L.T("instance.ul.state.off")
        : !UniversalLoaderInstalled ? L.T("instance.ul.state.missing")
        : UniversalLoaderPrepared ? L.T("instance.ul.state.ready")
        : L.T("instance.ul.state.pending");

    /// <summary>The chip next to the start button: only while it is on and runs this instance.</summary>
    public bool ShowUniversalLoaderChip => UniversalLoaderOn && UniversalLoaderSupported;

    public string UniversalLoaderChip => L.F("instance.ul.chip", UniversalLoaderState);

    /// <summary>[Prepare again] and [Start without it this time] make sense once it is on, installed and runs this instance.</summary>
    public bool BakeEnabled => UniversalLoaderOn && UniversalLoaderInstalled && UniversalLoaderSupported;

    /// <summary>Turned on but not installed yet: [Download] right on the card, no trip to Settings.</summary>
    public bool CanDownloadUniversalLoader => UniversalLoaderOn && !UniversalLoaderInstalled && BakeLoaderRelease.Pinned.IsPublished;

    partial void OnUniversalLoaderOnChanged(bool value)
    {
        if (!_loadingUniversalLoader && Current is { } c) _service.SetUniversalLoader(c.Id, value);
        NotifyUniversalLoader();
    }

    partial void OnUniversalLoaderUnsupportedChanged(string value) => NotifyUniversalLoader();
    partial void OnUniversalLoaderInstalledChanged(bool value) => NotifyUniversalLoader();
    partial void OnUniversalLoaderPreparedChanged(bool value) => NotifyUniversalLoader();

    private void NotifyUniversalLoader()
    {
        OnPropertyChanged(nameof(UniversalLoaderSupported));
        OnPropertyChanged(nameof(CanToggleUniversalLoader));
        OnPropertyChanged(nameof(UniversalLoaderState));
        OnPropertyChanged(nameof(ShowUniversalLoaderChip));
        OnPropertyChanged(nameof(UniversalLoaderChip));
        OnPropertyChanged(nameof(BakeEnabled));
        OnPropertyChanged(nameof(CanDownloadUniversalLoader));
    }

    /// <summary>Reads the card's state again: the switch, the fit, the record and (in the background) the mod count.</summary>
    internal void RefreshUniversalLoader()
    {
        var generation = ++_universalLoaderGeneration;
        ShowUniversalLoaderChanges = false;
        if (Current is not { } c)
        {
            _loadingUniversalLoader = true;
            UniversalLoaderOn = false;
            _loadingUniversalLoader = false;
            UniversalLoaderUnsupported = "";
            UniversalLoaderMods = "";
            return;
        }
        var installed = _service.InstalledBakeCapabilities;
        _loadingUniversalLoader = true;
        try
        {
            UniversalLoaderOn = _service.UsesUniversalLoader(c.Id);
            UniversalLoaderInherited = Amber.Core.Launch.InstanceSettings.Load(_service.Directory, c.Id).UniversalLoader is null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            UniversalLoaderOn = false;
        }
        finally
        {
            _loadingUniversalLoader = false;
        }
        UniversalLoaderInstalled = installed is not null;
        UniversalLoaderUnsupported = UniversalLoaderFit.Unsupported(_service.UniversalLoaderCapabilities, c.Loader, c.McVersion) ?? "";
        UniversalLoaderPrepared = BakeRecord.Read(c.WorkDir) is not null;
        NotifyUniversalLoader();

        var extras = CrossLoader.Extras(_service.UniversalLoaderCapabilities, c.Loader, c.McVersion);
        _ = Task.Run(() => UniversalLoaderFit.CrossMods(new ModManager(c.ModsDir).List(), c.Loader, extras)).ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            if (generation != _universalLoaderGeneration || Current != c) return;
            var count = t.IsCompletedSuccessfully ? t.Result : 0;
            UniversalLoaderMods = count == 0 ? L.T("instance.ul.mods_none") : L.F("instance.ul.mods", count);
        }), TaskScheduler.Default);
    }

    /// <summary>The chip next to the start button: to the card in the Settings tab.</summary>
    [RelayCommand]
    private void GoToUniversalLoader()
    {
        TabIndex = (int)InstanceTab.Settings;
        UniversalLoaderCardRequested?.Invoke();
    }

    /// <summary>The view brings the card into view.</summary>
    public event Action? UniversalLoaderCardRequested;

    /// <summary>[Start without it this time]: one launch without the loader; the setting stays.</summary>
    [RelayCommand]
    private void LaunchWithoutUniversalLoader()
    {
        if (Current is { } c) _service.RequestLaunch(new LaunchRequest(c.Id, SkipBake: true));
    }

    /// <summary>[See what it changed]: the notable problems the loader wrote during the last game, in plain words.</summary>
    [RelayCommand]
    private void ShowUniversalLoaderChangesNow()
    {
        UniversalLoaderChanges.Clear();
        if (Current is not { } c) return;
        var record = BakeRecord.Read(c.WorkDir);
        if (record?.Launch.ProblemsFile is not { } file || !File.Exists(file))
        {
            UniversalLoaderChangesNote = L.T(record is null ? "instance.ul.changes_never" : "instance.ul.changes_none");
        }
        else
        {
            foreach (var line in BakeRuntimeProblem.ReadFile(file).Where(p => p.IsNotable).Select(p => p.Describe()).Distinct().Take(50))
                UniversalLoaderChanges.Add(line);
            UniversalLoaderChangesNote = UniversalLoaderChanges.Count == 0 ? L.T("instance.ul.changes_none") : "";
        }
        ShowUniversalLoaderChanges = true;
    }

    /// <summary>Downloads and installs the pinned loader through Settings (which owns the loader path); returns its status line.</summary>
    public Func<IProgress<(double Percent, long Bytes)>, Task<string>>? DownloadLoader { get; set; }

    /// <summary>[Download] on the card: the same download as in Settings › Game, with its progress on this page.</summary>
    [RelayCommand]
    private async Task DownloadUniversalLoaderAsync()
    {
        if (DownloadLoader is not { } download || !BakeLoaderRelease.Pinned.IsPublished) return;
        await RunWorkAsync(async _ =>
        {
            SetStatus(StatusMessage.Info("instance.ul.downloading"));
            var result = await download(new Progress<(double Percent, long Bytes)>(p =>
            {
                Work.Bytes(p.Bytes);
                Progress = p.Percent;
            }));
            Status = result;
        });
        RefreshUniversalLoader();
    }
}
