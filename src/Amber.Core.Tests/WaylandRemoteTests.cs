using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using Amber.Core.RemoteControl;
using Microsoft.Win32.SafeHandles;
using Amber.Core.RemoteControl.Unix;

namespace Amber.Core.Tests;

/// <summary>
/// Linux Wayland 被远程控制：多块屏幕的外框和坐标换算、gst-launch 参数、按键鼠标码。
/// 设了 AMBER_WAYLAND_TEST=1 且有 PipeWire 时，开两个纯色视频源测读帧和拼图；
/// 再设 AMBER_WAYLAND_PORTAL_TEST=1（真的 Wayland 桌面，背景色 #336699）时经系统门户真截一次屏。
/// 注意：无界面的 sway 1.9 + xdg-desktop-portal-wlr 0.7 在 Start 时报 “unable to receive a valid format from wlr_screencopy”，门户那一步要在真桌面上测。
/// </summary>
public class WaylandRemoteTests
{
    private static readonly WaylandPortal.Stream Left = new(41, 0, 0, 1920, 1080);
    private static readonly WaylandPortal.Stream Right = new(42, 1920, 200, 1280, 720);

    [Fact]
    public void Bounds_CoverAllScreens()
    {
        Assert.Equal(new ScreenBounds(0, 0, 0, 0), WaylandSession.BoundsOf([]));
        Assert.Equal(new ScreenBounds(0, 0, 3200, 1080), WaylandSession.BoundsOf([Left, Right]));
        Assert.Equal(new ScreenBounds(-100, -50, 900, 650), WaylandSession.BoundsOf([new(1, -100, -50, 900, 650)]));
    }

    [Fact]
    public void Locate_PicksScreenAndConvertsToLocal()
    {
        Assert.Null(WaylandSession.Locate([], 5, 5));
        Assert.Equal((41u, 100.0, 200.0), WaylandSession.Locate([Left, Right], 100, 200));
        Assert.Equal((42u, 10.0, 0.0), WaylandSession.Locate([Left, Right], 1930, 200));
        // 右边屏幕上方的空缺：找最近的一块（右屏，贴到它的上边）。
        Assert.Equal((42u, 580.0, 0.0), WaylandSession.Locate([Left, Right], 2500, 50));
        // 出了桌面：夹到边上。
        Assert.Equal((41u, 0.0, 1079.0), WaylandSession.Locate([Left, Right], -30, 5000));
    }

    [Fact]
    public void GstArgs_OneScreenDirect_ManyComposed()
    {
        var one = string.Join(' ', WaylandSession.GstArgs([Right], WaylandSession.BoundsOf([Right]), 7));
        Assert.Contains("pipewiresrc fd=7 path=42", one);
        Assert.DoesNotContain("compositor", one);
        Assert.EndsWith("video/x-raw,format=BGRx,width=1280,height=720 ! fdsink fd=1 sync=false", one);

        var both = string.Join(' ', WaylandSession.GstArgs([Left, Right], WaylandSession.BoundsOf([Left, Right]), 7));
        Assert.Contains("compositor name=desk background=black sink_0::xpos=0 sink_0::ypos=0 sink_1::xpos=1920 sink_1::ypos=200 !", both);
        Assert.Contains("width=3200,height=1080 ! fdsink fd=1", both);
        Assert.Contains("path=41 always-copy=true do-timestamp=true ! videoconvert ! videoscale ! video/x-raw,format=BGRx,width=1920,height=1080 ! desk.sink_0", both);
        Assert.EndsWith("width=1280,height=720 ! desk.sink_1", both);
    }

    [Fact]
    public void Buttons_AreEvdevCodes()
    {
        Assert.Equal(0x110, WaylandInputSink.EvdevButton(RemoteMouseButton.Left));
        Assert.Equal(0x111, WaylandInputSink.EvdevButton(RemoteMouseButton.Right));
        Assert.Equal(0x112, WaylandInputSink.EvdevButton(RemoteMouseButton.Middle));
        // 门户要 evdev 键码 = X11 键码 - 8。
        Assert.Equal(30u, RemoteKeyCodes.X11(0x1E, false) - 8); // KEY_A
    }

    [Fact]
    public void ViewOnlyScreenAsksThePortalForTheScreenOnly()
    {
        if (!OperatingSystem.IsLinux()) return;
        // Constructing a source doesn't start the portal session (that happens on the first capture).
        using var viewOnly = new WaylandScreenSource(viewOnly: true);
        Assert.False(viewOnly.WantsControl);
        using var withControl = new WaylandScreenSource();
        Assert.True(withControl.WantsControl);
    }

