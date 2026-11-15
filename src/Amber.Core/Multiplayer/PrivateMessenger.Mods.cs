using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Amber.Core.Multiplayer;

/// <summary>What the host answered when asked for its mod list.</summary>
public enum ModListAnswer
{
    /// <summary>Here is the list.</summary>
    Sent,
    /// <summary>The host turned off “let people who join sync mods from me”.</summary>
    Off,
    /// <summary>No answer in time (offline, an older Jay version, or the list was damaged).</summary>
    NoAnswer,
}

public sealed record ModListReply(ModListAnswer Answer, ModManifest? Manifest);

/// <summary>
/// Mod sync over the room's end-to-end encrypted private channel (official §6.14 “check the mods”): the joiner asks the
/// host for its mod list, and for single mod files that are not on a public site. The host only ever sends files from the
/// list it published, and only while it allows syncing; the joiner accepts only files it asked for and checks their SHA-1.
/// </summary>
public sealed partial class PrivateMessenger
{
    /// <summary>The list is gzip'd JSON in pieces of this size (frames are at most 8 KB).</summary>
    private const int ModPieceSize = 3 * 1024;
    private const int MaxModPieces = 160;
    private static readonly TimeSpan ModListTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ModFileTimeout = TimeSpan.FromMinutes(10);

    private sealed class ModListWait
    {
        public required string From;
        public readonly TaskCompletionSource<ModListReply> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string?[]? Pieces;
    }

    private readonly ConcurrentDictionary<string, ModListWait> _modLists = new();
    private readonly ConcurrentDictionary<(string Peer, string Sha1), TaskCompletionSource<string?>> _modFiles = new();
    private readonly ConcurrentDictionary<string, DateTime> _modListAsked = new();

    /// <summary>
    /// Host side: the room's mod list, or null when syncing is off. <see cref="ModFileProvider"/> finds a listed file by SHA-1.
    /// </summary>
    public Func<ModManifest?>? ModListProvider { get; set; }

    public Func<string, string?>? ModFileProvider { get; set; }

    /// <summary>Joiner: ask the host for its mod list.</summary>
    public async Task<ModListReply> RequestModListAsync(string host, CancellationToken ct = default)
    {
        var rid = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        var wait = new ModListWait { From = host };
        _modLists[rid] = wait;
        try
        {
            if (!await SendAsync(host, new JsonObject { ["t"] = "mods?", ["rid"] = rid }, ct)) return new(ModListAnswer.NoAnswer, null);
            return await wait.Done.Task.WaitAsync(ModListTimeout, ct);
        }
        catch (TimeoutException)
        {
            return new(ModListAnswer.NoAnswer, null);
        }
        finally
        {
            _modLists.TryRemove(rid, out _);
        }
    }

