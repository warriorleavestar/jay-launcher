namespace Amber.Core;

/// <summary>用于界面和测试在用户操作前说清平台限制；后续补齐某项时只改这里的能力判定。</summary>
public static class PlatformFeatures
{
    /// <summary>完整局域网模式：Windows 经 UAC，Linux 经 pkexec，macOS 经系统管理员密码框。Linux 没有 pkexec（也不是 root）时不能用。</summary>
    public static bool FullLan => FullLanUnavailableReason is null;

    /// <summary>不能用完整局域网模式的原因（已翻译）；能用时为 null。</summary>
    public static string? FullLanUnavailableReason => OperatingSystem.IsWindows() ? null : Multiplayer.UnixElevation.UnavailableReason();

    /// <summary>经别人上网（消费出口）：接管系统路由、DNS 和防漏规则。Windows 用 WFP；Linux 要 ip + nftables，macOS 用 route + pf + scutil。</summary>
    public static bool FullTrafficExit => OperatingSystem.IsWindows() || FullLan && Multiplayer.UnixExitNetwork.Supported;
    public static bool RemoteControl => OperatingSystem.IsWindows();
}
