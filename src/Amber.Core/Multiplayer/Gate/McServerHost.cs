using Amber.Core.Localization;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Amber.Core.Game;
using Amber.Core.Install;
using Amber.Core.Java;
using Amber.Core.Net;

namespace Amber.Core.Multiplayer.Gate;

/// <summary>Jay 服务器版的 MC 服务端设置（server.json）。</summary>
public sealed class McServerSettings
{
    public string Version { get; set; } = "";
    public string JavaPath { get; set; } = "";
    public int MemoryMb { get; set; } = 2048;
    public int Port { get; set; } = 25565;
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(McServerSettings))]
internal sealed partial class McServerJsonContext : JsonSerializerContext;

/// <summary>
/// Jay 服务器版管理的原版 MC 服务端：下载服务端 jar（按官方清单核对 SHA-1）、准备 Java、写 server.properties、启动和正常关闭。
/// 目录：&lt;root&gt;/mc（服务端和存档）、&lt;root&gt;/runtime（自动下载的 Java）、&lt;root&gt;/server.json。
/// server.properties 固定成只监听 127.0.0.1：玩家只能经过网关进来。
/// </summary>
public sealed class McServerHost
{
    private readonly string _root;

    public McServerHost(string root) => _root = root;

    public string McDirectory => Path.Combine(_root, "mc");
    private string SettingsPath => Path.Combine(_root, "server.json");
    public bool IsSetUp => File.Exists(SettingsPath) && File.Exists(Path.Combine(McDirectory, "server.jar"));

    public McServerSettings LoadSettings() =>
        JsonSerializer.Deserialize(File.ReadAllText(SettingsPath), McServerJsonContext.Default.McServerSettings) ?? new McServerSettings();

