namespace Amber.Core.Launch;

/// <summary>一局游戏的状态（官方设计 §6.3 开始按钮的状态机）。</summary>
public enum LaunchState
{
    /// <summary>检查文件、Java、账号、启动前命令。</summary>
    Preparing,
    /// <summary>进程起来了，还没进主界面。</summary>
    Entering,
    /// <summary>进了主界面。</summary>
    Playing,
    /// <summary>点了退出，等游戏自己存盘退出。</summary>
    Exiting,
    /// <summary>正常结束（自己退出或玩家结束）。</summary>
    Exited,
    /// <summary>崩溃（非 0 退出，或没进主界面就退出且日志里有明确原因）。</summary>
    Crashed,
    /// <summary>准备阶段出错，游戏没起来。</summary>
    Failed,
    /// <summary>准备阶段被取消。</summary>
    Cancelled,
}

/// <summary>
/// 一局游戏的状态机（Core 里，可测试）。只允许表里的转换，其余的转换被忽略并返回 false，
/// 迟到的回调（比如进程已退出后才到的“进入游戏”日志行）不会把状态改回去。
/// </summary>
public sealed class LaunchSession(string instanceId, DateTimeOffset started)
{
    public string InstanceId { get; } = instanceId;
    public DateTimeOffset Started { get; } = started;
    public DateTimeOffset? Ended { get; private set; }
    public LaunchState State { get; private set; } = LaunchState.Preparing;
    /// <summary>进程起来的时间（算“正在玩”的时长）。</summary>
    public DateTimeOffset? ProcessStarted { get; private set; }
    public int? ExitCode { get; private set; }
    /// <summary>失败、崩溃时的一句话原因。</summary>
    public string? Reason { get; private set; }

    /// <summary>The steps of this start (check files, Java, prepare mods, sandbox, start, window), as they run.</summary>
    public LaunchSteps Steps { get; set; } = new();

    public bool IsActive => State is LaunchState.Preparing or LaunchState.Entering or LaunchState.Playing or LaunchState.Exiting;
    public bool HasProcess => State is LaunchState.Entering or LaunchState.Playing or LaunchState.Exiting;

    public event Action<LaunchSession>? Changed;

    private static readonly Dictionary<LaunchState, LaunchState[]> Allowed = new()
    {
        [LaunchState.Preparing] = [LaunchState.Entering, LaunchState.Failed, LaunchState.Cancelled],
        [LaunchState.Entering] = [LaunchState.Playing, LaunchState.Exiting, LaunchState.Exited, LaunchState.Crashed],
        [LaunchState.Playing] = [LaunchState.Exiting, LaunchState.Exited, LaunchState.Crashed],
        [LaunchState.Exiting] = [LaunchState.Exited, LaunchState.Crashed],
        [LaunchState.Exited] = [],
        [LaunchState.Crashed] = [],
        [LaunchState.Failed] = [],
        [LaunchState.Cancelled] = [],
    };

    public static bool CanMove(LaunchState from, LaunchState to) => Allowed[from].Contains(to);

    private bool Move(LaunchState to, DateTimeOffset now)
    {
        if (!CanMove(State, to)) return false;
        State = to;
        if (!IsActive) Ended = now;
        Changed?.Invoke(this);
        return true;
    }

    public bool ProcessUp(DateTimeOffset now)
    {
        if (!Move(LaunchState.Entering, now)) return false;
        ProcessStarted = now;
        return true;
    }

    public bool EnteredGame(DateTimeOffset now) => Move(LaunchState.Playing, now);
    public bool ExitRequested(DateTimeOffset now) => Move(LaunchState.Exiting, now);

    public bool ProcessEnded(DateTimeOffset now, int exitCode, bool crashed, string? reason = null)
    {
        ExitCode = exitCode;
        if (crashed) Reason = reason;
        return Move(crashed ? LaunchState.Crashed : LaunchState.Exited, now);
    }

    public bool Fail(DateTimeOffset now, string reason)
    {
        Reason = reason;
        return Move(LaunchState.Failed, now);
    }

    public bool Cancel(DateTimeOffset now) => Move(LaunchState.Cancelled, now);

    /// <summary>正在玩了多久（进程起来以后算；还没起来是 0）。</summary>
    public TimeSpan PlayTime(DateTimeOffset now) => ProcessStarted is { } s ? (Ended ?? now) - s : TimeSpan.Zero;
}

/// <summary>600ms 内连点开始只算一次（§6.3）。同一个实例和不同实例共用一个闸门：手抖点两个也只开一个。</summary>
public sealed class LaunchGate(TimeSpan? window = null)
{
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMilliseconds(600);
    private readonly TimeSpan _window = window ?? DefaultWindow;
    private DateTimeOffset? _last;

    public bool TryPass(DateTimeOffset now)
    {
        if (_last is { } last && now - last < _window && now >= last) return false;
        _last = now;
        return true;
    }
}
