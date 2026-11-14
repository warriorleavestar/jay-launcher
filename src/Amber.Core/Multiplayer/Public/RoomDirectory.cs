using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amber.Core.Localization;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Multiplayer.Public;

/// <summary>
/// 目录服务的协议：一条连接一个请求，请求和回复各是一段 JSON（UTF-8，最长 16 KB），只经 <see cref="DirectoryChannel"/>
/// 加密传输（不再有明文请求）。
/// announce 发布 / 续期房间（房主用房间的签名钥匙签过，房间编号由签名公钥派生，序号只能变大）；
/// withdraw 撤下；snap 取整份目录快照（公开房间 + 每个隐藏房间封好的条目，见 <see cref="DirectorySnapshots"/>：
/// 拿着邀请码的人在本机的快照里找房主，目录看不到谁找了哪个房间）；
/// find 按设备指纹码找房间（房主打开了“可以用指纹码找到这个房间”时，发布时带上指纹密钥，目录拿它认码）。
/// 目录服务只存这些，不经手任何钥匙；房主 90 秒没续期就从列表里消失。
/// <para>
/// post / fetch / drop are mailboxes for sealed mail between identities (<see cref="Contacts.SealedMail"/>): contact
/// requests and room invites that never touch a chat platform. The directory keeps opaque blobs under a daily mailbox id
/// for <see cref="MailTtl"/>; it can't read them, can't tell who sent them, and only the recipient can delete one early.
/// </para>
/// </summary>
public static class RoomDirectoryProtocol
{
    public const int MaxLine = 16 * 1024;

    /// <summary>Largest reply (a full snapshot of a busy directory); every other reply stays within <see cref="MaxLine"/>.</summary>
    public const int MaxReply = 16 * 1024 * 1024;

    /// <summary>Snapshot replies are padded to a multiple of this, so their size says little about what changed.</summary>
    public const int SnapshotPad = 64 * 1024;
    public const int MaxLookupKeys = 4;
    /// <summary>Sealed mail kept per mailbox; more is refused with "full" until the recipient picks some up or it expires.</summary>
    public const int MaxMailPerBox = 32;
    /// <summary>Mailboxes asked for in one fetch (yesterday's, today's and tomorrow's).</summary>
    public const int MaxFetchBoxes = 3;
    public static readonly TimeSpan MailTtl = TimeSpan.FromHours(48);
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan RenewEvery = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    /// <summary>How long a snapshot call may take until the reply's length arrives (then <see cref="SnapshotDeadline"/> applies).</summary>
    public static readonly TimeSpan SnapshotTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long a reply of <paramref name="bytes"/> may take to go out: a base plus the time at
    /// <see cref="RoomDirectoryServer.MinSnapshotRate"/>. The directory and the reader use the same rule.
    /// </summary>
    public static TimeSpan SnapshotDeadline(long bytes) => RequestTimeout + TimeSpan.FromSeconds((double)bytes / RoomDirectoryServer.MinSnapshotRate);

    /// <summary><paramref name="bytes"/> rounded up to a whole number of <see cref="SnapshotPad"/>: what is sent.</summary>
    public static long Padded(long bytes) => (bytes + SnapshotPad - 1) / SnapshotPad * SnapshotPad;

}

/// <summary>目录服务（跑在公共网络节点那台机器上，监听 127.0.0.1:47668，由同一台机器上的公共网络 EasyTier 把 10.235.0.1:47668 交过来）。</summary>
public sealed class RoomDirectoryServer : IAsyncDisposable
{
    public const int MaxRooms = 5000;
    public const int MaxConcurrent = 64;
    public const int MaxListed = 300;

    private sealed class Entry
    {
        public required RoomListing Listing;
        public bool Listed;
        /// <summary>Indexes (hex) of this room's hidden entries in the snapshot (<see cref="HiddenEntry"/>).</summary>
        public string[] Lookup = [];
        /// <summary>房主的指纹密钥（没打开指纹查找时为 null）。</summary>
        public byte[]? Search;
        public DateTimeOffset Expires;
        /// <summary>The host's verified network account (null when the host did not log in).</summary>
        public string? Account;
    }

    private sealed record SealedItem(string Blob, string DropHash, DateTimeOffset Expires);

    /// <summary>All mailboxes together hold at most this many items (about 32 MB of blobs at the largest size).</summary>
    public const int MaxSealedMail = 8000;

    private readonly TcpListener _listener;
    private readonly Func<DateTimeOffset> _now;
    private readonly Action<string>? _log;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _rooms = new(StringComparer.Ordinal);
    /// <summary>
    /// Hidden entry index (hex) → the rooms with an entry there (room id → slot tag). Several rooms may share an index, up
    /// to <see cref="MaxEntriesPerIndex"/>. Only holders of the invite can publish or open entries there, but holders
    /// aren't necessarily the host: a full index only costs the host that one entry (the room is still registered), and
    /// readers that don't know the host's key trust no index whose entries disagree about the host.
    /// </summary>
    private readonly Dictionary<string, Dictionary<string, string>> _lookup = new(StringComparer.Ordinal);

    /// <summary>Rooms that may have an entry under the same hidden index.</summary>
    public const int MaxEntriesPerIndex = 8;

    /// <summary>One snapshot item, serialized once: the JSON sent to readers and what a reader sees of it (for change checks).</summary>
    private sealed record SnapItem(string Json, string Visible);

    /// <summary>
    /// The snapshot as it is now: "r:&lt;id&gt;" → public listing, "h:&lt;index&gt;:&lt;tag&gt;" → sealed hidden entry
    /// (one per room and index; the tag is random, so it doesn't tie the entry to its room).
    /// </summary>
    private readonly Dictionary<string, SnapItem> _snapshot = new(StringComparer.Ordinal);
    /// <summary>Bytes of every item's JSON together (a full snapshot is about this plus a little framing).</summary>
    private long _snapshotBytes;
    /// <summary>The full snapshot and some small deltas, serialized, for <see cref="_snapshotVersion"/>.</summary>
    private byte[]? _fullReply;
    private readonly Dictionary<long, byte[]> _deltaReplies = [];
    private long _deltaReplyBytes;
    /// <summary>Bytes of cached deltas, and how many replies were serialized (tests).</summary>
    internal long CachedDeltaBytes { get { lock (_gate) return _deltaReplyBytes; } }
    internal int SnapshotsBuilt => Volatile.Read(ref _snapshotsBuilt);
    private int _snapshotsBuilt;
    internal SemaphoreSlim LargeSnapshotSlots => _snapshotSlots;
    /// <summary>Cached deltas together stay under this; only small deltas are cached at all.</summary>
    internal const long MaxCachedDeltaBytes = 2L * SmallSnapshot;
    /// <summary>
    /// A delta that would carry more than this share of the full snapshot is served as the (cached) full snapshot
    /// instead, so asking from many old versions costs no more than asking for the full one.
    /// </summary>
    internal const int DeltaShareOfFull = 4;
    private long _repliesVersion = -1;

    /// <summary>
    /// Largest a full snapshot may grow (items' JSON together). New publishes that would pass it are refused with "full",
    /// so a full snapshot always fits in <see cref="RoomDirectoryProtocol.MaxReply"/> with its padding.
    /// </summary>
    internal long SnapshotBudget { get; set; } = RoomDirectoryProtocol.MaxReply - 4L * RoomDirectoryProtocol.SnapshotPad;

    /// <summary>
    /// Large snapshot replies (a full snapshot of a busy directory) sent at the same time at most, and how many may start
    /// per second on average (a bucket of <see cref="LargeSnapshotBurst"/>). Small replies (the usual once-a-minute
    /// deltas) have their own <see cref="MaxConcurrentSmallSnapshots"/> slots, so large ones never hold them up.
    /// </summary>
    public const int MaxConcurrentSnapshots = 8;
    public const int MaxConcurrentSmallSnapshots = 32;
    public const int LargeSnapshotBurst = 32;
    public const double LargeSnapshotsPerSecond = 4;
    /// <summary>Replies up to this size count as small.</summary>
    public const int SmallSnapshot = 4 * RoomDirectoryProtocol.SnapshotPad;
    /// <summary>
    /// A reader must take a snapshot at least this fast (bytes per second) on top of <see cref="RoomDirectoryProtocol.RequestTimeout"/>,
    /// and no single chunk may wait longer than that: slow readers are dropped instead of holding a slot.
    /// </summary>
    public const int MinSnapshotRate = 256 * 1024;
    /// <summary>
    /// Bytes of large snapshot replies the directory starts sending per second on average (a bucket that holds
    /// <see cref="LargeSnapshotByteBurst"/>), so a few small requests can't make it send without end.
    /// </summary>
    public long LargeSnapshotBytesPerSecond { get; set; } = 4L * 1024 * 1024;

    /// <summary>The most the byte bucket holds (setting it fills the bucket).</summary>
    public long LargeSnapshotByteBurst
    {
        get { lock (_gate) return _largeByteBurst; }
        set { lock (_gate) _largeBytes = _largeByteBurst = Math.Max(value, RoomDirectoryProtocol.MaxReply); }
    }
    private long _largeByteBurst = 2L * RoomDirectoryProtocol.MaxReply;
    /// <summary>
    /// Snapshots one source address may ask for per minute (readers poll once a minute).
    /// </summary>
    public const int MaxSnapshotsPerSourcePerMinute = 6;
    /// <summary>How often ended per-source snapshot windows are swept out.</summary>
    internal static readonly TimeSpan SnapshotAsksSweepEvery = TimeSpan.FromSeconds(10);

