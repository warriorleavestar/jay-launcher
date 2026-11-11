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

/// <summary>工具箱：清理垃圾、下载任意文件、下载 Java、检查更新。</summary>
public sealed partial class ToolsViewModel(LauncherService service) : PageViewModel
{
    private CancellationTokenSource? _cts;

    public override string Title => L.T("tools.title");
    public override string Icon => "M21.500 7a5.500 5.500 0 0 1-7.600 5.100L7.200 18.800a2 2 0 0 1-2.800-2.800l6.700-6.700A5.500 5.500 0 0 1 17 2.500l-3.300 3.300 1 3.500 3.500 1 3.300-3.300c.3.600.5 1.300.5 2z";

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

    private async Task RunAsync(Func<CancellationToken, Task> work, string? title = null)
    {
        if (IsBusy) return;
        IsBusy = true;
        var cts = _cts = new CancellationTokenSource();
        var task = _task = title is null ? null : service.Tasks.Track("tools", title, () => { try { cts.Cancel(); } catch (ObjectDisposedException) { } });
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

    [RelayCommand]
    private Task DownloadFileAsync() => RunAsync(async ct =>
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
            _task?.Report(null, Status);
        }), ct);
        await Ui.FlushAsync();
        SetStatus(StatusMessage.Success("tools.download.completed", target));
    }, L.F("activity.kind.download", DownloadUrl.Trim()));

    [RelayCommand]
    private Task InstallJavaAsync() => RunAsync(async ct =>
    {
        var major = SelectedJavaMajor;
        SetStatus(StatusMessage.Info("tools.java.downloading", major));
        var path = await new JavaRuntimeInstaller(service.Downloader, service.Directory.Root).InstallAsync(null, major, new Progress<DownloadProgress>(p =>
        {
            Progress = 100.0 * p.CompletedFiles / Math.Max(1, p.TotalFiles);
            SetStatus(StatusMessage.Info("tools.java.progress", major, p.CompletedFiles, p.TotalFiles));
            _task?.Report(Progress / 100, Status);
        }), ct);
        await service.Java.ScanAsync(true);
        await Ui.FlushAsync();
        SetStatus(StatusMessage.Success("tools.java.installed", path));
    }, L.F("tools.java.downloading", SelectedJavaMajor));

    [RelayCommand]
    private async Task CheckUpdateAsync()
    {
        UpdateUrl = null;
        var feed = service.Settings.UpdateFeedUrl;
        if (string.IsNullOrWhiteSpace(feed))
        {
            UpdateStatus = L.F("tools.update.no_feed", UpdateChecker.CurrentVersion);
            return;
        }
        try
        {
            // Privacy plan item 2: a newer release is offered only when 2 of the 3 Jay trustees signed it.
            var update = await new UpdateCheck(service.Http, AppSettings.DataDir).CheckAsync(feed, UpdateChecker.CurrentVersion);
            if (update is null)
            {
                UpdateStatus = L.F("tools.update.latest", UpdateChecker.CurrentVersion);
            }
            else if (update.IsVerified)
            {
                UpdateStatus = L.F("tools.update.available", update.Info.Version, UpdateChecker.CurrentVersion);
                UpdateUrl = update.Info.Url;
            }
            else
            {
                // No button: the release page of an unverified release is exactly what an attacker would want opened.
                UpdateStatus = L.F("tools.update.unverified", update.Info.Version, UpdateCheck.Explain(update.Check.Verdict));
            }
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
        if (!UpdateChecker.TryGetSafeUpdateUrl(UpdateUrl, out var uri))
        {
            UpdateStatus = L.F("tools.update.failed", L.T("tools.download.invalid_url"));
            UpdateUrl = null;
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(uri!.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            UpdateStatus = L.F("tools.update.failed", e.Message);
        }
    }
}
