using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;

namespace Amber.App.Services;

/// <summary>重启 Jay：先按原来的路径和参数再开一个，再关掉自己（正常关窗口的流程，会先存盘、停掉联机）。</summary>
internal static class AppRestart
{
    public static void Restart()
    {
        try
        {
            if (Environment.ProcessPath is { } path)
            {
                var start = new ProcessStartInfo(path) { UseShellExecute = false, WorkingDirectory = Environment.CurrentDirectory };
                // dotnet Amber.App.dll 这样跑的（开发时）：参数里第一项是 dll，要一起带上；发布版的第一项是程序本身，跳过。
                var args = Environment.GetCommandLineArgs();
                var isHost = Path.GetFileNameWithoutExtension(path).Equals("dotnet", StringComparison.OrdinalIgnoreCase);
                foreach (var arg in isHost ? args : args.Skip(1)) start.ArgumentList.Add(arg);
                Process.Start(start);
            }
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // 开不了新进程也照样退出：熔断的目的是断开，下次用户自己打开。
        }
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown();
        else Environment.Exit(0);
    }
}
