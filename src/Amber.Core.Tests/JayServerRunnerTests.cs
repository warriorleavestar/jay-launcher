using Amber.Core.Multiplayer.Gate;

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
}
