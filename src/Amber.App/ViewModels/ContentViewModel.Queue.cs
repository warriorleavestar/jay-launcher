using System.Collections.ObjectModel;
using Amber.App.Localization;
using Amber.App.Services;
using Amber.Core.Content;
using Amber.Core.Game;
using Amber.Core.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

/// <summary>
/// Discover's "to install" bar (official design §6.12): [+ Queue] on a result or in the detail pane collects files,
/// and [Install N] puts them all in, with their required dependencies, as one activity task.
/// </summary>
public sealed partial class ContentViewModel
{
    public InstallQueue Queue { get; } = new();

    /// <summary>The bar's rows (a copy of the queue the view can bind to).</summary>
    public ObservableCollection<QueuedContent> QueueItems { get; } = [];

    public int QueueCount => Queue.Count;
    public bool HasQueue => Queue.Count > 0;

    /// <summary>"2 to install: Iris, JourneyMap".</summary>
    public string QueueText => Queue.Count == 0 ? "" :
        L.P("content.queue.summary", Queue.Count, Queue.Count, string.Join(L.T("common.list_separator"), Queue.Items.Take(4).Select(i => i.Title)) + (Queue.Count > 4 ? "…" : ""));

    public string InstallQueueLabel => L.P("content.queue.install", Queue.Count, Queue.Count);

    /// <summary>Starts a modpack's new instance once it is installed (MainViewModel).</summary>
    public Action<string>? LaunchInstance { get; set; }

    private void HookQueue()
    {
        Queue.Changed += () =>
        {
            QueueItems.Clear();
            foreach (var item in Queue.Items) QueueItems.Add(item);
            foreach (var row in Results) row.Queued = Queue.Contains(row.Project.Source, row.Project.Id);
            OnPropertyChanged(nameof(QueueCount));
            OnPropertyChanged(nameof(HasQueue));
            OnPropertyChanged(nameof(QueueText));
            OnPropertyChanged(nameof(InstallQueueLabel));
            InstallQueueCommand.NotifyCanExecuteChanged();
            QueueSelectedCommand.NotifyCanExecuteChanged();
        };
    }

    /// <summary>Where the selected file would go, or why it can't (no instance, vanilla mods, a datapack without a world).</summary>
    private QueuedContent? QueueEntryFor(FileItem file, string title, out StatusMessage? problem)
    {
        problem = null;
        if (CurrentInstance() is not { } instance)
        {
            problem = StatusMessage.Warning("content.install.instance_required");
            return null;
        }
        if (BlocksVanillaModInstall(CurrentType, instance))
        {
            problem = StatusMessage.Warning("content.install.vanilla_blocked", instance.Id);
            return null;
        }
        if (IsDatapack && TargetWorld is null)
        {
            problem = StatusMessage.Warning("content.install.world_required");
            return null;
        }
        var optional = OptionalDependencies.Where(d => d.Include && d.ProjectId is not null).Select(d => d.ProjectId!).ToHashSet(StringComparer.Ordinal);
        var optionalFiles = OptionalDependencies.Where(d => d.Include && d.ProjectId is null && d.FileId is not null).Select(d => d.FileId!).ToHashSet(StringComparer.Ordinal);
        return new QueuedContent(CurrentType, instance.Id, file.File, title, IsDatapack ? TargetWorld!.World.Path : null, optional, optionalFiles, _viaConnector);
    }

    private bool CanQueueSelected() => SelectedFile is not null && !IsModpack && !SelectedRequiresManualDownload;

    /// <summary>[+ Queue] in the detail pane: the chosen version and the ticked optional dependencies.</summary>
    [RelayCommand(CanExecute = nameof(CanQueueSelected))]
    private void QueueSelected()
    {
        if (SelectedFile is not { } file || Detail is not { } project) return;
        if (QueueEntryFor(file, project.Title, out var problem) is { } entry) Queue.Add(entry);
        else if (problem is not null) SetStatus(problem);
    }

    /// <summary>[+ Queue] / [Queued ✓] on a result row (Space on the selected row): picks the default version.</summary>
    [RelayCommand]
    private async Task ToggleQueueAsync(ProjectItem? item)
    {
        if (item is null || IsModpack) return;
        if (Queue.Remove(item.Project.Source, item.Project.Id)) return;
        if (!ReferenceEquals(Detail, item))
        {
            Selected = item;
            Detail = item;
        }
        await LoadFilesAsync();
        if (!ReferenceEquals(Detail, item)) return;
        if (SelectedFile is not { } file)
        {
            SetStatus(StatusMessage.Warning("content.queue.no_file", item.Title));
            return;
        }
        if (file.File.RequiresManualDownload)
        {
            ShowManualDownload(file.File);
            return;
        }
        if (QueueEntryFor(file, item.Title, out var problem) is { } entry) Queue.Add(entry);
        else if (problem is not null) SetStatus(problem);
    }

    [RelayCommand]
    private void RemoveQueued(QueuedContent? item)
    {
        if (item is not null) Queue.Remove(item.File.Source, item.File.ProjectId);
    }

    [RelayCommand]
    private void ClearQueue() => Queue.Clear();

    private bool CanInstallQueue() => Queue.Count > 0 && !IsBusy;

