using Amber.Core.Localization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Amber.Core.RemoteControl.Unix;

/// <summary>
/// Linux X11 桌面：截屏用 XGetImage 截整个根窗口（所有显示器拼在一起），注入用 XTest。
/// Wayland 桌面不行（XWayland 截不到原生 Wayland 窗口，也注入不了），那里要走系统门户和 PipeWire，以后补；见 <see cref="X11.UnavailableReason"/>。
/// 每个对象自己开一条 X 连接（截屏和注入在不同线程上，互不干扰）。
/// </summary>
[SupportedOSPlatform("linux")]
internal static class X11
{
    private const string LibX11 = "libX11.so.6";
    private const string LibXtst = "libXtst.so.6";
    private static readonly object InitLock = new();
    private static bool _threadsInitialized;

    private static (string? Reason, long At)? _cached;

    /// <summary>不能用时的原因（已翻译）；能用时 null。界面每秒都会问，结果缓存 10 秒（每次都要连一次 X 服务器）。</summary>
    public static string? UnavailableReason()
    {
        var now = Environment.TickCount64;
        if (_cached is { } c && now - c.At < 10_000) return c.Reason;
        var reason = Check();
        _cached = (reason, now);
        return reason;
    }

    private static string? Check()
    {
        if (IsWayland()) return L.T("core.remote.wayland");
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))) return L.T("core.remote.nodisplay");
        if (!NativeLibrary.TryLoad(LibXtst, out var xtst)) return L.F("core.remote.missinglib", "libxtst6 / libXtst");
        NativeLibrary.Free(xtst);
        try
        {
            var display = Open();
            if (display == 0) return L.T("core.remote.nodisplay");
            XCloseDisplay(display);
        }
        catch (DllNotFoundException) { return L.F("core.remote.missinglib", "libx11-6 / libX11"); }
        return null;
    }

    /// <summary>Wayland 会话：XDG_SESSION_TYPE=wayland，或者设了 WAYLAND_DISPLAY（这时 DISPLAY 往往是 XWayland）。</summary>
    internal static bool IsWayland() =>
        string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase)
        || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

    public static nint Open(string? displayName = null)
    {
        lock (InitLock)
        {
            // 多条连接在不同线程上用，Xlib 要先开线程支持（Avalonia 在 X11 上也会调，重复调用没关系）。
            if (!_threadsInitialized)
            {
                XInitThreads();
                _threadsInitialized = true;
            }
        }
        return XOpenDisplay(displayName);
    }

    [DllImport(LibX11)] public static extern int XInitThreads();
    [DllImport(LibX11)] public static extern nint XOpenDisplay(string? name);
    [DllImport(LibX11)] public static extern int XCloseDisplay(nint display);
    [DllImport(LibX11)] public static extern nuint XDefaultRootWindow(nint display);
    [DllImport(LibX11)] public static extern int XFlush(nint display);
    [DllImport(LibX11)] public static extern int XSync(nint display, int discard);

    [DllImport(LibX11)]
    public static extern int XGetGeometry(nint display, nuint drawable, out nuint root, out int x, out int y, out uint width, out uint height, out uint border, out uint depth);

    [DllImport(LibX11)] public static extern nint XGetImage(nint display, nuint drawable, int x, int y, uint width, uint height, nuint planeMask, int format);
    [DllImport(LibX11)] public static extern int XDestroyImage(nint image);

    [DllImport(LibX11)]
    public static extern int XQueryPointer(nint display, nuint window, out nuint root, out nuint child, out int rootX, out int rootY, out int winX, out int winY, out uint mask);

    [DllImport(LibXtst)] public static extern int XTestFakeMotionEvent(nint display, int screen, int x, int y, nuint delay);
    [DllImport(LibXtst)] public static extern int XTestFakeButtonEvent(nint display, uint button, int isPress, nuint delay);
    [DllImport(LibXtst)] public static extern int XTestFakeKeyEvent(nint display, uint keycode, int isPress, nuint delay);

    public const int ZPixmap = 2;

    /// <summary>XImage 结构里要用的字段（64 位和 32 位布局按指针大小算）。</summary>
    public readonly record struct ImageInfo(int Width, int Height, nint Data, int ByteOrder, int BytesPerLine, int BitsPerPixel, ulong RedMask, ulong BlueMask);

    public static ImageInfo ReadImage(nint image)
    {
        // struct XImage { int width, height, xoffset, format; char *data; int byte_order, bitmap_unit, bitmap_bit_order, bitmap_pad, depth, bytes_per_line, bits_per_pixel;
        //                 unsigned long red_mask, green_mask, blue_mask; ... }
        var p = IntPtr.Size;
        var dataOffset = 16;
        var intsOffset = dataOffset + p;
        var masksOffset = intsOffset + 7 * 4;
        if (masksOffset % p != 0) masksOffset += p - masksOffset % p;
        ulong ReadULong(int offset) => p == 8 ? (ulong)Marshal.ReadInt64(image, offset) : (uint)Marshal.ReadInt32(image, offset);
        return new ImageInfo(
            Marshal.ReadInt32(image, 0), Marshal.ReadInt32(image, 4), Marshal.ReadIntPtr(image, dataOffset),
            Marshal.ReadInt32(image, intsOffset), Marshal.ReadInt32(image, intsOffset + 20), Marshal.ReadInt32(image, intsOffset + 24),
            ReadULong(masksOffset), ReadULong(masksOffset + 2 * p));
    }
}

