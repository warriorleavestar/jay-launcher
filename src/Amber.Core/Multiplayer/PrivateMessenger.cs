using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amber.Core.Localization;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Multiplayer;

/// <summary>收到的一条私聊。</summary>
/// <summary>A private message. FromPublicKey is the key the message was sealed with, so it is the sender for certain.</summary>
public sealed record PrivateMessage(string FromFingerprint, byte[] FromPublicKey, string FromName, string Text, DateTimeOffset Time);

/// <summary>别人要发给自己的文件（等自己点接收）。</summary>
public sealed record FileOffer(string Id, string FromFingerprint, string FromName, string Name, long Size);

/// <summary>一次传文件的进度（发送和接收都用）。</summary>
public sealed record FileProgress(string Id, string Peer, string Name, long Size, long Done, bool Outgoing)
{
    /// <summary>传完了：收方是保存后的路径，发方是 ""。失败或被取消时是 null，原因见 <see cref="Error"/>。</summary>
    public string? SavedPath { get; init; }
    public string? Error { get; init; }
    public bool Finished { get; init; }
    /// <summary>Why a finished transfer did not complete (<see cref="FileEnd.None"/> when it did).</summary>
    public FileEnd End { get; init; }
    /// <summary>The other side accepted and the bytes are moving (before that, an outgoing file waits for an answer).</summary>
    public bool Started { get; init; }
}

/// <summary>Why a transfer stopped without completing. The file cards turn these into one plain line each.</summary>
public enum FileEnd
{
    None,
    /// <summary>The other person was not in the room when the offer or a chunk went out.</summary>
    Offline,
    /// <summary>They declined, or did not answer within five minutes. The cards say the same for both.</summary>
    NotAccepted,
    Cancelled,
    /// <summary>The connection stalled part way; sending again resumes where it stopped.</summary>
    Stalled,
    /// <summary>Anything else (corrupt data, a full disk, an unreadable file).</summary>
    Failed,
}

/// <summary>私聊、传文件要用的那条连接（真的是房间聊天的 <see cref="ChatClient"/>，测试里可以换）。</summary>
public interface IPrivateLink
{
    Task<bool> SendPrivateAsync(string to, byte[] sealedData, CancellationToken ct);
    event Action<PrivateFrame>? PrivateReceived;
}

/// <summary>
/// 房间成员之间的私聊和传文件，端到端加密（<see cref="PrivateSeal"/>），房主只按指纹转交。
/// 传文件：发方先发“要发这个文件”（名字、大小、SHA-256），收方点接收后发方按 3 KB 一块发，最多 16 块没确认；
/// 收方每收到一块回确认，写进临时文件，收完核对 SHA-256 后改成正式文件名。中途断开、被拒、被取消都会清掉临时文件。
/// </summary>
public sealed partial class PrivateMessenger : IAsyncDisposable
{
    public const int ChunkSize = 3 * 1024;
    public const int Window = 16;
    /// <summary>收文件前要求磁盘多留的空间（不限文件大小，只看收方磁盘够不够）。</summary>
    public const long DiskReserve = 64L * 1024 * 1024;
    public const int MaxTextLength = 2000;
    private static readonly TimeSpan AckTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan OfferTimeout = TimeSpan.FromMinutes(5);
    /// <summary>
    /// A receive that has had no chunk for this long is given up (the sender gives up after <see cref="AckTimeout"/>).
    /// Together with offers expiring after <see cref="OfferTimeout"/>, this keeps one member from holding all
    /// <see cref="MaxIncoming"/> receive slots forever.
    /// </summary>
    internal static readonly TimeSpan ReceiveIdleTimeout = TimeSpan.FromSeconds(60);
    /// <summary>At most this many incoming offers and receives at once; further offers are ignored.</summary>
    public const int MaxIncoming = 8;

    private readonly IPrivateLink _link;
    private readonly X25519KeyPair _me;
    private readonly Func<string, byte[]?> _keyOf;
    private readonly CancellationTokenSource _cts = new();