    public void SaveSettings(McServerSettings settings) =>
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, McServerJsonContext.Default.McServerSettings));

    /// <summary>下载（或换成）指定版本的服务端，准备好 Java。versionId 为 latest 时用最新正式版。</summary>
    public async Task<McServerSettings> SetupAsync(Downloader downloader, string versionId, int memoryMb, Action<string> status, CancellationToken ct = default)
    {
        Directory.CreateDirectory(McDirectory);
        status(L.T("core.gate.mc.reading_manifest"));
        var manifest = await new VanillaInstaller(downloader, new GameDirectory(Path.Combine(_root, "launcher-cache"))).GetManifestAsync(ct);
        if (versionId == "latest") versionId = manifest.Latest.GetValueOrDefault("release") ?? throw new InvalidDataException(L.T("core.gate.mc.no_latest"));
        var entry = manifest.Versions.FirstOrDefault(v => v.Id == versionId) ?? throw new ArgumentException(L.F("core.gate.mc.no_version", versionId));
        // The id comes from the downloaded manifest: like the client installer, accept only a plain file name inside the cache.
        var cache = Path.Combine(_root, "launcher-cache");
        var versionPath = GameDirectory.Contained(cache, GameDirectory.CheckId(versionId) + ".json");
        await downloader.DownloadOneAsync(new DownloadItem(entry.Url, versionPath, entry.Sha1, Within: cache), ct);
        var version = JsonSerializer.Deserialize(await File.ReadAllTextAsync(versionPath, ct), Json.Options.Info<VersionJson>())
                      ?? throw new InvalidDataException(L.T("core.gate.mc.bad_version"));
        var server = version.Downloads?.GetValueOrDefault("server") is { Url: { } url } s ? s : throw new InvalidDataException(L.F("core.gate.mc.no_server", versionId));
        status(L.F("core.gate.mc.downloading_server", versionId));
        await downloader.DownloadOneAsync(new DownloadItem(url, Path.Combine(McDirectory, "server.jar"), server.Sha1, server.Size, Within: McDirectory), ct);

        var major = version.JavaVersion?.MajorVersion ?? 8;
        var java = JavaFinder.Pick(JavaFinder.FindAll(), major)?.JavaPath;
        if (java is null)
        {
            status(L.F("core.gate.mc.downloading_java", major));
            java = await new JavaRuntimeInstaller(downloader, _root).InstallAsync(version.JavaVersion?.Component, major, null, ct);
        }
        var settings = new McServerSettings { Version = versionId, JavaPath = java, MemoryMb = memoryMb };
        SaveSettings(settings);
        WriteProperties(settings.Port);
        return settings;
    }

    public bool EulaAccepted =>
        File.Exists(Path.Combine(McDirectory, "eula.txt")) && File.ReadAllLines(Path.Combine(McDirectory, "eula.txt")).Any(l => l.Trim() == "eula=true");

    /// <summary>只在管理员明确同意后调用（https://aka.ms/MinecraftEULA）。</summary>
    public void AcceptEula() =>
        File.WriteAllText(Path.Combine(McDirectory, "eula.txt"), $"# Accepted by the server admin in Jay Server: https://aka.ms/MinecraftEULA ({DateTimeOffset.UtcNow:u})\neula=true\n");

    /// <summary>
    /// 改 server.properties 里和安全有关的几项，其他（motd、难度……）不动。
    /// 默认离线账号：online-mode=false，由网关核对“设备 + 用户名”。
    /// </summary>
    public void WriteProperties(int port, bool? onlineMode = null)
    {
        var path = Path.Combine(McDirectory, "server.properties");
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : [];
        var entries = ParseProperties(lines);
        var forced = new Dictionary<string, string>
        {
            ["server-ip"] = "127.0.0.1",
            ["server-port"] = port.ToString(),
            ["enable-query"] = "false",
            ["enable-rcon"] = "false",
        };
        if (onlineMode is { } online)
        {
            forced["online-mode"] = online ? "true" : "false";
            forced["enforce-secure-profile"] = online ? "true" : "false";
        }
        else if (!entries.Any(e => e.Key == "online-mode"))
        {
            forced["online-mode"] = "false";
            forced["enforce-secure-profile"] = "false";
        }
        // Java's Properties keeps the last entry for a key, in any spelling (key=v, key = v, key:v, key v, escaped
        // characters, continued over several lines): drop every entry for a forced key, whole, and write each forced
        // value once at the end, on a line of its own.
        var kept = new List<string>();
        foreach (var entry in entries)
            if (entry.Key is null || !forced.ContainsKey(entry.Key)) kept.AddRange(lines.Skip(entry.First).Take(entry.Count));
        // A last line that still ends in a continuation would swallow the next one: an empty line ends it.
        if (kept.Count > 0 && EndsWithContinuation(kept[^1])) kept.Add("");
        kept.AddRange(forced.Select(f => $"{f.Key}={f.Value}"));
        // Read back the way Java will: the server starts only if every forced value is the one in effect.
        var effective = new Dictionary<string, string>();
        foreach (var entry in ParseProperties(kept)) if (entry.Key is not null) effective[entry.Key] = entry.Value!;
        if (forced.Any(f => !effective.TryGetValue(f.Key, out var v) || v != f.Value))
            throw new InvalidOperationException(L.T("core.gate.mc.properties_unsafe"));
        File.WriteAllLines(path, kept);
    }

    /// <summary>One entry of a .properties file: the physical lines it spans and its decoded key and value (null for comments and blank lines).</summary>
    internal readonly record struct PropertyEntry(int First, int Count, string? Key, string? Value);

    /// <summary>
    /// Splits a .properties file into entries as java.util.Properties.load does: comment and blank lines stand alone; any
    /// other line continues onto the next while it ends in an odd number of backslashes (the next line's leading
    /// whitespace dropped, a comment marker there is plain text); keys and values are unescaped.
    /// </summary>
    internal static List<PropertyEntry> ParseProperties(IReadOnlyList<string> lines)
    {
        var entries = new List<PropertyEntry>();
        for (var i = 0; i < lines.Count;)
        {
            var first = i;
            var text = lines[i].TrimStart(' ', '\t', '\f');
            if (text.Length == 0 || text[0] is '#' or '!')
            {
                entries.Add(new PropertyEntry(first, 1, null, null));
                i++;
                continue;
            }
            var logical = new StringBuilder();
            while (true)
            {
                var part = i == first ? text : lines[i].TrimStart(' ', '\t', '\f');
                i++;
                var continues = EndsWithContinuation(part);
                logical.Append(continues ? part[..^1] : part);
                if (!continues || i >= lines.Count) break;
            }
            var (key, value) = SplitProperty(logical.ToString());
            entries.Add(new PropertyEntry(first, i - first, key, value));
        }
        return entries;
    }

    private static bool EndsWithContinuation(string line)
    {
        var count = 0;
        for (var i = line.Length - 1; i >= 0 && line[i] == '\\'; i--) count++;
        return count % 2 == 1;
    }

    /// <summary>Key and value of one logical line: the key ends at the first unescaped '=', ':' or whitespace.</summary>
    private static (string Key, string Value) SplitProperty(string line)
    {
        var keyEnd = 0;
        var valueStart = line.Length;
        var hasSeparator = false;
        var escaped = false;
        for (; keyEnd < line.Length; keyEnd++)
        {
            var c = line[keyEnd];
            if (!escaped && c is '=' or ':') { valueStart = keyEnd + 1; hasSeparator = true; break; }
            if (!escaped && c is ' ' or '\t' or '\f') { valueStart = keyEnd + 1; break; }
            escaped = c == '\\' && !escaped;
        }
        for (; valueStart < line.Length; valueStart++)
        {
            var c = line[valueStart];
            if (c is ' ' or '\t' or '\f') continue;
            if (!hasSeparator && c is '=' or ':') { hasSeparator = true; continue; }
            break;
        }
        return (Unescape(line[..keyEnd]), Unescape(line[valueStart..]));
    }

    private static string Unescape(string text)
    {
        var result = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c != '\\' || i + 1 >= text.Length) { if (c != '\\') result.Append(c); continue; }
            c = text[++i];
            if (c == 'u' && i + 4 < text.Length && ushort.TryParse(text.AsSpan(i + 1, 4), System.Globalization.NumberStyles.HexNumber, null, out var code))
            {
                result.Append((char)code);
                i += 4;
            }
            else result.Append(c switch { 't' => '\t', 'r' => '\r', 'n' => '\n', 'f' => '\f', _ => c });
        }
        return result.ToString();
    }

    /// <summary>The key of a single server.properties line as Java reads it; null for blank lines and comments.</summary>
    internal static string? PropertyKey(string line) => ParseProperties([line]) is [{ Key: var key }] ? key : null;

    /// <summary>启动服务端。输出逐行交给 output；返回的对象用来发控制台命令和关闭。</summary>
    public RunningServer Start(Action<string> output)
    {
        var settings = LoadSettings();
        // 每次启动前再确认一遍只听本机（防止有人手动改了 server.properties）。
        WriteProperties(settings.Port);
        var info = new ProcessStartInfo(settings.JavaPath)
        {
            WorkingDirectory = McDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        info.ArgumentList.Add($"-Xms{Math.Min(settings.MemoryMb, 1024)}M");
        info.ArgumentList.Add($"-Xmx{settings.MemoryMb}M");
        info.ArgumentList.Add("-jar");
        info.ArgumentList.Add("server.jar");
        info.ArgumentList.Add("nogui");
        var process = Process.Start(info) ?? throw new InvalidOperationException(L.T("core.gate.mc.java_failed"));
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) output(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) output(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return new RunningServer(process);
    }

    public sealed class RunningServer(Process process) : IDisposable
    {
        public Process Process => process;
        public bool HasExited => process.HasExited;

        public void Command(string line)
        {
            if (process.HasExited) return;
            process.StandardInput.WriteLine(line);
            process.StandardInput.Flush();
        }

        /// <summary>正常关闭（存盘），超时才强制结束。</summary>
        public async Task StopAsync(TimeSpan timeout)
        {
            if (process.HasExited) return;
            Command("stop");
            using var cts = new CancellationTokenSource(timeout);
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
            }
        }

        public void Dispose() => process.Dispose();
    }
}
