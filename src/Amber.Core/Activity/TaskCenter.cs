namespace Amber.Core.Activity;

public enum TaskState { Running, Succeeded, PartlySucceeded, Failed, Cancelled }

/// <summary>任务做完的结果：成功、部分成功（比如 5 个模组装好 4 个）、失败。取消由 TaskCenter 自己判断。</summary>
public sealed record TaskOutcome(TaskState State, string? Message = null)
{
    public static TaskOutcome Success(string? message = null) => new(TaskState.Succeeded, message);
    public static TaskOutcome Partial(string message) => new(TaskState.PartlySucceeded, message);
    public static TaskOutcome Failure(string message) => new(TaskState.Failed, message);
}

/// <summary>
/// 一个长任务（安装、导入、更新、备份、导出、下载 Java、清理、下载文件）在活动中心里的一行。
/// 进度 0–1，不知道多少时是 null（不确定进度条）。
/// </summary>
public sealed class TaskHandle
{
    private readonly TaskCenter _owner;
    private readonly Action? _cancel;

    internal TaskHandle(TaskCenter owner, string kind, string title, string? dedupeKey, Action? cancel, DateTimeOffset started)
    {
        _owner = owner;
        Kind = kind;
        Title = title;
        DedupeKey = dedupeKey;
        _cancel = cancel;
        Started = started;
    }

    public Guid Id { get; } = Guid.NewGuid();
    public string Kind { get; }
    public string Title { get; private set; }
    public string? DedupeKey { get; }
    public DateTimeOffset Started { get; }
    public DateTimeOffset? Finished { get; private set; }
    public TaskState State { get; private set; } = TaskState.Running;
    public double? Progress { get; private set; }
    /// <summary>进度下面那一行小字（“4.1 MB/s · 约 20 秒”、当前步骤）。</summary>
    public string? Detail { get; private set; }
    /// <summary>结束后的结果说明（失败原因、部分成功的明细）。</summary>
    public string? Message { get; private set; }
    public bool CanCancel => _cancel is not null && State == TaskState.Running;
    public bool IsRunning => State == TaskState.Running;
    public bool CancelRequested { get; private set; }

    /// <summary>报告进度。任务已经结束时忽略（迟到的进度回调不能把结果改回“进行中”）。</summary>
    public void Report(double? progress, string? detail = null)
    {
        lock (_owner.Gate)
        {
            if (State != TaskState.Running) return;
            Progress = progress is { } p ? Math.Clamp(p, 0, 1) : null;
            if (detail is not null) Detail = detail;
        }
        _owner.RaiseChanged(this);
    }

    public void Rename(string title)
    {
        lock (_owner.Gate) Title = title;
        _owner.RaiseChanged(this);
    }

    public void Cancel()
    {
        lock (_owner.Gate)
        {
            if (State != TaskState.Running || _cancel is null || CancelRequested) return;
            CancelRequested = true;
        }
        _cancel();
        _owner.RaiseChanged(this);
    }

    /// <summary>结束这个任务。只认第一次：重复调用返回 false。</summary>
    public bool Complete(TaskOutcome outcome)
    {
        lock (_owner.Gate)
        {
            if (State != TaskState.Running) return false;
            State = outcome.State == TaskState.Running ? TaskState.Succeeded : outcome.State;
            Message = outcome.Message;
            Finished = _owner.Now();
            if (State == TaskState.Succeeded) Progress = 1;
        }
        _owner.OnCompleted(this);
        return true;
    }

    public bool Succeed(string? message = null) => Complete(TaskOutcome.Success(message));
    public bool Fail(string message) => Complete(TaskOutcome.Failure(message));
    public bool Cancelled() => Complete(new TaskOutcome(TaskState.Cancelled));
}

/// <summary>任务里拿到的上下文：报告进度。</summary>
public sealed class TaskContext(TaskHandle handle)
{
    public TaskHandle Handle { get; } = handle;
    public void Report(double? progress, string? detail = null) => Handle.Report(progress, detail);
}

/// <summary>
/// 所有长任务的登记处（官方设计 §11.6）：活动中心绑定它。同一个 dedupeKey 同时只跑一个；能取消；
/// 失败、部分成功都留着结果，直到清除。不碰界面：事件在报告进度的那个线程上触发，界面自己切回界面线程。
/// </summary>
public sealed class TaskCenter(Func<DateTimeOffset>? clock = null)
{
    internal readonly object Gate = new();
    private readonly List<TaskHandle> _all = [];
    internal Func<DateTimeOffset> Now { get; } = clock ?? (() => DateTimeOffset.Now);

