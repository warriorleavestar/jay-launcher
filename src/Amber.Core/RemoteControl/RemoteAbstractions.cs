using Amber.Core.Localization;
namespace Amber.Core.RemoteControl;

/// <summary>被共享的那块屏幕在桌面坐标里的位置（多显示器时是整个虚拟桌面，左上角可能是负数）。</summary>
public readonly record struct ScreenBounds(int X, int Y, int Width, int Height);

/// <summary>
/// 截屏来源。Windows 用 GDI（以后换 Windows.Graphics.Capture）；Mac/Linux 由合作者按这个接口补。
/// 只在内存里用，画面从不落盘、不进日志。
/// </summary>
public interface IScreenSource : IDisposable
{
    /// <summary>当前共享区域（每次截屏前可能变：插拔显示器、改分辨率）。</summary>
    ScreenBounds Bounds { get; }

    /// <summary>截一张 BGRA 画面到 frame（尺寸不对时 frame 自己重新分配）。失败返回 false（比如锁屏时安全桌面截不到）。</summary>
    bool TryCapture(ScreenFrame frame);

    /// <summary>鼠标指针在桌面坐标里的位置；拿不到时 null。</summary>
    (int X, int Y)? CursorPosition();
}

/// <summary>
/// 注入鼠标键盘。Windows 用 SendInput；坐标是桌面坐标（和 ScreenBounds 同一套）。
/// </summary>
public interface IInputSink : IDisposable
{
    void Mouse(RemoteMouseAction action, RemoteMouseButton button, int desktopX, int desktopY, int delta);
    void Key(ushort scanCode, bool up, bool extended);
}

/// <summary>画面块的编码。App 里用 Skia 的 JPEG；Core 自带一个无损的（测试和没有图像库时用）。</summary>
public interface ITileCodec
{
    /// <summary>编码的编号（写在 Accept 里，两边对不上就断开）。</summary>
    byte Id { get; }

    /// <summary>把一块 BGRA 像素编码。quality 0–100（无损编码忽略）。</summary>
    byte[] Encode(ReadOnlySpan<byte> bgra, int width, int height, int stride, int quality);

    /// <summary>解码到目标 BGRA 缓冲（按 stride 写 width×height）。数据不对返回 false。</summary>
    bool Decode(ReadOnlySpan<byte> data, Span<byte> bgra, int width, int height, int stride);
}

/// <summary>谁在请求、请求什么（同意弹窗显示用）。名字和指纹来自握手认出来的成员 / 敲门时证明过的房主身份。</summary>
public sealed record RemoteConsentRequest(string Name, string Fingerprint);

/// <summary>被控方的决定。ViewOnly：只让看，不让控制。</summary>
public sealed record RemoteConsent(bool Accepted, bool ViewOnly)
{
    public static readonly RemoteConsent Denied = new(false, true);
}

/// <summary>
/// 同意策略：被控方怎么决定接不接受一次远程控制。可以换（以后可能加“信任此人”）；
/// 默认实现是每次都问（<see cref="AskEveryTimePolicy"/>）。
/// </summary>
public interface IRemoteConsentPolicy
{
    Task<RemoteConsent> DecideAsync(RemoteConsentRequest request, CancellationToken ct);
}

/// <summary>每次都弹窗问；30 秒不点、取消或出错都算拒绝。</summary>
public sealed class AskEveryTimePolicy(Func<RemoteConsentRequest, CancellationToken, Task<RemoteConsent>> prompt, TimeSpan? timeout = null) : IRemoteConsentPolicy
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    public TimeSpan Timeout { get; } = timeout ?? DefaultTimeout;

    public async Task<RemoteConsent> DecideAsync(RemoteConsentRequest request, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        try
        {
            var task = prompt(request, cts.Token);
            return await task.WaitAsync(cts.Token) ?? RemoteConsent.Denied;
        }
        catch (OperationCanceledException)
        {
            return RemoteConsent.Denied;
        }
    }
}

/// <summary>
/// 按当前系统选截屏和注入的实现：Windows 用 GDI + SendInput，Linux 用 X11（XGetImage + XTest），macOS 用 CoreGraphics + CGEvent。
/// 这里只管“这台电脑能不能被别人看 / 控制”；看别人、控制别人在哪个系统上都行（只要解码和显示）。
/// </summary>
public static class RemotePlatform
{
    /// <summary>这台电脑能被看 / 控制。</summary>
    public static bool Supported => UnavailableReason is null;

    /// <summary>不能被看 / 控制的原因（已翻译）；能时 null。Linux 上每次查一下（显示环境可能变），Windows 和 macOS 总是能。</summary>
    public static string? UnavailableReason =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? null
        : OperatingSystem.IsLinux() ? Unix.X11.UnavailableReason()
        : L.T("multiplayer.remote.unsupported_os");

    /// <summary>能用但还要用户在系统里授权（macOS 的屏幕录制 / 辅助功能）时的提示（已翻译）；不需要时 null。</summary>
    public static string? SetupHint
    {
        get
        {
            if (!OperatingSystem.IsMacOS()) return null;
            try
            {
                var screen = Unix.MacCg.CGPreflightScreenCaptureAccess();
                var input = Unix.MacCg.AXIsProcessTrusted();
                return screen && input ? null : L.T(screen ? "core.remote.mac_accessibility" : "core.remote.mac_permissions");
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return null; }
        }
    }

    /// <summary>截屏来源；不支持或者（macOS）还没授权时 null，调用方按“不支持”拒绝。</summary>
    public static IScreenSource? CreateScreen()
    {
        try
        {
            if (OperatingSystem.IsWindows()) return new Windows.GdiScreenSource();
            if (OperatingSystem.IsMacOS()) return Unix.MacCg.EnsureScreenAccess() ? new Unix.MacScreenSource() : null;
            if (OperatingSystem.IsLinux()) return Unix.X11.UnavailableReason() is null ? new Unix.X11ScreenSource() : null;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException) { }
        return null;
    }

    /// <summary>鼠标键盘注入；不支持或者（macOS）还没授权辅助功能时 null（这时只能看，不能控制）。</summary>
    public static IInputSink? CreateInput()
    {
        try
        {
            if (OperatingSystem.IsWindows()) return new Windows.SendInputSink();
            if (OperatingSystem.IsMacOS()) return Unix.MacCg.EnsureAccessibility() ? new Unix.MacInputSink() : null;
            if (OperatingSystem.IsLinux()) return Unix.X11.UnavailableReason() is null ? new Unix.X11InputSink() : null;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException) { }
        return null;
    }
}
