using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Amber.Core.Multiplayer.Public;

/// <summary>
/// A hidden room's entry in the directory snapshot. The host seals where to knock (room id, address, host key) under a
/// key derived from the invite's lobby key and files it under an index derived from the same key. Everyone downloads
/// every entry; only someone holding the invite can find and open this one, so the directory never learns who looked.
/// <para>
/// Every entry has the same size (<see cref="SealedLength"/>), so entries say nothing about their room.
/// </para>
/// <para>
/// The index is self-certifying: it is the hash of an Ed25519 key derived from the lobby key, and the directory only
/// files an entry under it when the publish is signed with that key (<see cref="SignPublish"/>). Only holders of the
/// invite can derive the key, so nobody else can put anything under an invite's index.
/// </para>
/// </summary>
public static class HiddenEntry
{
    /// <summary>Length of the index the directory files an entry under.</summary>
    public const int IndexLength = 32;

    /// <summary>Padded plaintext length: room id, address and host key as JSON fit with plenty to spare.</summary>
    public const int PlaintextLength = 256;

    private const int NonceLength = 12;
    private const int TagLength = 16;

    /// <summary>Length of every sealed entry.</summary>
    public const int SealedLength = NonceLength + PlaintextLength + TagLength;

    /// <summary>Length of the entry key's public half and of a publish signature.</summary>
    public const int PublicKeyLength = 32;
    public const int SignatureLength = 64;

    /// <summary>How far a publish signature's time may be from the directory's clock.</summary>
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(10);

    private static readonly byte[] EntryKeyInfo = "jay-dir-entry-key-v1"u8.ToArray();
    private static readonly byte[] IndexDomain = "jay-dir-index-v2"u8.ToArray();
    private static readonly byte[] PublishDomain = "jay-dir-entry-v1\n"u8.ToArray();
    private static readonly byte[] SealInfo = "jay-dir-seal-v1"u8.ToArray();

    /// <summary>The public half of the entry key for an invite's <paramref name="lobbyKey"/> (at least 16 bytes).</summary>
    public static byte[] PublicKey(ReadOnlySpan<byte> lobbyKey)
    {
        var seed = EntrySeed(lobbyKey);
        try { return new Org.BouncyCastle.Crypto.Parameters.Ed25519PrivateKeyParameters(seed).GeneratePublicKey().GetEncoded(); }
        finally { CryptographicOperations.ZeroMemory(seed); }
    }

    /// <summary>The index for an invite's <paramref name="lobbyKey"/>: the hash of its entry key.</summary>
    public static byte[] Index(ReadOnlySpan<byte> lobbyKey) => IndexOf(PublicKey(lobbyKey));

    /// <summary>The index an entry key may publish under (the directory checks this).</summary>
    public static byte[] IndexOf(ReadOnlySpan<byte> publicKey) => SHA256.HashData([.. IndexDomain, .. publicKey]);

    /// <summary>
    /// Signs publishing <paramref name="sealedEntry"/> for room <paramref name="roomId"/> in the listing version
    /// <paramref name="seq"/> (the listing's signing time in Unix milliseconds).
    /// </summary>
    public static byte[] SignPublish(ReadOnlySpan<byte> lobbyKey, ReadOnlySpan<byte> sealedEntry, string roomId, long seq)
    {
        var seed = EntrySeed(lobbyKey);
        try
        {
            var key = new Org.BouncyCastle.Crypto.Parameters.Ed25519PrivateKeyParameters(seed);
            var message = PublishBytes(IndexOf(key.GeneratePublicKey().GetEncoded()), sealedEntry, roomId, seq);
            var signer = new Org.BouncyCastle.Crypto.Signers.Ed25519Signer();
            signer.Init(true, key);
            signer.BlockUpdate(message, 0, message.Length);
            return signer.GenerateSignature();
        }
        finally { CryptographicOperations.ZeroMemory(seed); }
    }

    /// <summary>True when <paramref name="signature"/> by <paramref name="publicKey"/> publishes this entry under this index.</summary>
    public static bool VerifyPublish(ReadOnlySpan<byte> index, ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> sealedEntry, string roomId, long seq, byte[] signature) =>
        publicKey.Length == PublicKeyLength && signature.Length == SignatureLength && index.SequenceEqual(IndexOf(publicKey))
        && RoomSigner.Verify(publicKey.ToArray(), PublishBytes(index, sealedEntry, roomId, seq), signature);