/// <summary>X11 截屏：整个根窗口。支持最常见的 24/32 位真彩色（内存里就是 BGRX），别的格式返回 false。</summary>
[SupportedOSPlatform("linux")]
public sealed class X11ScreenSource : IScreenSource
{
    private nint _display;
    private readonly nuint _root;

    public X11ScreenSource() : this(null) { }

    /// <summary>测试用：指定 X 显示（比如 Xvfb 的 ":93"）。</summary>
    internal X11ScreenSource(string? displayName)
    {
        _display = X11.Open(displayName);
        if (_display == 0) throw new InvalidOperationException("XOpenDisplay failed");
        _root = X11.XDefaultRootWindow(_display);
    }

    public ScreenBounds Bounds
    {
        get
        {
            if (_display == 0 || X11.XGetGeometry(_display, _root, out _, out _, out _, out var w, out var h, out _, out _) == 0) return new(0, 0, 0, 0);
            return new(0, 0, (int)w, (int)h);
        }
    }

    public bool TryCapture(ScreenFrame frame)
    {
        var b = Bounds;
        if (b.Width <= 0 || b.Height <= 0) return false;
        var image = X11.XGetImage(_display, _root, 0, 0, (uint)b.Width, (uint)b.Height, nuint.MaxValue, X11.ZPixmap);
        if (image == 0) return false;
        try
        {
            var info = X11.ReadImage(image);
            // 小端、32 位一个像素、红色在高位：内存顺序就是 B G R X。
            if (info.BitsPerPixel != 32 || info.ByteOrder != 0 || info.RedMask != 0xFF0000 || info.BlueMask != 0xFF || info.Data == 0) return false;
            frame.Resize(info.Width, info.Height);
            var row = info.Width * 4;
            for (var y = 0; y < info.Height; y++)
                Marshal.Copy(info.Data + y * info.BytesPerLine, frame.Pixels, y * row, row);
            var px = frame.Pixels;
            for (var i = 3; i < px.Length; i += 4) px[i] = 255;
            return true;
        }
        finally
        {
            X11.XDestroyImage(image);
        }
    }

    public (int X, int Y)? CursorPosition() =>
        _display != 0 && X11.XQueryPointer(_display, _root, out _, out _, out var x, out var y, out _, out _, out _) != 0 ? (x, y) : null;

    public void Dispose()
    {
        if (_display != 0) X11.XCloseDisplay(_display);
        _display = 0;
    }
}

/// <summary>X11 注入：XTest 假鼠标键盘事件，坐标是根窗口坐标（和 <see cref="X11ScreenSource.Bounds"/> 同一套）。</summary>
[SupportedOSPlatform("linux")]
public sealed class X11InputSink : IInputSink
{
    private nint _display;
    private readonly object _lock = new();

    public X11InputSink() : this(null) { }

    internal X11InputSink(string? displayName)
    {
        _display = X11.Open(displayName);
        if (_display == 0) throw new InvalidOperationException("XOpenDisplay failed");
    }

    public void Mouse(RemoteMouseAction action, RemoteMouseButton button, int desktopX, int desktopY, int delta)
    {
        lock (_lock)
        {
            if (_display == 0) return;
            X11.XTestFakeMotionEvent(_display, -1, desktopX, desktopY, 0);
            switch (action)
            {
                case RemoteMouseAction.Down or RemoteMouseAction.Up when ButtonNumber(button) is { } n:
                    X11.XTestFakeButtonEvent(_display, n, action == RemoteMouseAction.Down ? 1 : 0, 0);
                    break;
                case RemoteMouseAction.Wheel or RemoteMouseAction.HWheel when delta != 0:
                    // 4 上 5 下 6 左 7 右；Windows 的滚轮正数是往上 / 往右。
                    var wheel = action == RemoteMouseAction.Wheel ? (delta > 0 ? 4u : 5u) : (delta > 0 ? 7u : 6u);
                    for (var i = 0; i < RemoteKeyCodes.WheelNotches(delta); i++)
                    {
                        X11.XTestFakeButtonEvent(_display, wheel, 1, 0);
                        X11.XTestFakeButtonEvent(_display, wheel, 0, 0);
                    }
                    break;
            }
            X11.XFlush(_display);
        }
    }

    internal static uint? ButtonNumber(RemoteMouseButton button) => button switch
    {
        RemoteMouseButton.Left => 1,
        RemoteMouseButton.Middle => 2,
        RemoteMouseButton.Right => 3,
        _ => null,
    };

    public void Key(ushort scanCode, bool up, bool extended)
    {
        if (RemoteKeyCodes.X11(scanCode, extended) is not { } keycode) return;
        lock (_lock)
        {
            if (_display == 0) return;
            X11.XTestFakeKeyEvent(_display, keycode, up ? 0 : 1, 0);
            X11.XFlush(_display);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_display != 0) X11.XCloseDisplay(_display);
            _display = 0;
        }
    }
}
