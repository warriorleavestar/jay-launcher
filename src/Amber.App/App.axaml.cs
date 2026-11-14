using Amber.App.Services;
using Amber.App.ViewModels;
using Amber.App.Views;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace Amber.App;

public sealed class App : Application
{
    public override void Initialize()
    {
        StartupTrace.Mark("platform");
        AvaloniaXamlLoader.Load(this);
        StartupTrace.Mark("theme");
    }

    public static void ApplyTheme(string theme)
    {
        if (Current is null) return;
        Current.RequestedThemeVariant = theme switch
        {
            "light" => ThemeVariant.Light,
            "dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }

    public override void OnFrameworkInitializationCompleted()
    {
        DevCrashGuard.HookDispatcher();
        // Observe only (not Handled): the exit trail keeps the stack even when nothing reaches stderr.
        Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) =>
            Amber.Core.Diagnostics.ExitTrail.Exception("Dispatcher.UnhandledException", e.Exception);
        // Linux 系统没有中文字体时，系统画的标题栏会把中文显示成方框，标题改用「Jay」。
        LinuxFonts.GuardWindowTitles();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settings = AppSettings.Load();
            Amber.Core.Localization.L.SetLanguage(settings.Language);
            // 主题包在主窗口创建前套上（启动保护：上次这个主题让 Jay 崩了，这次就用默认主题）。
            ThemeService.ApplyAtStartup(settings, desktop.Args ?? []);
            settings.ReduceMotionMode = Amber.Core.Layout.MotionPreference.Migrate(settings.ReduceMotionMode, settings.ReduceMotion);
            SystemMotion.Start(settings.ReduceMotionMode);
            DensityService.Apply(settings.Density);
            Amber.Core.Localization.L.Changed += ThemeService.OnLanguageChanged;
            var service = new LauncherService(settings);
            var viewModel = new MainViewModel(service);
            StartupTrace.Mark("viewmodels");
            desktop.MainWindow = new MainWindow(settings) { DataContext = viewModel };
            // 窗口打开并画完几帧以后才算主题没问题。
            desktop.MainWindow.Opened += (_, _) => Avalonia.Threading.DispatcherTimer.RunOnce(ThemeService.StartupSucceeded, TimeSpan.FromSeconds(3));
            // 关主窗口就退出：远程控制的小条、查看窗口这些不能让进程留在后台（被控时关 Jay 以为断了，其实还在被控制）。
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnMainWindowClose;
            StartupTrace.Mark("window");
            desktop.Exit += (_, e) =>
            {
                // A clean exit (even within the 3 s above) proves the theme did not crash Jay; a crash never gets here.
                ThemeService.StartupSucceeded();
                Amber.Core.Diagnostics.ExitTrail.CleanExit($"app exit, code {e.ApplicationExitCode}");
                // 开着的服务器先存盘停下，再清理别的。
                ViewModels.HostServerViewModel.StopRunningServer();
                service.Dispose();
            };
            // 开发截图和自检不清理游戏回收目录或真实用户的无痕配置。
            if (Environment.GetEnvironmentVariable("AMBER_SNAPSHOT_DIR") is not { Length: > 0 }
                && Environment.GetEnvironmentVariable("AMBER_SELFTEST") is not { Length: > 0 })
            {
                // 回收目录里过期的条目放到后台清，不拖慢启动。
                _ = Task.Run(() =>
                {
                    try { if (service.PurgeTrash() > 0) service.CleanBakeCacheInBackground(); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                });
                // 清掉以前留下的无痕模式临时配置（Amber 没等到游戏退出就关了、崩溃了之类）。
                _ = Task.Run(() => Amber.Core.Launch.GameLauncher.SweepNoTrace());
                // Once a day: tell the user about a signed newer Jay (a notice only, never a download).
                UpdateNotifier.Start(service);
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}
