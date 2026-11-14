using Amber.App.Services;
using Amber.App.ViewModels;
using Amber.Core.Diagnostics;
using Amber.Core.Game;
using Amber.Core.Localization;
using Avalonia.Threading;

namespace Amber.Core.Tests;

/// <summary>Windows 沙盒首次运行测试发现的问题：没 OpenGL 却显示“正常退出”、新手卡装完后启动页不刷新、实例数把依赖的原版也算上。</summary>
public sealed class SandboxFindingsTests : IDisposable
{
    private readonly string _game = Path.Combine(Path.GetTempPath(), "amber-sandbox-test-" + Guid.NewGuid().ToString("N")[..8]);

    public SandboxFindingsTests() => Directory.CreateDirectory(_game);

    public void Dispose()
    {
        try { Directory.Delete(_game, true); } catch (IOException) { }
    }

    private void Instance(string id, string? inheritsFrom = null) => File.WriteAllText(
        Path.Combine(Directory.CreateDirectory(Path.Combine(_game, "versions", id)).FullName, id + ".json"),
        inheritsFrom is null
            ? $$"""{"id":"{{id}}","type":"release","mainClass":"x","libraries":[]}"""
            : $$"""{"id":"{{id}}","inheritsFrom":"{{inheritsFrom}}","type":"release","mainClass":"y","libraries":[]}""");

    private LauncherService Service() => new(new AppSettings
    {
        GameDirectory = _game,
        SavePathForTest = Path.Combine(_game, "settings.json"),
    });

    [Theory]
    [InlineData("[12:00:01] [Render thread/ERROR]: GLFW error during init: [0x10006]WGL: The driver does not appear to support OpenGL")]
    [InlineData("GLFW error 65542: WGL: The driver does not appear to support OpenGL")]
    public void NoOpenGlIsAClearGraphicsCause(string line)
    {
        var analysis = CrashAnalyzer.Analyze(line);
        Assert.True(HomeViewModel.HasClearCause(analysis));
        Assert.Equal(L.T("core.crash.graphics.opengl.title"), analysis.Findings[0].Title);
    }

    [Fact]
    public void OnlyPossibleCausesAreNotClear()
    {
        Assert.False(HomeViewModel.HasClearCause(CrashAnalyzer.Analyze("Render thread: nothing special")));
        Assert.False(HomeViewModel.HasClearCause(CrashAnalyzer.Analyze("java.lang.NoSuchMethodError: foo")));
    }

    [Theory]
    [InlineData(false, 0, false, false, true)]   // 没进主界面就退出，退出码 0：要看日志
    [InlineData(false, 0, true, true, true)]     // 只是按时间猜的“进了游戏”
    [InlineData(false, 0, true, false, false)]   // 真进了主界面，玩家自己退出
    [InlineData(true, 0, false, false, false)]   // 玩家在启动器里点了结束
    [InlineData(false, 1, false, false, false)]  // 非 0 退出码本来就按崩溃处理
    public void ExitedBeforeGameOnlyForUnexpectedCleanExits(bool stopped, int code, bool inGame, bool guessed, bool expected) =>
        Assert.Equal(expected, HomeViewModel.ExitedBeforeGame(stopped, code, inGame, guessed));

    [Fact]
    public void LoaderDependencyIsNotCountedAsASeparateInstance()
    {
        Instance("26.3");
        Instance("fabric-loader-0.20.1-26.3", "26.3");
        Assert.Equal(1, GameDirectoryDetector.CountInstances(_game));

        // 另外单独装的原版照样算。
        Instance("1.21.1");
        Assert.Equal(2, GameDirectoryDetector.CountInstances(_game));
    }

    [Fact]
    public void HomeRefreshesAfterOnboardingInstalls()
    {
        using var service = Service();
        UiTestThread.Run(() =>
        {
            service.Accounts.AddOffline("TestPlayer");
            var home = new HomeViewModel(service, new LogViewModel());
            Assert.Equal("home.status.no_versions", home.StatusInfo!.Key);
            Assert.Equal(L.T("home.button.need_instance"), home.PrimaryLabel);

            // 新手卡装完：先设当前实例，再通知已安装列表变了（和 OnboardingViewModel.InstallAsync 的顺序一样）。
            Instance("26.3");
            Instance("fabric-loader-0.20.1-26.3", "26.3");
            service.SetCurrentInstance("fabric-loader-0.20.1-26.3");
            service.NotifyInstalledChanged();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("fabric-loader-0.20.1-26.3", home.SelectedVersion);
            Assert.Equal(L.T("home.launch"), home.PrimaryLabel);
            Assert.NotEqual("home.status.no_versions", home.StatusInfo?.Key);
        });
    }