    /// <summary>[Install N]: resolve each entry's required dependencies, then download everything as one task.</summary>
    [RelayCommand(CanExecute = nameof(CanInstallQueue))]
    private async Task InstallQueueAsync()
    {
        var items = Queue.Items.ToList();
        if (items.Count == 0) return;
        IsBusy = true;
        var cts = _workCts = new CancellationTokenSource();
        var task = _service.Tasks.Track("content", L.P("content.queue.task", items.Count, items.Count),
            () => { try { cts.Cancel(); } catch (ObjectDisposedException) { } });
        try
        {
            var groups = new List<(string Target, InstallPlan Plan, string Where)>();
            foreach (var group in items.GroupBy(i => (i.InstanceId, i.Type, i.WorldPath)))
            {
                var instance = _service.GetInstance(group.Key.InstanceId);
                var target = group.Key.WorldPath is { } world ? DatapackManager.DirectoryOf(world) : instance.DirectoryFor(group.Key.Type);
                var plans = new List<InstallPlan>();
                foreach (var item in group)
                {
                    SetStatus(StatusMessage.Info("content.install.resolving"));
                    task.Report(null, item.Title);
                    if (item.Type != ContentType.Mod) plans.Add(new InstallPlan([item.File], []));
                    else if (item.ViaConnector)
                    {
                        var hasConnector = ConnectorInstaller.IsInstalled(new ModManager(instance.ModsDir).List().Where(m => m.Enabled).Select(m => m.Requirements));
                        plans.Add(await ConnectorInstaller.PlanFabricModAsync(item.File, instance, _service.Modrinth, _service.ContentInstaller, null, hasConnector, cts.Token));
                    }
                    else
                    {
                        plans.Add(await _service.ContentInstaller.ResolveAsync(item.File, instance, null, cts.Token,
                            optionalProjects: item.OptionalProjects?.ToHashSet(StringComparer.Ordinal) ?? [], optionalFiles: item.OptionalFiles?.ToHashSet(StringComparer.Ordinal) ?? []));
                    }
                }
                groups.Add((target, InstallQueue.Merge(plans), instance.Id));
            }
            var total = groups.Sum(g => g.Plan.Files.Count);
            var done = 0;
            foreach (var (target, plan, _) in groups)
            {
                var before = done;
                await _service.ContentInstaller.InstallAsync(plan, target, new Progress<Amber.Core.Net.DownloadProgress>(p =>
                {
                    Progress = 100.0 * (before + p.CompletedFiles) / Math.Max(1, total);
                    task.Report(Progress / 100, p.CurrentFile);
                    SetStatus(StatusMessage.Info("content.install.progress", before + p.CompletedFiles, total, p.CurrentFile));
                }), cts.Token);
                done += plan.Files.Count;
            }
            await Ui.FlushAsync();
            var dependencies = total - items.Count;
            var where = string.Join(L.T("common.list_separator"), groups.Select(g => g.Where).Distinct());
            var problems = groups.SelectMany(g => g.Plan.Problems).Distinct().ToList();
            var note = string.Join(L.T("common.list_separator"), problems);
            var message = (problems.Count == 0, dependencies > 0) switch
            {
                (true, true) => new StatusMessage("content.queue.done", [items.Count, dependencies, where], StatusKind.Success),
                (true, false) => new StatusMessage("content.queue.done_plain", [items.Count, where], StatusKind.Success),
                (false, true) => new StatusMessage("content.queue.done_problems", [items.Count, dependencies, where, note], StatusKind.Warning),
                _ => new StatusMessage("content.queue.done_plain_problems", [items.Count, where, note], StatusKind.Warning),
            };
            message = message with { Count = items.Count };
            SetStatus(message);
            task.Succeed(Status);
            Queue.Clear();
            _service.NotifyContentChanged();
        }
        catch (ManualDownloadRequiredException e)
        {
            task.Fail(L.T("activity.task.manual"));
            ShowManualDownload(e.File);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            task.Cancelled();
            SetStatus(StatusMessage.Warning("common.cancelled"));
        }
        catch (Exception e)
        {
            task.Fail(ErrorText.Describe(e));
            SetStatus(StatusMessage.Failure("common.install_failed", e, ErrorText.Of(e)));
        }
        finally
        {
            task.Cancelled();
            IsBusy = false;
            Progress = 0;
        }
    }

    /// <summary>A modpack's [Install and play] (the one amber button on this screen).</summary>
    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallModpackAndPlayAsync()
    {
        if (!IsModpack || SelectedFile?.File is not { } file) return;
        if (file.RequiresManualDownload)
        {
            ShowManualDownload(file, ManualDownloadContext.ModpackArchive);
            return;
        }
        if (await InstallModpackFromFileAsync(file) is { } id) LaunchInstance?.Invoke(id);
    }

    /// <summary>✕ on the detail pane and Esc: the list stays where it was.</summary>
    [RelayCommand]
    private void CloseDetail()
    {
        Selected = null;
        Detail = null;
    }
}

public sealed partial class ProjectItem
{
    /// <summary>In the "to install" bar: the row's button reads "Queued ✓".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QueueLabel))]
    public partial bool Queued { get; set; }

    public string QueueLabel => L.T(Queued ? "content.queue.queued" : "content.queue.add");
}
