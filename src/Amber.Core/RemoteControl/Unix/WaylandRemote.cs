using System.Diagnostics;
using System.Runtime.Versioning;
using Amber.Core.Localization;
using Microsoft.Win32.SafeHandles;

namespace Amber.Core.RemoteControl.Unix;

/// <summary>
/// Linux Wayland：一次被看 / 被控对应一个门户会话（<see cref="WaylandPortal"/>）。截屏和注入共用同一个会话：
/// <see cref="RemotePlatform.CreateScreen"/> 先建它，紧接着的 <see cref="RemotePlatform.CreateInput"/> 用同一个。
/// 画面：开一个 gst-launch 子进程，每块共享的屏幕一个 pipewiresrc（转成 BGRx、缩放到逻辑尺寸），
/// 多块屏幕由 compositor 按位置拼成整张桌面，写到标准输出；后台线程一帧帧读，截屏时拿最新的一帧。
/// 只开一个进程是因为门户给的 PipeWire 描述符只能有一个客户端用（两个进程共用同一个连接会互相打乱，谁都收不到画面）。坐标都是逻辑像素，和门户注入鼠标用的坐标是同一套。
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class WaylandSession : IDisposable
{
    /// <summary>等用户在系统授权框里点同意，最多这么久。</summary>
    internal static TimeSpan ConsentTimeout { get; set; } = TimeSpan.FromMinutes(2);

    private static WaylandSession? _latest;
    private readonly CancellationTokenSource _cts = new();
    private readonly Lazy<Task<bool>> _start;
    private DesktopReader? _reader;
    private WaylandPortal? _portal;
    private SafeFileHandle? _pipewire;
    private IReadOnlyList<WaylandPortal.Stream> _streams = [];

    public WaylandSession() => _start = new Lazy<Task<bool>>(() => Task.Run(StartAsync));

    /// <summary>测试用：不经门户，直接用一个已经连上 PipeWire 的描述符和已知的节点。</summary>
    internal WaylandSession(IReadOnlyList<WaylandPortal.Stream> streams, SafeFileHandle pipewire)
    {
        _streams = streams;
        _pipewire = pipewire;
        _start = new Lazy<Task<bool>>(() => Task.Run(StartReadersAsync));
    }

    /// <summary>最近一次为截屏建的会话（注入用它）。</summary>
    public static WaylandSession? Latest => Volatile.Read(ref _latest);

    public static WaylandSession Create()
    {
        var session = new WaylandSession();
        Volatile.Write(ref _latest, session);
        return session;
    }

    /// <summary>启动失败的原因（只给开发者看，不翻译）。</summary>
    public string? LastError { get; private set; }

    public bool CanControl => _portal?.CanControl == true;
    public IReadOnlyList<WaylandPortal.Stream> Streams => Volatile.Read(ref _streams);

    /// <summary>门户会话和画面都就绪（第一次调用会弹系统授权框并等用户点）。</summary>
    public bool EnsureStarted()
    {
        try { return _start.Value.Wait(ConsentTimeout + TimeSpan.FromSeconds(20)) && _start.Value.Result; }
        catch (AggregateException) { return false; }
    }

    private async Task<bool> StartAsync()
    {
        try
        {
            using var consent = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            consent.CancelAfter(ConsentTimeout);
            _portal = await WaylandPortal.StartAsync(wantControl: true, consent.Token);
            _pipewire = await _portal.OpenPipeWireRemote();
            Volatile.Write(ref _streams, _portal.Streams);
            return await StartReadersAsync();
        }
        catch (Exception e) when (e is IOException or Tmds.DBus.Protocol.DBusExceptionBase or OperationCanceledException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            LastError = e.GetType().Name + ": " + e.Message;
            return false;
        }
    }

    /// <summary>每块屏幕开一个 gst-launch，等它们都来了第一帧。</summary>
    private async Task<bool> StartReadersAsync()
    {
        try
        {
            // 描述符只在开 gst-launch 的这一下允许子进程继承，开完马上收回，免得游戏、EasyTier 之类后来开的进程也拿到。
            if (!FdInherit.Set(_pipewire!, inherit: true)) { LastError = "fcntl(F_SETFD) failed"; return false; }
            DesktopReader reader;
            try
            {
                reader = _reader = new DesktopReader(Streams, Bounds, _pipewire!);
            }
            finally
            {
                FdInherit.Set(_pipewire!, inherit: false);
            }
            var deadline = Stopwatch.StartNew();
            while (!reader.HasFrame && deadline.Elapsed < TimeSpan.FromSeconds(15) && !_cts.IsCancellationRequested)
            {
                if (reader.Exited) { LastError = "gst-launch-1.0 exited: " + reader.LastError; return false; }
                await Task.Delay(50);
            }
            if (!reader.HasFrame) LastError = "no frame within 15s: " + reader.LastError;
            return reader.HasFrame;
        }
        catch (Exception e) when (e is IOException or Tmds.DBus.Protocol.DBusExceptionBase or OperationCanceledException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            LastError = e.GetType().Name + ": " + e.Message;
            return false;
        }
    }

    public ScreenBounds Bounds => BoundsOf(Streams);

    /// <summary>几块屏幕合起来的外框。</summary>
    internal static ScreenBounds BoundsOf(IReadOnlyList<WaylandPortal.Stream> streams)
    {
        if (streams.Count == 0) return new(0, 0, 0, 0);
        var left = streams.Min(s => s.X);
        var top = streams.Min(s => s.Y);
        return new(left, top, streams.Max(s => s.X + s.Width) - left, streams.Max(s => s.Y + s.Height) - top);
    }

    public bool TryCapture(ScreenFrame frame)
    {
        if (!EnsureStarted()) return false;
        var b = Bounds;
        if (b.Width <= 0 || b.Height <= 0 || _reader is not { Exited: false } reader) return false;
        frame.Resize(b.Width, b.Height);
        reader.CopyInto(frame);
        var px = frame.Pixels;
        for (var i = 3; i < px.Length; i += 4) px[i] = 255;
        return true;
    }

    /// <summary>桌面坐标落在哪块屏幕上（落在缝里时找最近的一块），换成那块屏幕里的坐标。</summary>
    public (uint Node, double X, double Y)? Locate(int x, int y) => Locate(Streams, x, y);

    internal static (uint Node, double X, double Y)? Locate(IReadOnlyList<WaylandPortal.Stream> streams, int x, int y)
    {
        if (streams.Count == 0) return null;
        var best = streams.MinBy(s =>
        {
            var dx = Math.Max(0, Math.Max(s.X - x, x - (s.X + s.Width - 1)));
            var dy = Math.Max(0, Math.Max(s.Y - y, y - (s.Y + s.Height - 1)));
            return dx * (long)dx + dy * (long)dy;
        });
        return (best.Node, Math.Clamp(x - best.X, 0, Math.Max(0, best.Width - 1)), Math.Clamp(y - best.Y, 0, Math.Max(0, best.Height - 1)));
    }

    public WaylandPortal? Portal => _portal;

    public void Dispose()
    {
        _cts.Cancel();
        _reader?.Dispose();
        _pipewire?.Dispose();
        if (_portal is { } p) _ = p.DisposeAsync().AsTask();
        Interlocked.CompareExchange(ref _latest, null, this);
    }

    /// <summary>
    /// gst-launch 的参数：一块屏幕直接转换输出；多块时每块进 compositor 的一个输入，摆到它在桌面里的位置（相对外框左上角），空缺处填黑。
    /// </summary>
    internal static List<string> GstArgs(IReadOnlyList<WaylandPortal.Stream> streams, ScreenBounds bounds, int fd)
    {
        List<string> args = ["-q"];
        void Source(WaylandPortal.Stream s) => args.AddRange(
        [
            "pipewiresrc", $"fd={fd}", $"path={s.Node}", "always-copy=true", "do-timestamp=true", "!",
            "videoconvert", "!", "videoscale", "!", $"video/x-raw,format=BGRx,width={s.Width},height={s.Height}", "!",
        ]);
        string[] sink = [$"video/x-raw,format=BGRx,width={bounds.Width},height={bounds.Height}", "!", "fdsink", "fd=1", "sync=false"];
        if (streams.Count == 1)
        {
            Source(streams[0]);
            args.AddRange(sink);
            return args;
        }
        args.Add("compositor");
        args.Add("name=desk");
        args.Add("background=black");
        for (var i = 0; i < streams.Count; i++)
        {
            args.Add($"sink_{i}::xpos={streams[i].X - bounds.X}");
            args.Add($"sink_{i}::ypos={streams[i].Y - bounds.Y}");
        }
        args.Add("!");
        args.Add("videoconvert");
        args.Add("!");
        args.AddRange(sink);
        for (var i = 0; i < streams.Count; i++)
        {
            Source(streams[i]);
            args.Add($"desk.sink_{i}");
        }
        return args;
    }

    /// <summary>整张桌面：gst-launch 子进程把 PipeWire 画面转成固定尺寸的 BGRx 原始帧写到标准输出，这里一帧帧读进来。</summary>
    private sealed class DesktopReader : IDisposable
    {
        private readonly Process _process;
        private readonly object _lock = new();
        private readonly int _width, _height;
        private byte[] _latest;
        private volatile bool _hasFrame;
        private volatile bool _disposed;

        public bool HasFrame => _hasFrame;
        public bool Exited => _process.HasExited;
        /// <summary>gst-launch 最后一行错误输出。</summary>
        public string? LastError { get; private set; }

        public DesktopReader(IReadOnlyList<WaylandPortal.Stream> streams, ScreenBounds bounds, SafeFileHandle pipewire)
        {
            (_width, _height) = (bounds.Width, bounds.Height);
            _latest = new byte[_width * _height * 4];
            var info = new ProcessStartInfo("gst-launch-1.0")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
            };
            foreach (var a in GstArgs(streams, bounds, (int)pipewire.DangerousGetHandle())) info.ArgumentList.Add(a);
            _process = Process.Start(info) ?? throw new InvalidOperationException("gst-launch-1.0 did not start");
            _process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) LastError = e.Data; };
            _process.BeginErrorReadLine();
            new Thread(ReadLoop) { IsBackground = true, Name = "Jay wayland capture" }.Start();
        }

        private void ReadLoop()
        {
            var stdout = _process.StandardOutput.BaseStream;
            var buffer = new byte[_latest.Length];
            try
            {
                while (!_disposed)
                {
                    stdout.ReadExactly(buffer);
                    lock (_lock) (buffer, _latest) = (_latest, buffer);
                    _hasFrame = true;
                }
            }
            catch (Exception e) when (e is IOException or EndOfStreamException or ObjectDisposedException) { }
        }

        public void CopyInto(ScreenFrame frame)
        {
            lock (_lock) Buffer.BlockCopy(_latest, 0, frame.Pixels, 0, Math.Min(_latest.Length, frame.Pixels.Length));
        }

        public void Dispose()
        {
            _disposed = true;
            try { if (!_process.HasExited) _process.Kill(); } catch (InvalidOperationException) { }
            _process.Dispose();
        }
    }

    // ---- 能不能用（界面每秒问一次，结果缓存）----

    private static (string? Reason, long At)? _cached;

    public static string? UnavailableReason()
    {
        var now = Environment.TickCount64;
        if (_cached is { } c && now - c.At < 30_000) return c.Reason;
        var reason = Check();
        _cached = (reason, now);
        return reason;
    }

    private static string? Check()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS")) && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR")))
            return L.T("core.remote.wayland_nodbus");
        try
        {
            using var p = Process.Start(new ProcessStartInfo("gst-inspect-1.0", ["--exists", "pipewiresrc"]) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true });
            if (p is null || !p.WaitForExit(5000) || p.ExitCode != 0) return L.T("core.remote.wayland_nogst");
        }
        catch (System.ComponentModel.Win32Exception) { return L.T("core.remote.wayland_nogst"); }
        return null;
    }
}

