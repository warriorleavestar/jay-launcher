using Amber.Core.Localization;
using Amber.Core.Multiplayer.Public;
using Amber.Core.Net;

namespace Amber.Core.Multiplayer.Gate;

/// <summary>
/// Keeps a gate's signed address record (<see cref="GateRecord"/>) on its public network's directories while the gate runs.
/// It looks at the stored state every <see cref="Poll"/>, so <c>records on/off</c> and changed entries apply to a running
/// gate: it joins the network (with its own EasyTier) while records are on and leaves it when they are turned off or the
/// network changes, and it puts the record again when its content changes or every <see cref="GateRecord.Renew"/>
/// (after a failure, again after <see cref="Retry"/>).
/// </summary>
public sealed class GateRecordPublisher : IAsyncDisposable
{
    /// <summary>How often the stored state is read again.</summary>
    public static readonly TimeSpan Poll = TimeSpan.FromMinutes(1);
    /// <summary>How long to wait after a failed join or put, unless records are turned off and on or the network changes.</summary>
    public static readonly TimeSpan Retry = TimeSpan.FromMinutes(5);

    /// <summary>A joined public network: its directories, and whether the connection has ended.</summary>
    internal interface IJoined : IDisposable
    {
        RoomDirectoryClient Directories { get; }
        bool HasExited { get; }
    }

    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    private GateRecordPublisher(Func<CancellationToken, Task> loop) => _loop = Task.Run(() => loop(_stop.Token));

    /// <summary>Starts watching the records setting; publishes whenever records are on.</summary>
    public static GateRecordPublisher Start(GateStore store, GateConfig config, string workDir, Action<string> log) =>
        new(ct => RunAsync(store, config, (network, c) => JoinAsync(network, workDir, c), log, Poll, Retry, ct));

    /// <summary>Test hook: the same loop with another way to join and other intervals.</summary>
    internal static GateRecordPublisher Start(GateStore store, GateConfig config, Func<string, CancellationToken, Task<IJoined>> join,
        Action<string> log, TimeSpan poll, TimeSpan retry) =>
        new(ct => RunAsync(store, config, join, log, poll, retry, ct));

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

    private sealed class Joined(PublicNetworkSession session) : IJoined
    {
        public RoomDirectoryClient Directories { get; } = new(session);
        public bool HasExited => session.HasExited;
        public void Dispose() => session.Dispose();
    }

    private static async Task<IJoined> JoinAsync(string network, string workDir, CancellationToken ct)
    {
        var profile = Profile(network);
        PublicNetworkSession session;
        using (var http = Downloader.CreateHttpClient())
        {
            var installer = new EasyTierInstaller(new Downloader(http, SourceChain.Default()), workDir);
            await installer.EnsureAsync(null, ct);
            session = PublicNetworkSession.Start(installer, new PublicNetworkOptions(profile, EasyTierArgs.FreeTcpPort(), EasyTierArgs.FreeTcpPort()),
                Path.Combine(workDir, "record-network"));
        }
        try
        {
            await session.WaitForDirectoryAsync(TimeSpan.FromMinutes(2), ct);
            return new Joined(session);
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    /// <summary>What a put would carry now: the locator id and the encoded entries. Changes mean the record must be put again.</summary>
    private static string Content(GateStore store, GateConfig config, GateState state) =>
        state.RecordId + "|" + Convert.ToBase64String(GateRecord.Encode(store.RecordEntries(config, state)));

    private static async Task RunAsync(GateStore store, GateConfig config, Func<string, CancellationToken, Task<IJoined>> join,
        Action<string> log, TimeSpan poll, TimeSpan retry, CancellationToken ct)
    {
        IJoined? joined = null;
        string? joinedNetwork = null;
        string? lastContent = null;
        var lastPut = DateTimeOffset.MinValue;
        var nextTry = DateTimeOffset.MinValue;
        // The record setting the wait after a failure is for: records turned off and on again (even between two rounds),
        // or another network, try at once.
        (string? Network, int Changes)? failedFor = null;
        string? readFailure = null;
        void Leave()
        {
            joined?.Dispose();
            joined = null;
            joinedNetwork = null;
            lastContent = null;
        }
        try
        {
            while (!ct.IsCancellationRequested)
            {
                GateState state;
                try
                {
                    state = store.Read();
                    readFailure = null;
                }
                // A state file that can't be read just now (being replaced, or damaged until someone fixes it) skips this
                // round; publishing goes on once it reads again. Logged when it starts failing, not every round.
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    if (e.Message != readFailure) log(L.F("core.gate.record.failed", e.Message));
                    readFailure = e.Message;
                    await Task.Delay(poll, ct);
                    continue;
                }
                var network = state.RecordNetwork;
                if (failedFor is { } failed && failed != (network, state.RecordChanges))
                {
                    nextTry = DateTimeOffset.MinValue;
                    failedFor = null;
                }
                // Records turned off, moved to another network, or the connection ended: leave.
                if (joined is not null && (network != joinedNetwork || joined.HasExited)) Leave();
                var now = DateTimeOffset.UtcNow;
                if (network is not null && now >= nextTry)
                {
                    try
                    {
                        if (joined is null)
                        {
                            // Joining can take minutes when the network is slow to answer: a change of the record setting
                            // meanwhile ends this attempt, and the next round starts on the new setting at once.
                            joined = await JoinUnlessChangedAsync(store, join, network, state.RecordChanges, poll, ct);
                            if (joined is null) continue;
                            joinedNetwork = network;
                        }
                        var content = Content(store, config, state);
                        if (content != lastContent || now - lastPut >= GateRecord.Renew)
                        {
                            await PublishOnceAsync(joined.Directories, store, config, now, ct);
                            lastContent = content;
                            lastPut = now;
                            log(L.F("core.gate.record.published", Profile(network).Name));
                        }
                    }
                    // Any failure is logged and tried again: an exception leaving this loop would end publishing for good and
                    // surface later from the server's stop.
                    catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
                    {
                        log(L.F("core.gate.record.failed", e.Message));
                        Leave();
                        nextTry = DateTimeOffset.UtcNow + retry;
                        failedFor = (network, state.RecordChanges);
                    }
                }
                await Task.Delay(poll, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            Leave();
        }
    }

    /// <summary>
    /// Joins <paramref name="network"/>, or gives up and returns null when the record setting changes before the join is
    /// done (records turned off, turned off and on again, or moved to another network).
    /// </summary>
    private static async Task<IJoined?> JoinUnlessChangedAsync(GateStore store, Func<string, CancellationToken, Task<IJoined>> join,
        string network, int changes, TimeSpan poll, CancellationToken ct)
    {
        using var superseded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var watch = WatchSettingAsync(store, network, changes, poll, superseded);
        try
        {
            return await join(network, superseded.Token);
        }
        catch (Exception) when (!ct.IsCancellationRequested && superseded.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            await superseded.CancelAsync();
            await watch;
        }
    }

    private static async Task WatchSettingAsync(GateStore store, string network, int changes, TimeSpan poll, CancellationTokenSource superseded)
    {
        try
        {
            while (true)
            {
                await Task.Delay(poll, superseded.Token);
                GateState state;
                try { state = store.Read(); }
                catch (Exception e) when (e is not OperationCanceledException) { continue; }
                if (state.RecordNetwork != network || state.RecordChanges != changes)
                {
                    await superseded.CancelAsync();
                    return;
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try { await _loop; }
        catch (OperationCanceledException) { }
        _stop.Dispose();
    }
}
