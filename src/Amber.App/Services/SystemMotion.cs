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
            SystemQueries++;
            if (_read) return _system;
            _read = true;
            try { _system = Read(); } catch { _system = null; }
            return _system;
        }
    }

    /// <summary>启动时调用：先按“不减少”或用户的明确选择套上，系统设置在后台读完再补一次（读系统可能要起一个进程，不卡启动）。</summary>
    public static void Start(string? mode)
    {
        _started = true;
        Apply(MotionPreference.Normalize(mode) == MotionPreference.System ? MotionPreference.Off : mode);
        if (MotionPreference.Normalize(mode) != MotionPreference.System) return;
        _ = Task.Run(() => SystemReduces).ContinueWith(_ => Avalonia.Threading.Dispatcher.UIThread.Post(() => Apply(mode)), TaskScheduler.Default);
    }

    private static string? _mode;
    private static bool _started;

    /// <summary>Test hook: how many times <see cref="SystemReduces"/> was asked for on this thread.</summary>
    [ThreadStatic] internal static int SystemQueries;

    /// <summary>
    /// The theme changed: its animation lengths apply unless motion is reduced (reduced motion always wins).
    /// Before <see cref="Start"/> (a theme pack applied at startup) this does nothing: Start applies the themed lengths
    /// itself, and the user's choice isn't known yet, so reading the system setting here would block the UI thread.
    /// </summary>
    public static void Reapply()
    {
        if (_started) Apply(_mode);
    }

    public static void Apply(string? mode)
    {
        _mode = mode;
        var reduce = MotionPreference.ShouldReduce(mode, MotionPreference.Normalize(mode) == MotionPreference.System ? SystemReduces : null);
        if (Application.Current?.Resources is { } resources)
            foreach (var (key, ms) in MotionPreference.Durations(reduce))
                resources[key] = TimeSpan.FromMilliseconds(!reduce && ThemeService.Current?.Durations.TryGetValue(key, out var themed) == true ? themed : ms);
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
            return MotionPreference.ParseMacReduceMotion(Run("defaults", ["read", "com.apple.universalaccess", "reduceMotion"]));
        if (OperatingSystem.IsLinux())
            return MotionPreference.ParseGnomeEnableAnimations(Run("gsettings", ["get", "org.gnome.desktop.interface", "enable-animations"]));
        return null;
    }

    /// <summary>
    /// Runs a settings helper and returns its standard output, or null when it fails or doesn't finish within
    /// <paramref name="timeoutMs"/> (it is then killed). <paramref name="started"/> is a test hook that sees the process.
    /// </summary>
    internal static string? Run(string file, IReadOnlyList<string> args, int timeoutMs = 2000, Action<Process>? started = null)
    {
        var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        if (p is null) return null;
        started?.Invoke(p);
        // Read in the background so the timeout also bounds a helper that never exits; a hung one is killed.
        var output = p.StandardOutput.ReadToEndAsync();
        _ = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs))
        {
            try
            {
                p.Kill(entireProcessTree: true);
                p.WaitForExit(1000);
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or AggregateException) { }
            return null;
        }
        return output.Wait(1000) && p.ExitCode == 0 ? output.Result : null;
    }

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint param, ref int value, uint winIni);
}