    private static byte[] PublishBytes(ReadOnlySpan<byte> index, ReadOnlySpan<byte> sealedEntry, string roomId, long seq)
    {
        var time = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(time, seq);
        return [.. PublishDomain, .. index, .. sealedEntry, .. Encoding.UTF8.GetBytes(roomId), .. time];
    }

    private static byte[] EntrySeed(ReadOnlySpan<byte> lobbyKey)
    {
        Check(lobbyKey);
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, lobbyKey.ToArray(), 32, [], EntryKeyInfo);
    }

    /// <summary>Seals where to knock for holders of <paramref name="lobbyKey"/>. The index is bound as associated data.</summary>
    public static byte[] Seal(ReadOnlySpan<byte> lobbyKey, DirectoryHit hit)
    {
        var text = Encoding.UTF8.GetBytes(new JsonObject
        {
            ["id"] = hit.Id,
            ["ip"] = hit.Ip,
            ["key"] = Convert.ToHexStringLower(hit.HostKey),
        }.ToJsonString());
        if (text.Length > PlaintextLength) throw new ArgumentException("Hidden entry too large", nameof(hit));
        var plaintext = new byte[PlaintextLength];
        plaintext.AsSpan().Fill((byte)' ');
        text.CopyTo(plaintext, 0);
        var index = Index(lobbyKey);
        var key = SealKey(lobbyKey);
        try
        {
            using var aead = new ChaCha20Poly1305(key);
            var output = new byte[SealedLength];
            RandomNumberGenerator.Fill(output.AsSpan(0, NonceLength));
            aead.Encrypt(output.AsSpan(0, NonceLength), plaintext, output.AsSpan(NonceLength, PlaintextLength), output.AsSpan(NonceLength + PlaintextLength), index);
            return output;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>Opens an entry sealed by <see cref="Seal"/>. Null when it was altered, isn't for this key or doesn't parse.</summary>
    public static DirectoryHit? Open(ReadOnlySpan<byte> lobbyKey, ReadOnlySpan<byte> sealedEntry)
    {
        if (sealedEntry.Length != SealedLength) return null;
        var index = Index(lobbyKey);
        var key = SealKey(lobbyKey);
        var plaintext = new byte[PlaintextLength];
        try
        {
            using var aead = new ChaCha20Poly1305(key);
            aead.Decrypt(sealedEntry[..NonceLength], sealedEntry.Slice(NonceLength, PlaintextLength), sealedEntry[(NonceLength + PlaintextLength)..], plaintext, index);
            if (JsonNode.Parse(Encoding.UTF8.GetString(plaintext).TrimEnd(' ')) is not JsonObject o) return null;
            var id = o["id"]?.GetValue<string>();
            var ip = o["ip"]?.GetValue<string>();
            var host = o["key"]?.GetValue<string>();
            if (id is not { Length: > 0 } || ip is null || host is not { Length: 64 } || !RoomListing.IsPublicNetworkIp(ip)) return null;
            return new DirectoryHit(id, ip, Convert.FromHexString(host));
        }
        catch (Exception e) when (e is CryptographicException or System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static byte[] SealKey(ReadOnlySpan<byte> lobbyKey) => HKDF.DeriveKey(HashAlgorithmName.SHA256, lobbyKey.ToArray(), 32, [], SealInfo);

    private static void Check(ReadOnlySpan<byte> lobbyKey)
    {
        if (lobbyKey.Length < 16) throw new ArgumentException("Lobby key too short", nameof(lobbyKey));
    }
}

/// <summary>
/// This Jay's copy of every directory's snapshot (op <c>snap</c>): the public rooms and every hidden room's sealed entry.
/// Lookups by invite and the public list are answered from this copy, so the only thing a directory sees from a reader
/// is "I have version N, what changed?", the same request from everyone.
/// <para>
/// While <see cref="StartPolling"/> runs (the multiplayer page is connected to the public network), the copy is refreshed
/// every <see cref="PollEvery"/> on its own schedule and reads never touch the network, so when someone looks something
/// up says nothing either. Without polling (command line, tests), each read refreshes first.
/// </para>
/// </summary>
public sealed class DirectorySnapshots
{
    /// <summary>How often a polling copy is refreshed.</summary>
    public static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(60);

    private static readonly ConditionalWeakTable<IPublicNetwork, DirectorySnapshots> PerNetwork = new();

    /// <summary>The shared copy for <paramref name="network"/> (one per network session).</summary>
    public static DirectorySnapshots For(IPublicNetwork network) => PerNetwork.GetValue(network, _ => new DirectorySnapshots());

    /// <summary>
    /// A directory's copy is dropped when it hasn't been refreshed for this long (the directory went away): its rooms
    /// would have expired there by now.
    /// </summary>
    public static readonly TimeSpan StaleAfter = 2 * PollEvery + RoomDirectoryProtocol.Ttl;

    private sealed class Copy
    {
        public string Epoch = "";
        public long Version;
        public DateTimeOffset RefreshedAt;
        public readonly Dictionary<string, RoomListing> Rooms = new(StringComparer.Ordinal);
        /// <summary>"index:tag" → sealed entry (several rooms may have an entry under one index).</summary>
        public readonly Dictionary<string, byte[]> Hidden = new(StringComparer.Ordinal);
    }

    private readonly Func<DateTimeOffset> _now;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Copy> _copies = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _refreshing = new(1, 1);
    private CancellationTokenSource? _polling;
    /// <summary>Done once the first refresh was tried, whether it worked or not.</summary>
    private readonly TaskCompletionSource _firstAttempt = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Waits after failed refreshes before trying again (then back to <see cref="PollEvery"/>).</summary>
    internal static readonly TimeSpan[] RetryAfterFailure = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)];
    private Exception? _lastError;
    private DateTimeOffset? _refreshedAt;
    /// <summary>Done when the next refresh that starts from now on has finished (see <see cref="WaitForNextRefreshAsync"/>).</summary>
    private TaskCompletionSource? _nextRound;

    public DirectorySnapshots() : this(() => DateTimeOffset.UtcNow) { }

    internal DirectorySnapshots(Func<DateTimeOffset> now) => _now = now;

    /// <summary>True while a poller keeps this copy fresh.</summary>
    public bool IsPolling
    {
        get { lock (_gate) return _polling is not null; }
    }

    /// <summary>When the copy was last refreshed from at least one directory.</summary>
    public DateTimeOffset? RefreshedAt
    {
        get { lock (_gate) return _refreshedAt; }
    }

    /// <summary>
    /// Refreshes the copy every <see cref="PollEvery"/> until the returned handle is disposed. The first refresh runs at
    /// once. One poller per copy; starting again replaces the old one.
    /// </summary>
    public IDisposable StartPolling(Func<RoomDirectoryClient> client, TimeSpan? every = null)
    {
        var cts = new CancellationTokenSource();
        var token = cts.Token;
        lock (_gate)
        {
            _polling?.Cancel();
            _polling = cts;
        }
        var interval = every ?? PollEvery;
        _ = Task.Run(async () =>
        {
            var failures = 0;
            while (!token.IsCancellationRequested)
            {
                // Nothing a directory sends may stop the poller: a bad reply only costs that round.
                try
                {
                    await RefreshAsync(client(), token);
                    failures = 0;
                }
                catch (Exception e) when (e is not OutOfMemoryException) { failures++; }
                // A failed round (busy, unreachable) is tried again soon, a few times, before going back to the schedule.
                var wait = failures is > 0 and <= 3 ? RetryAfterFailure[failures - 1] : interval;
                try { await Task.Delay(Jitter(wait < interval ? wait : interval), token); }
                catch (OperationCanceledException) { break; }
            }
        });
        return new Stop(this, cts);
    }

    /// <summary>±20%, so readers don't all come back at the same moment after a directory restart or a busy spell.</summary>
    internal static TimeSpan Jitter(TimeSpan wait) => wait * (0.8 + 0.4 * Random.Shared.NextDouble());

    private sealed class Stop(DirectorySnapshots owner, CancellationTokenSource cts) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 1) return;
            lock (owner._gate)
                if (owner._polling == cts) owner._polling = null;
            cts.Cancel();
            cts.Dispose();
            owner._firstAttempt.TrySetResult();
        }
    }

    /// <summary>
    /// Fetches what changed from every directory and applies it. Throws when no directory answered with a usable
    /// snapshot. Copies of directories that weren't asked this round, or haven't answered for <see cref="StaleAfter"/>,
    /// are dropped.
    /// </summary>
    public async Task RefreshAsync(RoomDirectoryClient client, CancellationToken ct)
    {
        await _refreshing.WaitAsync(ct);
        TaskCompletionSource? round;
        lock (_gate)
        {
            round = _nextRound;
            _nextRound = null;
        }
        try
        {
            Dictionary<string, (string Epoch, long Version)> have;
            lock (_gate) have = _copies.ToDictionary(p => p.Key, p => (p.Value.Epoch, p.Value.Version), StringComparer.Ordinal);
            List<(string Ip, JsonObject? Reply, Exception? Error)> replies;
            try { replies = await client.SnapshotAsync(have, ct); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                lock (_gate) _lastError = e;
                _firstAttempt.TrySetResult();
                throw;
            }
            var parsed = replies.Select(r => (r.Ip, Parsed: r.Reply is null ? null : Parse(r.Reply), r.Error)).ToList();
            var any = false;
            Exception? error = null;
            var now = _now();
            lock (_gate)
            {
                foreach (var gone in _copies.Keys.Where(ip => !parsed.Any(r => r.Ip == ip)).ToList()) _copies.Remove(gone);
                foreach (var (ip, reply, failure) in parsed)
                {
                    if (reply is null)
                    {
                        error ??= failure ?? new InvalidDataException(Localization.L.T("core.mp.public.bad_reply"));
                        continue;
                    }
                    any |= Apply(ip, reply, now);
                }
                DropStale(now);
                if (any)
                {
                    _refreshedAt = now;
                    _lastError = null;
                }
                else _lastError = error ?? new IOException(Localization.L.T("core.mp.public.no_directory"));
            }
            _firstAttempt.TrySetResult();
            // Nobody answered: a reader that asked for fresh data gets the error; a poller keeps the copy it has.
            if (!any) throw error ?? new IOException(Localization.L.T("core.mp.public.no_directory"));
        }
        finally
        {
            _refreshing.Release();
            round?.TrySetResult();
            // Even a cancelled first round counts as tried: readers waiting for it get an answer now.
            _firstAttempt.TrySetResult();
        }
    }

    private void DropStale(DateTimeOffset now)
    {
        foreach (var (ip, copy) in _copies.Where(p => now - p.Value.RefreshedAt > StaleAfter).ToList()) _copies.Remove(ip);
    }

    /// <summary>One directory's reply, checked through before anything is applied.</summary>
    private sealed record Reply(string Epoch, long Version, bool Full, long? From, List<string> Gone, List<RoomListing> Rooms, List<(string Key, byte[] Blob)> Hidden);

    /// <summary>Whether a snapshot reply is well formed throughout.</summary>
    internal static bool IsUsable(JsonObject reply) => Parse(reply) is not null;

    /// <summary>Reads a snapshot reply. Null when anything in it is malformed (then none of it is used).</summary>
    private static Reply? Parse(JsonObject reply)
    {
        try
        {
            if (reply["ok"]?.GetValue<bool>() != true || reply["epoch"]?.GetValue<string>() is not { Length: > 0 and <= 64 } epoch
                || reply["ver"]?.GetValue<long>() is not { } version) return null;
            var full = reply["full"]?.GetValue<bool>() == true;
            var from = reply["from"]?.GetValue<long>();
            var gone = new List<string>();
            foreach (var key in reply["gone"] as JsonArray ?? [])
                if (key?.GetValue<string>() is { } k) gone.Add(k);
            var rooms = (reply["rooms"] as JsonArray ?? []).Select(RoomListing.FromJson).OfType<RoomListing>().ToList();
            var hidden = new List<(string, byte[])>();
            foreach (var item in (reply["hidden"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var index = item["k"]?.GetValue<string>();
                var tag = item["t"]?.GetValue<string>();
                var blob = item["b"]?.GetValue<string>();
                if (index is not { Length: HiddenEntry.IndexLength * 2 } || !index.All(char.IsAsciiHexDigit)
                    || tag is not { Length: > 0 and <= 32 } || !tag.All(char.IsAsciiHexDigit) || blob is null) continue;
                var bytes = Convert.FromBase64String(blob);
                if (bytes.Length == HiddenEntry.SealedLength) hidden.Add((index.ToLowerInvariant() + ":" + tag.ToLowerInvariant(), bytes));
            }
            return new Reply(epoch, version, full, from, gone, rooms, hidden);
        }
        catch (Exception e) when (e is FormatException or InvalidOperationException or ArgumentException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    internal IReadOnlyList<string> CopiesForTests() { lock (_gate) return [.. _copies.Keys]; }

    internal bool ApplyForTests(string ip, JsonObject reply)
    {
        if (Parse(reply) is not { } parsed) return false;
        lock (_gate) return Apply(ip, parsed, _now());
    }

    /// <summary>Applies one directory's checked reply. False when it isn't usable on top of the copy (a delta from elsewhere).</summary>
    private bool Apply(string ip, Reply reply, DateTimeOffset now)
    {
        _copies.TryGetValue(ip, out var copy);
        // A delta only makes sense on top of the copy it was computed from.
        if (!reply.Full && (copy is null || copy.Epoch != reply.Epoch || reply.From != copy.Version))
        {
            // Deltas for some other copy: drop ours, so the next round asks for the full snapshot.
            _copies.Remove(ip);
            return false;
        }
        if (copy is null || reply.Full) _copies[ip] = copy = new Copy();
        foreach (var key in reply.Gone)
        {
            if (key.StartsWith("r:", StringComparison.Ordinal)) copy.Rooms.Remove(key[2..]);
            else if (key.StartsWith("h:", StringComparison.Ordinal)) copy.Hidden.Remove(key[2..].ToLowerInvariant());
        }
        foreach (var room in reply.Rooms) copy.Rooms[room.Id] = room;
        foreach (var (key, blob) in reply.Hidden) copy.Hidden[key] = blob;
        copy.Epoch = reply.Epoch;
        copy.Version = reply.Version;
        copy.RefreshedAt = now;
        return true;
    }

    /// <summary>
    /// Makes sure the copy is good to read: while polling, waits until the first refresh was tried and then needs a copy
    /// that is still fresh, else throws the last error; without polling, refreshes now.
    /// </summary>
    internal async Task ReadyAsync(RoomDirectoryClient client, CancellationToken ct)
    {
        if (!IsPolling)
        {
            await RefreshAsync(client, ct);
            return;
        }
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // The first snapshot of a busy directory can take as long as the directory gives it.
        wait.CancelAfter(RoomDirectoryProtocol.SnapshotTimeout + RoomDirectoryProtocol.SnapshotDeadline(RoomDirectoryProtocol.MaxReply));
        try { await _firstAttempt.Task.WaitAsync(wait.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        lock (_gate)
        {
            DropStale(_now());
            if (_copies.Count > 0) return;
            throw _lastError ?? new IOException(Localization.L.T("core.mp.public.no_directory"));
        }
    }

    /// <summary>
    /// While polling, waits for the next scheduled refresh that starts after this call to finish (at most about one
    /// poll interval), so something published a moment ago can be found without asking the directories out of turn.
    /// Returns at once when not polling.
    /// </summary>
    internal async Task WaitForNextRefreshAsync(CancellationToken ct)
    {
        Task round;
        lock (_gate)
        {
            if (_polling is null) return;
            round = (_nextRound ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(NextRefreshWait);
        try { await round.WaitAsync(wait.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
    }

    /// <summary>Longest wait in <see cref="WaitForNextRefreshAsync"/>: the longest poll interval plus time for the round.</summary>
    internal static readonly TimeSpan NextRefreshWait = PollEvery * 1.2 + RoomDirectoryProtocol.RequestTimeout;

    /// <summary>Public rooms from every directory, each room once at its highest seq.</summary>
    public IReadOnlyList<RoomListing> Rooms()
    {
        lock (_gate)
        {
            DropStale(_now());
            var rooms = new Dictionary<string, RoomListing>(StringComparer.Ordinal);
            foreach (var room in _copies.Values.SelectMany(c => c.Rooms.Values))
                if (!rooms.TryGetValue(room.Id, out var have) || room.Seq > have.Seq) rooms[room.Id] = room;
            return [.. rooms.Values];
        }
    }

    /// <summary>
    /// The one place this invite points at, or null when there is none or the entries disagree. With
    /// <paramref name="expectedHost"/> it is the first of <see cref="FindAll"/>; prefer that and try each.
    /// </summary>
    public DirectoryHit? Find(ReadOnlySpan<byte> lobbyKey, byte[]? expectedHost = null) =>
        FindAll(lobbyKey, expectedHost) is [var one, ..] ? one : null;

    /// <summary>
    /// Where this invite says the host is, from the hidden entries and the listings that carry it. Holding an invite (or
    /// seeing a public one) doesn't make anyone the host, so entries may disagree, and a host key alone proves nothing
    /// (it is public): an entry is the whole (host key, address, room).
    /// <list type="bullet">
    /// <item>Without <paramref name="expectedHost"/>: one answer only when every entry gives the same host key and
    /// address; anything else is a conflict and gives nothing.</item>
    /// <item>With it: the distinct addresses given for that host, for the caller to try in turn (the knock handshake
    /// proves the host; a wrong address just fails): every hidden-entry address, then a few listed ones.</item>
    /// </list>
    /// </summary>
    public IReadOnlyList<DirectoryHit> FindAll(ReadOnlySpan<byte> lobbyKey, byte[]? expectedHost = null)
    {
        var prefix = Convert.ToHexStringLower(HiddenEntry.Index(lobbyKey)) + ":";
        List<byte[]> candidates;
        List<(DirectoryHit Hit, int Copy)> listed;
        var keyHex = Convert.ToHexStringLower(lobbyKey);
        var key = lobbyKey.ToArray();
        lock (_gate)
        {
            DropStale(_now());
            candidates = [.. _copies.Values.SelectMany(c => c.Hidden).Where(p => p.Key.StartsWith(prefix, StringComparison.Ordinal)).Select(p => p.Value)];
            // A room listed with this invite in the open (public, direct or password rooms carry it) has no hidden entry;
            // its listing says where it is.
            listed = [.. _copies.Values.SelectMany((c, i) => c.Rooms.Values
                .Where(r => r.Lobby == keyHex || (JayInvite.TryParse(r.Invite, out var invite) && invite.LobbyKey.AsSpan().SequenceEqual(key)))
                .Select(r => (new DirectoryHit(r.Id, r.Ip, r.HostKey), i)))];
        }
        var hidden = candidates.Select(blob => HiddenEntry.Open(key, blob)).OfType<DirectoryHit>().ToList();
        if (expectedHost is not null)
        {
            bool Ours(DirectoryHit h) => h.HostKey.AsSpan().SequenceEqual(expectedHost);
            // Hidden entries come first, all of them: only invite holders can write them, at most a few per invite.
            var first = hidden.Where(Ours).DistinctBy(h => h.Ip).ToList();
            // Then listed addresses, at most a few: those more directories give first, ties in random order so the
            // same entries can't always come first.
            var rest = listed.Where(l => Ours(l.Hit) && !first.Any(f => f.Ip == l.Hit.Ip))
                .GroupBy(l => l.Hit.Ip)
                .Select(g => (g.First().Hit, Copies: g.Select(l => l.Copy).Distinct().Count(), Order: Random.Shared.Next()))
                .OrderByDescending(x => x.Copies).ThenBy(x => x.Order)
                .Take(MaxAddressesToTry)
                .Select(x => x.Hit);
            return [.. first.Concat(rest)];
        }
        var places = hidden.Concat(listed.Select(l => l.Hit))
            .DistinctBy(h => (Convert.ToHexStringLower(h.HostKey), h.Ip)).ToList();
        return places.Count == 1 ? places : [];
    }

    /// <summary>
    /// At most this many listed addresses are tried for one host, after every hidden-entry address (each one that isn't
    /// the host learns of the attempt).
    /// </summary>
    public const int MaxAddressesToTry = 4;
}
