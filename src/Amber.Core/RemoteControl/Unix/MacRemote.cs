using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Amber.Core.RemoteControl.Unix;

/// <summary>
/// macOS：截屏用 CoreGraphics 截所有显示器拼起来的区域（按“点”的分辨率，和鼠标坐标同一套），注入用 CGEvent。
/// 系统要求用户授权：截屏要“屏幕录制”，注入要“辅助功能”。没授权时截到的只有桌面背景、注入没反应，
/// 所以先查权限，没有就请系统弹授权提示并返回 null（这次按不支持拒绝，授权后重新打开 Jay 再试）。
/// 这些函数里用到的结构（CGRect / CGPoint）都是纯 double，按值传递在 arm64 和 x64 上都没问题；可变参数的函数一个都不用。
/// </summary>
[SupportedOSPlatform("macos")]
internal static class MacCg
{
    private const string CoreGraphics = Interop.MacFrameworks.CoreGraphics;
    private const string CoreFoundation = Interop.MacFrameworks.CoreFoundation;
    private const string ApplicationServices = Interop.MacFrameworks.ApplicationServices;

    [StructLayout(LayoutKind.Sequential)] public struct CGPoint { public double X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct CGRect { public double X, Y, Width, Height; }

    [DllImport(CoreGraphics)] public static extern int CGGetActiveDisplayList(uint max, [Out] uint[]? displays, out uint count);
    [DllImport(CoreGraphics)] public static extern CGRect CGDisplayBounds(uint display);
    [DllImport(CoreGraphics)] public static extern nint CGWindowListCreateImage(CGRect bounds, uint listOption, uint windowId, uint imageOption);
    [DllImport(CoreGraphics)] public static extern nuint CGImageGetWidth(nint image);
    [DllImport(CoreGraphics)] public static extern nuint CGImageGetHeight(nint image);
    [DllImport(CoreGraphics)] public static extern void CGImageRelease(nint image);
    [DllImport(CoreGraphics)] public static extern nint CGColorSpaceCreateDeviceRGB();
    [DllImport(CoreGraphics)] public static extern void CGColorSpaceRelease(nint space);
    [DllImport(CoreGraphics)] public static extern nint CGBitmapContextCreate(nint data, nuint width, nuint height, nuint bitsPerComponent, nuint bytesPerRow, nint space, uint bitmapInfo);
    [DllImport(CoreGraphics)] public static extern void CGContextDrawImage(nint context, CGRect rect, nint image);
    [DllImport(CoreGraphics)] public static extern void CGContextRelease(nint context);

    [DllImport(CoreGraphics)] public static extern nint CGEventCreate(nint source);
    [DllImport(CoreGraphics)] public static extern CGPoint CGEventGetLocation(nint ev);
    [DllImport(CoreGraphics)] public static extern nint CGEventCreateMouseEvent(nint source, uint type, CGPoint position, uint button);
    [DllImport(CoreGraphics)] public static extern nint CGEventCreateKeyboardEvent(nint source, ushort key, [MarshalAs(UnmanagedType.U1)] bool down);
    /// <summary>macOS 13 起才有的不定参数版本的替代（CGEventCreateScrollWheelEvent 是可变参数，arm64 上不能这样调）。</summary>
    [DllImport(CoreGraphics)] public static extern nint CGEventCreateScrollWheelEvent2(nint source, uint units, uint wheelCount, int wheel1, int wheel2, int wheel3);
    [DllImport(CoreGraphics)] public static extern void CGEventSetFlags(nint ev, ulong flags);
    [DllImport(CoreGraphics)] public static extern void CGEventSetIntegerValueField(nint ev, uint field, long value);
    [DllImport(CoreGraphics)] public static extern void CGEventPost(uint tap, nint ev);

    [DllImport(CoreGraphics)] [return: MarshalAs(UnmanagedType.U1)] public static extern bool CGPreflightScreenCaptureAccess();
    [DllImport(CoreGraphics)] [return: MarshalAs(UnmanagedType.U1)] public static extern bool CGRequestScreenCaptureAccess();
    [DllImport(ApplicationServices)] [return: MarshalAs(UnmanagedType.U1)] public static extern bool AXIsProcessTrusted();

    [DllImport(CoreFoundation)] public static extern void CFRelease(nint obj);

    public const uint OnScreenOnly = 1;            // kCGWindowListOptionOnScreenOnly
    public const uint NominalResolution = 1 << 4;  // kCGWindowImageNominalResolution：按点截，Retina 屏不翻倍
    public const uint AlphaPremultipliedFirst = 2;
    public const uint ByteOrder32Little = 2 << 12; // 和上面合起来就是内存里的 B G R A
    public const uint HidEventTap = 0;             // kCGHIDEventTap
    public const uint ScrollUnitLine = 1;
    public const uint MouseClickState = 1;         // kCGMouseEventClickState

    /// <summary>所有在用显示器拼起来的矩形（全局坐标，主屏左上角是原点，单位是点）。</summary>
    public static CGRect Desktop()
    {
        if (CGGetActiveDisplayList(0, null, out var count) != 0 || count == 0) return default;
        var ids = new uint[count];
        if (CGGetActiveDisplayList(count, ids, out count) != 0) return default;
        double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
        foreach (var id in ids.AsSpan(0, (int)count))
        {
            var r = CGDisplayBounds(id);
            left = Math.Min(left, r.X);
            top = Math.Min(top, r.Y);
            right = Math.Max(right, r.X + r.Width);
            bottom = Math.Max(bottom, r.Y + r.Height);
        }
        return new CGRect { X = left, Y = top, Width = right - left, Height = bottom - top };
    }

    /// <summary>打开系统设置的对应隐私页（没有权限时给用户指路）。</summary>
    public static void OpenPrivacyPane(string anchor)
    {
        try { Process.Start(new ProcessStartInfo("open", $"x-apple.systempreferences:com.apple.preference.security?{anchor}") { UseShellExecute = false })?.Dispose(); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    private static int _screenRequested;
    private static int _accessibilityRequested;

    /// <summary>有屏幕录制权限；没有时第一次会请系统弹授权提示（每次启动只弹一次）。</summary>
    public static bool EnsureScreenAccess()
    {
        if (CGPreflightScreenCaptureAccess()) return true;
        if (Interlocked.Exchange(ref _screenRequested, 1) == 0 && !CGRequestScreenCaptureAccess()) OpenPrivacyPane("Privacy_ScreenCapture");
        return false;
    }

    /// <summary>有辅助功能权限（注入鼠标键盘）；没有时第一次打开系统设置的辅助功能页。</summary>
    public static bool EnsureAccessibility()
    {
        if (AXIsProcessTrusted()) return true;
        if (Interlocked.Exchange(ref _accessibilityRequested, 1) == 0) OpenPrivacyPane("Privacy_Accessibility");
        return false;
    }
}

/// <summary>macOS 截屏：所有显示器，按点的分辨率。坐标和 CGEvent 的鼠标坐标是同一套。</summary>
[SupportedOSPlatform("macos")]
public sealed class MacScreenSource : IScreenSource
{
    private readonly nint _colorSpace = MacCg.CGColorSpaceCreateDeviceRGB();

    public ScreenBounds Bounds
    {
        get
        {
            var r = MacCg.Desktop();
            return new((int)Math.Floor(r.X), (int)Math.Floor(r.Y), (int)Math.Ceiling(r.Width), (int)Math.Ceiling(r.Height));
        }
    }

    public bool TryCapture(ScreenFrame frame)
    {
        var desktop = MacCg.Desktop();
        if (desktop.Width <= 0 || desktop.Height <= 0) return false;
        var image = MacCg.CGWindowListCreateImage(desktop, MacCg.OnScreenOnly, 0, MacCg.NominalResolution);
        if (image == 0) return false;
        try
        {
            var w = (int)MacCg.CGImageGetWidth(image);
            var h = (int)MacCg.CGImageGetHeight(image);
            if (w <= 0 || h <= 0) return false;
            frame.Resize(w, h);
            var handle = GCHandle.Alloc(frame.Pixels, GCHandleType.Pinned);
            try
            {
                var context = MacCg.CGBitmapContextCreate(handle.AddrOfPinnedObject(), (nuint)w, (nuint)h, 8, (nuint)(w * 4), _colorSpace,
                    MacCg.AlphaPremultipliedFirst | MacCg.ByteOrder32Little);
                if (context == 0) return false;
                MacCg.CGContextDrawImage(context, new MacCg.CGRect { Width = w, Height = h }, image);
                MacCg.CGContextRelease(context);
            }
            finally
            {
                handle.Free();
            }
            return true;
        }
        finally
        {
            MacCg.CGImageRelease(image);
        }
    }

    public (int X, int Y)? CursorPosition()
    {
        var ev = MacCg.CGEventCreate(0);
        if (ev == 0) return null;
        try
        {
            var p = MacCg.CGEventGetLocation(ev);
            return ((int)Math.Round(p.X), (int)Math.Round(p.Y));
        }
        finally
        {
            MacCg.CFRelease(ev);
        }
    }

    public void Dispose()
    {
        if (_colorSpace != 0) MacCg.CGColorSpaceRelease(_colorSpace);
    }
}

/// <summary>macOS 注入：CGEvent 发到 HID 层。按住键时拖动要发“拖动”事件；修饰键要自己记着，带在后面每个键事件上。</summary>
[SupportedOSPlatform("macos")]
public sealed class MacInputSink : IInputSink
{
    private readonly object _lock = new();
    private RemoteMouseButton _held;
    private ulong _flags;
    private readonly HashSet<ushort> _modifiersDown = [];
    private readonly ClickCounter _clicks = new();

    public void Mouse(RemoteMouseAction action, RemoteMouseButton button, int desktopX, int desktopY, int delta)
    {
        lock (_lock)
        {
            var at = new MacCg.CGPoint { X = desktopX, Y = desktopY };
            switch (action)
            {
                case RemoteMouseAction.Move:
                    Post(MacCg.CGEventCreateMouseEvent(0, MouseEvents.Move(_held), at, MouseEvents.ButtonNumber(_held)));
                    break;
                case RemoteMouseAction.Down when button != RemoteMouseButton.None:
                    _held = button;
                    Post(MacCg.CGEventCreateMouseEvent(0, MouseEvents.Down(button), at, MouseEvents.ButtonNumber(button)),
                        clickState: _clicks.Down(button, desktopX, desktopY, Environment.TickCount64));
                    break;
                case RemoteMouseAction.Up when button != RemoteMouseButton.None:
                    if (_held == button) _held = RemoteMouseButton.None;
                    Post(MacCg.CGEventCreateMouseEvent(0, MouseEvents.Up(button), at, MouseEvents.ButtonNumber(button)), clickState: _clicks.Up(button));
                    break;
                case RemoteMouseAction.Wheel or RemoteMouseAction.HWheel when delta != 0:
                    var lines = RemoteKeyCodes.WheelNotches(delta) * Math.Sign(delta);
                    try
                    {
                        // 第一个轮是竖直（正数往上，和 Windows 一致），第二个是水平（正数往左，Windows 的正数是往右）。
                        Post(action == RemoteMouseAction.Wheel
                            ? MacCg.CGEventCreateScrollWheelEvent2(0, MacCg.ScrollUnitLine, 1, lines, 0, 0)
                            : MacCg.CGEventCreateScrollWheelEvent2(0, MacCg.ScrollUnitLine, 2, 0, -lines, 0));
                    }
                    catch (EntryPointNotFoundException) { } // macOS 12 及更早没有这个函数：滚轮不转发
                    break;
            }
        }
    }

    public void Key(ushort scanCode, bool up, bool extended)
    {
        if (RemoteKeyCodes.Mac(scanCode, extended) is not { } key) return;
        lock (_lock)
        {
            var flag = RemoteKeyCodes.MacModifierFlag(key);
            if (flag != 0)
            {
                if (up) _modifiersDown.Remove(key);
                else _modifiersDown.Add(key);
                _flags = 0;
                foreach (var k in _modifiersDown) _flags |= RemoteKeyCodes.MacModifierFlag(k);
            }
            var ev = MacCg.CGEventCreateKeyboardEvent(0, key, !up);
            if (ev == 0) return;
            MacCg.CGEventSetFlags(ev, _flags);
            Post(ev);
        }
    }

    private static void Post(nint ev, int clickState = 0)
    {
        if (ev == 0) return;
        if (clickState > 0) MacCg.CGEventSetIntegerValueField(ev, MacCg.MouseClickState, clickState);
        MacCg.CGEventPost(MacCg.HidEventTap, ev);
        MacCg.CFRelease(ev);
    }

    public void Dispose() { }
}

/// <summary>
/// The click count macOS reads from an injected mouse event (double-click, triple-click): another press of the same
/// button within the double-click time and a few pixels of the last one counts up, anything else starts at 1. The
/// release carries the count of its press.
/// </summary>
internal sealed class ClickCounter
{
    /// <summary>macOS's default double-click interval.</summary>
    public const long IntervalMs = 500;
    public const int Slop = 4;
    public const int Max = 3;

    private RemoteMouseButton _button;
    private int _x, _y, _count;
    private long _at;

    public int Down(RemoteMouseButton button, int x, int y, long nowMs)
    {
        var again = _count > 0 && button == _button && nowMs - _at <= IntervalMs && Math.Abs(x - _x) <= Slop && Math.Abs(y - _y) <= Slop;
        _count = again ? Math.Min(_count + 1, Max) : 1;
        (_button, _x, _y, _at) = (button, x, y, nowMs);
        return _count;
    }

    public int Up(RemoteMouseButton button) => button == _button && _count > 0 ? _count : 1;
}

/// <summary>CGEvent 的鼠标事件类型和按键编号（单独放出来方便测试）。</summary>
internal static class MouseEvents
{
    public static uint Down(RemoteMouseButton b) => b switch { RemoteMouseButton.Left => 1u, RemoteMouseButton.Right => 3u, _ => 25u };
    public static uint Up(RemoteMouseButton b) => b switch { RemoteMouseButton.Left => 2u, RemoteMouseButton.Right => 4u, _ => 26u };
    /// <summary>没按键是“移动”，按着键是对应的“拖动”。</summary>
    public static uint Move(RemoteMouseButton held) => held switch { RemoteMouseButton.Left => 6u, RemoteMouseButton.Right => 7u, RemoteMouseButton.Middle => 27u, _ => 5u };
    public static uint ButtonNumber(RemoteMouseButton b) => b switch { RemoteMouseButton.Right => 1u, RemoteMouseButton.Middle => 2u, _ => 0u };
}
