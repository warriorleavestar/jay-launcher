namespace Amber.Core.Activity;

/// <summary>请求上的一个按钮。Id 是处理时传回来的选择。</summary>
public sealed record RequestChoice(string Id, string Label, bool IsDefault = false, bool IsDanger = false);

/// <summary>
/// 要你处理、有时限的事（敲门、收文件、远程控制、全面连接测试、多开确认、联机网络想换外观）。
/// IsSafety 的用锁定样式、排在前面。Timeout 到了自动按 TimeoutChoice 处理（没写就是 "timeout"）。
/// 同一个 DedupeKey 只有一条：再发返回原来那条。
/// </summary>
public sealed record RequestSpec(string Kind, string Title, string? Body, IReadOnlyList<RequestChoice> Choices,
    bool IsSafety = false, TimeSpan? Timeout = null, string? DedupeKey = null, string? TimeoutChoice = null);

/// <summary>请求是在哪处理的：请求条、房间卡、置顶小窗、系统通知、超时、程序自己（对方撤回之类）。</summary>
public enum RequestSource { Bar, Panel, Popup, SystemNotification, Timeout, Program }

public sealed class RequestHandle
{
    internal RequestHandle(RequestSpec spec, DateTimeOffset posted)
    {
        Spec = spec;
        Posted = posted;
    }

    public Guid Id { get; } = Guid.NewGuid();
    public RequestSpec Spec { get; }
    public DateTimeOffset Posted { get; }
    public DateTimeOffset? Deadline => Spec.Timeout is { } t ? Posted + t : null;
    public bool IsResolved => Choice is not null;
    public string? Choice { get; internal set; }
    public RequestSource? ResolvedBy { get; internal set; }
    public DateTimeOffset? Resolved { get; internal set; }

    /// <summary>还剩多久（没有时限是 null，过了是 0）。</summary>
    public TimeSpan? Remaining(DateTimeOffset now) => Deadline is { } d ? d > now ? d - now : TimeSpan.Zero : null;
}

/// <summary>
/// 请求的登记处（官方设计 §11.6）：一个请求可能同时出现在请求条、房间卡、置顶小窗、系统通知里，
/// 任一处处理了，Resolved 事件让其他几处一起撤掉。只认第一次处理。
/// </summary>
public sealed class RequestCenter(Func<DateTimeOffset>? clock = null)
{
    private readonly object _gate = new();
    private readonly List<RequestHandle> _pending = [];
    private readonly List<RequestHandle> _history = [];
    private readonly Func<DateTimeOffset> _now = clock ?? (() => DateTimeOffset.Now);

    public const int MaxHistory = 50;

    public event Action<RequestHandle>? Posted;

    /// <summary>请求被处理了（参数里有选择和来源）。每个请求只触发一次。</summary>
    public event Action<RequestHandle>? Resolved;

    /// <summary>等你处理的请求：安全类在前，其余按时间先后。</summary>
    public IReadOnlyList<RequestHandle> Pending
    {
        get
        {
            lock (_gate) return _pending.OrderByDescending(r => r.Spec.IsSafety).ThenBy(r => r.Posted).ToList();
        }
    }

    /// <summary>已经处理过的（活动中心“通知”里回看），最近的在前。</summary>
    public IReadOnlyList<RequestHandle> History
    {
        get { lock (_gate) return _history.AsEnumerable().Reverse().ToList(); }
    }

    public RequestHandle Post(RequestSpec spec)
    {
        if (spec.Choices.Count == 0) throw new ArgumentException("A request needs at least one choice.", nameof(spec));
        RequestHandle handle;
        lock (_gate)
        {
            if (spec.DedupeKey is not null && _pending.FirstOrDefault(r => r.Spec.DedupeKey == spec.DedupeKey) is { } existing) return existing;
            handle = new RequestHandle(spec, _now());
            _pending.Add(handle);
        }
        Posted?.Invoke(handle);
        return handle;
    }

    /// <summary>处理一个请求。已经处理过（别处先点了、超时了）返回 false，什么也不做。</summary>
    public bool Resolve(RequestHandle handle, string choice, RequestSource from)
    {
        lock (_gate)
        {
            if (handle.IsResolved || !_pending.Remove(handle)) return false;
            handle.Choice = choice;
            handle.ResolvedBy = from;
            handle.Resolved = _now();
            _history.Add(handle);
            if (_history.Count > MaxHistory) _history.RemoveAt(0);
        }
        Resolved?.Invoke(handle);
        return true;
    }

    /// <summary>按 DedupeKey 撤掉一条（比如对方撤回了敲门）。</summary>
    public bool Withdraw(string dedupeKey, string choice = "withdrawn")
    {
        RequestHandle? handle;
        lock (_gate) handle = _pending.FirstOrDefault(r => r.Spec.DedupeKey == dedupeKey);
        return handle is not null && Resolve(handle, choice, RequestSource.Program);
    }

    /// <summary>到时限的请求按超时处理。界面每秒调一次；返回处理掉的条数。</summary>
    public int Expire()
    {
        var now = _now();
        List<RequestHandle> due;
        lock (_gate) due = _pending.Where(r => r.Deadline is { } d && d <= now).ToList();
        return due.Count(r => Resolve(r, r.Spec.TimeoutChoice ?? "timeout", RequestSource.Timeout));
    }
}
