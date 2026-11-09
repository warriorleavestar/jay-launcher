namespace Amber.Core;

/// <summary>用于界面和测试在用户操作前说清平台限制；后续补齐某项时只改这里的能力判定。</summary>
public static class PlatformFeatures
{
    public static bool FullLan => OperatingSystem.IsWindows();
    public static bool FullTrafficExit => OperatingSystem.IsWindows();
    /// <summary>这台电脑能被远程看 / 控制：Windows、macOS、Linux 的 X11 桌面（Wayland 还不行）。看别人在哪都行。</summary>
    public static bool RemoteControl => global::Amber.Core.RemoteControl.RemotePlatform.Supported;
}
