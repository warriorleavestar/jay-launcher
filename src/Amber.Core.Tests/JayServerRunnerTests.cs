using System.Net;
using System.Net.Sockets;
using Amber.Core.Multiplayer.Gate;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Tests;

/// <summary>命令行和 Jay 的“开服”共用的运行器：没初始化 / 没准备 MC 时给出明白的错，只开网关时能启动、停止。</summary>
public sealed class JayServerRunnerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jay-runner-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Fact]
    public void RefusesBeforeInitAndBeforeSetup()
    {
        Assert.False(JayServerRunner.IsInitialized(_root));
        Assert.Throws<InvalidOperationException>(() => JayServerRunner.Start(_root, true, _ => { }, _ => { }));
        GateStore.Initialize(JayServerRunner.GateDirectory(_root), "127.0.0.1", 0, 0);
        Assert.True(JayServerRunner.IsInitialized(_root));
        // 初始化了但没准备 MC 服务端。
        Assert.Throws<InvalidOperationException>(() => JayServerRunner.Start(_root, true, _ => { }, _ => { }));
    }

    [Fact]
    public async Task GateOnlyStartsAndStops()
    {
        var store = GateStore.Initialize(JayServerRunner.GateDirectory(_root), "127.0.0.1", 0, 0);
        var config = store.LoadConfig();
        config.ListenAddress = "127.0.0.1";
        store.SaveConfig(config);
        var lines = new List<string>();
        var runner = JayServerRunner.Start(_root, false, l => { lock (lines) lines.Add(l); }, _ => { });
        Assert.True(runner.Gate.Port > 0);
        Assert.Null(runner.Mc);
        Assert.False(runner.Exited.IsCompleted);
        lock (lines) Assert.Contains(lines, l => l.Contains(runner.Fingerprint));
        runner.Command("say hi"); // 没有 MC：忽略，不抛。
        await runner.StopAsync();
        await runner.Exited.WaitAsync(TimeSpan.FromSeconds(5));
        await runner.StopAsync(); // 停两次没事。
    }

    [Fact]
    public async Task TunnelReportsGateRejectionInsteadOfDroppingIt()
    {
        var lines = new List<string>();
        await using var tunnel = new TunnelClient(0, _ => throw new GateRejectedException(GateStatus.NotMember), l => { lock (lines) lines.Add(l); });
        using (var game = new TcpClient())
        {
            // 隧道会立刻重置这条连接：连接本身也可能报错，不影响要看的东西。
            try { await game.ConnectAsync(IPAddress.Loopback, tunnel.Port); }
            catch (SocketException) { }
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                lock (lines) if (lines.Count > 0) break;
                await Task.Delay(20);
            }
        }
        // 不比对文字：别的测试可能同时切换界面语言。
        lock (lines) Assert.Single(lines);
        Assert.Equal(1, tunnel.Failures);
    }

    /// <summary>Stopping runs every step even when one fails (the MC server is still told to save), then reports the first failure.</summary>
    [Fact]
    public async Task StopStepsAllRunAndTheFirstFailureIsRethrown()
    {
        var ran = new List<int>();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => JayServerRunner.RunAllAsync(
            () => { ran.Add(1); throw new InvalidOperationException("first"); },
            () => { ran.Add(2); throw new IOException("second"); },
            () => { ran.Add(3); return Task.CompletedTask; }));
        Assert.Equal("first", error.Message);
        Assert.Equal([1, 2, 3], ran);
    }

    /// <summary>A record publish that fails in an unexpected way is logged and tried again, not left to end publishing.</summary>
    [Fact]
    public async Task RecordPublishingRetriesAfterAnyFailure()
    {
        using var cts = new CancellationTokenSource();
        var attempts = 0;
        var log = new List<string>();
        await GateRecordPublisher.RetryAsync(_ =>
        {
            if (++attempts >= 3) cts.Cancel();
            throw new NotSupportedException("unexpected");
        }, l => { lock (log) log.Add(l); }, TimeSpan.FromMilliseconds(10), cts.Token);
        Assert.Equal(3, attempts);
        lock (log) Assert.Equal(3, log.Count(l => l.Contains("unexpected", StringComparison.Ordinal)));
    }
}