/// <summary>Wayland 截屏：用会话里各块屏幕的画面。第一次截屏会弹系统授权框。</summary>
[SupportedOSPlatform("linux")]
public sealed class WaylandScreenSource : IScreenSource
{
    private readonly WaylandSession _session = WaylandSession.Create();

    public ScreenBounds Bounds => _session.Bounds;
    public bool TryCapture(ScreenFrame frame) => _session.TryCapture(frame);
    internal string? LastError => _session.LastError;
    /// <summary>指针已经画进画面里了（cursor_mode=embedded），不单独报位置。</summary>
    public (int X, int Y)? CursorPosition() => null;
    public void Dispose() => _session.Dispose();
}

/// <summary>Wayland 注入：经门户的 RemoteDesktop 接口。只有 ScreenCast 门户（sway 等）时什么都不做（只能看）。</summary>
[SupportedOSPlatform("linux")]
public sealed class WaylandInputSink : IInputSink
{
    private readonly WaylandSession? _session = WaylandSession.Latest;

    public void Mouse(RemoteMouseAction action, RemoteMouseButton button, int desktopX, int desktopY, int delta)
    {
        if (_session?.Portal is not { CanControl: true } portal || _session.Locate(desktopX, desktopY) is not { } at) return;
        portal.PointerMotion(at.Node, at.X, at.Y);
        switch (action)
        {
            case RemoteMouseAction.Down or RemoteMouseAction.Up when EvdevButton(button) is { } b:
                portal.PointerButton(b, action == RemoteMouseAction.Down);
                break;
            case RemoteMouseAction.Wheel when delta != 0:
                portal.PointerAxisDiscrete(0, -Math.Sign(delta) * RemoteKeyCodes.WheelNotches(delta));
                break;
            case RemoteMouseAction.HWheel when delta != 0:
                portal.PointerAxisDiscrete(1, Math.Sign(delta) * RemoteKeyCodes.WheelNotches(delta));
                break;
        }
    }

    internal static int? EvdevButton(RemoteMouseButton b) => b switch
    {
        RemoteMouseButton.Left => 0x110,
        RemoteMouseButton.Right => 0x111,
        RemoteMouseButton.Middle => 0x112,
        _ => null,
    };

    public void Key(ushort scanCode, bool up, bool extended)
    {
        if (_session?.Portal is not { CanControl: true } portal || RemoteKeyCodes.X11(scanCode, extended) is not { } x11) return;
        portal.KeyboardKeycode((int)x11 - 8, !up);
    }

    public void Dispose() { }
}
