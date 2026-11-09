using System.Diagnostics;
using System.Runtime.InteropServices;
using Amber.Core.Layout;
using Avalonia;

namespace Amber.App.Services;

/// <summary>
/// 读系统的“减少动画”设置，按用户的选择算出要不要减少，并把 Motion* 时长资源换掉（官方设计 §4.7）。
/// Avalonia 没有这方面的接口：Windows 读 SPI_GETCLIENTAREAANIMATION，macOS 读 universalaccess 的 reduceMotion，
/// Linux 读 GNOME 的 enable-animations。都只在本机读，不联网。
/// </summary>
public static class SystemMotion
{
    private static bool? _system;
    private static bool _read;

    /// <summary>当前是否在减少动画（设置改了或系统读完后更新）。</summary>
    public static bool Reduced { get; private set; }

    public static event Action? Changed;

    /// <summary>系统设置：true = 系统要求减少动画；null = 读不到。读一次后缓存。</summary>
    public static bool? SystemReduces
    {
        get
        {
            if (_read) return _system;
            _read = true;
            try { _system = Read(); } catch { _system = null; }
            return _system;
        }
    }

    /// <summary>启动时调用：先按“不减少”或用户的明确选择套上，系统设置在后台读完再补一次（读系统可能要起一个进程，不卡启动）。</summary>
    public static void Start(string? mode)
    {
        Apply(MotionPreference.Normalize(mode) == MotionPreference.System ? MotionPreference.Off : mode);
        if (MotionPreference.Normalize(mode) != MotionPreference.System) return;
        _ = Task.Run(() => SystemReduces).ContinueWith(_ => Avalonia.Threading.Dispatcher.UIThread.Post(() => Apply(mode)), TaskScheduler.Default);
    }

    public static void Apply(string? mode)
    {
        var reduce = MotionPreference.ShouldReduce(mode, MotionPreference.Normalize(mode) == MotionPreference.System ? SystemReduces : null);
        if (Application.Current?.Resources is { } resources)
            foreach (var (key, ms) in MotionPreference.Durations(reduce))
                resources[key] = TimeSpan.FromMilliseconds(ms);
        var changed = reduce != Reduced;
        Reduced = reduce;
        if (changed) Changed?.Invoke();
    }

    private static bool? Read()
    {
        if (OperatingSystem.IsWindows())
        {
            // SPI_GETCLIENTAREAANIMATION：系统设置里“在 Windows 中显示动画”。关掉就是要求减少动画。
            var enabled = 1;
            return SystemParametersInfo(0x1042, 0, ref enabled, 0) ? enabled == 0 : null;
        }
        if (OperatingSystem.IsMacOS())
            return MotionPreference.ParseMacReduceMotion(Run("defaults", "read", "com.apple.universalaccess", "reduceMotion"));
        if (OperatingSystem.IsLinux())
            return MotionPreference.ParseGnomeEnableAnimations(Run("gsettings", "get", "org.gnome.desktop.interface", "enable-animations"));
        return null;
    }

    private static string? Run(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        if (p is null) return null;
        var output = p.StandardOutput.ReadToEnd();
        return p.WaitForExit(2000) && p.ExitCode == 0 ? output : null;
    }

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint param, ref int value, uint winIni);
}
