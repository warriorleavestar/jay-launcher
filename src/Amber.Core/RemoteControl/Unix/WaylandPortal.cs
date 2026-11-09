using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using Tmds.DBus.Protocol;

namespace Amber.Core.RemoteControl.Unix;

/// <summary>
/// Linux Wayland 桌面的屏幕共享会话：经 xdg-desktop-portal（D-Bus）申请。
/// 有 RemoteDesktop 门户（GNOME、KDE）时同时申请屏幕和键鼠，系统弹一次授权框；只有 ScreenCast 门户（wlroots 系的 sway 等）时只能看不能控。
/// 画面经 PipeWire 送来（<see cref="OpenPipeWireRemote"/> 拿到连接 PipeWire 的文件描述符）。
/// 会话跟着 D-Bus 连接走：连接断了（Jay 退出）系统自动结束共享。
/// 这里的提示只给开发者看（异常信息），不翻译。
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class WaylandPortal : IAsyncDisposable
{
    private const string Bus = "org.freedesktop.portal.Desktop";
    private const string ObjectPath = "/org/freedesktop/portal/desktop";
    private const string ScreenCastInterface = "org.freedesktop.portal.ScreenCast";
    private const string RemoteDesktopInterface = "org.freedesktop.portal.RemoteDesktop";

    /// <summary>一块共享出来的屏幕：PipeWire 节点号，在整个桌面里的位置和大小（逻辑像素）。</summary>
    public readonly record struct Stream(uint Node, int X, int Y, int Width, int Height);

    private readonly DBusConnection _connection;
    private string _session = "";
    private int _token;

    public bool CanControl { get; private set; }
    public IReadOnlyList<Stream> Streams { get; private set; } = [];

    private WaylandPortal(DBusConnection connection) => _connection = connection;

    // MessageWriter 是 ref struct，里面记着写到哪了：只能按 ref 传，也不能跨 await，所以先同步拼好消息再发。
    private delegate void WriteArgs(ref MessageWriter writer);
    private delegate void WriteTokenArgs(ref MessageWriter writer, string token);

    private MessageBuffer Build(string? path, string iface, string method, string? signature, MessageFlags flags, WriteArgs args)
    {
        var writer = _connection.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(Bus, path ?? ObjectPath, iface, method, signature, flags);
            args(ref writer);
            return writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <summary>
    /// 建会话并请用户在系统授权框里同意（可能要等用户一会儿）。用户拒绝、门户不存在时抛 IOException。
    /// <paramref name="wantControl"/> 为 false 时只申请看。
    /// </summary>
    public static async Task<WaylandPortal> StartAsync(bool wantControl, CancellationToken ct)
    {
        var address = DBusAddress.Session ?? throw new IOException("no D-Bus session bus");
        var connection = new DBusConnection(address);
        var portal = new WaylandPortal(connection);
        try
        {
            await connection.ConnectAsync();
            await portal.StartCoreAsync(wantControl, ct);
            return portal;
        }
        catch
        {
            await portal.DisposeAsync();
            throw;
        }
    }

    private async Task StartCoreAsync(bool wantControl, CancellationToken ct)
    {
        var hasRemoteDesktop = wantControl && await HasInterfaceAsync(RemoteDesktopInterface);
        var main = hasRemoteDesktop ? RemoteDesktopInterface : ScreenCastInterface;

        var created = await RequestAsync(main, "CreateSession", (ref MessageWriter w, string token) =>
        {
            w.WriteDictionary(new Dictionary<string, VariantValue>
            {
                ["handle_token"] = VariantValue.String(token),
                ["session_handle_token"] = VariantValue.String("jay" + NextToken()),
            });
        }, "a{sv}", ct);
        _session = created.TryGetValue("session_handle", out var handle) ? handle.GetString() : throw new IOException("portal returned no session");

        if (hasRemoteDesktop)
        {
            await RequestAsync(RemoteDesktopInterface, "SelectDevices", (ref MessageWriter w, string token) =>
            {
                w.WriteObjectPath(_session);
                w.WriteDictionary(new Dictionary<string, VariantValue>
                {
                    ["handle_token"] = VariantValue.String(token),
                    ["types"] = VariantValue.UInt32(1 | 2), // 键盘 | 鼠标
                });
            }, "oa{sv}", ct);
        }

        await RequestAsync(ScreenCastInterface, "SelectSources", (ref MessageWriter w, string token) =>
        {
            w.WriteObjectPath(_session);
            w.WriteDictionary(new Dictionary<string, VariantValue>
            {
                ["handle_token"] = VariantValue.String(token),
                ["types"] = VariantValue.UInt32(1),       // 整块显示器
                ["multiple"] = VariantValue.Bool(true),   // 多块显示器都要
                ["cursor_mode"] = VariantValue.UInt32(2), // 鼠标指针画进画面里
            });
        }, "oa{sv}", ct);

        var started = await RequestAsync(main, "Start", (ref MessageWriter w, string token) =>
        {
            w.WriteObjectPath(_session);
            w.WriteString("");
            w.WriteDictionary(new Dictionary<string, VariantValue> { ["handle_token"] = VariantValue.String(token) });
        }, "osa{sv}", ct);

        CanControl = hasRemoteDesktop && started.TryGetValue("devices", out var devices) && (devices.GetUInt32() & 2) != 0;
        if (!started.TryGetValue("streams", out var streams)) throw new IOException("portal returned no streams");
        Streams = ParseStreams(streams);
        if (Streams.Count == 0) throw new IOException("portal returned no streams");
    }

    /// <summary>streams 是 a(ua{sv})：节点号 + 属性（position (ii)、size (ii)）。没有位置的按从左到右排。</summary>
    private static List<Stream> ParseStreams(VariantValue value)
    {
        var list = new List<Stream>();
        var nextX = 0;
        for (var i = 0; i < value.Count; i++)
        {
            var item = value.GetItem(i);
            var node = item.GetItem(0).GetUInt32();
            var props = item.GetItem(1);
            int x = nextX, y = 0, w = 0, h = 0;
            for (var j = 0; j < props.Count; j++)
            {
                var entry = props.GetDictionaryEntry(j);
                var name = entry.Key.GetString();
                var v = entry.Value.Type == VariantValueType.Variant ? entry.Value.GetVariantValue() : entry.Value;
                if (name == "position") (x, y) = (v.GetItem(0).GetInt32(), v.GetItem(1).GetInt32());
                else if (name == "size") (w, h) = (v.GetItem(0).GetInt32(), v.GetItem(1).GetInt32());
            }
            list.Add(new Stream(node, x, y, w, h));
            nextX = x + w;
        }
        return list;
    }

    /// <summary>连接 PipeWire 的文件描述符（只能看到这次会话共享的屏幕）。调用方负责关闭。</summary>
    public async Task<SafeFileHandle> OpenPipeWireRemote()
    {
        var message = Build(null, ScreenCastInterface, "OpenPipeWireRemote", "oa{sv}", MessageFlags.None, (ref MessageWriter w) =>
        {
            w.WriteObjectPath(_session);
            w.WriteDictionary(new Dictionary<string, VariantValue>());
        });
        return await _connection.CallMethodAsync(message, static (m, _) => m.GetBodyReader().ReadHandle<SafeFileHandle>() ?? throw new IOException("portal returned no PipeWire fd"), null);
    }

    // ---- 键鼠（RemoteDesktop 门户）。坐标是某块屏幕里的逻辑像素；按键用 evdev 码。----

    public void PointerMotion(uint stream, double x, double y) =>
        Notify("NotifyPointerMotionAbsolute", "oa{sv}udd", (ref MessageWriter w) => { w.WriteUInt32(stream); w.WriteDouble(x); w.WriteDouble(y); });

    public void PointerButton(int evdevButton, bool pressed) =>
        Notify("NotifyPointerButton", "oa{sv}iu", (ref MessageWriter w) => { w.WriteInt32(evdevButton); w.WriteUInt32(pressed ? 1u : 0u); });

    /// <summary>axis 0 竖直、1 水平；steps 正数往下 / 往右（和 Windows 的滚轮方向相反）。</summary>
    public void PointerAxisDiscrete(uint axis, int steps) =>
        Notify("NotifyPointerAxisDiscrete", "oa{sv}ui", (ref MessageWriter w) => { w.WriteUInt32(axis); w.WriteInt32(steps); });

    public void KeyboardKeycode(int evdevKey, bool pressed) =>
        Notify("NotifyKeyboardKeycode", "oa{sv}iu", (ref MessageWriter w) => { w.WriteInt32(evdevKey); w.WriteUInt32(pressed ? 1u : 0u); });

    private void Notify(string method, string signature, WriteArgs args)
    {
        if (!CanControl || _session.Length == 0) return;
        // 不要回复：键鼠事件很多，等回复会拖慢；出错（会话已结束）时下一次截屏会发现。
        var message = Build(null, RemoteDesktopInterface, method, signature, MessageFlags.NoReplyExpected, (ref MessageWriter w) =>
        {
            w.WriteObjectPath(_session);
            w.WriteDictionary(new Dictionary<string, VariantValue>());
            args(ref w);
        });
        _connection.TrySendMessage(message);
    }

    // ---- 门户的“请求”模式：先订阅 Request 对象的 Response 信号，再调方法，等用户在系统框里点了以后的结果。----

    private string NextToken() => "t" + Interlocked.Increment(ref _token) + "_" + Environment.ProcessId;

    private async Task<Dictionary<string, VariantValue>> RequestAsync(string iface, string method, WriteTokenArgs args, string signature, CancellationToken ct)
    {
        var token = "jay" + NextToken();
        var sender = (_connection.UniqueName ?? throw new IOException("D-Bus not connected")).TrimStart(':').Replace('.', '_');
        var requestPath = $"/org/freedesktop/portal/desktop/request/{sender}/{token}";
        var response = new TaskCompletionSource<(uint Code, Dictionary<string, VariantValue> Results)>(TaskCreationOptions.RunContinuationsAsynchronously);
        // 这个重载在 0.94 标了过时（新重载要 Notification<T>），但行为一样且简单；升级 Tmds.DBus.Protocol 时再换。
#pragma warning disable CS0618
        using var watch = await _connection.WatchSignalAsync(Bus, requestPath, "org.freedesktop.portal.Request", "Response",
            static (m, _) =>
            {
                var reader = m.GetBodyReader();
                var code = reader.ReadUInt32();
                return (code, reader.ReadDictionaryOfStringToVariantValue());
            },
            (Exception? e, (uint Code, Dictionary<string, VariantValue> Results) value) =>
            {
                if (e is not null) response.TrySetException(e);
                else response.TrySetResult(value);
            }, null, false, ObserverFlags.None);
#pragma warning restore CS0618

        var message = Build(null, iface, method, signature, MessageFlags.None, (ref MessageWriter w) => args(ref w, token));
        await _connection.CallMethodAsync(message, static (m, _) => m.GetBodyReader().ReadObjectPathAsString(), null);
        using var registration = ct.Register(() => response.TrySetCanceled(ct));
        var (code, results) = await response.Task;
        // 0 成功；1 用户取消；2 其他错误。
        if (code != 0) throw new IOException(code == 1 ? $"portal {method}: cancelled by user" : $"portal {method}: failed ({code})");
        return results;
    }

    private async Task<bool> HasInterfaceAsync(string iface)
    {
        try
        {
            var message = Build(null, "org.freedesktop.DBus.Properties", "Get", "ss", MessageFlags.None, (ref MessageWriter w) =>
            {
                w.WriteString(iface);
                w.WriteString("version");
            });
            var version = await _connection.CallMethodAsync(message, static (m, _) => m.GetBodyReader().ReadVariantValue(), null);
            return version.Type != VariantValueType.Invalid;
        }
        catch (DBusErrorReplyException) { return false; }
    }

    public async ValueTask DisposeAsync()
    {
        if (_session.Length > 0)
        {
            try
            {
                _connection.TrySendMessage(Build(_session, "org.freedesktop.portal.Session", "Close", null, MessageFlags.NoReplyExpected, static (ref MessageWriter _) => { }));
                await Task.Delay(50);
            }
            catch (Exception e) when (e is DBusExceptionBase or ObjectDisposedException) { }
        }
        _connection.Dispose();
    }
}

/// <summary>开关文件描述符的 close-on-exec 标志：清掉时子进程（gst-launch）能继承它。</summary>
[SupportedOSPlatform("linux")]
internal static class FdInherit
{
    [DllImport("libc", SetLastError = true)] private static extern int fcntl(int fd, int cmd, int arg);
    private const int F_GETFD = 1, F_SETFD = 2, FD_CLOEXEC = 1;

    public static bool Set(SafeHandle handle, bool inherit)
    {
        var fd = (int)handle.DangerousGetHandle();
        var flags = fcntl(fd, F_GETFD, 0);
        return flags >= 0 && fcntl(fd, F_SETFD, inherit ? flags & ~FD_CLOEXEC : flags | FD_CLOEXEC) == 0;
    }
}
