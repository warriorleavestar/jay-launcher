using Amber.Core.Localization;
using Amber.Core.Multiplayer.Public;
using Amber.Core.Net;

namespace Amber.Core.Multiplayer.Gate;

/// <summary>
/// Keeps a gate's signed address record (<see cref="GateRecord"/>) on its public network's directories while the gate runs:
/// joins the network with its own EasyTier, puts the record, and puts it again every <see cref="GateRecord.Renew"/>
/// (sooner after a failure). Only runs while records are on (<see cref="GateStore.EnableRecords"/>).
/// </summary>
public sealed class GateRecordPublisher : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    private GateRecordPublisher(Func<CancellationToken, Task> loop) => _loop = Task.Run(() => loop(_stop.Token));

    /// <summary>Starts publishing when records are on; null when they are off.</summary>
    public static GateRecordPublisher? Start(GateStore store, GateConfig config, string workDir, Action<string> log)
    {
        if (store.Read().RecordNetwork is not { } network) return null;
        return new GateRecordPublisher(ct => RunAsync(store, config, network, workDir, log, ct));
    }

    /// <summary>The profile records go to: the Jay network, or the network in the stored connection string.</summary>
    public static PublicNetworkProfile Profile(string network) =>
        network == PublicNetworkProfile.Jay().Id ? PublicNetworkProfile.Jay()
        : NetworkString.Parse(network, trusted: []).ToProfile();

    /// <summary>One put of the current record. Throws the directory's rejection or the connection error.</summary>
    public static async Task PublishOnceAsync(RoomDirectoryClient directories, GateStore store, GateConfig config, DateTimeOffset now, CancellationToken ct)
    {
        var state = store.Read();
        if (state.RecordId is not { } id) throw new InvalidOperationException(L.T("core.gate.record.off"));
        var record = GateRecord.Make(store.LoadRecordSigner(), Convert.FromBase64String(id), store.RecordEntries(config, state), now);
        await directories.PutRecordAsync(record, ct);
    }

    private static Task RunAsync(GateStore store, GateConfig config, string network, string workDir, Action<string> log, CancellationToken ct) =>
        RetryAsync(token => PublishWhileConnectedAsync(store, config, network, workDir, log, token), log, TimeSpan.FromMinutes(5), ct);

    /// <summary>
    /// Runs <paramref name="attempt"/> again <paramref name="retry"/> after each failure until cancelled. Any failure is
    /// logged and retried: an exception leaving this loop would end publishing for good and surface later from the
    /// server's stop.
    /// </summary>
    internal static async Task RetryAsync(Func<CancellationToken, Task> attempt, Action<string> log, TimeSpan retry, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await attempt(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                log(L.F("core.gate.record.failed", e.Message));
            }
            try { await Task.Delay(retry, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private static async Task PublishWhileConnectedAsync(GateStore store, GateConfig config, string network, string workDir, Action<string> log, CancellationToken ct)
    {
        PublicNetworkSession? session = null;
        try
        {
            var profile = Profile(network);
            using (var http = Downloader.CreateHttpClient())
            {
                var installer = new EasyTierInstaller(new Downloader(http, SourceChain.Default()), workDir);
                await installer.EnsureAsync(null, ct);
                session = PublicNetworkSession.Start(installer, new PublicNetworkOptions(profile, EasyTierArgs.FreeTcpPort(), EasyTierArgs.FreeTcpPort()),
                    Path.Combine(workDir, "record-network"));
            }
            await session.WaitForDirectoryAsync(TimeSpan.FromMinutes(2), ct);
            var directories = new RoomDirectoryClient(session);
            while (!ct.IsCancellationRequested && !session.HasExited)
            {
                await PublishOnceAsync(directories, store, config, DateTimeOffset.UtcNow, ct);
                log(L.F("core.gate.record.published", profile.Name));
                await Task.Delay(GateRecord.Renew, ct);
            }
        }
        finally
        {
            session?.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try { await _loop; }
        catch (OperationCanceledException) { }
        _stop.Dispose();
    }
}
