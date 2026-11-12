using Avalonia;
using Avalonia.Media;

namespace Amber.App;

internal static class Program
{
    private static System.Runtime.InteropServices.PosixSignalRegistration? _ignoreHangUp;

    [STAThread]
    public static void Main(string[] args)
    {
        // Mac/Linux 完整局域网模式的 root 守护（经 pkexec / 管理员密码框启动），不开窗口。
        if (!OperatingSystem.IsWindows() && Amber.Core.Multiplayer.UnixElevatedGuardHost.IsGuardInvocation(args))
        {
            LinuxStartup.EnsureIcu(args);
            Environment.Exit(Amber.Core.Multiplayer.UnixElevatedGuardHost.Run(args));
        }
        // Windows strong sandbox: the one admin step (loopback exemption, parent folders), started by Jay itself with runas.
        if (OperatingSystem.IsWindows() && Amber.Core.Launch.WindowsSandboxSetup.IsInvocation(args))
            Environment.Exit(Amber.Core.Launch.WindowsSandboxSetup.Run(args));
        // Windows full LAN guard: Jay itself, started elevated through the admin prompt. No window.
        if (OperatingSystem.IsWindows() && Amber.Core.Multiplayer.WindowsElevatedGuardHost.IsGuardInvocation(args))
            Environment.Exit(Amber.Core.Multiplayer.WindowsElevatedGuardHost.Run(args));
        // Linux：先确认 ICU 和图形界面要用的库都在（缺了给出看得懂的提示）。必须排在任何区域、格式化代码之前。
        LinuxStartup.BeforeAnything(args);
        // Local-only breadcrumbs for exits that leave no crash report or console output (see ExitTrail).
        Amber.Core.Diagnostics.ExitTrail.Start(Services.AppSettings.DataDir);
        // A GUI app has no terminal to hang up. macOS sends SIGHUP to a whole orphaned process group when a member is
        // stopped, and Jay shares its group with EasyTier: don't let that end Jay (#229).
        if (!OperatingSystem.IsWindows())
            _ignoreHangUp = System.Runtime.InteropServices.PosixSignalRegistration.Create(
                System.Runtime.InteropServices.PosixSignal.SIGHUP, c => c.Cancel = true);
        StartupTrace.Mark("main");
        // 无人值守的开发运行（自检、截图、布局检查）：崩了写 crash.txt 并退出，不弹对话框。
        DevCrashGuard.InstallIfUnattended();
        // 开发用：伪本地化，检查长译文和漏翻的文字。
        if (args.Contains("--pseudo-loc") || Environment.GetEnvironmentVariable("AMBER_PSEUDO_LOC") == "1")
            Amber.Core.Localization.L.Pseudo = true;
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (DllNotFoundException e) when (LinuxStartup.TryReportStartupFailure(e))
        {
            Environment.Exit(1);
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
        // Linux 发行版不一定装了中文字体，Linux 版自带一份思源黑体（Noto Sans SC）做回退，中文、日文假名不会变成方框。
        if (OperatingSystem.IsLinux() && BundledCjkFont.Included)
            builder = builder.With(new FontManagerOptions
            {
                // 界面字体列表里的字体一个都没有时就用它（它也带拉丁字母）；其他字体缺字时也先找它。
                DefaultFamilyName = BundledCjkFont.Uri,
                FontFallbacks = [new FontFallback { FontFamily = new FontFamily(BundledCjkFont.Uri) }],
            });
        return builder;
    }
}

/// <summary>开发用：AMBER_STARTUP_LOG=文件，记录启动各阶段距进程启动的毫秒数。没设置时什么都不做。</summary>
internal static class StartupTrace
{
    private static readonly string? LogPath = Environment.GetEnvironmentVariable("AMBER_STARTUP_LOG") is { Length: > 0 } path ? path : null;
    private static readonly System.Text.StringBuilder Marks = new();

    public static bool Enabled => LogPath is not null;

    /// <summary>
    /// 单独记启动耗时的时候窗口打开就关掉。和自检、截图、布局检查这些一起用时不关，
    /// 不然它们会在窗口刚打开时就被这里结束（看起来像是“启动两秒后无声退出”）。
    /// </summary>
    public static bool CloseWhenOpened => Enabled && !new[] { "AMBER_SELFTEST", "AMBER_LAYOUTCHECK", "AMBER_SPIKE", "AMBER_SNAPSHOT_DIR", "AMBER_MEASURE" }
        .Any(name => Environment.GetEnvironmentVariable(name) is { Length: > 0 });

    public static void Mark(string name)
    {
        if (LogPath is null) return;
        var elapsed = DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime;
        Marks.Append($" {name}={elapsed.TotalMilliseconds:F0}");
    }

    public static void Flush()
    {
        if (LogPath is not null) File.AppendAllText(LogPath, Marks.ToString().Trim() + "\n");
    }
}

/// <summary>Linux 版自带的中文字体（只在发布 linux-* 时打包进去，其他系统用系统字体）。</summary>
internal static class BundledCjkFont
{
    public const string Uri = "avares://Jay/Assets/Fonts#Noto Sans SC";

    /// <summary>编译时定下：项目文件只在打包了字体时定义 JAY_BUNDLED_CJK_FONT（这时 AssetLoader 还没初始化，没法在运行时查）。</summary>
#if JAY_BUNDLED_CJK_FONT
    public static bool Included => true;
#else
    public static bool Included => false;
#endif
}
