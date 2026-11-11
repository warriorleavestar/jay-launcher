using System.Diagnostics;
using System.Text;
using Amber.Core.Multiplayer;

namespace Amber.Core.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public class ElevatedGuardNetworkTests
{
    private static string PowerShell => Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    [Fact]
    public async Task FailedCoreStartupIsAcknowledgedToCaller()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var guard = ElevatedGuard.Start(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".exe"), [], new Dictionary<string, string>(), elevate: false);
        // 启动结果一定会有：守护脚本回“failed”、连不上管道（ConnectTimeout 后）或者守护进程退出，三者之一。
        // 以前这里自己掐 20 秒，比产品自己的 30 秒连接期限还短：机器忙、PowerShell 启动慢时测试先超时，
        // 抛的是 TimeoutException 而不是要测的 IOException。现在等产品自己给出结果，外面这个期限只防挂死。
        var settle = ElevatedGuard.ConnectTimeout + TimeSpan.FromMinutes(2);
        var error = await Assert.ThrowsAsync<IOException>(() => guard.WaitForReadyAsync().WaitAsync(settle));
        Assert.NotEmpty(error.Message);
        Assert.True(guard.WaitForExit(settle));
        Assert.False(guard.StartPending);
        Assert.NotEmpty(guard.RecentOutput);
    }

    [Fact]
    public async Task KillingGuardAlsoKillsCoreWithoutFinally()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var guard = ElevatedGuard.Start(PowerShell, ["-NoProfile", "-NonInteractive", "-Command", "Write-Output ('pid=' + $PID); Start-Sleep 60"], new Dictionary<string, string>(), elevate: false);
        await guard.WaitForReadyAsync().WaitAsync(TimeSpan.FromSeconds(20));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        string? line;
        while ((line = guard.RecentOutput.FirstOrDefault(x => x.StartsWith("pid=", StringComparison.Ordinal))) is null) await Task.Delay(50, deadline.Token);
        var pid = int.Parse(line[4..]);
        using var core = Process.GetProcessById(pid);
        using var owner = Process.GetProcessById(guard.ProcessId);
        owner.Kill(); // 故意不用 entireProcessTree，子进程只能由 Job 回收。
        await core.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(core.HasExited);
        Assert.True(guard.WaitForExit(TimeSpan.FromSeconds(10)));
    }

    /// <summary>
    /// The guard is Jay itself with a short, fixed command line: no PowerShell, no encoded script, no room key.
    /// (Defender flagged the old hidden "powershell -EncodedCommand" guard as Trojan:Win32/Commando.B!ml.)
    /// </summary>
    [Fact]
    public void GuardCommandLineIsJayWithoutScriptOrSecret()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var guard = ElevatedGuard.Start(PowerShell, ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep 60"],
            new Dictionary<string, string> { [EasyTierArgs.SecretVariable] = "SECRET-cmdline" }, elevate: false);
        Assert.StartsWith(WindowsElevatedGuardHost.GuardFlag + " ", guard.CommandLine);
        Assert.DoesNotContain("SECRET", guard.CommandLine);
        Assert.DoesNotContain("EncodedCommand", guard.CommandLine, StringComparison.OrdinalIgnoreCase);
        Assert.True(guard.CommandLine.Length < 512);
        using var process = Process.GetProcessById(guard.ProcessId);
        Assert.Equal("Jay", process.ProcessName);
    }

    /// <summary>Command lines Jay would never write are refused before any pipe is opened.</summary>
    [Theory]
    [InlineData("--jay-lan-guard")]
    [InlineData("--jay-lan-guard 0123456789abcdef0123456789abcdef 12 S-1-5-21-1-2-3-1001")]
    [InlineData("--jay-lan-guard 0123456789ABCDEF0123456789ABCDEF 12 S-1-5-21-1-2-3-1001 C:\\core.exe")]
    [InlineData("--jay-lan-guard 0123456789abcdef0123456789abcdef -12 S-1-5-21-1-2-3-1001 C:\\core.exe")]
    [InlineData("--jay-lan-guard 0123456789abcdef0123456789abcdef 12 not-a-sid C:\\core.exe")]
    [InlineData("--jay-lan-guard 0123456789abcdef0123456789abcdef 12 S-1-5-21-1-2-3-1001 core.exe")]
    [InlineData("--other 0123456789abcdef0123456789abcdef 12 S-1-5-21-1-2-3-1001 C:\\core.exe")]
    public void GuardRefusesMalformedCommandLines(string commandLine)
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Equal(2, WindowsElevatedGuardHost.Run(commandLine.Split(' ')));
    }
}