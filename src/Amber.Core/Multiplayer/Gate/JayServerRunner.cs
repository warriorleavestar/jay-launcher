using Amber.Core.Localization;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Multiplayer.Gate;

/// <summary>
/// 一台正在运行的 Jay 服务器：网关 + （可选）MC 服务端。命令行版（jay-server run）和 Jay 里的“开服”共用这一块。
/// 熔断时让 MC 存盘；MC 自己退出了，<see cref="Exited"/> 就完成，调用方据此把网关一起停掉。
/// </summary>
public sealed class JayServerRunner : IAsyncDisposable
{
    private readonly X25519KeyPair _key;
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _stopped;
    private GateRecordPublisher? _records;

    public GateStore Store { get; }
    public GateConfig Config { get; }
    public GateServer Gate { get; }
    public McServerHost.RunningServer? Mc { get; }
    public string Fingerprint => MemberIdentity.FingerprintOf(_key.PublicKey);
    public byte[] ServerPublicKey => _key.PublicKey;

    /// <summary>MC 服务端退出时完成（没带 MC 时只在 <see cref="StopAsync"/> 后完成）。</summary>
    public Task Exited => _exited.Task;

    private JayServerRunner(GateStore store, GateConfig config, X25519KeyPair key, GateServer gate, McServerHost.RunningServer? mc)
    {
        Store = store;
        Config = config;
        _key = key;
        Gate = gate;
        Mc = mc;
    }

    /// <summary>网关数据目录（init 写的）。</summary>
    public static string GateDirectory(string root) => Path.Combine(root, "gate");

    public static bool IsInitialized(string root) => GateStore.IsInitialized(GateDirectory(root));

    /// <param name="root">服务器数据目录（下面有 gate/ 和 mc/）。</param>
    /// <param name="withMc">false：只开网关（后端是别处的 MC 服务器）。</param>
    /// <param name="log">网关日志。</param>
    /// <param name="mcOutput">MC 服务端的输出。</param>
    public static JayServerRunner Start(string root, bool withMc, Action<string> log, Action<string> mcOutput)
    {
        if (!IsInitialized(root)) throw new InvalidOperationException(L.T("core.gate.run.not_initialized"));
        var store = new GateStore(GateDirectory(root));
        var config = store.LoadConfig();
        var host = new McServerHost(root);
        if (withMc)
        {
            if (!host.IsSetUp) throw new InvalidOperationException(L.T("core.gate.run.not_setup"));
            if (!host.EulaAccepted) throw new InvalidOperationException(L.T("core.gate.run.no_eula"));
            config.BackendHost = "127.0.0.1";
            config.BackendPort = host.LoadSettings().Port;
        }
        var key = store.LoadServerKey();
        GateServer? gate = null;
        try
        {
            gate = new GateServer(store, config, key, log);
            gate.Start();
            var state = store.Read();
            var fingerprint = MemberIdentity.FingerprintOf(key.PublicKey);
            log(state.Privacy.IsSplit()
                ? L.F("core.gate.run.listening_split", gate.Port, state.Privacy == PrivacyLevel.PlusPlus ? "++" : "+", state.Relays.Count, fingerprint)
                : L.F("core.gate.run.listening", gate.Port, config.PublicHost, config.PublicPort, fingerprint));
            McServerHost.RunningServer? mc = null;
            if (withMc)
            {
                mc = host.Start(mcOutput);
                log(L.F("core.gate.run.mc_started", mc.Process.Id));
            }
            var runner = new JayServerRunner(store, config, key, gate, mc);
            // A server with no domain and records on: keep its signed address record on the public network's directories.
            runner._records = GateRecordPublisher.Start(store, config, Path.Combine(root, "records"), log);
            // 熔断时先存盘（不停服），万一之后有人去停服务端也不会坏档。
            gate.BreakerChanged += (open, _) =>
            {
                if (open) runner.Command("save-all");
            };
            if (mc is not null)
                _ = mc.Process.WaitForExitAsync().ContinueWith(_ =>
                {
                    if (Volatile.Read(ref runner._stopped) == 0) log(L.F("core.gate.run.mc_exited", SafeExitCode(mc)));
                    runner._exited.TrySetResult();
                }, TaskScheduler.Default);
            return runner;
        }
        catch
        {
            if (gate is not null) gate.DisposeAsync().AsTask().GetAwaiter().GetResult();
            key.Dispose();
            throw;
        }
    }

    private static int SafeExitCode(McServerHost.RunningServer mc)
    {
        try { return mc.Process.ExitCode; }
        catch (InvalidOperationException) { return -1; }
    }

    /// <summary>给 MC 服务端发一条控制台命令（没带 MC 或已退出时忽略）。</summary>
    public void Command(string line)
    {
        if (Mc is { HasExited: false } mc)
        {
            try { mc.Command(line.TrimStart('/')); }
            catch (Exception e) when (e is InvalidOperationException or IOException) { }
        }
    }

    /// <summary>当前的在线人数、节点心跳和熔断状态。</summary>
    public GateHealth? Health() => Store.ReadHealth();

    /// <summary>停网关，让 MC 存盘退出（最多等 timeout，超时强制结束）。</summary>
    public async Task StopAsync(TimeSpan? timeout = null)
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        // Every step runs even when an earlier one fails (above all, the MC server is still told to stop and save);
        // the first error is rethrown at the end.
        try
        {
            await RunAllAsync(
                async () => { if (_records is not null) await _records.DisposeAsync(); },
                async () => await Gate.DisposeAsync(),
                async () =>
                {
                    if (Mc is null) return;
                    try { await Mc.StopAsync(timeout ?? TimeSpan.FromSeconds(60)); }
                    finally { Mc.Dispose(); }
                });
        }
        finally
        {
            _key.Dispose();
            _exited.TrySetResult();
        }
    }

    /// <summary>Runs every step in order even when one fails, then rethrows the first failure.</summary>
    internal static async Task RunAllAsync(params Func<Task>[] steps)
    {
        Exception? first = null;
        foreach (var step in steps)
        {
            try { await step(); }
            catch (Exception e) { first ??= e; }
        }
        if (first is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(first);
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
