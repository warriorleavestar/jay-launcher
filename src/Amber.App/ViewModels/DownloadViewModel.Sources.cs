using System.Collections.ObjectModel;
using Amber.App.Localization;
using Amber.App.Services;
using Amber.Core.Content;
using Amber.Core.Game;
using Amber.Core.GameLibrary;
using Amber.Core.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

/// <summary>Where a new instance comes from (official design §6.11, the left rail).</summary>
public enum NewInstanceSource
{
    Version,
    PackFile,
    OnlinePack,
    Copy,
    Import,
}

/// <summary>A game folder another launcher left on this computer.</summary>
public sealed class OtherDirItem(string path, int count) : LocalizedObservable
{
    public string Path { get; } = path;
    public int Count { get; } = count;
    public string CountText => L.P("newinstance.import.count", Count, Count);
}

/// <summary>
/// The new-instance panel's other sources: a modpack file with a preview, the online modpacks in Discover, a copy of an
/// existing instance and a game folder from another launcher.
/// </summary>
public sealed partial class DownloadViewModel
{
    public OptionItem[] Sources { get; } = OptionItem.Keys(
        "newinstance.source.version", "newinstance.source.pack_file", "newinstance.source.online_pack", "newinstance.source.copy", "newinstance.source.import");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Source), nameof(IsVersionSource), nameof(IsPackFileSource), nameof(IsOnlinePackSource), nameof(IsCopySource), nameof(IsImportSource))]
    public partial int SourceIndex { get; set; }

    public NewInstanceSource Source => (NewInstanceSource)Math.Clamp(SourceIndex, 0, Sources.Length - 1);
    public bool IsVersionSource => Source == NewInstanceSource.Version;
    public bool IsPackFileSource => Source == NewInstanceSource.PackFile;
    public bool IsOnlinePackSource => Source == NewInstanceSource.OnlinePack;
    public bool IsCopySource => Source == NewInstanceSource.Copy;
    public bool IsImportSource => Source == NewInstanceSource.Import;

    /// <summary>"Installs to: D:\Games" in the button bar.</summary>
    public string InstallToText => L.F("newinstance.install_to", _service.Directory.Root);

    partial void OnSourceIndexChanged(int value)
    {
        OnPropertyChanged(nameof(InstallToText));
        if (Source == NewInstanceSource.Copy) RefreshCopySources();
        if (Source == NewInstanceSource.Import && !_scanned) _ = ScanOtherLaunchersAsync();
    }

    /// <summary>Opens the panel on a source (the library's "＋ New instance ▾" menu, the empty states).</summary>
    public void OpenSource(NewInstanceSource source) => SourceIndex = (int)source;

    // ---- Modpack file ----

    /// <summary>Starts an import (MainViewModel: the import runs on the Discover page, which handles files that need a manual download).</summary>
    public Func<string, bool, Task>? ImportPackFile { get; set; }

    /// <summary>The Discover page on modpacks.</summary>
    public Action? BrowseModpacks { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPackPreview), nameof(PackTitle), nameof(PackFacts))]
    [NotifyCanExecuteChangedFor(nameof(ImportPackCommand), nameof(ImportPackAndPlayCommand))]
    public partial ModpackInfo? PackInfo { get; set; }

    [ObservableProperty] public partial string? PackPath { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasPackError))] public partial string? PackError { get; set; }

    public bool HasPackPreview => PackInfo is not null;
    public bool HasPackError => PackError is not null;
    public string PackTitle => PackInfo is { } p ? (p.Version is { Length: > 0 } v ? $"{p.Name} {v}" : p.Name) : "";

    /// <summary>"Minecraft 1.20.1 · Fabric 0.15.11 · 182 files · MultiMC".</summary>
    public string PackFacts => PackInfo is not { } p ? "" : string.Join(" · ", new[]
    {
        $"Minecraft {p.McVersion}",
        p.Loader is { } loader ? $"{loader} {p.LoaderVersion}".TrimEnd() : L.T("home.version.vanilla"),
        L.P("newinstance.pack.files", p.FileCount, p.FileCount),
        p.Format,
    }.Concat(p.IsMixed ? [L.F("newinstance.pack.mixed", string.Join(" + ", p.ExtraLoaders))] : []));

    /// <summary>The file picker or a drop handed us a pack: read its manifest without importing anything.</summary>
    public async Task PreviewPackAsync(string path)
    {
        PackPath = path;
        PackInfo = null;
        PackError = null;
        try
        {
            PackInfo = await Task.Run(() => ModpackService.Inspect(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException or NotSupportedException or ArgumentException)
        {
            PackError = L.F("newinstance.pack.unreadable", ErrorText.Describe(e));
        }
    }

    private bool CanImportPack() => PackInfo is not null && PackPath is not null;

    [RelayCommand(CanExecute = nameof(CanImportPack))]
    private Task ImportPackAsync() => StartPackImport(play: false);

    [RelayCommand(CanExecute = nameof(CanImportPack))]
    private Task ImportPackAndPlayAsync() => StartPackImport(play: true);

    private Task StartPackImport(bool play)
    {
        if (PackPath is not { } path || ImportPackFile is null) return Task.CompletedTask;
        PackInfo = null;
        PackPath = null;
        return ImportPackFile(path, play);
    }

    [RelayCommand]
    private void ClearPack()
    {
        PackInfo = null;
        PackPath = null;
        PackError = null;
    }

    [RelayCommand]
    private void OpenDiscoverModpacks() => BrowseModpacks?.Invoke();

    // ---- Copy an existing instance ----

    public ObservableCollection<string> CopySources { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CopySharesFolder), nameof(CopyNameProblem), nameof(HasCopyNameProblem))]
    [NotifyCanExecuteChangedFor(nameof(CopyInstanceCommand))]
    public partial string? CopySourceId { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CopyNameProblem), nameof(HasCopyNameProblem))]
    [NotifyCanExecuteChangedFor(nameof(CopyInstanceCommand))]
    public partial string CopyName { get; set; } = "";

    [ObservableProperty] public partial bool CopySaves { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyInstanceCommand))]
    public partial bool IsCopying { get; set; }

    /// <summary>A shared-folder instance has no worlds or mods of its own to bring along: say so instead of the checkbox.</summary>
    public bool CopySharesFolder => CopySourceId is { } id && _service.UsesSharedDir(id);

    private InstanceNameProblem CopyCheck => CopySourceId is null ? InstanceNameProblem.None : NewInstanceNames.Check(CopyName, _service.InstalledVersions());

    public string CopyNameProblem => CopyCheck switch
    {
        InstanceNameProblem.Empty => L.T("newinstance.name.empty"),
        InstanceNameProblem.InvalidCharacters => L.T("newinstance.name.invalid"),
        InstanceNameProblem.TrailingDotOrSpace => L.T("newinstance.name.trailing"),
        InstanceNameProblem.Reserved => L.T("newinstance.name.reserved"),
        InstanceNameProblem.Taken => L.T("newinstance.name.taken"),
        _ => "",
    };

    public bool HasCopyNameProblem => CopyNameProblem.Length > 0;

    partial void OnCopySourceIdChanged(string? value)
    {
        if (value is not null) CopyName = NewInstanceNames.Default(value, null, _service.InstalledVersions());
    }

    private void RefreshCopySources()
    {
        var keep = CopySourceId;
        CopySources.Clear();
        foreach (var id in _service.InstalledVersions().Order(StringComparer.OrdinalIgnoreCase)) CopySources.Add(id);
        CopySourceId = keep is not null && CopySources.Contains(keep) ? keep : _service.CurrentOr(CopySources, CopySources.FirstOrDefault());
    }

    private bool CanCopy() => !IsCopying && CopySourceId is not null && CopyCheck == InstanceNameProblem.None;

    [RelayCommand(CanExecute = nameof(CanCopy))]
    private async Task CopyInstanceAsync()
    {
        if (CopySourceId is not { } source) return;
        var name = CopyName.Trim();
        var saves = CopySaves && !CopySharesFolder;
        var isolated = !_service.UsesSharedDir(source);
        var ctx = _service.Context;
        IsCopying = true;
        using var cts = new CancellationTokenSource();
        var task = _service.Tasks.Track("copy", L.F("newinstance.copy.task", source, name), () => { try { cts.Cancel(); } catch (ObjectDisposedException) { } });
        try
        {
            await Task.Run(() => InstanceCopier.Copy(ctx.Directory, source, name, saves, cts.Token));
            // The copy's own settings file came along; an isolation that came from another launcher's settings did not.
            if (isolated) _service.ForceIsolated(name);
            task.Succeed(L.F("newinstance.copy.done", name));
            _service.NotifyInstalledChanged();
            if (_service.IsCurrentDirectory(ctx)) _service.SetCurrentInstance(name);
            InstalledHasLoader = _service.GetInstance(name).Loader is not null;
            JustInstalled = name;
            RefreshCopySources();
            SetStatus(StatusMessage.Success("newinstance.copy.done", name));
        }
        catch (OperationCanceledException)
        {
            task.Cancelled();
            SetStatus(StatusMessage.Warning("common.cancelled"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            task.Fail(ErrorText.Describe(e));
            SetStatus(StatusMessage.Failure("newinstance.copy.failed", e, ErrorText.Of(e)));
        }
        finally
        {
            task.Cancelled();
            IsCopying = false;
        }
    }

    // ---- From another launcher ----

    public ObservableCollection<OtherDirItem> OtherDirectories { get; } = [];

    [ObservableProperty] public partial bool IsScanning { get; set; }
    [ObservableProperty] public partial bool NoOtherDirectories { get; set; }
    private bool _scanned;

    /// <summary>Where to look (self-checks and screenshots never scan the real .minecraft).</summary>
    public Func<IEnumerable<string>> OtherCandidates { get; set; } = () =>
        Environment.GetEnvironmentVariable("AMBER_SELFTEST") is { Length: > 0 }
        || Environment.GetEnvironmentVariable("AMBER_SNAPSHOT_DIR") is { Length: > 0 }
            ? [] : GameDirectoryDetector.CommonCandidates(AppContext.BaseDirectory);

    [RelayCommand]
    internal async Task ScanOtherLaunchersAsync()
    {
        _scanned = true;
        IsScanning = true;
        var exclude = _service.Directory.Root;
        var candidates = OtherCandidates;
        try
        {
            var found = await Task.Run(() => GameDirectoryDetector.Find(candidates(), exclude, 8));
            OtherDirectories.Clear();
            foreach (var d in found) OtherDirectories.Add(new OtherDirItem(d.Path, d.InstanceCount));
        }
        finally
        {
            IsScanning = false;
            NoOtherDirectories = OtherDirectories.Count == 0;
        }
    }

    /// <summary>
    /// "Use this folder": the game folder becomes the library's location; nothing in it is moved or changed. Several
    /// library locations side by side come with the storage settings (§6.20); until then this switches the location.
    /// </summary>
    [RelayCommand]
    public void UseGameDirectory(string? path)
    {
        if (path is null || IsBusy || IsCopying) return;
        var inner = Path.Combine(path, ".minecraft");
        if (!Directory.Exists(Path.Combine(path, "versions")) && Directory.Exists(Path.Combine(inner, "versions"))) path = inner;
        _service.Settings.GameDirectory = path;
        _service.SaveAndReload();
        _service.NotifyInstalledChanged();
        OnPropertyChanged(nameof(InstallToText));
        var count = GameDirectoryDetector.CountInstances(path);
        Toasts.Show(L.P("newinstance.import.used", count, path, count), Avalonia.Controls.Notifications.NotificationType.Success);
        _ = ScanOtherLaunchersAsync();
        CloseAfterStart?.Invoke();
    }

    /// <summary>Test hook: list folders without touching the disk outside the report folder.</summary>
    internal void SetOtherCandidatesForTest(params string[] paths) => OtherCandidates = () => paths;
}