    // ---- Privacy plan item 15: the game process sandbox ----

    [Fact]
    public void GameSandbox_IsOnByDefault_HidesJayData_GrantsTheLoaderRuntime()
    {
        using var service = Service();
        Assert.False(service.Settings.LaunchWithoutSandbox);
        Assert.True(service.Settings.StrongSandbox);
        var options = service.GameSandboxOptions;
        Assert.True(options.Enabled);
        Assert.Contains(AppSettings.DataDir, options.Hidden);
        // The universal loader's runtime folder sits beside the data folder: install and data read-only, cache writable.
        Assert.Contains(options.ExtraReadWrite, p => p.EndsWith("bake-cache", StringComparison.Ordinal));
        Assert.Contains(options.ExtraReadOnly, p => p.EndsWith("universal-loader", StringComparison.Ordinal));
        Assert.DoesNotContain(options.ExtraReadOnly.Concat(options.ExtraReadWrite), p => Amber.Core.Launch.GameSandbox.IsInside(p, AppSettings.DataDir));

        service.Settings.LaunchWithoutSandbox = true;
        Assert.False(service.GameSandboxOptions.Enabled);
        Assert.Equal(L.T("settings.sandbox.status.off"), service.SandboxStatusText);
    }

    [Fact]
    public async Task StrongSandbox_DeclinedOnce_UsesStandard_AndDoesNotAskAgainUntilRetry()
    {
        if (!OperatingSystem.IsWindows() || Amber.Core.Launch.WindowsSandbox.LoopbackExempt()) return;
        var java = Path.Combine(_game, "jdk21", "bin", "java.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(java)!);
        File.WriteAllText(java, "");
        File.WriteAllText(Path.Combine(_game, "jdk21", "release"), "JAVA_VERSION=\"21.0.5\"\n");
        using var service = Service();
        var asked = 0;
        var notes = new List<string>();
        // Never says yes: the test must not bring up Windows' admin prompt.
        Task<bool> Ask() { asked++; return Task.FromResult(false); }

        var first = await service.SandboxForLaunchAsync(java, Ask, notes.Add);
        Assert.Equal(Amber.Core.Launch.WindowsSandboxLevel.Standard, first.WindowsLevel);
        var noAdmin = Amber.Core.Launch.WindowsSandbox.Elevation() == Amber.Core.Launch.AdminPrompt.NoAdmin;
        if (!Amber.Core.Launch.WindowsSandbox.HasResolver)
        {
            // A build without the resolver: Java 21 cannot use strong mode, so nothing is asked or remembered.
            Assert.Equal(0, asked);
            Assert.Null(service.Settings.StrongSandboxFallback);
            return;
        }
        Assert.Equal(noAdmin ? 0 : 1, asked);
        Assert.Equal(noAdmin ? Amber.Core.Launch.WindowsSandboxPolicy.NoAdmin : Amber.Core.Launch.WindowsSandboxPolicy.Refused, service.Settings.StrongSandboxFallback);
        Assert.Single(notes);
        Assert.Contains(L.F("settings.sandbox.status.standard_because", ""), service.SandboxStatusText);

        var second = await service.SandboxForLaunchAsync(java, Ask, notes.Add);
        Assert.Equal(Amber.Core.Launch.WindowsSandboxLevel.Standard, second.WindowsLevel);
        Assert.Equal(noAdmin ? 0 : 1, asked);

        service.RetryStrongSandbox();
        Assert.Null(service.Settings.StrongSandboxFallback);
        Assert.Equal(L.T("settings.sandbox.status.strong_pending"), service.SandboxStatusText);
    }

    [WindowsFact]
    public async Task StrongSandbox_Off_UsesStandardWithoutAsking()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var service = Service();
        service.Settings.StrongSandbox = false;
        var asked = false;
        var options = await service.SandboxForLaunchAsync(Path.Combine(_game, "java.exe"), () => { asked = true; return Task.FromResult(true); }, _ => { });
        Assert.False(asked);
        Assert.Equal(Amber.Core.Launch.WindowsSandboxLevel.Standard, options.WindowsLevel);
        Assert.Equal(L.T("settings.sandbox.status.standard"), service.SandboxStatusText);
    }
}