    private sealed class Outgoing
    {
        public required string Id;
        public required string To;
        public required string Path;
        public required string Name;
        public required long Size;
        public required byte[] Hash;
        public readonly TaskCompletionSource<bool> Answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly SemaphoreSlim Credits = new(Window, int.MaxValue);
        public long Acked;
        /// <summary>对方已经有的块数（断点续传：上次没收完的部分），接收时对方告诉我们。</summary>
        public long From;
        public volatile bool Cancelled;
        public volatile bool Started;
    }

    private sealed class Incoming
    {
        public required FileOffer Offer;
        public required byte[] Hash;
        public required byte[] PeerKey;
        public FileStream? Stream;
        public string? TempPath;
        public string? TargetPath;
        public IncrementalHash? Hasher;
        public long Next;
        public long Received;
        /// <summary>When the offer or the last chunk arrived (UTC), for <see cref="SweepIdle"/>.</summary>
        public DateTime LastActivity;
        /// <summary>A mod file this side asked for (mod sync): accepted without asking, and its SHA-1 is checked on arrival.</summary>
        public string? ModSha1;
    }

    private readonly ConcurrentDictionary<string, Outgoing> _outgoing = new();
    private readonly ConcurrentDictionary<string, Incoming> _incoming = new();
    private readonly SemaphoreSlim _incomingGate = new(1, 1);
    /// <summary>收到的帧排队一个一个处理（文件块要按顺序写）。</summary>
    private readonly System.Threading.Channels.Channel<(PrivateFrame Frame, JsonObject Body)> _inbox =
        System.Threading.Channels.Channel.CreateUnbounded<(PrivateFrame, JsonObject)>(new() { SingleReader = true });
    private readonly Task _pump;
    private readonly Task _sweeper;

    /// <summary>Where received files go (Settings › Multiplayer › Files can change it; a receive in progress keeps its folder).</summary>
    public string Downloads { get; set; }

    /// <summary>
    /// The most bytes per second a file may go out at; 0 means as fast as the room allows. Read before every chunk,
    /// so a change (a game starting with "give way while playing") takes effect at once.
    /// </summary>
    public Func<long> SendLimit { get; set; } = () => 0;

    public event Action<PrivateMessage>? MessageReceived;
    public event Action<FileOffer>? FileOffered;
    public event Action<FileProgress>? Progress;
    /// <summary>An offer that was never answered expired (after <see cref="OfferTimeout"/>); its card can go.</summary>
    public event Action<string>? FileOfferExpired;

    /// <summary>The clock for idle timeouts (tests replace it).</summary>
    internal Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    /// <param name="keyOf">按指纹查这个人现在的公钥（房主发来的在线名单）；不在线返回 null。</param>
    /// <param name="downloads">收到的文件放在这里。</param>
    public PrivateMessenger(IPrivateLink link, X25519KeyPair me, Func<string, byte[]?> keyOf, string downloads)
    {
        _link = link;
        _me = me;
        _keyOf = keyOf;
        Downloads = downloads;
        _link.PrivateReceived += OnFrame;
        _pump = PumpAsync();
        _sweeper = SweepLoopAsync();
    }

