using System.Collections.ObjectModel;
using System.Diagnostics;
using Amber.Core.Localization;
using Amber.App.Services;
using Amber.Core.Java;
using Amber.Core.Launch;
using Amber.Core.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

public sealed partial class JunkItem(JunkGroup group) : ObservableObject
{
    public JunkGroup Group { get; } = group;
    public string Name => Group.Name;
    public string Description => Group.Description;
    public string Size => Group.Bytes >= 1024 * 1024 ? $"{Group.Bytes / 1024.0 / 1024:F1} MB" : $"{Group.Bytes / 1024.0:F0} KB";

    [ObservableProperty]
    public partial bool Selected { get; set; } = group.Bytes > 0;
}

/// <summary>
/// What used to be the toolbox page. It has no page of its own any more: Settings › Storage shows the clean-up,
/// Settings › Game the Java download, Settings › About the update check, and the "Connection problems?" drawer the
/// file download. One job (Java or file download) runs at a time.
/// </summary>
public sealed partial class ToolsViewModel : PageViewModel
{
    private readonly LauncherService service;
    private CancellationTokenSource? _cts;

    public ToolsViewModel(LauncherService service)
    {
        this.service = service;
        Junk.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasJunk));
    }

    public override string Title => L.T("tools.junk.title");
    public override string Icon => "M4 6h16v2H4z";
    public override bool HasView => false;

    /// <summary>The job that is running, or ran last: <see cref="JavaJob"/> or <see cref="DownloadJob"/>.</summary>
    public string? Job { get; private set; }
    internal const string JavaJob = "java";
    internal const string DownloadJob = "download";

    public bool IsInstallingJava => IsBusy && Job == JavaJob;
    public bool IsDownloadingFile => IsBusy && Job == DownloadJob;

    /// <summary>How the last Java download ended; shown under its card. Null while it runs (the progress line speaks then).</summary>
    public StatusMessage? JavaMessage => Job == JavaJob && !IsBusy ? StatusInfo : null;

    /// <summary>How the last file download ended.</summary>
    public StatusMessage? DownloadMessage => Job == DownloadJob && !IsBusy ? StatusInfo : null;

    /// <summary>The running job's name on its progress line.</summary>
    [ObservableProperty] public partial string BusyTitle { get; set; } = "";
    [ObservableProperty] public partial double? BytesPerSecond { get; set; }
    [ObservableProperty] public partial TimeSpan? Remaining { get; set; }

    public bool HasJunk => Junk.Count > 0;

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(StatusInfo) or nameof(IsBusy))
        {
            base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(JavaMessage)));
            base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(DownloadMessage)));
            base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(IsInstallingJava)));
            base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(IsDownloadingFile)));
        }
    }

    public ObservableCollection<JunkItem> Junk { get; } = [];
    public int[] JavaMajors { get; } = [8, 17, 21, 25];

    private string? _junkStatus;
    public string JunkStatus
    {
        get => _junkStatus ?? L.T("tools.junk.initial");
        set => SetProperty(ref _junkStatus, value);
    }
    [ObservableProperty] public partial string DownloadUrl { get; set; } = "";
    [ObservableProperty] public partial string DownloadFolder { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    [ObservableProperty] public partial string DownloadSha1 { get; set; } = "";
    [ObservableProperty] public partial int SelectedJavaMajor { get; set; } = 21;
    [ObservableProperty] public partial string UpdateStatus { get; set; } = "";
    [ObservableProperty] public partial string? UpdateUrl { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial double Progress { get; set; }

    [RelayCommand]
    private async Task ScanJunkAsync()
    {
        JunkStatus = L.T("tools.junk.scanning");
        var dir = service.Directory;
        var groups = await Task.Run(() => Toolbox.ScanJunk(dir));
        Junk.Clear();
        foreach (var g in groups) Junk.Add(new JunkItem(g));
        JunkStatus = L.F("tools.junk.summary", groups.Sum(g => g.Bytes) / 1024.0 / 1024);
    }

    [RelayCommand]
    private async Task CleanJunkAsync()
    {
        var selected = Junk.Where(j => j.Selected).Select(j => j.Group).ToList();
        if (selected.Count == 0) return;
        var freed = await Task.Run(() => Toolbox.Clean(selected));
        JunkStatus = L.F("tools.junk.cleaned", freed / 1024.0 / 1024);
        await ScanJunkAsync();
        JunkStatus = L.F("tools.junk.cleaned_skipped", freed / 1024.0 / 1024);
    }

    /// <summary>活动中心里的当前任务（下载文件、下载 Java）：进度跟着报过去。</summary>
    private Amber.Core.Activity.TaskHandle? _task;

    private async Task RunAsync(string job, Func<CancellationToken, Task> work, string title)
    {
        if (IsBusy) return;
        Job = job;
        BusyTitle = title;
        BytesPerSecond = null;
        Remaining = null;
        SetStatus(null);
        IsBusy = true;
        var cts = _cts = new CancellationTokenSource();
        var task = _task = service.Tasks.Track("tools", title, () => { try { cts.Cancel(); } catch (ObjectDisposedException) { } });
        try
        {
            await work(cts.Token);
            task?.Succeed(Status);
        }
        // 不是玩家点的取消（比如连接超时也抛 TaskCanceledException）按失败显示，不能说成“已取消”。
        catch (OperationCanceledException) when (_cts?.IsCancellationRequested == true)
        {
            task?.Cancelled();
            SetStatus(StatusMessage.Warning("common.cancelled"));
        }
        catch (Exception e)
        {
            task?.Fail(ErrorText.Describe(e));
            SetStatus(StatusMessage.Failure("instance.status.failed", e, ErrorText.Of(e)));
        }
        finally
        {
            task?.Cancelled();
            _task = null;
            IsBusy = false;
            Progress = 0;
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    /// <summary>Progress goes to the activity center's task, and its speed and time left come back for the line on the page.</summary>
    private void Report(double? fraction, long bytes)
    {
        if (_task is not { } task) return;
        task.Report(fraction, Status, bytes);
        BytesPerSecond = task.BytesPerSecond;
        Remaining = task.Remaining;
    }

    [RelayCommand]
    private Task DownloadFileAsync() => RunAsync(DownloadJob, async ct =>
    {
        if (!Uri.TryCreate(DownloadUrl.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException(L.T("tools.download.invalid_url"));
        var name = Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath));
        if (string.IsNullOrWhiteSpace(name)) name = "download.bin";
        var target = Path.Combine(DownloadFolder.Trim(), name);
        SetStatus(StatusMessage.Info("tools.download.downloading", name));
        var item = new DownloadItem(uri.ToString(), target, string.IsNullOrWhiteSpace(DownloadSha1) ? null : DownloadSha1.Trim());
        await service.Downloader.DownloadOneAsync(item, new Progress<long>(bytes =>
        {
            SetStatus(StatusMessage.Info("tools.download.progress", name, bytes / 1024.0 / 1024));
            Report(null, bytes);
        }), ct);
        await Ui.FlushAsync();
        SetStatus(StatusMessage.Success("tools.download.completed", target));
    }, L.F("activity.kind.download", DownloadUrl.Trim()));

    [RelayCommand]
    private Task InstallJavaAsync() => RunAsync(JavaJob, async ct =>
    {
        var major = SelectedJavaMajor;
        SetStatus(StatusMessage.Info("tools.java.downloading", major));
        var path = await new JavaRuntimeInstaller(service.Downloader, service.Directory.Root).InstallAsync(null, major, new Progress<DownloadProgress>(p =>
        {
            Progress = 100.0 * p.CompletedFiles / Math.Max(1, p.TotalFiles);
            SetStatus(StatusMessage.Info("tools.java.progress", major, p.CompletedFiles, p.TotalFiles));
            Report(Progress / 100, p.BytesDownloaded);
        }), ct);
        await service.Java.ScanAsync(true);
        await Ui.FlushAsync();
        SetStatus(StatusMessage.Success("tools.java.installed", path));
    }, L.F("tools.java.downloading", SelectedJavaMajor));

    /// <summary>What the manual check shows for an answer from <paramref name="feed"/>: the status line and the page to open (null: no button).</summary>
    internal static (string Status, string? Page) DescribeUpdate(string feed, VerifiedUpdate? update)
    {
        if (update is null) return (L.F("tools.update.latest", UpdateChecker.CurrentVersion), null);
        // No button for an unverified release: its page is exactly what an attacker would want opened.
        if (!update.IsVerified) return (L.F("tools.update.unverified", update.Info.Version, UpdateCheck.Explain(update.Check.Verdict)), null);
        // Only a page Jay can vouch for: the feed's html_url is not covered by the signatures.
        var page = AutoUpdateCheck.ReleasePage(feed, update);
        var status = L.F("tools.update.available", update.Info.Version, UpdateChecker.CurrentVersion);
        return (page is null ? status + " " + L.T("tools.update.no_page") : status, page);
    }

    [RelayCommand]
    private async Task CheckUpdateAsync()
    {
        UpdateUrl = null;
        // A blank update address means the official Jay releases.
        // Phones and tablets update through the stores: without an own address they don't ask the desktop releases.
        if (AutoUpdateCheck.ManualFeed(service.Settings.UpdateFeedUrl, AutoUpdateCheck.IsSupported) is not { } feed)
        {
            UpdateStatus = L.T("tools.update.store");
            return;
        }
        try
        {
            // Privacy plan item 2: a newer release is offered only when enough Jay release keys signed it.
            var update = await new UpdateCheck(service.Http, AppSettings.DataDir).CheckAsync(feed, UpdateChecker.CurrentVersion);
            (UpdateStatus, UpdateUrl) = DescribeUpdate(feed, update);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidDataException)
        {
            UpdateStatus = L.F("tools.update.failed", e.Message);
        }
    }

    [RelayCommand]
    private void OpenUpdateUrl()
    {
        if (UpdateUrl is null) return;
        try
        {
            if (TryOpenUpdatePage(UpdateUrl, url => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }))) return;
            UpdateStatus = L.F("tools.update.failed", L.T("tools.download.invalid_url"));
            UpdateUrl = null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            UpdateStatus = L.F("tools.update.failed", e.Message);
        }
    }

    /// <summary>Hands the update page to <paramref name="open"/> only when it is a safe absolute HTTP(S) address.</summary>
    internal static bool TryOpenUpdatePage(string? value, Action<string> open)
    {
        if (!UpdateChecker.TryGetSafeUpdateUrl(value, out var uri)) return false;
        open(uri!.AbsoluteUri);
        return true;
    }
}