    [Fact]
    public void Portal_CapturesRealScreen()
    {
        if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("AMBER_WAYLAND_PORTAL_TEST") != "1") return;
        Assert.True(X11.IsWayland());
        Assert.Null(RemotePlatform.UnavailableReason);
        using var screen = RemotePlatform.CreateScreen();
        Assert.IsType<WaylandScreenSource>(screen);
        using var input = RemotePlatform.CreateInput();
        Assert.IsType<WaylandInputSink>(input);
        var frame = new ScreenFrame();
        Assert.True(screen!.TryCapture(frame), ((WaylandScreenSource)screen).LastError);
        Assert.Equal(screen.Bounds.Width, frame.Width);
        Assert.True(frame.Width > 0 && frame.Height > 0);
        // 测试环境的桌面背景是 #336699（BGRA）。
        var mid = (frame.Height / 2 * frame.Width + frame.Width / 2) * 4;
        Assert.Equal(new byte[] { 0x99, 0x66, 0x33, 0xFF }, frame.Pixels[mid..(mid + 4)]);
        // sway 只有 ScreenCast 门户：能看不能控，注入不出错就行。
        input!.Mouse(RemoteMouseAction.Move, RemoteMouseButton.None, 10, 10, 0);
        Assert.True(screen.TryCapture(frame));
    }

    /// <summary>
    /// 不经门户：开两个 PipeWire 视频源（纯色），直接连 PipeWire，看两块“屏幕”的画面按位置拼起来。
    /// 门户只是给出节点号和一个连好的描述符，这里把它换成自己连的，测的是 gst-launch 读帧和拼图这一段。
    /// </summary>
    [Fact]
    public async Task PipeWire_ComposesTwoScreens()
    {
        if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("AMBER_WAYLAND_TEST") != "1") return;
        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR")!;
        var sources = new List<Process>();
        try
        {
            var a = await Source("jaytestA", 320, 240, 0x336699, sources);
            var b = await Source("jaytestB", 160, 120, 0xCC3300, sources);
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Connect(new UnixDomainSocketEndPoint(Path.Combine(runtime, "pipewire-0")));
            using var fd = new SafeFileHandle(socket.SafeHandle.DangerousGetHandle(), ownsHandle: false);
            using var session = new WaylandSession([new(a, 0, 0, 320, 240), new(b, 320, 0, 160, 120)], fd);
            var frame = new ScreenFrame();
            Assert.True(session.TryCapture(frame), session.LastError);
            Assert.Equal((480, 240), (frame.Width, frame.Height));
            Assert.Equal(new byte[] { 0x99, 0x66, 0x33, 0xFF }, Pixel(frame, 100, 100));
            Assert.Equal(new byte[] { 0x00, 0x33, 0xCC, 0xFF }, Pixel(frame, 400, 60));
            // 右边那块下面没有屏幕：黑的。
            Assert.Equal(new byte[] { 0, 0, 0, 0xFF }, Pixel(frame, 400, 200));
            socket.Dispose();
        }
        finally
        {
            foreach (var p in sources) { try { p.Kill(); } catch (InvalidOperationException) { } p.Dispose(); }
        }
    }

    private static byte[] Pixel(ScreenFrame f, int x, int y) => f.Pixels[((y * f.Width + x) * 4)..((y * f.Width + x) * 4 + 4)];

    /// <summary>开一个纯色的 PipeWire 视频源，返回它的节点号。</summary>
    private static async Task<uint> Source(string name, int w, int h, int rgb, List<Process> started)
    {
        var info = new ProcessStartInfo("gst-launch-1.0") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-q", "videotestsrc", "is-live=true", "pattern=solid-color", $"foreground-color=0xff{rgb:x6}", "!",
            $"video/x-raw,format=BGRx,width={w},height={h},framerate=10/1", "!", "pipewiresink", "mode=provide", $"stream-properties=props,node.name={name}" })
            info.ArgumentList.Add(arg);
        started.Add(Process.Start(info)!);
        for (var i = 0; i < 100; i++)
        {
            using var dump = Process.Start(new ProcessStartInfo("pw-dump") { UseShellExecute = false, RedirectStandardOutput = true })!;
            var json = await dump.StandardOutput.ReadToEndAsync();
            foreach (var o in JsonDocument.Parse(json).RootElement.EnumerateArray())
                if (o.TryGetProperty("info", out var i2) && i2.ValueKind == JsonValueKind.Object && i2.TryGetProperty("props", out var props)
                    && props.TryGetProperty("node.name", out var n) && n.GetString() == name)
                    return o.GetProperty("id").GetUInt32();
            await Task.Delay(100);
        }
        throw new TimeoutException(name + " did not appear in PipeWire");
    }
}