    /// <summary>Joiner: ask the host for one mod file from its list. Returns where it was saved, or null.</summary>
    public async Task<string?> RequestModFileAsync(string host, string sha1, CancellationToken ct = default)
    {
        sha1 = sha1.ToLowerInvariant();
        var done = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_modFiles.TryAdd((host, sha1), done)) return null;
        try
        {
            if (!await SendAsync(host, new JsonObject { ["t"] = "modfile?", ["sha1"] = sha1 }, ct)) return null;
            return await done.Task.WaitAsync(ModFileTimeout, ct);
        }
        catch (TimeoutException)
        {
            return null;
        }
        finally
        {
            _modFiles.TryRemove((host, sha1), out _);
        }
    }

    private JsonObject OfferBody(Outgoing job, string? modSha1)
    {
        var body = new JsonObject { ["t"] = "offer", ["id"] = job.Id, ["name"] = job.Name, ["size"] = job.Size, ["sha"] = Convert.ToHexStringLower(job.Hash) };
        if (modSha1 is not null) body["mod"] = modSha1;
        return body;
    }

    /// <summary>The offer is a mod file we asked this peer for: its SHA-1, else null.</summary>
    private string? WantedMod(string from, string? sha1, long size) =>
        sha1 is { Length: 40 } && size <= 512L * 1024 * 1024 && _modFiles.ContainsKey((from, sha1.ToLowerInvariant())) ? sha1.ToLowerInvariant() : null;

    private void CompleteMod(Incoming job, string? path)
    {
        if (job.ModSha1 is { } sha1 && _modFiles.TryGetValue((job.Offer.FromFingerprint, sha1), out var done)) done.TrySetResult(path);
    }

    /// <summary>An offer for a mod file we asked for (<paramref name="sha1"/>, null when it isn't one) can't be taken: stop waiting for it.</summary>
    private void RefuseMod(string from, string? sha1)
    {
        if (sha1 is not null && _modFiles.TryGetValue((from, sha1), out var done)) done.TrySetResult(null);
    }

    /// <summary>Host: send a requested mod file; when it can't be sent or the joiner doesn't take it, answer "modfileno".</summary>
    private async Task SendModFileAsync(string to, string path, string sha1)
    {
        bool sent;
        try { sent = await SendFileCoreAsync(to, path, sha1, _cts.Token); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { sent = false; }
        catch (OperationCanceledException) { return; }
        if (sent) return;
        try { await SendAsync(to, new JsonObject { ["t"] = "modfileno", ["sha1"] = sha1 }, _cts.Token); }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException
            or System.Net.Sockets.SocketException) { }
    }

    private static bool MatchesSha1(string path, string expected)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA1.HashData(stream)) == expected;
    }

    private async Task HandleModsAsync(PrivateFrame frame, string type, JsonObject body)
    {
        var rid = body["rid"]?.GetValue<string>();
        switch (type)
        {
            case "mods?" when rid is { Length: 16 }:
                // At most one list every 5 seconds per person.
                var now = DateTime.UtcNow;
                if (_modListAsked.TryGetValue(frame.FromFingerprint, out var last) && now - last < TimeSpan.FromSeconds(5)) return;
                _modListAsked[frame.FromFingerprint] = now;
                if (ModListProvider?.Invoke() is not { } manifest)
                {
                    await SendAsync(frame.FromFingerprint, new JsonObject { ["t"] = "modsoff", ["rid"] = rid }, _cts.Token);
                    return;
                }
                var pieces = Pack(manifest);
                for (var i = 0; i < pieces.Count; i++)
                    if (!await SendAsync(frame.FromFingerprint, new JsonObject { ["t"] = "mods", ["rid"] = rid, ["i"] = i, ["n"] = pieces.Count, ["d"] = pieces[i] }, _cts.Token)) return;
                break;
            case "modsoff" when rid is not null && _modLists.TryGetValue(rid, out var off) && off.From == frame.FromFingerprint:
                off.Done.TrySetResult(new(ModListAnswer.Off, null));
                break;
            case "mods" when rid is not null && _modLists.TryGetValue(rid, out var wait) && wait.From == frame.FromFingerprint:
                var index = body["i"]?.GetValue<int>() ?? -1;
                var count = body["n"]?.GetValue<int>() ?? 0;
                if (count is < 1 or > MaxModPieces || index < 0 || index >= count) return;
                wait.Pieces ??= new string?[count];
                if (wait.Pieces.Length != count) return;
                wait.Pieces[index] = body["d"]?.GetValue<string>();
                if (wait.Pieces.Any(p => p is null)) return;
                var received = Unpack(wait.Pieces!);
                wait.Done.TrySetResult(received is null ? new(ModListAnswer.NoAnswer, null) : new(ModListAnswer.Sent, received));
                break;
            case "modfile?":
                var sha1 = body["sha1"]?.GetValue<string>()?.ToLowerInvariant();
                // Only files from the published list, and only while syncing is allowed.
                if (sha1 is not { Length: 40 } || ModListProvider?.Invoke() is not { } list || !list.Mods.Any(m => m.Sha1 == sha1)
                    || ModFileProvider?.Invoke(sha1) is not { } path || !File.Exists(path))
                {
                    await SendAsync(frame.FromFingerprint, new JsonObject { ["t"] = "modfileno", ["sha1"] = sha1 ?? "" }, _cts.Token);
                    return;
                }
                _ = SendModFileAsync(frame.FromFingerprint, path, sha1);
                break;
            case "modfileno":
                var refused = body["sha1"]?.GetValue<string>()?.ToLowerInvariant();
                if (refused is not null && _modFiles.TryGetValue((frame.FromFingerprint, refused), out var no)) no.TrySetResult(null);
                break;
        }
    }

    internal static List<string> Pack(ModManifest manifest)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
            gzip.Write(System.Text.Encoding.UTF8.GetBytes(manifest.ToJson()));
        var bytes = buffer.ToArray();
        var pieces = new List<string>();
        for (var at = 0; at < bytes.Length; at += ModPieceSize)
            pieces.Add(Convert.ToBase64String(bytes, at, Math.Min(ModPieceSize, bytes.Length - at)));
        return pieces;
    }

    internal static ModManifest? Unpack(IEnumerable<string> pieces)
    {
        try
        {
            var bytes = pieces.SelectMany(Convert.FromBase64String).ToArray();
            using var gzip = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
            using var output = new MemoryStream();
            // A list of the largest allowed size is far below 2 MB; stop a gzip bomb there.
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = gzip.Read(chunk)) > 0)
            {
                output.Write(chunk, 0, read);
                if (output.Length > 2 * 1024 * 1024) return null;
            }
            return ModManifest.FromJson(System.Text.Encoding.UTF8.GetString(output.ToArray()));
        }
        catch (Exception e) when (e is FormatException or InvalidDataException)
        {
            return null;
        }
    }
}
