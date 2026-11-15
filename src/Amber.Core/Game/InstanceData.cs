using Amber.Core.Localization;
using Amber.Core.Content;
using System.IO.Compression;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Amber.Core.Game;

public sealed record WorldInfo(string FolderName, string Path, string Name, string GameMode, DateTimeOffset? LastPlayed, string? Version, long? SizeBytes = null);

/// <summary>saves 文件夹：列出世界、备份、从压缩包导入。</summary>
public static class WorldManager
{
    public static List<WorldInfo> List(string savesDir)
    {
        if (!Directory.Exists(savesDir)) return [];
        var result = new List<WorldInfo>();
        foreach (var folder in Directory.EnumerateDirectories(savesDir))
        {
            var levelDat = Path.Combine(folder, "level.dat");
            if (!File.Exists(levelDat)) continue;
            result.Add(Read(folder, levelDat));
        }
        return result.OrderByDescending(w => w.LastPlayed).ToList();
    }

    private static WorldInfo Read(string folder, string levelDat)
    {
        var folderName = Path.GetFileName(folder);
        try
        {
            var root = Nbt.ReadFile(levelDat);
            if (root.GetValueOrDefault("Data") is not Dictionary<string, object> data)
                return new WorldInfo(folderName, folder, folderName, L.T("core.game.world.unknownmode"), null, null);
            var last = data.GetValueOrDefault("LastPlayed") is long ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : (DateTimeOffset?)null;
            var version = data.GetValueOrDefault("Version") is Dictionary<string, object> v ? v.GetValueOrDefault("Name") as string : null;
            var hardcore = data.GetValueOrDefault("hardcore") is byte and not (byte)0;
            var mode = hardcore ? L.T("core.game.world.hardcore") : (data.GetValueOrDefault("GameType") as int? ?? 0) switch
            {
                0 => L.T("core.game.world.survival"),
                1 => L.T("core.game.world.creative"),
                2 => L.T("core.game.world.adventure"),
                3 => L.T("core.game.world.spectator"),
                _ => L.T("core.game.world.unknownmode"),
            };
            return new WorldInfo(folderName, folder, data.GetValueOrDefault("LevelName") as string ?? folderName, mode, last, version);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or EndOfStreamException or ArgumentException or IndexOutOfRangeException)
        {
            return new WorldInfo(folderName, folder, L.F("core.game.world.corruptlevel", folderName), L.T("core.game.world.unknownmode"), null, null);
        }
    }

