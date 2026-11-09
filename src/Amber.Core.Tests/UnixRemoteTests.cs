using System.Diagnostics;
using Amber.Core.RemoteControl;
using Amber.Core.RemoteControl.Unix;

namespace Amber.Core.Tests;

/// <summary>Mac/Linux 远程控制：按键码对照表、鼠标事件类型，以及（装了 Xvfb 的 Linux 上）真实的 X11 截屏和注入。</summary>
public class UnixRemoteTests
{
    // 控制方（App 的 RemoteKeyMap）会发的全部扫描码。
    private static readonly ushort[] Basic = [.. Enumerable.Range(0x01, 0x53).Select(i => (ushort)i), 0x56, 0x57, 0x58];
    private static readonly ushort[] Extended = [0x1C, 0x1D, 0x35, 0x37, 0x38, 0x47, 0x48, 0x49, 0x4B, 0x4D, 0x4F, 0x50, 0x51, 0x52, 0x53, 0x5B, 0x5C, 0x5D];

    [Fact]
    public void X11_EveryKeyHasAKeycode()
    {
        foreach (var s in Basic.Where(s => s != 0x54 && s != 0x55)) Assert.Equal(s + 8u, RemoteKeyCodes.X11(s, false));
        foreach (var s in Extended) Assert.NotNull(RemoteKeyCodes.X11(s, true));
        Assert.Equal(111u + 8, RemoteKeyCodes.X11(0x53, true)); // Delete
        Assert.Equal(103u + 8, RemoteKeyCodes.X11(0x48, true)); // 上
        Assert.Equal(125u + 8, RemoteKeyCodes.X11(0x5B, true)); // 左 Win（Super）
        Assert.Null(RemoteKeyCodes.X11(0x7F, false));
        Assert.Null(RemoteKeyCodes.X11(0x10, true));
    }

    [Fact]
    public void Mac_KeysMapToDistinctVirtualKeys()
    {
        var seen = new Dictionary<ushort, string>();
        foreach (var (s, ext) in Basic.Select(s => (s, false)).Concat(Extended.Select(s => (s, true))))
        {
            if (RemoteKeyCodes.Mac(s, ext) is not { } k)
            {
                // Mac 键盘上没有的：ScrollLock、PrintScreen（和没定义的 0x54/0x55）。
                Assert.Contains((s, ext), new[] { ((ushort)0x46, false), ((ushort)0x37, true), ((ushort)0x54, false), ((ushort)0x55, false) });
                continue;
            }
            Assert.True(seen.TryAdd(k, $"{s:X2}{(ext ? "e" : "")}"), $"0x{k:X2} 重复：{seen.GetValueOrDefault(k)} 和 {s:X2}");
        }
        Assert.Equal((ushort)0x00, RemoteKeyCodes.Mac(0x1E, false)); // A
        Assert.Equal((ushort)0x24, RemoteKeyCodes.Mac(0x1C, false)); // 回车
        Assert.Equal((ushort)0x37, RemoteKeyCodes.Mac(0x5B, true));  // Win → Command
        Assert.Equal(0x100000ul, RemoteKeyCodes.MacModifierFlag(0x37));
        Assert.Equal(0x20000ul, RemoteKeyCodes.MacModifierFlag(RemoteKeyCodes.Mac(0x36, false)!.Value));
        Assert.Equal(0ul, RemoteKeyCodes.MacModifierFlag(0x00));
    }

    [Fact]
    public void Mac_MouseEventTypes()
    {
        Assert.Equal(1u, MouseEvents.Down(RemoteMouseButton.Left));
        Assert.Equal(4u, MouseEvents.Up(RemoteMouseButton.Right));
        Assert.Equal(25u, MouseEvents.Down(RemoteMouseButton.Middle));
        Assert.Equal(5u, MouseEvents.Move(RemoteMouseButton.None));
        Assert.Equal(6u, MouseEvents.Move(RemoteMouseButton.Left)); // 按着左键移动是拖动
        Assert.Equal(2u, MouseEvents.ButtonNumber(RemoteMouseButton.Middle));
    }

    [Theory]
    [InlineData(120, 1)]
    [InlineData(-240, 2)]
    [InlineData(30, 1)]
    [InlineData(100000, 10)]
    public void WheelNotches(int delta, int notches) => Assert.Equal(notches, RemoteKeyCodes.WheelNotches(delta));

    [Fact]
    public void Platform_ReportsSupportPerOs()
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()) Assert.Null(RemotePlatform.UnavailableReason);
        else Assert.Equal(RemotePlatform.UnavailableReason is null, RemotePlatform.Supported);
    }

    /// <summary>真实的 X 服务器（Xvfb）：截到的尺寸对、注入的鼠标位置能读回来、按键和滚轮不报错。没有 Xvfb 时跳过。</summary>
    [Fact]
    public void X11_CaptureAndInjectOnXvfb()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/Xvfb")) return;
        const string display = ":93";
        using var xvfb = Process.Start(new ProcessStartInfo("/usr/bin/Xvfb", [display, "-screen", "0", "640x480x24", "-nolisten", "tcp"])
        { UseShellExecute = false, RedirectStandardError = true })!;
        try
        {
            X11ScreenSource? screen = null;
            for (var i = 0; i < 50 && screen is null; i++)
            {
                try { screen = new X11ScreenSource(display); }
                catch (InvalidOperationException) { Thread.Sleep(100); }
            }
            Assert.NotNull(screen);
            using (screen)
            using (var input = new X11InputSink(display))
            {
                Assert.Equal(new ScreenBounds(0, 0, 640, 480), screen.Bounds);
                var frame = new ScreenFrame();
                Assert.True(screen.TryCapture(frame));
                Assert.Equal(640, frame.Width);
                Assert.Equal(480, frame.Height);
                Assert.All(Enumerable.Range(0, 100).Select(i => frame.Pixels[i * 4 + 3]), a => Assert.Equal(255, a));

                input.Mouse(RemoteMouseAction.Move, RemoteMouseButton.None, 123, 321, 0);
                Assert.Equal((123, 321), screen.CursorPosition());
                input.Mouse(RemoteMouseAction.Down, RemoteMouseButton.Left, 200, 100, 0);
                input.Mouse(RemoteMouseAction.Up, RemoteMouseButton.Left, 210, 110, 0);
                Assert.Equal((210, 110), screen.CursorPosition());
                input.Mouse(RemoteMouseAction.Wheel, RemoteMouseButton.None, 210, 110, -240);
                input.Key(0x1E, up: false, extended: false);
                input.Key(0x1E, up: true, extended: false);
                input.Key(0x48, up: false, extended: true);
                input.Key(0x48, up: true, extended: true);
                Assert.True(screen.TryCapture(frame));
            }
        }
        finally
        {
            try { xvfb.Kill(); } catch (InvalidOperationException) { }
            xvfb.WaitForExit(5000);
        }
    }
}
