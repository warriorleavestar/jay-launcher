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

    private static readonly byte[] IndexInfo = "jay-dir-index-v1"u8.ToArray();
    private static readonly byte[] SealInfo = "jay-dir-seal-v1"u8.ToArray();

    /// <summary>The index for an invite's <paramref name="lobbyKey"/> (at least 16 bytes).</summary>
    public static byte[] Index(ReadOnlySpan<byte> lobbyKey)
    {
        Check(lobbyKey);
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, lobbyKey.ToArray(), IndexLength, [], IndexInfo);
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

    private sealed class Copy
    {
        public string Epoch = "";
        public long Version;
        public readonly Dictionary<string, RoomListing> Rooms = new(StringComparer.Ordinal);
        public readonly Dictionary<string, byte[]> Hidden = new(StringComparer.Ordinal);
    }

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Copy> _copies = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _refreshing = new(1, 1);
    private CancellationTokenSource? _polling;
    private TaskCompletionSource _firstRefresh = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Exception? _lastError;

    /// <summary>True while a poller keeps this copy fresh.</summary>
    public bool IsPolling
    {
        get { lock (_gate) return _polling is not null; }
    }

    /// <summary>When the copy was last refreshed from at least one directory.</summary>
    public DateTimeOffset? RefreshedAt { get; private set; }

    /// <summary>
    /// Refreshes the copy every <see cref="PollEvery"/> until the returned handle is disposed. The first refresh runs at
    /// once. One poller per copy; starting again replaces the old one.
    /// </summary>
    public IDisposable StartPolling(Func<RoomDirectoryClient> client, TimeSpan? every = null)
    {
        var cts = new CancellationTokenSource();
        lock (_gate)
        {
            _polling?.Cancel();
            _polling = cts;
        }
        var interval = every ?? PollEvery;
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try { await RefreshAsync(client(), cts.Token); }
                catch (Exception e) when (e is IOException or InvalidDataException or OperationCanceledException or InvalidOperationException) { }
                try { await Task.Delay(interval, cts.Token); }
                catch (OperationCanceledException) { break; }
            }
        });
        return new Stop(this, cts);
    }

    private sealed class Stop(DirectorySnapshots owner, CancellationTokenSource cts) : IDisposable
    {
        public void Dispose()
        {
            lock (owner._gate)
                if (owner._polling == cts) owner._polling = null;
            cts.Cancel();
        }
    }

    /// <summary>
    /// Fetches what changed from every directory and applies it. Throws when no directory answered and there is no copy
    /// from an earlier refresh.
    /// </summary>
    public async Task RefreshAsync(RoomDirectoryClient client, CancellationToken ct)
    {
        await _refreshing.WaitAsync(ct);
        try
        {
            Dictionary<string, (string Epoch, long Version)> have;
            lock (_gate) have = _copies.ToDictionary(p => p.Key, p => (p.Value.Epoch, p.Value.Version), StringComparer.Ordinal);
            var replies = await client.SnapshotAsync(have, ct);
            var any = false;
            Exception? error = null;
            lock (_gate)
            {
                foreach (var (ip, reply, failure) in replies)
                {
                    if (reply is null)
                    {
                        error ??= failure;
                        continue;
                    }
                    any |= Apply(ip, reply);
                }
            }
            if (any)
            {
                RefreshedAt = DateTimeOffset.UtcNow;
                _lastError = null;
                _firstRefresh.TrySetResult();
            }
            else
            {
                // Nobody answered: a reader that asked for fresh data gets the error; a poller keeps the copy it has.
                _lastError = error ?? new IOException(Localization.L.T("core.mp.public.no_directory"));
                throw _lastError;
            }
        }
        finally
        {
            _refreshing.Release();
        }
    }

    /// <summary>Applies one directory's reply. False when the reply isn't a usable snapshot.</summary>
    private bool Apply(string ip, JsonObject reply)
    {
        if (reply["ok"]?.GetValue<bool>() != true || reply["epoch"]?.GetValue<string>() is not { Length: > 0 and <= 64 } epoch
            || reply["ver"]?.GetValue<long>() is not { } version) return false;
        if (!_copies.TryGetValue(ip, out var copy)) _copies[ip] = copy = new Copy();
        var full = reply["full"]?.GetValue<bool>() == true;
        // A delta only makes sense on top of the copy it was computed from.
        if (!full && (copy.Epoch != epoch || reply["from"]?.GetValue<long>() != copy.Version)) return false;
        if (full)
        {
            copy.Rooms.Clear();
            copy.Hidden.Clear();
        }
        foreach (var key in (reply["gone"] as JsonArray ?? []).Select(k => k?.GetValue<string>()).OfType<string>())
        {
            if (key.StartsWith("r:", StringComparison.Ordinal)) copy.Rooms.Remove(key[2..]);
            else if (key.StartsWith("h:", StringComparison.Ordinal)) copy.Hidden.Remove(key[2..]);
        }
        foreach (var room in (reply["rooms"] as JsonArray ?? []).Select(RoomListing.FromJson).OfType<RoomListing>())
            copy.Rooms[room.Id] = room;
        foreach (var item in (reply["hidden"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var index = item["k"]?.GetValue<string>();
            var blob = item["b"]?.GetValue<string>();
            if (index is not { Length: HiddenEntry.IndexLength * 2 } || !index.All(char.IsAsciiHexDigit) || blob is null) continue;
            try
            {
                var bytes = Convert.FromBase64String(blob);
                if (bytes.Length == HiddenEntry.SealedLength) copy.Hidden[index.ToLowerInvariant()] = bytes;
            }
            catch (FormatException) { }
        }
        copy.Epoch = epoch;
        copy.Version = version;
        return true;
    }

    /// <summary>
    /// Makes sure the copy is good to read: while polling, waits for the first refresh (or returns the copy as it is
    /// when that failed); otherwise refreshes now.
    /// </summary>
    internal async Task ReadyAsync(RoomDirectoryClient client, CancellationToken ct)
    {
        if (!IsPolling)
        {
            await RefreshAsync(client, ct);
            return;
        }
        Task first;
        lock (_gate) first = _firstRefresh.Task;
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(RoomDirectoryProtocol.RequestTimeout);
        try { await first.WaitAsync(wait.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            if (_lastError is { } error) throw error;
            throw new IOException(Localization.L.T("core.mp.public.no_directory"));
        }
    }

    /// <summary>Public rooms from every directory, each room once at its highest seq.</summary>
    public IReadOnlyList<RoomListing> Rooms()
    {
        lock (_gate)
        {
            var rooms = new Dictionary<string, RoomListing>(StringComparer.Ordinal);
            foreach (var room in _copies.Values.SelectMany(c => c.Rooms.Values))
                if (!rooms.TryGetValue(room.Id, out var have) || room.Seq > have.Seq) rooms[room.Id] = room;
            return [.. rooms.Values];
        }
    }

    /// <summary>Where to knock for the room behind an invite's lobby key, from the local copy only. Null when no entry opens.</summary>
    public DirectoryHit? Find(ReadOnlySpan<byte> lobbyKey)
    {
        var index = Convert.ToHexStringLower(HiddenEntry.Index(lobbyKey));
        List<byte[]> candidates;
        lock (_gate) candidates = [.. _copies.Values.Select(c => c.Hidden.GetValueOrDefault(index)).OfType<byte[]>()];
        foreach (var blob in candidates)
            if (HiddenEntry.Open(lobbyKey, blob) is { } hit) return hit;
        return null;
    }
}
