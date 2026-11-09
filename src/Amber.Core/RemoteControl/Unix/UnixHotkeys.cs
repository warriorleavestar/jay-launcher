using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Amber.Core.RemoteControl.Unix;

/// <summary>
/// 被控期间的全局停止热键 Ctrl+Shift+F12（Mac 上是 ⌃⇧F12），和 Windows 的 <c>GlobalHotkey</c> 一样：按下时调用回调，被别的程序占了时 Registered 是 false。
/// Linux X11：XGrabKey 抢根窗口上的这个组合键（NumLock / CapsLock 开着时也要能按），自己开一条 X 连接在后台线程上收事件。
/// macOS：Carbon 的 RegisterEventHotKey（不需要辅助功能权限），必须在界面线程上注册和注销。
/// </summary>
public interface IStopHotkey : IDisposable
{
    bool Registered { get; }
}

public static class StopHotkey
{
    /// <summary>按当前系统建热键；不支持（Wayland 等）时返回 null。macOS 上要在界面线程调用。</summary>
    public static IStopHotkey? Create(Action pressed)
    {
        try
        {
            if (OperatingSystem.IsLinux()) return X11.IsWayland() || X11.UnavailableReason() is not null ? null : new X11Hotkey(pressed);
            if (OperatingSystem.IsMacOS()) return new MacHotkey(pressed);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException) { }
        return null;
    }
}

[SupportedOSPlatform("linux")]
internal sealed class X11Hotkey : IStopHotkey
{
    private const string LibX11 = "libX11.so.6";
    private const int KeyPress = 2;
    private const uint ShiftMask = 1, LockMask = 2, ControlMask = 4, Mod2Mask = 16; // Mod2 一般是 NumLock
    private const uint F12Keycode = 88 + 8; // evdev KEY_F12 + 8

    private readonly nint _display;
    private readonly nuint _root;
    private readonly Thread _thread;
    private volatile bool _disposed;

    public bool Registered { get; }

    [DllImport(LibX11)] private static extern int XGrabKey(nint display, int keycode, uint modifiers, nuint grabWindow, int ownerEvents, int pointerMode, int keyboardMode);
    [DllImport(LibX11)] private static extern int XUngrabKey(nint display, int keycode, uint modifiers, nuint grabWindow);
    [DllImport(LibX11)] private static extern int XPending(nint display);
    [DllImport(LibX11)] private static extern int XNextEvent(nint display, nint eventReturn);
    [DllImport(LibX11)] private static extern nint XSetErrorHandler(nint handler);

    private static readonly uint[] LockVariants = [0, LockMask, Mod2Mask, LockMask | Mod2Mask];
    private static int _grabFailed;
    private static readonly object GrabLock = new();

    public X11Hotkey(Action pressed) : this(pressed, null) { }

    internal X11Hotkey(Action pressed, string? displayName)
    {
        _display = X11.Open(displayName);
        if (_display == 0) throw new InvalidOperationException("XOpenDisplay failed");
        _root = X11.XDefaultRootWindow(_display);
        // 别的程序已经抢了这个组合键时 X 服务器回 BadAccess；默认的错误处理会直接结束进程，先换成只记一下的。
        // 错误处理是整个进程共用的（Avalonia 也装了自己的），抢完马上换回原来的。
        lock (GrabLock)
        {
            nint previous;
            unsafe { previous = XSetErrorHandler((nint)(delegate* unmanaged<nint, nint, int>)&OnError); }
            Volatile.Write(ref _grabFailed, 0);
            foreach (var locks in LockVariants) XGrabKey(_display, (int)F12Keycode, ControlMask | ShiftMask | locks, _root, 0, 1, 1);
            X11.XSync(_display, 0);
            XSetErrorHandler(previous);
            Registered = Volatile.Read(ref _grabFailed) == 0;
        }
        _thread = new Thread(() => Loop(pressed)) { IsBackground = true, Name = "Jay remote hotkey" };
        _thread.Start();
    }

    [UnmanagedCallersOnly]
    private static int OnError(nint display, nint error)
    {
        Volatile.Write(ref _grabFailed, 1);
        return 0;
    }

    private void Loop(Action pressed)
    {
        var ev = Marshal.AllocHGlobal(256); // XEvent 是 192 字节的联合体
        try
        {
            while (!_disposed)
            {
                if (XPending(_display) == 0)
                {
                    Thread.Sleep(50);
                    continue;
                }
                XNextEvent(_display, ev);
                if (Marshal.ReadInt32(ev) == KeyPress && !_disposed) pressed();
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ev);
            foreach (var locks in LockVariants) XUngrabKey(_display, (int)F12Keycode, ControlMask | ShiftMask | locks, _root);
            X11.XCloseDisplay(_display);
        }
    }

    /// <summary>
    /// Stops the loop and waits for it to close its X connection. Returning early would leave the thread polling a display
    /// the caller may tear down next (session end, X server restart); Xlib's default I/O error handler then exits the whole process.
    /// </summary>
    public void Dispose()
    {
        _disposed = true;
        if (Thread.CurrentThread != _thread) _thread.Join(TimeSpan.FromSeconds(2));
    }
}

[SupportedOSPlatform("macos")]
internal sealed class MacHotkey : IStopHotkey
{
    private const string Carbon = Interop.MacFrameworks.Carbon;
    private const uint KeyboardClass = 0x6B657962; // 'keyb'
    private const uint HotKeyPressed = 5;          // kEventHotKeyPressed
    private const uint ControlKey = 0x1000, ShiftKey = 0x0200;
    private const uint F12 = 0x6F;                 // kVK_F12

    [StructLayout(LayoutKind.Sequential)] private struct EventTypeSpec { public uint Class, Kind; }
    [StructLayout(LayoutKind.Sequential)] private struct EventHotKeyId { public uint Signature, Id; }

    [DllImport(Carbon)] private static extern nint GetApplicationEventTarget();
    [DllImport(Carbon)] private static extern int RegisterEventHotKey(uint keyCode, uint modifiers, EventHotKeyId id, nint target, uint options, out nint hotKey);
    [DllImport(Carbon)] private static extern int UnregisterEventHotKey(nint hotKey);
    [DllImport(Carbon)] private static extern unsafe int InstallEventHandler(nint target, delegate* unmanaged<nint, nint, nint, int> handler, uint count, EventTypeSpec* types, nint userData, out nint handlerRef);
    [DllImport(Carbon)] private static extern int RemoveEventHandler(nint handlerRef);

    private static Action? _pressed;
    private readonly nint _hotKey;
    private readonly nint _handler;

    public bool Registered { get; }

    public unsafe MacHotkey(Action pressed)
    {
        _pressed = pressed;
        var spec = new EventTypeSpec { Class = KeyboardClass, Kind = HotKeyPressed };
        if (InstallEventHandler(GetApplicationEventTarget(), &OnHotKey, 1, &spec, 0, out _handler) != 0) return;
        var id = new EventHotKeyId { Signature = 0x4A415952, Id = 1 }; // 'JAYR'
        Registered = RegisterEventHotKey(F12, ControlKey | ShiftKey, id, GetApplicationEventTarget(), 0, out _hotKey) == 0;
    }

    [UnmanagedCallersOnly]
    private static int OnHotKey(nint callRef, nint ev, nint userData)
    {
        try { _pressed?.Invoke(); }
        catch (Exception) { } // 回调不能把异常抛回 Carbon
        return 0;
    }

    public void Dispose()
    {
        if (_hotKey != 0) UnregisterEventHotKey(_hotKey);
        if (_handler != 0) RemoveEventHandler(_handler);
        _pressed = null;
    }
}