    /// <summary>结束了的任务最多留多少条（最早的先清）。</summary>
    public const int MaxFinished = 50;

    /// <summary>任务列表或某个任务的进度变了（参数是变了的任务，增删时也给）。</summary>
    public event Action<TaskHandle>? Changed;

    /// <summary>任务结束（成功、部分成功、失败、取消）。结果卡、Toast、系统通知由订阅者决定。</summary>
    public event Action<TaskHandle>? Completed;

    public IReadOnlyList<TaskHandle> All
    {
        get { lock (Gate) return [.. _all]; }
    }

    public IReadOnlyList<TaskHandle> Active
    {
        get { lock (Gate) return _all.Where(t => t.IsRunning).ToList(); }
    }

    public bool HasActive
    {
        get { lock (Gate) return _all.Any(t => t.IsRunning); }
    }

    /// <summary>所有在跑的、知道进度的任务的平均进度（顶栏活动按钮外圈）。没有在跑的是 null。</summary>
    public double? OverallProgress
    {
        get
        {
            lock (Gate)
            {
                var running = _all.Where(t => t.IsRunning).ToList();
                if (running.Count == 0) return null;
                return running.Average(t => t.Progress ?? 0);
            }
        }
    }

    /// <summary>
    /// 登记一个由别处跑的任务（现有页面自己管取消和进度，只把状态报过来）。
    /// 同一个 dedupeKey 已经有在跑的：返回那个，不新建。
    /// </summary>
    public TaskHandle Track(string kind, string title, Action? cancel = null, string? dedupeKey = null)
    {
        TaskHandle handle;
        lock (Gate)
        {
            if (dedupeKey is not null && _all.FirstOrDefault(t => t.IsRunning && t.DedupeKey == dedupeKey) is { } existing) return existing;
            handle = new TaskHandle(this, kind, title, dedupeKey, cancel, Now());
            _all.Add(handle);
            TrimFinished();
        }
        RaiseChanged(handle);
        return handle;
    }

    /// <summary>
    /// 跑一个任务。同一个 dedupeKey 已经有在跑的：不再跑，返回那个。
    /// run 抛 OperationCanceledException 且是取消引起的算“已取消”；抛别的异常算失败（消息是异常文字）。
    /// </summary>
    public TaskHandle Start(string kind, string title, Func<TaskContext, CancellationToken, Task<TaskOutcome>> run, string? dedupeKey = null)
    {
        var cts = new CancellationTokenSource();
        lock (Gate)
        {
            if (dedupeKey is not null && _all.FirstOrDefault(t => t.IsRunning && t.DedupeKey == dedupeKey) is { } existing)
            {
                cts.Dispose();
                return existing;
            }
        }
        var handle = Track(kind, title, () => { try { cts.Cancel(); } catch (ObjectDisposedException) { } }, dedupeKey);
        _ = RunAsync(handle, run, cts);
        return handle;
    }

    private static async Task RunAsync(TaskHandle handle, Func<TaskContext, CancellationToken, Task<TaskOutcome>> run, CancellationTokenSource cts)
    {
        try
        {
            var outcome = await run(new TaskContext(handle), cts.Token).ConfigureAwait(false);
            handle.Complete(outcome);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            handle.Cancelled();
        }
        catch (Exception e)
        {
            handle.Fail(e.Message);
        }
        finally
        {
            cts.Dispose();
        }
    }

    /// <summary>清除已结束的任务（活动中心的【清除已完成】）。</summary>
    public int ClearFinished()
    {
        int removed;
        lock (Gate) removed = _all.RemoveAll(t => !t.IsRunning);
        if (removed > 0) Changed?.Invoke(null!);
        return removed;
    }

    /// <summary>取消所有能取消的任务（关窗时“全部结束并退出”）。</summary>
    public void CancelAll()
    {
        foreach (var t in Active) t.Cancel();
    }

    internal void RaiseChanged(TaskHandle handle) => Changed?.Invoke(handle);

    internal void OnCompleted(TaskHandle handle)
    {
        Changed?.Invoke(handle);
        Completed?.Invoke(handle);
    }

    private void TrimFinished()
    {
        var finished = _all.Where(t => !t.IsRunning).ToList();
        for (var i = 0; i < finished.Count - MaxFinished; i++) _all.Remove(finished[i]);
    }
}