    /// <summary>打包备份到 saves 旁边的 backups 文件夹。返回备份文件路径。</summary>
    public static string Backup(WorldInfo world, string backupsDir)
    {
        Directory.CreateDirectory(backupsDir);
        var target = Path.Combine(backupsDir, $"{world.FolderName}-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        // session.lock 被运行中的游戏占用，不需要备份。
        using var zip = ZipFile.Open(target, ZipArchiveMode.Create);
        foreach (var file in Directory.EnumerateFiles(world.Path, "*", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(file) == "session.lock") continue;
            var name = world.FolderName + "/" + Path.GetRelativePath(world.Path, file).Replace('\\', '/');
            var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
            using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var output = entry.Open();
            input.CopyTo(output);
        }
        return target;
    }

    /// <summary>
    /// How much a world import may unpack. Zips come from other people (a received file, a download), so the entry count
    /// and the sizes the zip claims are checked before anything is written, the bytes actually written are counted
    /// against the same limits, and the free space must cover what the zip says it holds.
    /// </summary>
    public sealed record ImportLimits(int MaxEntries, long MaxDirectoryBytes, long MaxEntryBytes, long MaxTotalBytes, long KeepFree)
    {
        public static readonly ImportLimits Default = new(200_000, 64L * 1024 * 1024, 8L * 1024 * 1024 * 1024, 64L * 1024 * 1024 * 1024, 256L * 1024 * 1024);

        /// <summary>Free bytes on the drive of a folder; null when it can't be told (then only the other limits apply).</summary>
        public Func<string, long?> FreeSpace { get; init; } = FreeBytes;

        private static long? FreeBytes(string folder)
        {
            try { return new DriveInfo(Path.GetFullPath(folder)).AvailableFreeSpace; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
        }
    }

    /// <summary>
    /// 从压缩包导入世界（压缩包里某层有 level.dat 即可）。返回新文件夹名。
    /// Unpacks into a staging folder next to saves and moves it in only when everything was written; a failure or a
    /// cancel removes the staging folder, so saves never holds half a world and the worlds there are never touched.
    /// </summary>
    public static string Import(string zipPath, string savesDir, ImportLimits? limits = null, CancellationToken ct = default)
    {
        limits ??= ImportLimits.Default;
        // The zip's own list first, one entry at a time: counts and claimed sizes, before the zip is opened for real.
        long declared = 0;
        ZipDirectory.Scan(zipPath, limits.MaxEntries, limits.MaxDirectoryBytes, e =>
        {
            if (e.Size > limits.MaxEntryBytes) throw new InvalidDataException(L.F("core.game.world.entry_too_big", e.Name));
            declared += e.Size;
            if (declared > limits.MaxTotalBytes) throw new InvalidDataException(L.T("core.game.world.too_big"));
            return true;
        }, ct);
        var saves = Path.GetFullPath(savesDir);
        using var zip = ZipFile.OpenRead(zipPath);
        // 先统一归档路径的两种分隔符，并在任何写入前验证全部路径。
        var entries = zip.Entries.Select(e => (Entry: e, Name: ImportPath(e.FullName))).ToList();
        var level = entries.Where(e => e.Name.Split('/').Last() == "level.dat").OrderBy(e => e.Name.Length).FirstOrDefault();
        if (level.Entry is null) throw new InvalidDataException(L.T("core.game.world.missinglevel"));
        var prefix = level.Name[..^"level.dat".Length];
        var baseName = prefix.TrimEnd('/').Split('/').LastOrDefault(s => s.Length > 0) ?? Path.GetFileNameWithoutExtension(zipPath);
        CheckWorldComponent(baseName);

        var files = entries.Where(e => e.Name.StartsWith(prefix, StringComparison.Ordinal) && !e.Name.EndsWith('/')).ToList();
        // Every path has passed its check; only now is anything created.
        Directory.CreateDirectory(saves);
        if (limits.FreeSpace(saves) is { } free && free - limits.KeepFree < declared)
            throw new IOException(L.T("core.game.world.no_space"));
        // Next to saves (the game doesn't list it as a world while it fills); inside saves when saves is a link, so the
        // last move stays on one drive.
        var trimmed = saves.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var stagingParent = new DirectoryInfo(trimmed).LinkTarget is null ? Path.GetDirectoryName(trimmed) ?? saves : saves;
        var staging = Path.Combine(stagingParent, ".jay-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            long written = 0;
            var buffer = new byte[81920];
            foreach (var (entry, name) in files)
            {
                ct.ThrowIfCancellationRequested();
                var path = GameDirectory.Contained(staging, name[prefix.Length..]);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                // Counted while copying: an entry that unpacks to more than its zip says is refused, whatever it claimed.
                var limit = Math.Min(entry.Length, limits.MaxEntryBytes);
                long done = 0;
                using (var input = entry.Open())
                using (var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    int n;
                    while ((n = input.Read(buffer)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        done += n;
                        written += n;
                        if (done > limit) throw new InvalidDataException(L.F("core.game.world.entry_too_big", name));
                        if (written > declared || written > limits.MaxTotalBytes) throw new InvalidDataException(L.T("core.game.world.too_big"));
                        output.Write(buffer, 0, n);
                    }
                }
                try { File.SetLastWriteTime(path, entry.LastWriteTime.DateTime); }
                catch (Exception e) when (e is ArgumentOutOfRangeException or IOException) { }
            }

            var folder = baseName;
            for (var i = 2; Directory.Exists(GameDirectory.Contained(saves, folder)) || File.Exists(GameDirectory.Contained(saves, folder))
                || new DirectoryInfo(GameDirectory.Contained(saves, folder)).LinkTarget is not null; i++)
                folder = $"{baseName} ({i})";
            Directory.Move(staging, GameDirectory.Contained(saves, folder));
            return folder;
        }
        catch
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    internal static string ImportPath(string name)
    {
        var normalized = name.Replace('\\', '/');
        if (normalized.Length == 0 || normalized.StartsWith('/') || normalized.Contains(':'))
            throw new InvalidDataException(L.F("core.game.world.unsafepath", name));
        foreach (var part in normalized.TrimEnd('/').Split('/')) CheckWorldComponent(part);
        return normalized;
    }

    internal static void CheckWorldComponent(string name)
    {
        GameDirectory.CheckId(name);
        // 世界可跨平台移动，不能让 Windows 把归档名解释成设备或改写为另一个路径。
        if (name.Any(c => c < 32) || name.IndexOfAny(['"', '<', '>', '|', '?', '*']) >= 0 || ModpackService.IsDeviceName(name))
            throw new InvalidDataException(L.F("core.game.world.unsafepath", name));
    }
}

public sealed record ServerEntry(string Name, string Address);

public sealed record ServerStatus(bool Online, string? Motd, string? VersionName, int? Players, int? MaxPlayers, long? PingMs, string? Error);

/// <summary>servers.dat：游戏的多人服务器列表。保留游戏写入的其他字段。</summary>
/// <summary>从 servers.dat 删掉的一项：原来的位置和原始 NBT（用来撤销）。</summary>
public sealed record RemovedServer(int Index, Dictionary<string, object> Raw)
{
    /// <summary>已经放回去了（同一条撤销两次不会放两份）。</summary>
    public bool Restored { get; set; }
}

public static class ServerList
{
    internal const int MaxStatusPacketBytes = 1024 * 1024;
    private static readonly SemaphoreSlim PingSlots = new(4, 4);
    public static List<ServerEntry> Read(string workDir)
    {
        var path = Path.Combine(workDir, "servers.dat");
        if (!File.Exists(path)) return [];
        try
        {
            var root = Nbt.ReadFile(path);
            if (root.GetValueOrDefault("servers") is not Nbt.NbtList list) return [];
            return list.Items.OfType<Dictionary<string, object>>()
                .Select(s => new ServerEntry(DisplayName(s), Address(s)))
                .Where(s => s.Address.Length > 0).ToList();
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or IndexOutOfRangeException)
        {
            return [];
        }
    }

    /// <summary>游戏允许没有名字的条目，界面上显示成“Minecraft Server”（和游戏自己一样）。</summary>
    private static string DisplayName(Dictionary<string, object> server) => server.GetValueOrDefault("name") as string ?? "Minecraft Server";

    private static string Address(Dictionary<string, object> server) => server.GetValueOrDefault("ip") as string ?? "";

    public static void Add(string workDir, ServerEntry server)
    {
        var (root, list) = Load(workDir);
        list.Items.RemoveAll(i => i is Dictionary<string, object> d && (d.GetValueOrDefault("ip") as string) == server.Address);
        list.Items.Add(new Dictionary<string, object> { ["name"] = server.Name, ["ip"] = server.Address, ["hidden"] = (byte)0 });
        Nbt.WriteFile(Path.Combine(workDir, "servers.dat"), root);
    }

    /// <summary>
    /// 删掉一条服务器，返回删掉的原始条目和位置（撤销时原样放回，图标等字段不丢）；找不到返回 null。
    /// 只删点的那一条：index 是 Read 返回的列表里的位置，那一条名字和地址都对得上就删它；否则删第一条名字和地址都一样的，
    /// 没给名字时删第一条地址一样的。名字按界面上显示的比（没名字的条目显示成“Minecraft Server”）。同地址的其他条目不动。
    /// </summary>
    public static RemovedServer? Remove(string workDir, string address, string? name = null, int? index = null)
    {
        var (root, list) = Load(workDir);
        bool Matches(object item, bool checkName) => item is Dictionary<string, object> d && Address(d) == address
            && (!checkName || DisplayName(d) == name);
        // Read 跳过了不是条目或没有地址的，界面上的位置要换算回文件里的位置。
        var shown = list.Items.Select((item, raw) => (item, raw))
            .Where(x => x.item is Dictionary<string, object> d && Address(d).Length > 0).Select(x => x.raw).ToList();
        var at = index is { } i && i >= 0 && i < shown.Count && Matches(list.Items[shown[i]], name is not null) ? shown[i]
            : list.Items.FindIndex(item => Matches(item, name is not null));
        if (at < 0) return null;
        var removed = new RemovedServer(at, (Dictionary<string, object>)list.Items[at]);
        list.Items.RemoveAt(at);
        Nbt.WriteFile(Path.Combine(workDir, "servers.dat"), root);
        return removed;
    }

    /// <summary>撤销 Remove：把删掉的那一条原样放回原来的位置（列表变短了就放到末尾）。同一条只放一次。</summary>
    public static void Restore(string workDir, RemovedServer removed)
    {
        if (removed.Restored) return;
        var (root, list) = Load(workDir);
        list.Items.Insert(Math.Min(removed.Index, list.Items.Count), removed.Raw);
        Nbt.WriteFile(Path.Combine(workDir, "servers.dat"), root);
        removed.Restored = true;
    }

    private static (Dictionary<string, object> Root, Nbt.NbtList List) Load(string workDir)
    {
        var path = Path.Combine(workDir, "servers.dat");
        var root = File.Exists(path) ? Nbt.ReadFile(path) : [];
        if (root.GetValueOrDefault("servers") is not Nbt.NbtList list)
        {
            list = new Nbt.NbtList(10, []);
            root["servers"] = list;
        }
        return (root, list);
    }

    /// <summary>host 或 host:port 拆开。默认端口 25565，IPv6 要写成 [addr]:port。</summary>
    public static (string Host, int Port) ParseAddress(string address)
    {
        address = address.Trim();
        if (address.StartsWith('['))
        {
            var close = address.IndexOf(']');
            var host = address[1..close];
            return (host, close + 2 < address.Length && int.TryParse(address[(close + 2)..], out var p6) ? p6 : 25565);
        }
        var colon = address.LastIndexOf(':');
        return colon > 0 && address.IndexOf(':') == colon && int.TryParse(address[(colon + 1)..], out var port)
            ? (address[..colon], port)
            : (address, 25565);
    }

    /// <summary>服务器列表 Ping（1.7+ 协议的状态查询）。取得查询名额后才开始网络超时；排队可由 ct 取消。</summary>
    public static async Task<ServerStatus> PingAsync(string address, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var acquired = false;
        try
        {
            // 所有 Core 调用者共享连接/帧预算，排队不会提前消耗正常服务器的响应时间。
            await PingSlots.WaitAsync(ct).ConfigureAwait(false);
            acquired = true;
            linked.CancelAfter(timeout ?? TimeSpan.FromSeconds(5));
            var (host, port) = ParseAddress(address);
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(host, port, linked.Token).ConfigureAwait(false);
            using var stream = tcp.GetStream();

            var handshake = new MemoryStream();
            WriteVarInt(handshake, 0x00);
            WriteVarInt(handshake, 767);
            var hostBytes = Encoding.UTF8.GetBytes(host);
            WriteVarInt(handshake, hostBytes.Length);
            handshake.Write(hostBytes);
            handshake.WriteByte((byte)(port >> 8));
            handshake.WriteByte((byte)port);
            WriteVarInt(handshake, 1);
            await WritePacketAsync(stream, handshake.ToArray(), linked.Token).ConfigureAwait(false);
            await WritePacketAsync(stream, [0x00], linked.Token).ConfigureAwait(false);

            var json = await ReadStatusPacketAsync(stream, linked.Token).ConfigureAwait(false);

            var watch = System.Diagnostics.Stopwatch.StartNew();
            var ping = new byte[9];
            ping[0] = 0x01;
            await WritePacketAsync(stream, ping, linked.Token).ConfigureAwait(false);
            long? ms = null;
            try
            {
                var pong = await ReadPacketAsync(stream, 9, linked.Token).ConfigureAwait(false);
                if (!pong.AsSpan().SequenceEqual(ping)) throw InvalidStatus();
                ms = watch.ElapsedMilliseconds;
            }
            catch (Exception e) when (e is EndOfStreamException or IOException) { /* 有的服务器不回 pong */ }

            return new ServerStatus(true, MotdText(json?["description"]), json?["version"]?["name"]?.GetValue<string>(),
                json?["players"]?["online"]?.GetValue<int>(), json?["players"]?["max"]?.GetValue<int>(), ms, null);
        }
        catch (Exception e) when (e is SocketException or IOException or OperationCanceledException or JsonException or EndOfStreamException or InvalidDataException or InvalidOperationException or FormatException)
        {
            if (ct.IsCancellationRequested) throw;
            return new ServerStatus(false, null, null, null, null, null, e is OperationCanceledException ? L.T("core.mp.server.timeout") : e.Message);
        }
        finally
        {
            if (acquired) PingSlots.Release();
        }
    }

    // 状态包很小；先限制完整帧，再在帧内验证 ID 和字符串长度，绝不按内层声明另行分配。
    internal static async Task<JsonObject> ReadStatusPacketAsync(Stream stream, CancellationToken ct = default)
    {
        var frame = await ReadPacketAsync(stream, MaxStatusPacketBytes, ct).ConfigureAwait(false);
        using var packet = new MemoryStream(frame, writable: false);
        if (await ReadVarIntAsync(packet, ct).ConfigureAwait(false) != 0) throw InvalidStatus();
        var length = await ReadVarIntAsync(packet, ct).ConfigureAwait(false);
        if (length <= 0 || length != packet.Length - packet.Position) throw InvalidStatus();
        return JsonNode.Parse(frame.AsSpan((int)packet.Position, length)) as JsonObject ?? throw InvalidStatus();
    }

    private static async Task<byte[]> ReadPacketAsync(Stream stream, int maxBytes, CancellationToken ct)
    {
        var length = await ReadVarIntAsync(stream, ct).ConfigureAwait(false);
        if (length <= 0 || length > maxBytes) throw InvalidStatus();
        var frame = new byte[length];
        await stream.ReadExactlyAsync(frame, ct).ConfigureAwait(false);
        return frame;
    }

    private static InvalidDataException InvalidStatus() => new(L.T("core.mp.server.invalidstatus"));

    private static string? MotdText(JsonNode? node)
    {
        switch (node)
        {
            case JsonValue v when v.TryGetValue<string>(out var s):
                return s;
            case JsonObject o:
                var builder = new StringBuilder(o["text"]?.GetValue<string>() ?? "");
                foreach (var extra in o["extra"] as JsonArray ?? [])
                    builder.Append(MotdText(extra));
                return builder.ToString();
            default:
                return null;
        }
    }

    private static async Task WritePacketAsync(Stream stream, byte[] body, CancellationToken ct)
    {
        var packet = new MemoryStream();
        WriteVarInt(packet, body.Length);
        packet.Write(body);
        await stream.WriteAsync(packet.ToArray(), ct).ConfigureAwait(false);
    }

    private static void WriteVarInt(Stream s, int value)
    {
        var v = (uint)value;
        while (v >= 0x80)
        {
            s.WriteByte((byte)(v | 0x80));
            v >>= 7;
        }
        s.WriteByte((byte)v);
    }

    private static async Task<int> ReadVarIntAsync(Stream stream, CancellationToken ct)
    {
        var result = 0;
        var buffer = new byte[1];
        for (var shift = 0; shift < 35; shift += 7)
        {
            await stream.ReadExactlyAsync(buffer, ct).ConfigureAwait(false);
            if (shift == 28 && (buffer[0] & 0xf0) != 0) throw InvalidStatus();
            result |= (buffer[0] & 0x7f) << shift;
            if ((buffer[0] & 0x80) == 0) return result;
        }
        throw new InvalidDataException(L.T("core.mp.server.varinttoolong"));
    }
}