    private async Task SweepLoopAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(15), _cts.Token);
                await SweepIdle();
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Drops offers nobody answered within <see cref="OfferTimeout"/> and receives that got no chunk within
    /// <see cref="ReceiveIdleTimeout"/>, and tells the sender. A stalled receive keeps its .jaypart, so sending again resumes.
    /// </summary>
    internal async Task SweepIdle()
    {
        var now = Clock();
        foreach (var (id, job) in _incoming.ToArray())
        {
            var accepted = job.Stream is not null;
            if (now - job.LastActivity < (accepted ? ReceiveIdleTimeout : OfferTimeout)) continue;
            await _incomingGate.WaitAsync();
            try
            {
                // Re-check under the gate: a chunk may have arrived meanwhile.
                if (now - job.LastActivity < (accepted ? ReceiveIdleTimeout : OfferTimeout) || !_incoming.TryRemove(new(id, job))) continue;
                if (accepted) Abort(job, L.T("core.mp.private.stalled"), FileEnd.Stalled);
            }
            finally
            {
                _incomingGate.Release();
            }
            if (!accepted) FileOfferExpired?.Invoke(id);
            try { await SendAsync(job.Offer.FromFingerprint, new JsonObject { ["t"] = "cancel", ["id"] = id }, _cts.Token); }
            catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException or System.Net.Sockets.SocketException) { }
        }
    }

    private async Task PumpAsync()
    {
        try
        {
            await foreach (var (frame, body) in _inbox.Reader.ReadAllAsync(_cts.Token)) await HandleAsync(frame, body);
        }
        catch (OperationCanceledException) { }
    }

    // ---- 发送 ----

    /// <summary>房主每人 5 秒最多转交 600 帧，超出的直接丢：自己先限在 500 帧以内。</summary>
    private readonly RateLimiter _sendLimiter = new(500, TimeSpan.FromSeconds(5));

    private async Task<bool> SendAsync(string to, JsonObject body, CancellationToken ct)
    {
        if (_keyOf(to) is not { } key) return false;
        while (!_sendLimiter.TryAcquire()) await Task.Delay(10, ct);
        // 不走 JsonSerializer（Native AOT 下没有反射序列化）：JsonObject 自己写成 UTF-8。
        var plain = System.Text.Encoding.UTF8.GetBytes(body.ToJsonString());
        return await _link.SendPrivateAsync(to, PrivateSeal.Seal(_me, key, plain), ct);
    }

    /// <summary>发一条私聊。对方不在线返回 false。</summary>
    public Task<bool> SendTextAsync(string to, string text, CancellationToken ct = default)
    {
        var clean = RoomChat.Clean(text, MaxTextLength);
        if (clean.Length == 0) return Task.FromResult(true);
        return SendAsync(to, new JsonObject { ["t"] = "msg", ["text"] = clean, ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }, ct);
    }

    /// <summary>
    /// 发文件：先问对方，对方接收后开始传。返回 true 表示对方完整收到；拒绝、超时、断开返回 false。
    /// 进度经 <see cref="Progress"/> 报告。
    /// </summary>
    public Task<bool> SendFileAsync(string to, string path, CancellationToken ct = default) => SendFileCoreAsync(to, path, null, ct);

    /// <summary>
    /// <see cref="SendFileAsync(string,string,CancellationToken)"/> under an id the caller made with <see cref="NewTransferId"/>,
    /// so a file card can follow the transfer from the first progress report.
    /// </summary>
    public Task<bool> SendFileAsync(string to, string path, string id, CancellationToken ct = default) =>
        IsTransferId(id) && !_outgoing.ContainsKey(id) ? SendFileCoreAsync(to, path, null, ct, id) : throw new ArgumentException("bad transfer id", nameof(id));

    /// <summary>A fresh transfer id (16 hex characters, the form the other side accepts).</summary>
    public static string NewTransferId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));

    public static bool IsTransferId(string? id) => id is { Length: 16 } && id.All(char.IsAsciiHexDigitLower);

    private async Task<bool> SendFileCoreAsync(string to, string path, string? modSha1, CancellationToken ct, string? id = null)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException(path);
        if (!AllowsFile(info.Length)) throw new InvalidDataException(L.T("core.mp.private.not_allowed"));
        byte[] hash;
        await using (var read = File.OpenRead(path)) hash = await SHA256.HashDataAsync(read, ct);
        var job = new Outgoing
        {
            Id = id ?? NewTransferId(),
            To = to,
            Path = path,
            Name = SafeName(info.Name),
            Size = info.Length,
            Hash = hash,
        };
        _outgoing[job.Id] = job;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        try
        {
            if (!await SendAsync(to, OfferBody(job, modSha1), linked.Token))
                return Fail(job, L.T("core.mp.private.offline"), FileEnd.Offline);
            Report(job, 0);
            bool accepted;
            try { accepted = await job.Answer.Task.WaitAsync(OfferTimeout, linked.Token); }
            catch (TimeoutException) { return Fail(job, L.T("core.mp.private.no_answer"), FileEnd.NotAccepted); }
            if (!accepted) return job.Cancelled ? Fail(job, L.T("core.mp.private.cancelled"), FileEnd.Cancelled) : Fail(job, L.T("core.mp.private.declined"), FileEnd.NotAccepted);
            job.Started = true;

            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkSize, useAsync: true);
            var buffer = new byte[ChunkSize];
            // 断点续传：对方上次收到哪一块，就从下一块接着发。
            long index = Math.Clamp(Interlocked.Read(ref job.From), 0, (job.Size + ChunkSize - 1) / ChunkSize);
            file.Seek(index * ChunkSize, SeekOrigin.Begin);
            Interlocked.Exchange(ref job.Acked, index);
            Report(job, index * ChunkSize);
            var pace = new Pace(System.Diagnostics.Stopwatch.StartNew());
            while (true)
            {
                if (job.Cancelled) return Fail(job, L.T("core.mp.private.cancelled"), FileEnd.Cancelled);
                var read = await file.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, linked.Token);
                if (read == 0) break;
                if (!await job.Credits.WaitAsync(AckTimeout, linked.Token)) return Fail(job, L.T("core.mp.private.stalled"), FileEnd.Stalled);
                if (job.Cancelled) return Fail(job, L.T("core.mp.private.cancelled"), FileEnd.Cancelled);
                if (!await SendAsync(to, new JsonObject { ["t"] = "chunk", ["id"] = job.Id, ["i"] = index, ["d"] = Convert.ToBase64String(buffer, 0, read) }, linked.Token))
                    return Fail(job, L.T("core.mp.private.offline"), FileEnd.Stalled);
                index++;
                await PaceAsync(pace, read, linked.Token);
            }
            // 等最后几块的确认，再告诉对方“发完了”。
            var deadline = DateTime.UtcNow + AckTimeout;
            while (Interlocked.Read(ref job.Acked) < index)
            {
                if (job.Cancelled) return Fail(job, L.T("core.mp.private.cancelled"), FileEnd.Cancelled);
                if (DateTime.UtcNow > deadline) return Fail(job, L.T("core.mp.private.stalled"), FileEnd.Stalled);
                await Task.Delay(50, linked.Token);
            }
            await SendAsync(to, new JsonObject { ["t"] = "done", ["id"] = job.Id }, linked.Token);
            Progress?.Invoke(new FileProgress(job.Id, to, job.Name, job.Size, job.Size, true) { Finished = true, SavedPath = "", Started = true });
            return true;
        }
        catch (OperationCanceledException)
        {
            _ = SendAsync(to, new JsonObject { ["t"] = "cancel", ["id"] = job.Id }, CancellationToken.None);
            return Fail(job, L.T("core.mp.private.cancelled"), FileEnd.Cancelled);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Net.Sockets.SocketException or ObjectDisposedException or InvalidOperationException)
        {
            return Fail(job, e.Message, FileEnd.Failed);
        }
        finally
        {
            _outgoing.TryRemove(job.Id, out _);
        }
    }

    private bool Fail(Outgoing job, string error, FileEnd end)
    {
        // Leaving the room ends every transfer: that is a lost connection (send again to resume), not a cancel.
        if (_closing && end == FileEnd.Cancelled) (end, error) = job.Started ? (FileEnd.Stalled, L.T("core.mp.private.stalled")) : (FileEnd.Offline, L.T("core.mp.private.offline"));
        Progress?.Invoke(new FileProgress(job.Id, job.To, job.Name, job.Size, Math.Min(Interlocked.Read(ref job.Acked) * ChunkSize, job.Size), true)
            { Finished = true, Error = error, End = end, Started = job.Started });
        return false;
    }

    private void Report(Outgoing job, long done) =>
        Progress?.Invoke(new FileProgress(job.Id, job.To, job.Name, job.Size, Math.Min(done, job.Size), true) { Started = job.Started });

    /// <summary>Where a paced send is: when it started (after resuming) and how much has gone since.</summary>
    private sealed class Pace(System.Diagnostics.Stopwatch clock)
    {
        public readonly System.Diagnostics.Stopwatch Clock = clock;
        public long Sent;
    }

    /// <summary>Waits as long as <see cref="SendLimit"/> asks: the bytes sent so far may not run ahead of the limit.</summary>
    private async Task PaceAsync(Pace pace, int bytes, CancellationToken ct)
    {
        pace.Sent += bytes;
        var limit = SendLimit();
        if (limit <= 0)
        {
            // Unlimited now: start counting afresh, so a later limit does not see a debt from the fast part.
            pace.Sent = 0;
            pace.Clock.Restart();
            return;
        }
        var due = TimeSpan.FromSeconds((double)pace.Sent / limit) - pace.Clock.Elapsed;
        if (due > TimeSpan.Zero) await Task.Delay(due < TimeSpan.FromSeconds(2) ? due : TimeSpan.FromSeconds(2), ct);
    }

    /// <summary>
    /// 收方点了接收：开始收，存到下载文件夹。
    /// 没收完的部分留在 “文件名.哈希前12位.jaypart” 里（失败、取消、断开都不删）：同一个文件再发过来时接着收，不用从头传。
    /// </summary>
    public async Task AcceptAsync(string id)
    {
        if (!_incoming.TryGetValue(id, out var job) || job.Stream is not null) return;
        Directory.CreateDirectory(Downloads);
        var temp = PartPath(Downloads, job.Offer.Name, job.Hash);
        var have = File.Exists(temp) ? new FileInfo(temp).Length : 0;
        if (have > job.Offer.Size) have = 0;
        var from = have / ChunkSize;
        // Written without adding DiskReserve to the size: a peer-declared size near long.MaxValue must not wrap around.
        var need = job.Offer.Size - from * ChunkSize;
        if (FreeSpaceOf(Downloads) is { } free && (free < DiskReserve || need > free - DiskReserve))
        {
            _incoming.TryRemove(id, out _);
            await SendAsync(job.Offer.FromFingerprint, new JsonObject { ["t"] = "decline", ["id"] = id }, _cts.Token);
            throw new IOException(L.F("core.mp.private.no_space", job.Offer.Name));
        }
        job.TargetPath = UniquePath(Downloads, job.Offer.Name);
        job.TempPath = temp;
        var stream = new FileStream(temp, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, ChunkSize, useAsync: true);
        var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            // 只留整块，已有的部分先算进哈希，最后整个文件对一遍 SHA-256。
            stream.SetLength(from * ChunkSize);
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer, _cts.Token)) > 0) hasher.AppendData(buffer, 0, read);
        }
        catch
        {
            await stream.DisposeAsync();
            hasher.Dispose();
            throw;
        }
        if (!_incoming.ContainsKey(id))
        {
            // The offer expired while the partial file was being hashed.
            await stream.DisposeAsync();
            hasher.Dispose();
            return;
        }
        job.Stream = stream;
        job.Hasher = hasher;
        job.Next = from;
        job.Received = from * ChunkSize;
        job.LastActivity = Clock();
        await SendAsync(job.Offer.FromFingerprint, new JsonObject { ["t"] = "accept", ["id"] = id, ["from"] = from }, _cts.Token);
        ReportIn(job);
    }

    public async Task DeclineAsync(string id)
    {
        if (!_incoming.TryRemove(id, out var job)) return;
        await SendAsync(job.Offer.FromFingerprint, new JsonObject { ["t"] = "decline", ["id"] = id }, _cts.Token);
    }

    /// <summary>取消一次传输（发送或接收都行）。</summary>
    public async Task CancelAsync(string id)
    {
        if (_outgoing.TryGetValue(id, out var send))
        {
            send.Cancelled = true;
            send.Answer.TrySetResult(false);
            send.Credits.Release();
            await SendAsync(send.To, new JsonObject { ["t"] = "cancel", ["id"] = id }, _cts.Token);
        }
        if (_incoming.TryRemove(id, out var receive))
        {
            // 先关掉临时文件再告诉对方：对方马上重发时，这边要已经能接着收。
            await _incomingGate.WaitAsync();
            try { Abort(receive, L.T("core.mp.private.cancelled"), FileEnd.Cancelled); }
            finally { _incomingGate.Release(); }
            await SendAsync(receive.Offer.FromFingerprint, new JsonObject { ["t"] = "cancel", ["id"] = id }, _cts.Token);
        }
    }

    // ---- 接收 ----

    private void OnFrame(PrivateFrame frame)
    {
        // Replies and offers are keyed by the sender's fingerprint, so it must be the fingerprint of the key that sealed it.
        if (MemberIdentity.FingerprintOf(frame.FromPublicKey) != frame.FromFingerprint) return;
        var plain = PrivateSeal.Open(_me, frame.FromPublicKey, frame.Sealed);
        if (plain is null) return;
        JsonObject? body;
        try { body = JsonNode.Parse(plain) as JsonObject; }
        catch (JsonException) { return; }
        if (body is not null) _inbox.Writer.TryWrite((frame, body));
    }

    private async Task HandleAsync(PrivateFrame frame, JsonObject body)
    {
        try
        {
            var type = body["t"]?.GetValue<string>();
            var id = body["id"]?.GetValue<string>();
            switch (type)
            {
                case "msg":
                    var text = RoomChat.Clean(body["text"]?.GetValue<string>(), MaxTextLength);
                    if (text.Length > 0) MessageReceived?.Invoke(new PrivateMessage(frame.FromFingerprint, frame.FromPublicKey, frame.FromName, text, DateTimeOffset.UtcNow));
                    break;
                case "offer" when id is { Length: 16 } && !_incoming.ContainsKey(id):
                    var size = body["size"]?.GetValue<long>() ?? -1;
                    var sha = body["sha"]?.GetValue<string>();
                    var name = SafeName(body["name"]?.GetValue<string>() ?? "");
                    if (size < 0 || sha is not { Length: 64 } || _incoming.Count >= MaxIncoming) return;
                    if (!AllowsFile(size))
                    {
                        await SendAsync(frame.FromFingerprint, new JsonObject { ["t"] = "decline", ["id"] = id }, _cts.Token);
                        return;
                    }
                    var offer = new FileOffer(id, frame.FromFingerprint, frame.FromName, name, size);
                    var wanted = WantedMod(frame.FromFingerprint, body["mod"]?.GetValue<string>(), size);
                    _incoming[id] = new Incoming { Offer = offer, Hash = Convert.FromHexString(sha), PeerKey = frame.FromPublicKey, ModSha1 = wanted, LastActivity = Clock() };
                    // A mod file we asked for during a sync: take it without a card. Anything else asks the user as before.
                    if (wanted is not null) await AcceptAsync(id);
                    else FileOffered?.Invoke(offer);
                    break;
                case "mods?" or "mods" or "modsoff" or "modfile?" or "modfileno":
                    await HandleModsAsync(frame, type, body);
                    break;
                case "accept" or "decline" when id is not null && _outgoing.TryGetValue(id, out var job) && job.To == frame.FromFingerprint:
                    if (type == "accept") Interlocked.Exchange(ref job.From, Math.Max(0, body["from"]?.GetValue<long>() ?? 0));
                    job.Answer.TrySetResult(type == "accept");
                    break;
                case "ack" when id is not null && _outgoing.TryGetValue(id, out var acked) && acked.To == frame.FromFingerprint:
                    var done = Interlocked.Increment(ref acked.Acked);
                    acked.Credits.Release();
                    Report(acked, done * ChunkSize);
                    break;
                case "cancel" when id is not null:
                    if (_outgoing.TryGetValue(id, out var cancelled) && cancelled.To == frame.FromFingerprint)
                    {
                        cancelled.Cancelled = true;
                        cancelled.Answer.TrySetResult(false);
                        cancelled.Credits.Release();
                    }
                    if (_incoming.TryGetValue(id, out var gone) && gone.Offer.FromFingerprint == frame.FromFingerprint && _incoming.TryRemove(id, out _))
                        Abort(gone, L.T("core.mp.private.cancelled"), FileEnd.Cancelled);
                    break;
                case "chunk" when id is not null && _incoming.TryGetValue(id, out var receiving) && receiving.Offer.FromFingerprint == frame.FromFingerprint:
                    await ReceiveChunkAsync(receiving, body);
                    break;
                case "done" when id is not null && _incoming.TryGetValue(id, out var finished) && finished.Offer.FromFingerprint == frame.FromFingerprint:
                    await FinishAsync(finished);
                    break;
            }
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException or IOException or UnauthorizedAccessException or ObjectDisposedException
            or OperationCanceledException or System.Net.Sockets.SocketException)
        {
        }
    }

    private async Task ReceiveChunkAsync(Incoming job, JsonObject body)
    {
        await _incomingGate.WaitAsync();
        try
        {
            if (job.Stream is null || job.Hasher is null) return;
            var index = body["i"]?.GetValue<long>() ?? -1;
            var data = Convert.FromBase64String(body["d"]?.GetValue<string>() ?? "");
            // 经房主的一条连接转交，顺序不会乱；乱了（或者超过声明的大小）就当出错，放弃这次接收。
            if (index != job.Next || data.Length is 0 or > ChunkSize || job.Received + data.Length > job.Offer.Size)
            {
                if (_incoming.TryRemove(job.Offer.Id, out _)) Abort(job, L.T("core.mp.private.corrupt"), FileEnd.Failed);
                await SendAsync(job.Offer.FromFingerprint, new JsonObject { ["t"] = "cancel", ["id"] = job.Offer.Id }, _cts.Token);
                return;
            }
            try
            {
                await job.Stream.WriteAsync(data, _cts.Token);
            }
            catch (IOException)
            {
                // 传到一半磁盘满了（或者盘被拔了）：放弃这次接收，已收的部分留着，腾出空间后再发一次就接着收。
                if (_incoming.TryRemove(job.Offer.Id, out _)) Abort(job, L.F("core.mp.private.no_space", job.Offer.Name), FileEnd.Failed);
                await SendAsync(job.Offer.FromFingerprint, new JsonObject { ["t"] = "cancel", ["id"] = job.Offer.Id }, _cts.Token);
                return;
            }
            job.Hasher.AppendData(data);
            job.Next++;
            job.Received += data.Length;
            job.LastActivity = Clock();
        }
        finally
        {
            _incomingGate.Release();
        }
        await SendAsync(job.Offer.FromFingerprint, new JsonObject { ["t"] = "ack", ["id"] = job.Offer.Id, ["i"] = job.Next - 1 }, _cts.Token);
        ReportIn(job);
    }

    private async Task FinishAsync(Incoming job)
    {
        await _incomingGate.WaitAsync();
        try
        {
            if (!_incoming.TryRemove(job.Offer.Id, out _) || job.Stream is null || job.Hasher is null) return;
            await job.Stream.DisposeAsync();
            job.Stream = null;
            var hash = job.Hasher.GetHashAndReset();
            if (job.Received != job.Offer.Size || !CryptographicOperations.FixedTimeEquals(hash, job.Hash))
            {
                Abort(job, L.T("core.mp.private.corrupt"), FileEnd.Failed, discard: true);
                return;
            }
            if (job.ModSha1 is { } expected && !MatchesSha1(job.TempPath!, expected))
            {
                Abort(job, L.T("core.mp.private.corrupt"), FileEnd.Failed, discard: true);
                return;
            }
            var target = File.Exists(job.TargetPath!) ? UniquePath(Path.GetDirectoryName(job.TargetPath!)!, job.Offer.Name) : job.TargetPath!;
            File.Move(job.TempPath!, target);
            Progress?.Invoke(new FileProgress(job.Offer.Id, job.Offer.FromFingerprint, job.Offer.Name, job.Offer.Size, job.Received, false) { Finished = true, SavedPath = target, Started = true });
            CompleteMod(job, target);
        }
        finally
        {
            _incomingGate.Release();
        }
    }

    private void ReportIn(Incoming job) =>
        Progress?.Invoke(new FileProgress(job.Offer.Id, job.Offer.FromFingerprint, job.Offer.Name, job.Offer.Size, job.Received, false) { Started = true });

    /// <summary>放弃这次接收。没收完的部分默认留着（下次接着收）；只有整个文件对不上哈希时才删（<paramref name="discard"/>）。</summary>
    private void Abort(Incoming job, string error, FileEnd end, bool discard = false)
    {
        var started = job.Stream is not null || job.Received > 0;
        try
        {
            job.Stream?.Dispose();
            job.Stream = null;
            if (discard && job.TempPath is { } temp && File.Exists(temp)) File.Delete(temp);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        job.Hasher?.Dispose();
        Progress?.Invoke(new FileProgress(job.Offer.Id, job.Offer.FromFingerprint, job.Offer.Name, job.Offer.Size, job.Received, false) { Finished = true, Error = error, End = end, Started = started });
        CompleteMod(job, null);
    }

    /// <summary>公共网络的策略：这个大小的文件能不能传（网络配置里可以关掉传文件或限大小）。收发两头都查。</summary>
    public Func<long, bool> AllowsFile { get; set; } = _ => true;

    /// <summary>查剩余空间的方法（测试里换掉）。</summary>
    internal Func<string, long?> FreeSpaceOf { get; set; } = FreeSpace;

    /// <summary>下载文件夹所在磁盘的剩余空间；查不到就返回 null（不拦）。</summary>
    internal static long? FreeSpace(string dir)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dir))!).AvailableFreeSpace; }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>对方给的文件名只留最后一段、去掉不能用的字符；空的叫 file。</summary>
    public static string SafeName(string name) => FileNames.Bare(name, "file");

    /// <summary>没收完的文件放这里：同名同内容（哈希一样）的文件再发来时找得到，接着收。</summary>
    internal static string PartPath(string dir, string name, byte[] hash) =>
        Path.Combine(dir, $"{name}.{Convert.ToHexStringLower(hash)[..12]}.jaypart");

    private static string UniquePath(string dir, string name)
    {
        var path = Path.Combine(dir, name);
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (var i = 1; File.Exists(path); i++) path = Path.Combine(dir, $"{stem} ({i}){ext}");
        return path;
    }

    private volatile bool _closing;

    public async ValueTask DisposeAsync()
    {
        _closing = true;
        _link.PrivateReceived -= OnFrame;
        _inbox.Writer.TryComplete();
        _cts.Cancel();
        try { await _pump; } catch (OperationCanceledException) { }
        try { await _sweeper; } catch (OperationCanceledException) { }
        foreach (var job in _outgoing.Values)
        {
            job.Cancelled = true;
            job.Answer.TrySetResult(false);
        }
        foreach (var id in _incoming.Keys.ToList())
            if (_incoming.TryRemove(id, out var job)) Abort(job, L.T("core.mp.private.stalled"), FileEnd.Stalled);
        _cts.Dispose();
    }
}