    /// <summary>Don't count loopback sources against <see cref="MaxSnapshotsPerSourcePerMinute"/> (off by default).</summary>
    public bool UncountedLoopback { get; set; }
    private readonly SemaphoreSlim _snapshotSlots = new(MaxConcurrentSnapshots, MaxConcurrentSnapshots);
    private readonly SemaphoreSlim _smallSnapshotSlots = new(MaxConcurrentSmallSnapshots, MaxConcurrentSmallSnapshots);
    private double _largeTokens = LargeSnapshotBurst;
    private double _largeBytes = 2L * RoomDirectoryProtocol.MaxReply;
    private DateTimeOffset _largeTokensAt = DateTimeOffset.MinValue;
    private readonly Dictionary<string, (int Count, DateTimeOffset Until)> _snapshotAsks = new(StringComparer.Ordinal);
    private DateTimeOffset _snapshotAsksSwept = DateTimeOffset.MinValue;
    /// <summary>Snapshot changes in order (version, key), for deltas. Trimmed to <see cref="MaxSnapshotLog"/>.</summary>
    private readonly Queue<(long Version, string Key)> _snapshotLog = new();
    private long _snapshotVersion;
    /// <summary>Deltas can be served from this version on (older ones get the full snapshot).</summary>
    private long _snapshotFloor;
    /// <summary>Changes with every start, so a client never applies a delta to a copy from an earlier run.</summary>
    private readonly string _snapshotEpoch = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));

    /// <summary>Snapshot changes kept for deltas (a few renewals of every room).</summary>
    public const int MaxSnapshotLog = 8 * MaxRooms;
    /// <summary>指纹密钥（十六进制）→ 房间编号，先到先得。</summary>
    private readonly Dictionary<string, string> _search = new(StringComparer.Ordinal);
    /// <summary>撤下过的房间和撤下时的序号（之后拿旧签名重新发布的不认）。只留最近的一些。</summary>
    private readonly Dictionary<string, long> _withdrawn = new(StringComparer.Ordinal);
    /// <summary>Mailbox id → sealed mail waiting there.</summary>
    private readonly Dictionary<string, List<SealedItem>> _mailboxes = new(StringComparer.Ordinal);
    private int _sealedCount;
    /// <summary>Lookup (hex) → signed record (<see cref="SignedRecord"/>), replaced only by its signer with a higher seq.</summary>
    private readonly Dictionary<string, SignedRecord> _records = new(StringComparer.Ordinal);
    /// <summary>Record puts per source address in the current hour.</summary>
    private readonly Dictionary<string, (int Count, DateTimeOffset Until)> _recordPuts = new(StringComparer.Ordinal);

    /// <summary>Signed records at most (about 110 MB of base64 blobs at the largest size; real records are far smaller).</summary>
    public const int MaxRecords = 20000;

    /// <summary>Record puts (new or renewed) one source address may make per hour.</summary>
    public const int MaxRecordPutsPerSource = 60;

    /// <summary>How far ahead of this directory's clock a record's expiry may be beyond <see cref="SignedRecord.MaxLifetime"/>.</summary>
    private static readonly TimeSpan RecordClockSkew = TimeSpan.FromMinutes(10);
    private readonly SemaphoreSlim _slots = new(MaxConcurrent, MaxConcurrent);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _accept;

    public int Port { get; }

    /// <summary>账号密码登录（jay-server directory --accounts 时才有）。</summary>
    public LoginAuthority? Login { get; set; }

    /// <summary>
    /// Oldest Jay allowed to use this directory (from the network config's policy.minVersion). Every request carries the
    /// client's version in "jay"; older or unversioned clients get error "version" plus "min", and Jay tells the user to update.
    /// </summary>
    public Version? MinClientVersion { get; set; }

    /// <summary>Only hosts logged in to this network's accounts may publish rooms (set when the network config requires login).</summary>
    public bool RequireAccountToPublish { get; set; }

    /// <summary>
    /// This directory's X25519 identity for <see cref="DirectoryChannel"/>. Clients of a network that pins it ("dirKeys")
    /// or certifies it insist on it. Null: a throwaway key for this process (the channel is still encrypted, but clients
    /// can only trust it on first use).
    /// </summary>
    public X25519KeyPair? Identity { get; set; }

    private readonly Lazy<X25519KeyPair> _ephemeralIdentity = new(X25519KeyPair.Generate);

    /// <summary>The network's EasyTier secret, for <see cref="DirectoryChannel"/>'s psk.</summary>
    public string NetworkSecret { get; set; } = "";

    /// <summary>
    /// This directory's certificate from the network's root key (<see cref="DirectoryCertificate"/>), sent in the
    /// handshake so Jay can accept a key that isn't pinned in the connection string. Null: none.
    /// </summary>
    public byte[]? Certificate { get; set; }

    /// <summary>Volunteer nodes ("volunteer" / "nodes" ops). Null = this network doesn't take volunteers (error "off").</summary>
    public VolunteerRegistry? Volunteers { get; set; }

    /// <summary>房间审核规则（按顺序，任何一条拒绝就不收）。见 <see cref="IRoomGate"/>。</summary>
    public IReadOnlyList<IRoomGate> Gates { get; set; } = [];

    /// <summary>目录事件的订阅者。见 <see cref="IDirectoryObserver"/>。</summary>
    public IReadOnlyList<IDirectoryObserver> Observers { get; set; } = [];

    /// <summary>自定义请求（op 以 x- 开头）。见 <see cref="IDirectoryExtension"/>。</summary>
    public IReadOnlyList<IDirectoryExtension> Extensions { get; set; } = [];

    /// <summary>锁里攒下的事件，出锁后再通知（订阅者不在锁里跑）。</summary>
    private readonly List<Action<IDirectoryObserver>> _events = [];

    public RoomDirectoryServer(IPAddress address, int port, Action<string>? log = null, Func<DateTimeOffset>? clock = null)
    {
        _now = clock ?? (() => DateTimeOffset.UtcNow);
        _log = log;
        _listener = new TcpListener(address, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _accept = AcceptLoopAsync();
    }

    /// <summary>Signed records held (a total, for metrics).</summary>
    public int RecordCount
    {
        get { lock (_gate) { PruneRecords(); return _records.Count; } }
    }

    public int Count
    {
        get
        {
            try { lock (_gate) { Prune(); return _rooms.Count; } }
            finally { Flush(); }
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException) { break; }
            if (!_slots.Wait(0))
            {
                client.Dispose();
                continue;
            }
            var slot = new SlotLease(_slots);
            _ = Task.Run(async () =>
            {
                try { await HandleAsync(client, slot); }
                finally
                {
                    client.Dispose();
                    slot.Release();
                }
            });
        }
    }

    /// <summary>General slots free right now (tests).</summary>
    internal int FreeSlots => _slots.CurrentCount;

    /// <summary>A connection's general slot, given back once: when the connection ends, or earlier (see <see cref="SnapshotAsync"/>).</summary>
    private sealed class SlotLease(SemaphoreSlim slots)
    {
        private int _released;

        public void Release()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) slots.Release();
        }
    }

    private async Task HandleAsync(TcpClient client, SlotLease slot)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        deadline.CancelAfter(RoomDirectoryProtocol.RequestTimeout);
        try
        {
            var stream = client.GetStream();
            var first = new byte[1];
            if (!await SecureStream.ReadExactAsync(stream, first, deadline.Token)) return;
            // Only the authenticated channel is spoken. An old plaintext JSON request is dropped at once; anything else
            // fails the handshake's first-message check and is dropped without a reply too.
            if (first[0] == DirectoryChannel.LegacyFirstByte) return;
            if (await DirectoryChannel.AcceptAsync(stream, first[0], Identity ?? _ephemeralIdentity.Value, NetworkSecret, deadline.Token, Certificate) is not { } secure) return;
            if (Text(secure.Request["op"]) == "snap")
            {
                await SnapshotAsync(secure, Source(client), slot);
                return;
            }
            await secure.Reply(Handle(secure.Request, Source(client)), deadline.Token);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or OperationCanceledException or SocketException or EndOfStreamException
            or ObjectDisposedException) { }
        // A request this code didn't expect to fail on: dropped like the rest, but noted (once a minute at most).
        catch (Exception e) when (e is InvalidOperationException or FormatException) { LogDropped(e); }
    }

    private long _droppedLoggedAt;

    private void LogDropped(Exception e)
    {
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _droppedLoggedAt);
        if (now - last < 60_000 && last != 0) return;
        if (Interlocked.CompareExchange(ref _droppedLoggedAt, now, last) != last) return;
        _log?.Invoke($"Directory request dropped: {e.GetType().Name}: {e.Message}");
    }

    /// <summary>A JSON string value, or null for anything else (a number where a string belongs isn't an exception).</summary>
    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary>
    /// Sends a snapshot: its own few slots and a per-source limit, so slow or greedy readers can't hold up the other ops;
    /// the reply is serialized once per version and sent in chunks, each of which must go out within
    /// <see cref="RoomDirectoryProtocol.RequestTimeout"/> (a reader that stops reading is dropped, one that reads slowly isn't).
    /// </summary>
    /// <param name="general">
    /// The connection's general slot: given back once the send holds a snapshot slot, so slow readers (bounded by the
    /// snapshot slots) don't also hold the slots every other op needs.
    /// </param>
    private async Task SnapshotAsync((JsonObject Request, DirectoryReply Reply, DirectorySnapshotReply Snapshot) secure, string? source,
        SlotLease? general = null)
    {
        using var quick = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        quick.CancelAfter(RoomDirectoryProtocol.RequestTimeout);
        OnRequest?.Invoke((JsonObject)secure.Request.DeepClone());
        if (VersionError(secure.Request) is { } old)
        {
            await secure.Reply(old, quick.Token);
            return;
        }
        if (source is not null && !(UncountedLoopback && IPAddress.TryParse(source, out var address) && IPAddress.IsLoopback(address)))
        {
            bool limited;
            lock (_gate)
            {
                var now = _now();
                // Windows that ended, or that end further off than one minute (the clock went back), start over.
                bool Over(DateTimeOffset until) => until <= now || until > now + TimeSpan.FromMinutes(1);
                // Sweeping every source costs time under the lock, so it happens at most every few seconds; the caller's
                // own window is checked each time.
                if (now - _snapshotAsksSwept >= SnapshotAsksSweepEvery || now < _snapshotAsksSwept)
                {
                    _snapshotAsksSwept = now;
                    foreach (var (who, _) in _snapshotAsks.Where(p => Over(p.Value.Until)).ToList())
                        _snapshotAsks.Remove(who);
                }
                var (count, until) = _snapshotAsks.TryGetValue(source, out var used) && !Over(used.Until) ? used : (0, now + TimeSpan.FromMinutes(1));
                limited = count >= MaxSnapshotsPerSourcePerMinute;
                if (!limited) _snapshotAsks[source] = (count + 1, until);
            }
            if (limited)
            {
                await secure.Reply(Error("rate"), quick.Token);
                return;
            }
        }
        // A slot comes first, chosen from what the request says (cheap, no scanning): a reader whose copy is from another
        // run or too old needs the full snapshot. Planning and serializing happen only with a slot in hand.
        if (SnapshotWanted(secure.Request) is not { } wanted)
        {
            await secure.Reply(Error("bad"), quick.Token);
            return;
        }
        SemaphoreSlim? slots = null;
        try
        {
            if (wanted.Full && wanted.Size > SmallSnapshot) slots = TakeLargeSlot(RoomDirectoryProtocol.Padded(wanted.Size));
            else if (_smallSnapshotSlots.Wait(0)) slots = _smallSnapshotSlots;
            if (slots is null)
            {
                await secure.Reply(Error("busy"), quick.Token);
                return;
            }
            general?.Release();
            SnapshotPlan plan;
            try { plan = PlanSnapshot(secure.Request); }
            finally { Flush(); }
            // A delta nearly as big as everything is answered with everything: that takes a large slot too.
            if (plan.Size > SmallSnapshot && slots == _smallSnapshotSlots)
            {
                slots.Release();
                slots = TakeLargeSlot(RoomDirectoryProtocol.Padded(plan.Size));
                if (slots is null)
                {
                    await secure.Reply(Error("busy"), quick.Token);
                    return;
                }
            }
            var reply = BuildSnapshot(plan);
            using var whole = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            // The reader's deadline is from the length it is told, which is the padded one: use the same here.
            whole.CancelAfter(RoomDirectoryProtocol.SnapshotDeadline(RoomDirectoryProtocol.Padded(reply.LongLength)));
            await secure.Snapshot(reply, RoomDirectoryProtocol.SnapshotPad, RoomDirectoryProtocol.RequestTimeout, whole.Token);
        }
        finally { slots?.Release(); }
    }

    /// <summary>
    /// What a "snap" request asks for, without scanning anything: whether it needs the full snapshot, and about how
    /// big that is. Null when its fields have the wrong types.
    /// </summary>
    private (bool Full, long Size)? SnapshotWanted(JsonObject request)
    {
        var epoch = request["epoch"];
        var have = request["have"];
        if (epoch is not null && Text(epoch) is null) return null;
        long version = -1;
        if (have is not null && !(have is JsonValue value && value.TryGetValue(out version))) return null;
        lock (_gate)
        {
            var full = Text(epoch) != _snapshotEpoch || version < _snapshotFloor || version > _snapshotVersion;
            return (full, _snapshotBytes + 256);
        }
    }

    /// <summary>A large-snapshot slot plus a start and <paramref name="bytes"/> from the buckets, or null (all or nothing).</summary>
    private SemaphoreSlim? TakeLargeSlot(long bytes)
    {
        if (!_snapshotSlots.Wait(0)) return null;
        if (TakeLargeToken(bytes)) return _snapshotSlots;
        _snapshotSlots.Release();
        return null;
    }

    /// <summary>
    /// Takes one start from the large-snapshot bucket (refilled at <see cref="LargeSnapshotsPerSecond"/>) and
    /// <paramref name="bytes"/> from the byte bucket (refilled at <see cref="LargeSnapshotBytesPerSecond"/>): both or neither.
    /// </summary>
    private bool TakeLargeToken(long bytes)
    {
        lock (_gate)
        {
            var now = _now();
            if (_largeTokensAt != DateTimeOffset.MinValue)
            {
                // A clock that went back adds nothing (and takes nothing): the buckets don't sink below what they hold.
                var seconds = Math.Max(0, (now - _largeTokensAt).TotalSeconds);
                _largeTokens = Math.Min(LargeSnapshotBurst, _largeTokens + seconds * LargeSnapshotsPerSecond);
                _largeBytes = Math.Min(_largeByteBurst, _largeBytes + seconds * LargeSnapshotBytesPerSecond);
            }
            _largeTokensAt = now;
            if (_largeTokens < 1 || _largeBytes < bytes) return false;
            _largeTokens--;
            _largeBytes -= bytes;
            return true;
        }
    }

    private static string? Source(TcpClient client) => (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString();

    /// <summary>Sees every request as it arrives (tests check that readers all send the same thing).</summary>
    internal Action<JsonObject>? OnRequest { get; set; }

    /// <summary>处理一个请求（测试和框架的宿主直接调）。</summary>
    public JsonObject Handle(JsonObject request) => Handle(request, source: null);

    /// <summary>Handles one request. <paramref name="source"/>: the caller's address, for per-source limits.</summary>
    internal JsonObject Handle(JsonObject request, string? source)
    {
        OnRequest?.Invoke((JsonObject)request.DeepClone());
        try
        {
            var op = request["op"]?.GetValue<string>();
            if (VersionError(request) is { } old) return old;
            return op switch
            {
                "announce" => Announce(request),
                "withdraw" => Withdraw(request),
                "snap" => JsonNode.Parse(BuildSnapshot(PlanSnapshot(request))) as JsonObject ?? Error("bad"),
                "find" => Find(request),
                "post" => Post(request),
                "fetch" => Fetch(request),
                "drop" => Drop(request),
                "rec.put" => RecordPut(request, source),
                "rec.get" => RecordGet(request),
                "volunteer" => Volunteers is { } v ? v.Register(request["url"]?.GetValue<string>(), request["operator"], source) : Error("off"),
                // Blocks this connection's task for at most a few seconds while the directory dials the address.
                "probe" => Volunteers is { } p ? p.CheckAsync(request["url"]?.GetValue<string>()).GetAwaiter().GetResult() : Error("off"),
                "nodes" => Volunteers is { } v ? NodesReply(v.Pick()) : Error("off"),
                "login" when Login is { } login => LoginRequest(login, request),
                _ when DirectoryExtensions.IsValidOp(op) && Extensions.FirstOrDefault(x => x.Op == op) is { } extension => Extend(extension, request),
                _ => Error("op"),
            };
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException or ArgumentException or JsonException)
        {
            return Error("bad");
        }
        finally
        {
            Flush();
        }
    }

    private JsonObject? VersionError(JsonObject request) =>
        MinClientVersion is { } min && !ClientVersion.Meets(Text(request["jay"]), min)
            ? new JsonObject { ["ok"] = false, ["error"] = "version", ["min"] = min.ToString() }
            : null;

    /// <summary>Logins checked at once. Each costs a slow password hash, so a flood of them can't take every core.</summary>
    public const int MaxConcurrentLogins = 2;

    /// <summary>Failed logins the whole directory takes per minute; after that every login gets "busy" until the minute ends.</summary>
    public const int MaxFailedLoginsPerMinute = 60;

    /// <summary><see cref="MaxFailedLoginsPerMinute"/>, or fewer in tests.</summary>
    internal int FailedLoginsPerMinute { get; init; } = MaxFailedLoginsPerMinute;

    private readonly SemaphoreSlim _loginSlots = new(MaxConcurrentLogins, MaxConcurrentLogins);
    private readonly Lock _loginGate = new();
    private DateTimeOffset _failedLoginsSince = DateTimeOffset.MinValue;
    private int _failedLogins;

    private JsonObject LoginRequest(LoginAuthority login, JsonObject request)
    {
        // Callers over the network can't be told apart, so these limits are directory-wide. Names that exist and names
        // that don't are treated the same, so "busy" says nothing about which accounts exist.
        if (!_loginSlots.Wait(0)) return Error("busy");
        try
        {
            lock (_loginGate)
            {
                var now = _now();
                if (now - _failedLoginsSince >= TimeSpan.FromMinutes(1) || now < _failedLoginsSince) (_failedLoginsSince, _failedLogins) = (now, 0);
                if (_failedLogins >= FailedLoginsPerMinute) return Error("busy");
            }
            var reply = login.Handle(request);
            var ok = reply["ok"]?.GetValue<bool>() == true;
            var error = reply["error"]?.GetValue<string>();
            if (error is "wrong" or "locked" or "disabled")
                lock (_loginGate) _failedLogins++;
            lock (_gate) _events.Add(o => o.OnLogin(ok, error));
            return reply;
        }
        finally { _loginSlots.Release(); }
    }

    private JsonObject Extend(IDirectoryExtension extension, JsonObject request)
    {
        JsonObject reply;
        try { reply = extension.Handle(request, View()); }
        catch (Exception e) when (e is not OutOfMemoryException) { return Error("ext"); }
        reply["ok"] ??= true;
        return reply;
    }

    /// <summary>把锁里攒下的事件发给订阅者。订阅者出错不影响目录。</summary>
    private void Flush()
    {
        Action<IDirectoryObserver>[] events;
        lock (_gate)
        {
            if (_events.Count == 0 || Observers.Count == 0) { _events.Clear(); return; }
            events = [.. _events];
            _events.Clear();
        }
        foreach (var observer in Observers)
            foreach (var e in events)
            {
                try { e(observer); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { _log?.Invoke(L.F("core.mp.public.observer_error", ex.Message)); }
            }
    }

    /// <summary>目录快照：房间总数、公开数、公开列表。</summary>
    public DirectoryView View()
    {
        lock (_gate)
        {
            Prune();
            var listed = _rooms.Values.Where(e => e.Listed && e.Listing.Mode != RoomVisibility.Hidden).Select(e => e.Listing).ToList();
            return new DirectoryView(_rooms.Count, listed.Count, listed);
        }
    }

    /// <summary>运营者看的公开房间（隐藏房间不给内容，只在 <see cref="View"/> 里计数）。</summary>
    public IReadOnlyList<DirectoryRoomInfo> ListedRooms()
    {
        try
        {
            lock (_gate)
            {
                Prune();
                return [.. _rooms.Values.Where(e => e.Listed && e.Listing.Mode != RoomVisibility.Hidden).OrderByDescending(e => e.Listing.Updated)
                    .Select(e => new DirectoryRoomInfo(e.Listing.Id, e.Listing.Name, e.Listing.Host, e.Listing.HostFingerprint, e.Listing.Mode,
                        e.Listing.Players, e.Listing.Game, e.Listing.Updated, e.Account))];
            }
        }
        finally { Flush(); }
    }

    /// <summary>运营者删掉一个房间（房主下次续期时会再过一遍审核规则；要彻底挡住就同时封禁）。</summary>
    public bool Remove(string id)
    {
        try
        {
            lock (_gate)
            {
                if (!_rooms.TryGetValue(id, out var entry)) return false;
                RemoveLocked(id, entry, RoomRemoval.Removed);
                return true;
            }
        }
        finally { Flush(); }
    }

    /// <summary>The verified account of a room's host, if any (for "ban this room's host").</summary>
    public (string HostFingerprint, string? Account)? HostOf(string id)
    {
        lock (_gate) return _rooms.TryGetValue(id, out var e) ? (e.Listing.HostFingerprint, e.Account) : null;
    }

    /// <summary>Removes every room published under a network account. Returns how many.</summary>
    public int RemoveAccount(string account)
    {
        try
        {
            lock (_gate)
            {
                var ids = _rooms.Where(p => p.Value.Account == account).Select(p => p.Key).ToList();
                foreach (var id in ids) RemoveLocked(id, _rooms[id], RoomRemoval.Removed);
                return ids.Count;
            }
        }
        finally { Flush(); }
    }

    /// <summary>删掉某个房主（联机身份指纹）开的所有房间。返回删了几个。</summary>
    public int RemoveHost(string hostFingerprint)
    {
        try
        {
            lock (_gate)
            {
                var ids = _rooms.Where(p => string.Equals(p.Value.Listing.HostFingerprint, hostFingerprint, StringComparison.OrdinalIgnoreCase)).Select(p => p.Key).ToList();
                foreach (var id in ids) RemoveLocked(id, _rooms[id], RoomRemoval.Removed);
                return ids.Count;
            }
        }
        finally { Flush(); }
    }

    private static JsonObject Error(string code) => new() { ["ok"] = false, ["error"] = code };

    private static JsonObject Error(RoomVerdict verdict)
    {
        var reply = Error(verdict.Code!);
        if (verdict.Reason is { Length: > 0 } reason) reply["reason"] = reason;
        return reply;
    }

    /// <summary>"nodes" reply: just the addresses, already spread over operators (who runs which stays in the directory).</summary>
    private static JsonObject NodesReply(IReadOnlyList<VolunteerNode> nodes) => new()
    {
        ["ok"] = true,
        ["nodes"] = new JsonArray([.. nodes.Select(n => (JsonNode)n.Url)]),
    };

    private JsonObject Announce(JsonObject request)
    {
        // FromJson 已经核对过签名和编号。
        if (RoomListing.FromJson(request["room"]) is not { } listing || listing.Ip.Length == 0) return Error("room");
        var listed = request["listed"]?.GetValue<bool>() ?? false;
        var hidden = new Dictionary<string, string>(StringComparer.Ordinal);
        var clockOff = false;
        foreach (var item in (request["hidden"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var index = item["k"]?.GetValue<string>()?.ToLowerInvariant();
            var blob = item["b"]?.GetValue<string>();
            var key = item["p"]?.GetValue<string>();
            var sig = item["s"]?.GetValue<string>();
            if (!IsHex(index, HiddenEntry.IndexLength * 2) || blob is null || blob.Length != (HiddenEntry.SealedLength + 2) / 3 * 4
                || !IsHex(key, HiddenEntry.PublicKeyLength * 2) || !IsHex(sig, HiddenEntry.SignatureLength * 2)) return Error("bad");
            var bytes = Convert.FromBase64String(blob);
            if (bytes.Length != HiddenEntry.SealedLength) return Error("bad");
            if (hidden.Count >= RoomDirectoryProtocol.MaxLookupKeys) continue;
            // Only a holder of the invite can sign for its index; the signature covers this room and this version of
            // the listing, and the listing's version is its time, so it can't be replayed for another room or much later.
            // A host whose clock is off still gets a public room listed; only its hidden entries are left out (and it is told).
            var nowMs = _now().ToUnixTimeMilliseconds();
            var skew = (long)HiddenEntry.MaxClockSkew.TotalMilliseconds;
            if (listing.Seq < nowMs - skew || listing.Seq > nowMs + skew)
            {
                clockOff = true;
                continue;
            }
            if (!HiddenEntry.VerifyPublish(Convert.FromHexString(index!), Convert.FromHexString(key!), bytes, listing.Id, listing.Seq, Convert.FromHexString(sig!))) return Error("lookup");
            hidden.TryAdd(index!, blob);
        }
        var fp = request["fp"]?.GetValue<string>();
        if (fp is not null && (fp.Length != 64 || !fp.All(char.IsAsciiHexDigit))) return Error("bad");
        fp = fp?.ToLowerInvariant();
        if (!listed && hidden.Count == 0 && fp is null) return Error(clockOff ? "clock" : "empty");
        // The host's account: a login token this node's login service signed for exactly this host identity.
        string? account = null;
        if (Login is { } login && request["account"]?.GetValue<string>() is { Length: > 0 and <= 2048 } token)
            account = AccountToken.Verify(token, login.SignKey, login.Network, listing.HostKey, _now())?.User;
        if (RequireAccountToPublish && account is null) return Error("login");
        lock (_gate)
        {
            Prune();
            if (_rooms.TryGetValue(listing.Id, out var entry))
            {
                // 旧的签名版本（重放）不认。
                if (listing.Seq <= entry.Listing.Seq) return Error("stale");
            }
            else
            {
                // A room this directory doesn't hold must come with a recent version: the version is the host's signing
                // time, so a listing copied from an earlier snapshot can't bring a closed room back after it expired or
                // after a restart. A host whose clock is behind is told this directory's time and signs again (see
                // RoomDirectoryClient.AnnounceAsync); renewals of rooms already held aren't checked.
                var nowMs = _now().ToUnixTimeMilliseconds();
                if (listing.Seq < nowMs - (long)MaxNewListingAge.TotalMilliseconds)
                {
                    var old = Error("stale");
                    old["now"] = nowMs;
                    return old;
                }
                if (_rooms.Count >= MaxRooms) return Error("full");
                if (_withdrawn.TryGetValue(listing.Id, out var gone) && listing.Seq <= gone) return Error("stale");
                entry = new Entry { Listing = listing };
            }
            // An index that is already full of other rooms' entries: only that entry is left out, the room itself is
            // still registered (whoever filled the index can't keep the room off the directory).
            var crowded = false;
            foreach (var key in hidden.Keys.ToList())
                if (_lookup.TryGetValue(key, out var holders) && !holders.ContainsKey(listing.Id) && holders.Count >= MaxEntriesPerIndex)
                {
                    hidden.Remove(key);
                    crowded = true;
                }
            if (!listed && hidden.Count == 0 && fp is null) return Error("lookup");
            var keys = hidden.Keys.ToArray();
            // Everything this publish adds to the snapshot must fit (a renewal mostly replaces what the room has there).
            var adding = hidden.Count * (HiddenItemLength + 1L) + (listed ? listing.ToJson().ToJsonString().Length + 64L : 0);
            if (_snapshotBytes - SnapshotBytesOf(listing.Id, entry) + adding > SnapshotBudget) return Error("full");
            if (fp is not null && _search.TryGetValue(fp, out var owner) && owner != listing.Id) return Error("lookup");
            var isNew = !_rooms.ContainsKey(listing.Id);
            var announcement = new RoomAnnouncement(listing, listed, isNew, fp is not null, account);
            foreach (var gate in Gates)
            {
                var verdict = gate.Check(announcement);
                if (verdict.Allowed) continue;
                _events.Add(o => o.OnRejected(listing, verdict));
                // 续期被拒（比如刚被封禁）：已经在目录里的也拿掉。
                if (!isNew) RemoveLocked(listing.Id, entry, RoomRemoval.Removed);
                return Error(verdict);
            }
            foreach (var old in entry.Lookup)
                if (!hidden.ContainsKey(old)) RemoveHiddenLocked(old, listing.Id);
            foreach (var (key, blob) in hidden)
            {
                if (!_lookup.TryGetValue(key, out var holders)) _lookup[key] = holders = new Dictionary<string, string>(StringComparer.Ordinal);
                if (!holders.TryGetValue(listing.Id, out var tag)) holders[listing.Id] = tag = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
                var json = new JsonObject { ["k"] = key, ["t"] = tag, ["b"] = blob }.ToJsonString();
                SetSnapshot("h:" + key + ":" + tag, new SnapItem(json, json));
            }
            entry.Lookup = keys;
            if (entry.Search is { } oldSearch) _search.Remove(Convert.ToHexStringLower(oldSearch));
            entry.Search = fp is null ? null : Convert.FromHexString(fp);
            if (fp is not null) _search[fp] = listing.Id;
            entry.Listing = listing with { Updated = _now() };
            entry.Listed = listed;
            entry.Account = account;
            entry.Expires = _now() + RoomDirectoryProtocol.Ttl;
            if (entry.Expires < _nextRoomExpiry) _nextRoomExpiry = entry.Expires;
            _rooms[listing.Id] = entry;
            SetSnapshot("r:" + listing.Id, listed && listing.Mode != RoomVisibility.Hidden ? PublicItem(entry.Listing) : null);
            var published = entry.Listing;
            _events.Add(o => o.OnAnnounced(published, listed, isNew));
            var reply = new JsonObject { ["ok"] = true };
            if (clockOff) reply["warn"] = "clock";
            else if (crowded) reply["warn"] = "lookup";
            return reply;
        }
    }

    /// <summary>How old a listing's version (its signing time) may be when it adds a room this directory doesn't hold.</summary>
    public static readonly TimeSpan MaxNewListingAge = HiddenEntry.MaxClockSkew;

    private JsonObject Withdraw(JsonObject request)
    {
        var id = request["id"]?.GetValue<string>();
        var seq = request["seq"]?.GetValue<long>();
        var sig = request["sig"]?.GetValue<string>();
        if (id is null || seq is not { } s || sig is not { Length: 128 } || !sig.All(char.IsAsciiHexDigit)) return Error("bad");
        lock (_gate)
        {
            if (!_rooms.TryGetValue(id, out var entry)) return new JsonObject { ["ok"] = true };
            if (s <= entry.Listing.Seq || !RoomSigner.Verify(entry.Listing.SignKey, RoomSigner.WithdrawBytes(id, s), Convert.FromHexString(sig))) return Error("owner");
            RemoveLocked(id, entry, RoomRemoval.Withdrawn);
            if (_withdrawn.Count >= MaxRooms) _withdrawn.Clear();
            _withdrawn[id] = s;
            return new JsonObject { ["ok"] = true };
        }
    }

    /// <summary>Length of a hidden item's JSON: {"k":"&lt;64 hex&gt;","t":"&lt;16 hex&gt;","b":"&lt;base64&gt;"}.</summary>
    private static readonly int HiddenItemLength = new JsonObject
    {
        ["k"] = new string('0', HiddenEntry.IndexLength * 2),
        ["t"] = new string('0', 16),
        ["b"] = Convert.ToBase64String(new byte[HiddenEntry.SealedLength]),
    }.ToJsonString().Length;

    /// <summary>
    /// A public room as a snapshot item. What readers see leaves out the renewal fields (seq, signature, time), so a
    /// renewal that changes nothing else isn't a snapshot change: readers keep the earlier signed version, which is just
    /// as valid, and the room's expiry shows up as "gone" either way.
    /// </summary>
    private static SnapItem PublicItem(RoomListing listing)
    {
        var json = listing.ToJson();
        var text = json.ToJsonString();
        json.Remove("seq");
        json.Remove("sig");
        json.Remove("updated");
        return new SnapItem(text, json.ToJsonString());
    }

    /// <summary>What a room has in the snapshot now (bytes of its items' JSON).</summary>
    private long SnapshotBytesOf(string roomId, Entry entry)
    {
        long bytes = _snapshot.TryGetValue("r:" + roomId, out var room) ? room.Json.Length + 1 : 0;
        foreach (var index in entry.Lookup)
            if (_lookup.TryGetValue(index, out var holders) && holders.TryGetValue(roomId, out var tag) && _snapshot.TryGetValue("h:" + index + ":" + tag, out var item))
                bytes += item.Json.Length + 1;
        return bytes;
    }

    /// <summary>Takes <paramref name="roomId"/>'s entry under hidden <paramref name="index"/> out of the snapshot.</summary>
    private void RemoveHiddenLocked(string index, string roomId)
    {
        if (!_lookup.TryGetValue(index, out var holders) || !holders.Remove(roomId, out var tag)) return;
        if (holders.Count == 0) _lookup.Remove(index);
        SetSnapshot("h:" + index + ":" + tag, null);
    }

    /// <summary>Records a snapshot change (null removes the key). Values that look the same to readers aren't logged.</summary>
    private void SetSnapshot(string key, SnapItem? value)
    {
        if (value is null)
        {
            if (!_snapshot.Remove(key, out var gone)) return;
            _snapshotBytes -= gone.Json.Length + 1;
        }
        else
        {
            if (_snapshot.TryGetValue(key, out var have))
            {
                if (have.Visible == value.Visible) return;
                _snapshotBytes -= have.Json.Length + 1;
            }
            _snapshot[key] = value;
            _snapshotBytes += value.Json.Length + 1;
        }
        _snapshotVersion++;
        _snapshotLog.Enqueue((_snapshotVersion, key));
        while (_snapshotLog.Count > MaxSnapshotLog) _snapshotFloor = _snapshotLog.Dequeue().Version;
    }

    /// <summary>
    /// What a snapshot request will get: already serialized bytes (the cached full snapshot or a cached delta), or the
    /// items to serialize and about how big that will be. Taken under the lock; serializing happens later, outside it.
    /// </summary>
    private sealed record SnapshotPlan(byte[]? Cached, bool Full, long Version, long Have, List<(string Key, SnapItem? Item)> Items, long Size);

    /// <summary>
    /// Plans the snapshot reply: everything when the caller's copy is from another run or too old, otherwise only the
    /// keys that changed after its version. A delta bigger than a quarter of everything is answered with everything (one
    /// cached reply per version). A delta between a small reply and that size is built for its request, so it is held to
    /// the large-reply slots and byte budget like the full snapshot.
    /// </summary>
    private SnapshotPlan PlanSnapshot(JsonObject request)
    {
        var epoch = request["epoch"]?.GetValue<string>();
        var have = request["have"]?.GetValue<long>() ?? -1;
        lock (_gate)
        {
            Prune();
            var version = _snapshotVersion;
            if (_repliesVersion != version)
            {
                _fullReply = null;
                _deltaReplies.Clear();
                _deltaReplyBytes = 0;
                _repliesVersion = version;
            }
            var full = epoch != _snapshotEpoch || have < _snapshotFloor || have > version;
            if (!full)
            {
                if (_deltaReplies.TryGetValue(have, out var cachedDelta)) return new SnapshotPlan(cachedDelta, false, version, have, [], cachedDelta.Length);
                List<(string Key, SnapItem? Item)> changed = [.. _snapshotLog.Where(c => c.Version > have).Select(c => c.Key)
                    .Distinct(StringComparer.Ordinal).Select(k => (k, _snapshot.GetValueOrDefault(k)))];
                var size = changed.Sum(i => (long)(i.Item?.Json.Length ?? i.Key.Length + 3) + 1) + 256;
                if (size * DeltaShareOfFull <= _snapshotBytes + 256 || size <= SmallSnapshot) return new SnapshotPlan(null, false, version, have, changed, size);
            }
            if (_fullReply is { } cachedFull) return new SnapshotPlan(cachedFull, true, version, have, [], cachedFull.Length);
            return new SnapshotPlan(null, true, version, have, [.. _snapshot.Select(p => (p.Key, (SnapItem?)p.Value))], _snapshotBytes + 256);
        }
    }

    /// <summary>
    /// The snapshot reply (UTF-8 JSON) for <paramref name="plan"/>. Public rooms are signed listings; hidden entries are
    /// sealed (<see cref="HiddenEntry"/>). Items are serialized when they change and each reply once per version, so
    /// every reader at the same version gets the same bytes.
    /// </summary>
    private byte[] BuildSnapshot(SnapshotPlan plan)
    {
        if (plan.Cached is { } cached) return cached;
        Interlocked.Increment(ref _snapshotsBuilt);
        var (full, version, have, items) = (plan.Full, plan.Version, plan.Have, plan.Items);
        var text = new StringBuilder(full ? (int)Math.Min(int.MaxValue, items.Sum(i => (long)(i.Item?.Json.Length ?? 0) + 1) + 256) : 256);
        text.Append("{\"ok\":true,\"epoch\":\"").Append(_snapshotEpoch).Append("\",\"ver\":").Append(version).Append(",\"full\":").Append(full ? "true" : "false");
        if (!full) text.Append(",\"from\":").Append(have);
        void List(string name, IEnumerable<string> json)
        {
            text.Append(",\"").Append(name).Append("\":[");
            var first = true;
            foreach (var item in json)
            {
                if (!first) text.Append(',');
                text.Append(item);
                first = false;
            }
            text.Append(']');
        }
        List("rooms", items.Where(i => i.Item is not null && i.Key.StartsWith("r:", StringComparison.Ordinal)).Select(i => i.Item!.Json));
        List("hidden", items.Where(i => i.Item is not null && i.Key.StartsWith("h:", StringComparison.Ordinal)).Select(i => i.Item!.Json));
        List("gone", items.Where(i => i.Item is null).Select(i => JsonValue.Create(i.Key).ToJsonString()));
        text.Append('}');
        var bytes = Encoding.UTF8.GetBytes(text.ToString());
        lock (_gate)
        {
            if (_repliesVersion == version)
            {
                if (full) _fullReply = bytes;
                else if (bytes.Length <= SmallSnapshot && _deltaReplyBytes + bytes.Length <= MaxCachedDeltaBytes && _deltaReplies.TryAdd(have, bytes))
                    _deltaReplyBytes += bytes.Length;
            }
        }
        return bytes;
    }

    /// <summary>按指纹码找房间：拿每个登记了指纹密钥的房间算一遍标签（几千个 HMAC，很快）。找到了给整份签过名的房间信息。</summary>
    private JsonObject Find(JsonObject request)
    {
        if (!FingerprintCode.TryParse(request["code"]?.GetValue<string>(), out var code)) return Error("bad");
        lock (_gate)
        {
            Prune();
            foreach (var entry in _rooms.Values)
                if (entry.Search is { } key && FingerprintCode.Matches(key, code))
                    return new JsonObject { ["ok"] = true, ["room"] = entry.Listing.ToJson() };
            return new JsonObject { ["ok"] = true };
        }
    }

    private static bool IsHex(string? text, int length) => text is not null && text.Length == length && text.All(char.IsAsciiHexDigit);

    private JsonObject Post(JsonObject request)
    {
        var box = request["box"]?.GetValue<string>()?.ToLowerInvariant();
        var blob = request["blob"]?.GetValue<string>();
        var drop = request["drop"]?.GetValue<string>()?.ToLowerInvariant();
        if (!IsHex(box, 32) || !IsHex(drop, 64) || blob is null || blob.Length > (Contacts.SealedMail.MaxBlob + 2) / 3 * 4) return Error("bad");
        try { if (Convert.FromBase64String(blob).Length is 0 or > Contacts.SealedMail.MaxBlob) return Error("bad"); }
        catch (FormatException) { return Error("bad"); }
        lock (_gate)
        {
            PruneMail();
            if (!_mailboxes.TryGetValue(box!, out var items))
            {
                if (_sealedCount >= MaxSealedMail) return Error("full");
                _mailboxes[box!] = items = [];
            }
            if (items.Any(i => i.Blob == blob)) return new JsonObject { ["ok"] = true };
            if (items.Count >= RoomDirectoryProtocol.MaxMailPerBox || _sealedCount >= MaxSealedMail) return Error("full");
            var expires = _now() + RoomDirectoryProtocol.MailTtl;
            items.Add(new SealedItem(blob, drop!, expires));
            if (expires < _nextMailExpiry) _nextMailExpiry = expires;
            _sealedCount++;
            return new JsonObject { ["ok"] = true };
        }
    }

    /// <summary>Room left for blobs in one fetch reply (the reply is one line of at most <see cref="RoomDirectoryProtocol.MaxLine"/>).</summary>
    private const int FetchBudget = 12 * 1024;

    private JsonObject Fetch(JsonObject request)
    {
        var boxes = (request["boxes"] as JsonArray ?? []).Select(b => b?.GetValue<string>()?.ToLowerInvariant()).Where(b => IsHex(b, 32))
            .Distinct().Take(RoomDirectoryProtocol.MaxFetchBoxes).ToList();
        var from = Math.Max(0, request["from"]?.GetValue<int>() ?? 0);
        lock (_gate)
        {
            PruneMail();
            var all = boxes.SelectMany(box => _mailboxes.TryGetValue(box!, out var items) ? items.Select(i => (Box: box!, i.Blob)) : []).ToList();
            var found = new JsonArray();
            var used = 0;
            var next = from;
            for (; next < all.Count; next++)
            {
                var (box, blob) = all[next];
                if (used > 0 && used + blob.Length + 64 > FetchBudget) break;
                used += blob.Length + 64;
                found.Add((JsonNode)new JsonObject { ["box"] = box, ["blob"] = blob });
            }
            var reply = new JsonObject { ["ok"] = true, ["items"] = found };
            if (next < all.Count) reply["next"] = next;
            return reply;
        }
    }

    private JsonObject Drop(JsonObject request)
    {
        var box = request["box"]?.GetValue<string>()?.ToLowerInvariant();
        var token = request["token"]?.GetValue<string>();
        if (!IsHex(box, 32) || !IsHex(token, Contacts.MailMessage.TokenLength * 2)) return Error("bad");
        var hash = Contacts.SealedMail.DropHash(Convert.FromHexString(token!));
        lock (_gate)
        {
            if (_mailboxes.TryGetValue(box!, out var items))
            {
                _sealedCount -= items.RemoveAll(i => i.DropHash == hash);
                if (items.Count == 0) _mailboxes.Remove(box!);
            }
            return new JsonObject { ["ok"] = true };
        }
    }

    private JsonObject RecordPut(JsonObject request, string? source)
    {
        if (SignedRecord.FromJson(request["record"]) is not { } record) return Error("bad");
        var now = _now();
        if (record.Expires <= now || record.Expires > now + SignedRecord.MaxLifetime + RecordClockSkew) return Error("bad");
        var lookup = Convert.ToHexStringLower(record.LookupKey);
        lock (_gate)
        {
            PruneRecords();
            if (_records.TryGetValue(lookup, out var have))
            {
                // First signer owns the lookup until its record expires; nobody else can replace or shadow it.
                if (!CryptographicOperations.FixedTimeEquals(have.Signer, record.Signer)) return Error("owner");
                if (record.Seq == have.Seq && record.Signature.AsSpan().SequenceEqual(have.Signature)) return new JsonObject { ["ok"] = true };
                if (record.Seq <= have.Seq) return Error("stale");
            }
            else if (_records.Count >= MaxRecords) return Error("full");
            if (source is not null)
            {
                var (count, until) = _recordPuts.TryGetValue(source, out var used) && used.Until > now ? used : (0, now + TimeSpan.FromHours(1));
                if (count >= MaxRecordPutsPerSource) return Error("rate");
                _recordPuts[source] = (count + 1, until);
            }
            _records[lookup] = record;
            if (record.Expires < _nextRecordExpiry) _nextRecordExpiry = record.Expires;
            return new JsonObject { ["ok"] = true };
        }
    }

    private JsonObject RecordGet(JsonObject request)
    {
        var lookup = request["lookup"]?.GetValue<string>()?.ToLowerInvariant();
        if (!IsHex(lookup, SignedRecord.LookupLength * 2)) return Error("bad");
        lock (_gate)
        {
            PruneRecords();
            return _records.TryGetValue(lookup!, out var record) ? new JsonObject { ["ok"] = true, ["record"] = record.ToJson() } : Error("not_found");
        }
    }

    // The earliest expiry in each table (or later, never earlier): the prunes below only scan a table once something in it
    // may have expired, instead of on every request. Exact under any clock, including the tests' fake ones.
    private DateTimeOffset _nextRoomExpiry = DateTimeOffset.MaxValue;
    private DateTimeOffset _nextRecordExpiry = DateTimeOffset.MaxValue;
    private DateTimeOffset _nextMailExpiry = DateTimeOffset.MaxValue;

    private void PruneRecords()
    {
        var now = _now();
        if (now >= _nextRecordExpiry)
        {
            foreach (var (lookup, record) in _records.Where(p => p.Value.Expires <= now).ToList()) _records.Remove(lookup);
            _nextRecordExpiry = _records.Count == 0 ? DateTimeOffset.MaxValue : _records.Values.Min(r => r.Expires);
        }
        foreach (var (source, used) in _recordPuts.Where(p => p.Value.Until <= now).ToList()) _recordPuts.Remove(source);
    }

    private void PruneMail()
    {
        var now = _now();
        if (now >= _nextMailExpiry)
        {
            var next = DateTimeOffset.MaxValue;
            foreach (var (box, items) in _mailboxes.ToList())
            {
                _sealedCount -= items.RemoveAll(i => i.Expires <= now);
                if (items.Count == 0) _mailboxes.Remove(box);
                foreach (var item in items) if (item.Expires < next) next = item.Expires;
            }
            _nextMailExpiry = next;
        }
    }

    private void Prune()
    {
        var now = _now();
        if (now < _nextRoomExpiry) return;
        foreach (var (id, entry) in _rooms.Where(p => p.Value.Expires <= now).ToList()) RemoveLocked(id, entry, RoomRemoval.Expired);
        _nextRoomExpiry = _rooms.Count == 0 ? DateTimeOffset.MaxValue : _rooms.Values.Min(e => e.Expires);
    }

    private void RemoveLocked(string id, Entry entry, RoomRemoval reason)
    {
        if (!_rooms.Remove(id)) return;
        _events.Add(o => o.OnRemoved(id, reason));
        SetSnapshot("r:" + id, null);
        foreach (var key in entry.Lookup) RemoveHiddenLocked(key, id);
        if (entry.Search is { } search && _search.TryGetValue(Convert.ToHexStringLower(search), out var finder) && finder == id)
            _search.Remove(Convert.ToHexStringLower(search));
    }

    private int _disposed;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _cts.Cancel();
        _listener.Stop();
        try { await _accept; } catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException) { }
        _cts.Dispose();
    }
}

/// <summary>查到的房主（拿着邀请码的人在目录里找到的）。</summary>
public sealed record DirectoryHit(string Id, string Ip, byte[] HostKey);

/// <summary>
/// 目录服务的客户端：经公共网络连目录节点（10.235.0.1，网络有备用目录节点时还有 .2 .3 .4，见 <see cref="PublicNetworkProfile.DirectoryIps"/>）。
/// 写（发布、续期、撤下）发给每个在线的目录节点，所以每个目录都有完整的房间表，一台挂了别的照常；
/// 读（列表）合并所有在线目录的结果，查找按顺序问到有结果为止；捎话、登录交给第一个肯收的。
/// 备用目录节点之间不用互相信任或同步：房间信息本来就是房主签过名的。
/// </summary>
public sealed class RoomDirectoryClient(IPublicNetwork network, IReadOnlyList<string>? ips = null, int port = PublicNetworkProfile.DirectoryPort)
{
    /// <summary>This Jay's version, sent with every request so a network can turn away clients older than its policy.minVersion.</summary>
    public string Version { get; init; } = Launch.UpdateChecker.CurrentVersion;

    private async Task<IReadOnlyList<string>> TargetsAsync(CancellationToken ct)
    {
        if (ips is { Count: > 0 }) return ips;
        try
        {
            var online = await network.DirectoriesAsync(ct);
            return online.Count > 0 ? online : [PublicNetworkProfile.DirectoryIp];
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception) { return [PublicNetworkProfile.DirectoryIp]; }
    }

    /// <summary>
    /// The directory keys to insist on. Defaults to the network's pinned keys (<see cref="PublicNetworkProfile.DirectoryKeys"/>).
    /// When there are any, every request goes through <see cref="DirectoryChannel"/> and a directory that can't prove it
    /// holds one of them gets nothing; there is no fallback to the legacy protocol.
    /// </summary>
    public IReadOnlyList<byte[]> PinnedKeys { get; init; } = network.Profile?.DirectoryKeys ?? [];

    /// <summary>Root keys that certify directory keys (<see cref="PublicNetworkProfile.DirectoryRoots"/>). Same rule as pinned keys.</summary>
    public IReadOnlyList<byte[]> RootKeys { get; init; } = network.Profile?.DirectoryRoots ?? [];

    /// <summary>How many of <see cref="RootKeys"/> must have signed a directory's certificate.</summary>
    public int RootsNeed { get; init; } = network.Profile?.DirectoryRootsNeed ?? 1;

    /// <summary>
    /// Where to remember directory keys of networks that pin nothing (trust on first use). Null: such networks use the
    /// legacy protocol as before.
    /// </summary>
    public DirectoryKeyMemory? KeyMemory { get; init; }

    /// <summary>This Jay's copy of the directories' snapshots (shared by every client of the same network session).</summary>
    public DirectorySnapshots Snapshots { get; init; } = DirectorySnapshots.For(network);

    /// <summary>Sealed hidden entries made for earlier announces, reused while nothing in them changes (so renewals aren't snapshot changes).</summary>
    private readonly Dictionary<string, byte[]> _sealed = new(StringComparer.Ordinal);

    /// <summary>The network's EasyTier secret, for the channel's psk. Defaults to the network's.</summary>
    public string NetworkSecret { get; init; } = network.Profile?.Secret ?? "";

    /// <summary>The network signed volunteer registrations name (<see cref="VolunteerOperator.NetworkTag"/>). Defaults to the network's.</summary>
    public string NetworkId { get; init; } = VolunteerOperator.NetworkTag(network.Profile);

    private DirectoryTrust Trust => new()
    {
        Network = network.Profile?.NetworkName ?? "",
        Pinned = PinnedKeys,
        Roots = RootKeys,
        RootsNeed = RootsNeed,
        Memory = KeyMemory,
    };

    /// <summary>Every request goes through <see cref="DirectoryChannel"/>; there is no plaintext protocol any more.</summary>
    /// <param name="snapshot">Once a snapshot reply says how long it is, the deadline becomes the time the directory itself
    /// allows for that size (<see cref="RoomDirectoryProtocol.SnapshotDeadline"/>), so slow links still get big snapshots.</param>
    private async Task<JsonObject> CallAsync(string ip, JsonObject request, CancellationToken ct, TimeSpan? timeout = null, bool snapshot = false)
    {
        request["jay"] = Version;
        var trust = Trust;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // Connecting and the handshake get the usual time whatever the request; a longer timeout only starts once the
        // request is out (a directory that doesn't answer the handshake is given up on as quickly as any other).
        deadline.CancelAfter(RoomDirectoryProtocol.RequestTimeout);
        try
        {
            await using var stream = await network.ConnectAsync(ip, port, deadline.Token);
            return await DirectoryChannel.CallAsync(stream, trust, ip, NetworkSecret, request, deadline.Token,
                snapshot ? total => deadline.CancelAfter(RoomDirectoryProtocol.SnapshotDeadline(total)) : null,
                timeout is { } longer ? () => deadline.CancelAfter(longer) : null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException(L.T("core.mp.public.no_directory"));
        }
        catch (SocketException e)
        {
            throw new IOException(e.Message, e);
        }
    }

    /// <summary>发给每个目录，返回每个的回复（连不上的是异常）。</summary>
    private async Task<List<(JsonObject? Reply, Exception? Error)>> FanOutAsync(JsonObject request, CancellationToken ct)
    {
        var targets = await TargetsAsync(ct);
        var tasks = targets.Select(async ip =>
        {
            try { return ((JsonObject?)await CallAsync(ip, (JsonObject)request.DeepClone(), ct), (Exception?)null); }
            catch (Exception e) when (e is IOException or InvalidDataException or EndOfStreamException) { return (null, e); }
        }).ToList();
        var results = (await Task.WhenAll(tasks)).ToList();
        ct.ThrowIfCancellationRequested();
        return results;
    }

    /// <summary>至少一个目录收下就算成功；都不收时，有明确拒绝就报拒绝，否则报连不上。</summary>
    /// <summary>The directory's time from a "stale" reply that says the listing was too old, when it is ahead of <paramref name="seq"/>.</summary>
    private static long? DirectoryTimeIfTooOld(JsonObject? reply, long seq) =>
        reply is not null && !IsOk(reply) && reply["error"] is JsonValue e && e.TryGetValue<string>(out var code) && code == "stale"
        && reply["now"] is JsonValue n && n.TryGetValue<long>(out var now) && now > seq ? now : null;

    private static List<JsonObject> NeedOne(List<(JsonObject? Reply, Exception? Error)> results)
    {
        var ok = results.Where(r => r.Reply?["ok"]?.GetValue<bool>() == true).Select(r => r.Reply!).ToList();
        if (ok.Count > 0) return ok;
        if (results.FirstOrDefault(r => r.Reply is not null).Reply is { } rejected) Check(rejected);
        throw results.Select(r => r.Error).OfType<Exception>().FirstOrDefault() ?? new IOException(L.T("core.mp.public.no_directory"));
    }

    /// <summary>按顺序问，第一个成功的回复（<paramref name="accept"/> 决定算不算数，不算就问下一个）。</summary>
    private async Task<JsonObject> FirstAsync(JsonObject request, Func<JsonObject, bool> accept, CancellationToken ct)
    {
        JsonObject? last = null;
        Exception? error = null;
        foreach (var ip in await TargetsAsync(ct))
        {
            try
            {
                var reply = await CallAsync(ip, (JsonObject)request.DeepClone(), ct);
                if (accept(reply)) return reply;
                // 都没有结果时，回一个“成功但没找到”比回别的目录的错误更准。
                if (last is null || (!IsOk(last) && IsOk(reply))) last = reply;
            }
            catch (Exception e) when (e is IOException or InvalidDataException or EndOfStreamException) { error ??= e; }
        }
        return last ?? throw error ?? new IOException(L.T("core.mp.public.no_directory"));
    }

    private static bool IsOk(JsonObject reply) => reply["ok"]?.GetValue<bool>() == true;

    private static void Check(JsonObject reply)
    {
        if (reply["ok"]?.GetValue<bool>() == true) return;
        var code = reply["error"]?.GetValue<string>() ?? "";
        // 网络节点自己的审核规则拒绝时会带一句原因（节点写的，只当文字显示）。
        var reason = reply["reason"]?.GetValue<string>() is { } r ? RoomChat.Clean(r, DirectoryExtensions.MaxReason) : null;
        throw new DirectoryRejectedException(code, code switch
        {
            "owner" or "lookup" or "stale" => L.T("core.mp.public.err_owner"),
            "full" => L.T("core.mp.public.err_full"),
            "gone" => L.T("core.mp.public.err_gone"),
            "rate" => L.T("core.mp.public.err_rate"),
            "busy" => L.T("core.mp.public.err_busy"),
            "clock" => L.T("core.mp.public.err_clock"),
            "version" => L.F("core.mp.public.err_version", reply["min"]?.GetValue<string>() is { Length: <= 32 } min ? min : "?"),
            "login" => L.T("core.mp.public.err_login"),
            "off" => L.T("core.mp.public.volunteer_off"),
            "operator" => L.T("core.mp.public.volunteer_operator"),
            _ when !string.IsNullOrWhiteSpace(reason) => L.F("core.mp.public.err_policy", reason),
            _ => L.F("core.mp.public.err_other", code),
        });
    }

    /// <summary>
    /// Puts a signed record (<see cref="SignedRecord"/>) on every directory, over the authenticated channel only.
    /// Succeeds when at least one directory keeps it; otherwise throws the rejection (owner, stale, full, rate) or the
    /// connection error.
    /// </summary>
    public async Task PutRecordAsync(SignedRecord record, CancellationToken ct) =>
        NeedOne(await FanOutAsync(new JsonObject { ["op"] = "rec.put", ["record"] = record.ToJson() }, ct));

    /// <summary>
    /// Asks every directory for the record under <paramref name="lookup"/> and returns the newest one that is signed,
    /// unexpired, under that lookup and whose signer <paramref name="acceptSigner"/> accepts (check it against what the
    /// code expects, so a directory can't hand over someone else's record). Null when none qualifies.
    /// </summary>
    /// <param name="seen">
    /// The highest seqs this computer accepted before: a record older than that for the lookup is not taken (null), and
    /// a newer one is noted.
    /// </param>
    public async Task<SignedRecord?> GetRecordAsync(byte[] lookup, Func<byte[], bool> acceptSigner, CancellationToken ct, DateTimeOffset? now = null,
        RecordSeqMemory? seen = null)
    {
        if (lookup.Length != SignedRecord.LookupLength) throw new ArgumentException("Lookup length", nameof(lookup));
        var results = await FanOutAsync(new JsonObject { ["op"] = "rec.get", ["lookup"] = Convert.ToHexStringLower(lookup) }, ct);
        var at = now ?? DateTimeOffset.UtcNow;
        var best = results
            .Select(r => r.Reply is { } reply && IsOk(reply) ? SignedRecord.FromJson(reply["record"]) : null)
            .OfType<SignedRecord>()
            .Where(r => r.LookupKey.AsSpan().SequenceEqual(lookup) && r.Expires > at && acceptSigner(r.Signer))
            .MaxBy(r => r.Seq);
        // Older than a record this computer already took for the lookup: rolled back, so not used.
        if (best is not null && seen is not null)
        {
            if (best.Seq < seen.Floor(lookup)) best = null;
            else seen.Note(lookup, best.Seq);
        }
        if (best is not null || results.Any(r => r.Reply is not null)) return best;
        throw results.Select(r => r.Error).OfType<Exception>().FirstOrDefault() ?? new IOException(L.T("core.mp.public.no_directory"));
    }

    /// <summary>
    /// Asks every directory what changed since the version in <paramref name="have"/> (by directory address; missing
    /// means "everything"). Every reader sends the same request, whatever it is looking for.
    /// </summary>
    /// <param name="arrived">Called with each directory's answer as soon as it is in, before the slower ones answer.</param>
    internal async Task<List<(string Ip, JsonObject? Reply, Exception? Error)>> SnapshotAsync(IReadOnlyDictionary<string, (string Epoch, long Version)> have, CancellationToken ct,
        Action<(string Ip, JsonObject? Reply, Exception? Error)>? arrived = null)
    {
        var targets = await TargetsAsync(ct);
        var tasks = targets.Select(async ip =>
        {
            var request = new JsonObject { ["op"] = "snap" };
            if (have.TryGetValue(ip, out var mine))
            {
                request["epoch"] = mine.Epoch;
                request["have"] = mine.Version;
            }
            (string Ip, JsonObject? Reply, Exception? Error) result;
            try
            {
                var reply = await CallAsync(ip, request, ct, RoomDirectoryProtocol.SnapshotTimeout, snapshot: true);
                if (!IsOk(reply)) Check(reply);
                result = (ip, reply, null);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or EndOfStreamException) { result = (ip, null, e); }
            arrived?.Invoke(result);
            return result;
        }).ToList();
        var results = (await Task.WhenAll(tasks)).ToList();
        ct.ThrowIfCancellationRequested();
        return results;
    }

    /// <summary>公开列表：本机快照里所有目录的公开房间，同一个房间取序号最大的版本。</summary>
    public async Task<IReadOnlyList<RoomListing>> ListAsync(CancellationToken ct)
    {
        await Snapshots.ReadyAsync(this, ct);
        return [.. Snapshots.Rooms().OrderByDescending(r => r.Players).ThenByDescending(r => r.Updated).Take(RoomDirectoryServer.MaxListed)];
    }

    /// <summary>发布或续期（发给每个在线的目录）。返回目录里给房主捎的话（直连回复码）。</summary>
    /// <param name="listing">已经用 <see cref="RoomSigner.Sign"/> 签好的房间信息。</param>
    /// <param name="lobbyKeys">Lobby keys of the invites still open: each gets a sealed hidden entry, so holders of the invite find the room in their snapshot.</param>
    /// <param name="searchKey">本机的指纹密钥：给了就能用这台电脑的指纹码找到这个房间。</param>
    /// <param name="account">This network's login token, if logged in: lets networks that require login accept the room and tie it to the account.</param>
    /// <param name="behind">
    /// Called with a directory's time (Unix milliseconds) when it refused the listing as too old for a room it doesn't hold:
    /// this computer's clock is behind. The caller moves its <see cref="RoomSigner"/> on (<see cref="RoomSigner.AdvanceTo"/>)
    /// and publishes again.
    /// </param>
    /// <returns>A warning to show the host (the room is listed but its invites can't be found), or null.</returns>
    public async Task<AnnounceWarning?> AnnounceAsync(RoomListing listing, bool listed, IEnumerable<byte[]> lobbyKeys, CancellationToken ct,
        byte[]? searchKey = null, string? account = null, Action<long>? behind = null)
    {
        var hit = new DirectoryHit(listing.Id, listing.Ip, listing.HostKey);
        var hidden = new JsonArray();
        // Listed rooms publish them too: the entry gives the same place as the listing, so it agrees with it, and it keeps
        // anyone else holding the invite from being the only entry under its key.
        foreach (var lobby in lobbyKeys.Take(RoomDirectoryProtocol.MaxLookupKeys))
        {
            var entry = SealedFor(lobby, hit);
            hidden.Add((JsonNode)new JsonObject
            {
                ["k"] = Convert.ToHexStringLower(HiddenEntry.Index(lobby)),
                ["b"] = Convert.ToBase64String(entry),
                // Proves this publish comes from a holder of the invite (the index is the hash of this key).
                ["p"] = Convert.ToHexStringLower(HiddenEntry.PublicKey(lobby)),
                ["s"] = Convert.ToHexStringLower(HiddenEntry.SignPublish(lobby, entry, listing.Id, listing.Seq)),
            });
        }
        var request = new JsonObject
        {
            ["op"] = "announce",
            ["room"] = listing.ToJson(),
            ["listed"] = listed,
            ["hidden"] = hidden,
        };
        if (searchKey is not null) request["fp"] = Convert.ToHexStringLower(searchKey);
        if (account is not null) request["account"] = account;
        var results = await FanOutAsync(request, ct);
        if (behind is not null && results.Select(r => DirectoryTimeIfTooOld(r.Reply, listing.Seq)).Max() is { } directoryNow) behind(directoryNow);
        var replies = NeedOne(results);
        if (replies.Any(r => r["warn"]?.GetValue<string>() == "clock")) return AnnounceWarning.Clock;
        return replies.Any(r => r["warn"]?.GetValue<string>() == "lookup") ? AnnounceWarning.Lookup : null;
    }

    /// <summary>
    /// Registers (or renews) this computer as a volunteer node with every online directory. Returns whether at least
    /// one directory lists it already (the first registration is probed first, so it lists on the next renewal).
    /// </summary>
    /// <param name="signer">This device's Jay account certificate and keys: the directory then knows which person runs
    /// the node (<see cref="VolunteerOperator"/>). Null registers without saying.</param>
    public async Task<bool> VolunteerAsync(string url, Amber.Core.Identity.JayProof? signer, CancellationToken ct)
    {
        var address = VolunteerNodes.Normalize(url) ?? throw new ArgumentException(L.T("core.mp.public.volunteer_bad_address"));
        var request = new JsonObject { ["op"] = "volunteer", ["url"] = address };
        if (signer is not null) request["operator"] = VolunteerOperator.Sign(signer, NetworkId, address, NextSigningTime(address));
        List<JsonObject> replies;
        try { replies = NeedOne(await FanOutAsync(request, ct)); }
        catch (DirectoryRejectedException e) when (e.Code == "owner") { throw new DirectoryRejectedException("owner", L.T("core.mp.public.volunteer_owner")); }
        catch (DirectoryRejectedException e) when (e.Code == "stale") { throw new DirectoryRejectedException("stale", L.T("core.mp.public.volunteer_stale")); }
        catch (DirectoryRejectedException e) when (e.Code == "rate") { throw new DirectoryRejectedException("rate", L.T("core.mp.public.volunteer_rate")); }
        catch (DirectoryRejectedException e) when (e.Code == "op")
        {
            // Directories from before volunteer nodes existed.
            throw new DirectoryRejectedException("off", L.T("core.mp.public.volunteer_off"));
        }
        return replies.Any(r => r["listed"]?.GetValue<bool>() == true);
    }

    /// <summary>The clock signed volunteer registrations use (tests replace it).</summary>
    internal Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.UtcNow;

    // Each signed registration of an address must carry a later time than the last (seconds): never the same twice.
    private readonly Dictionary<string, long> _lastVolunteerAt = new(StringComparer.Ordinal);

    private DateTimeOffset NextSigningTime(string address)
    {
        lock (_lastVolunteerAt)
        {
            var at = Clock().ToUnixTimeSeconds();
            if (_lastVolunteerAt.TryGetValue(address, out var last) && at <= last) at = last + 1;
            _lastVolunteerAt[address] = at;
            return DateTimeOffset.FromUnixTimeSeconds(at);
        }
    }

    /// <summary>
    /// Asks a directory to dial <paramref name="url"/> now: true when it got through. Use it to test port forwarding
    /// before volunteering (something must be listening on the port during the test).
    /// </summary>
    public async Task<bool> ProbeAsync(string url, CancellationToken ct)
    {
        var address = VolunteerNodes.Normalize(url) ?? throw new ArgumentException(L.T("core.mp.public.volunteer_bad_address"));
        var reply = await FirstAsync(new JsonObject { ["op"] = "probe", ["url"] = address }, IsOk, ct);
        if (!IsOk(reply) && reply["error"]?.GetValue<string>() == "op") throw new DirectoryRejectedException("off", L.T("core.mp.public.volunteer_off"));
        Check(reply);
        return reply["reachable"]?.GetValue<bool>() == true;
    }

    /// <summary>Volunteer nodes the directories know (merged, public addresses only). Empty when the network takes none.</summary>
    public async Task<IReadOnlyList<string>> NodesAsync(CancellationToken ct)
    {
        var results = await FanOutAsync(new JsonObject { ["op"] = "nodes" }, ct);
        var nodes = new List<string>();
        foreach (var reply in results.Select(r => r.Reply).OfType<JsonObject>().Where(IsOk))
            foreach (var node in reply["nodes"] as JsonArray ?? [])
                if (node is JsonValue value && value.TryGetValue<string>(out var text) && VolunteerNodes.Normalize(text) is { } url)
                    nodes.Add(url);
        return [.. nodes.Distinct().Take(VolunteerNodes.PerRequest * 2)];
    }

    public async Task WithdrawAsync(RoomSigner signer, CancellationToken ct)
    {
        var (seq, sig) = signer.SignWithdraw(DateTimeOffset.UtcNow);
        NeedOne(await FanOutAsync(new JsonObject { ["op"] = "withdraw", ["id"] = signer.RoomId, ["seq"] = seq, ["sig"] = Convert.ToHexStringLower(sig) }, ct));
    }

    private byte[] SealedFor(byte[] lobbyKey, DirectoryHit hit)
    {
        var cacheKey = Convert.ToHexStringLower(SHA256.HashData([.. lobbyKey, .. Encoding.UTF8.GetBytes($"|{hit.Id}|{hit.Ip}|"), .. hit.HostKey]));
        lock (_sealed)
        {
            if (_sealed.TryGetValue(cacheKey, out var have)) return have;
            if (_sealed.Count >= 16) _sealed.Clear();
            return _sealed[cacheKey] = HiddenEntry.Seal(lobbyKey, hit);
        }
    }

    /// <summary>
    /// 按邀请码里的大厅密钥找房主：只在本机的目录快照里找（目录看不到谁找了哪个房间）。没有时返回 null。
    /// </summary>
    public async Task<DirectoryHit?> LookupAsync(ReadOnlyMemory<byte> lobbyKey, CancellationToken ct, byte[]? expectedHost = null) =>
        (await LookupAllAsync(lobbyKey, ct, expectedHost)) is [var one, ..] ? one : null;

    /// <summary>
    /// Every address the directory copies give for this invite (see <see cref="DirectorySnapshots.FindAll"/>), at once.
    /// With <paramref name="freshSince"/>, nothing found and a copy kept fresh by polling that wasn't refreshed after that
    /// time, waits for the next scheduled refresh and looks again (a room opened after the copy was taken isn't in it);
    /// <paramref name="waiting"/> is called before that wait.
    /// </summary>
    public async Task<IReadOnlyList<DirectoryHit>> LookupAllAsync(ReadOnlyMemory<byte> lobbyKey, CancellationToken ct, byte[]? expectedHost = null,
        DateTimeOffset? freshSince = null, Action? waiting = null)
    {
        await Snapshots.ReadyAsync(this, ct);
        var hits = Snapshots.FindAll(lobbyKey.Span, expectedHost);
        if (hits.Count > 0 || freshSince is not { } since || !Snapshots.IsPolling || Snapshots.RefreshedAt > since) return hits;
        waiting?.Invoke();
        await Snapshots.WaitForNextRefreshAsync(ct);
        return Snapshots.FindAll(lobbyKey.Span, expectedHost);
    }

    /// <summary>按设备指纹码找房间；没找到返回 null。</summary>
    public async Task<RoomListing?> FindAsync(string code, CancellationToken ct)
    {
        var reply = await FirstAsync(new JsonObject { ["op"] = "find", ["code"] = code }, r => IsOk(r) && r["room"] is not null, ct);
        Check(reply);
        return RoomListing.FromJson(reply["room"]);
    }

    /// <summary>登录公共网络的账号：账号密码封给节点的登录公钥，回来的凭证绑定自己的联机身份。交给第一个开了登录的目录节点。</summary>
    public async Task<(string Token, AccountToken Parsed)> LoginAsync(X25519KeyPair me, NetworkLogin login, string network, string user, string password,
        CancellationToken ct, IReadOnlyDictionary<string, string>? factors = null)
    {
        // 每个目录节点各算一次（请求里有时间戳，换个节点重发也一样）；没开登录的节点回 op，问下一个。
        var request = NetworkLoginClient.Request(me, login, user, password, DateTimeOffset.UtcNow, factors);
        var reply = await FirstAsync(request, r => r["error"]?.GetValue<string>() != "op", ct);
        return NetworkLoginClient.Read(reply, me, login, network, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Leaves sealed mail in a mailbox at every directory that takes it (so one directory that drops it can't make it
    /// disappear). Throws when none took it.
    /// </summary>
    public async Task PostMailAsync(string box, byte[] blob, string dropHash, CancellationToken ct) =>
        NeedOne(await FanOutAsync(new JsonObject { ["op"] = "post", ["box"] = box, ["blob"] = Convert.ToBase64String(blob), ["drop"] = dropHash }, ct));

    /// <summary>Most pages read from one directory per fetch (a flooded mailbox can't keep Jay busy forever).</summary>
    public const int MaxFetchPages = 16;

    /// <summary>Everything waiting in these mailboxes at any directory, each blob once. Throws when no directory answered.</summary>
    public async Task<IReadOnlyList<(string Box, byte[] Blob)>> FetchMailAsync(IReadOnlyList<string> boxes, CancellationToken ct)
    {
        var targets = await TargetsAsync(ct);
        var pages = await Task.WhenAll(targets.Select(async ip =>
        {
            var replies = new List<JsonObject>();
            Exception? error = null;
            var from = 0;
            try
            {
                for (var page = 0; page < MaxFetchPages; page++)
                {
                    var request = new JsonObject
                    {
                        ["op"] = "fetch",
                        ["boxes"] = new JsonArray([.. boxes.Select(b => (JsonNode)JsonValue.Create(b))]),
                        ["from"] = from,
                    };
                    var reply = await CallAsync(ip, request, ct);
                    replies.Add(reply);
                    if (!IsOk(reply) || reply["next"]?.GetValue<int>() is not { } next || next <= from) break;
                    from = next;
                }
            }
            catch (Exception e) when (e is IOException or InvalidDataException or EndOfStreamException or InvalidOperationException or FormatException) { error = e; }
            return (replies, error);
        }));
        ct.ThrowIfCancellationRequested();
        NeedOne([.. pages.Select(p => (p.replies.FirstOrDefault(), p.replies.Count > 0 ? null : p.error))]);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<(string, byte[])>();
        foreach (var item in pages.SelectMany(p => p.replies).Where(IsOk).SelectMany(r => (r["items"] as JsonArray ?? []).OfType<JsonObject>()))
        {
            var box = item["box"]?.GetValue<string>();
            var blob = item["blob"]?.GetValue<string>();
            if (box is null || blob is null || !boxes.Contains(box) || !seen.Add(blob)) continue;
            try
            {
                var bytes = Convert.FromBase64String(blob);
                if (bytes.Length <= Contacts.SealedMail.MaxBlob) result.Add((box, bytes));
            }
            catch (FormatException) { }
        }
        return result;
    }

    /// <summary>Deletes mail this Jay has read, at every directory (best effort: it expires anyway).</summary>
    public async Task DropMailAsync(string box, byte[] token, CancellationToken ct) =>
        await FanOutAsync(new JsonObject { ["op"] = "drop", ["box"] = box, ["token"] = Convert.ToHexStringLower(token) }, ct);

}

/// <summary>目录服务拒绝了请求（口令不对、满了、房间不在了）。</summary>
public sealed class DirectoryRejectedException(string code, string message) : IOException(message)
{
    public string Code { get; } = code;
}

/// <summary>A problem a directory took a room's publish with (see <see cref="RoomDirectoryClient.AnnounceAsync"/>).</summary>
public enum AnnounceWarning
{
    /// <summary>This computer's clock is off: the room's invites can't be found through the directory.</summary>
    Clock,
    /// <summary>The directory left the room's invite entry out (its index is full).</summary>
    Lookup,
}

public static class AnnounceWarnings
{
    /// <summary>The text for a warning, in the current UI language (looked up when shown, not when it happened).</summary>
    public static string Text(this AnnounceWarning warning) => warning switch
    {
        AnnounceWarning.Clock => L.T("core.mp.public.err_clock"),
        _ => L.T("core.mp.public.warn_lookup"),
    };
}
