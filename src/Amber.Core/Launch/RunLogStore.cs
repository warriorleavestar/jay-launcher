using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Amber.Core.Launch;

/// <summary>How a run ended, kept next to its log.</summary>
public enum RunOutcome { Running, Exited, Crashed, Stopped, Failed }

/// <summary>One saved run: when, how long, how it ended, and where its log is.</summary>
public sealed record RunRecord(string LogPath, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, RunOutcome Outcome, int? ExitCode, string? Summary, string? CrashReport)
{
    public TimeSpan? Duration => EndedAt is { } end ? end - StartedAt : null;
    public bool LogExists => File.Exists(LogPath);
}

/// <summary>A saved log line.</summary>
public sealed record StoredLogLine(DateTimeOffset Time, GameLogLevel Level, string Message, bool FromJay);

/// <summary>
/// One log per run, kept in the instance's <c>logs/jay</c> folder (official design §6.9) so the Logs tab can show
/// earlier runs after Jay restarts. Each run is <c>{time}.log</c> (one line per entry) plus <c>{time}.json</c> (how it
/// ended). With no-trace mode on, chat lines are not written. Only the newest <see cref="Kept"/> runs are kept.
/// </summary>
public sealed class RunLogStore : IDisposable
{
    public const int Kept = 20;
    private const string StampFormat = "yyyyMMdd-HHmmss";
    private static readonly Regex Chat = new(ChatLogFilter.ChatRegex, RegexOptions.CultureInvariant);

    private readonly StreamWriter? _writer;
    private readonly string _metaPath;
    private readonly bool _noTrace;
    private readonly object _gate = new();
    private RunRecord _record;

    private RunLogStore(string logPath, string metaPath, DateTimeOffset startedAt, bool noTrace)
    {
        _metaPath = metaPath;
        _noTrace = noTrace;
        _record = new RunRecord(logPath, startedAt, null, RunOutcome.Running, null, null, null);
        try
        {
            _writer = new StreamWriter(new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = false };
            WriteMeta();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A read-only game folder only loses the saved copy; the live log still works.
            _writer = null;
        }
    }

    public RunRecord Record => _record;

    public static string Dir(string workDir) => Path.Combine(workDir, "logs", "jay");

    /// <summary>Starts the saved log for a run. Old runs beyond <see cref="Kept"/> are removed.</summary>
    public static RunLogStore Begin(string workDir, DateTimeOffset startedAt, bool noTrace)
    {
        var dir = Dir(workDir);
        try { Directory.CreateDirectory(dir); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        var stamp = startedAt.LocalDateTime.ToString(StampFormat, CultureInfo.InvariantCulture);
        var name = stamp;
        for (var i = 2; File.Exists(Path.Combine(dir, name + ".log")); i++) name = $"{stamp}-{i}";
        Prune(dir, Kept - 1);
        return new RunLogStore(Path.Combine(dir, name + ".log"), Path.Combine(dir, name + ".json"), startedAt, noTrace);
    }

    public void Add(DateTimeOffset time, GameLogLevel level, string message, bool fromJay = false)
    {
        if (_writer is null || (_noTrace && !fromJay && Chat.IsMatch(message))) return;
        lock (_gate)
        {
            try
            {
                _writer.Write(Format(new StoredLogLine(time, level, message, fromJay)));
                _writer.Write('\n');
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException) { }
        }
    }

    /// <summary>Records how the run ended and closes the log.</summary>
    public void Finish(DateTimeOffset endedAt, RunOutcome outcome, int? exitCode = null, string? summary = null, string? crashReport = null)
    {
        lock (_gate)
        {
            _record = _record with { EndedAt = endedAt, Outcome = outcome, ExitCode = exitCode, Summary = summary, CrashReport = crashReport };
            try { _writer?.Flush(); } catch (Exception e) when (e is IOException or ObjectDisposedException) { }
            WriteMeta();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            try { _writer?.Dispose(); } catch (IOException) { }
        }
    }

    /// <summary>Saved runs, newest first. A run without its meta file still shows (outcome unknown → Exited).</summary>
    public static IReadOnlyList<RunRecord> List(string workDir)
    {
        var dir = Dir(workDir);
        if (!Directory.Exists(dir)) return [];
        var result = new List<RunRecord>();
        foreach (var meta in Directory.EnumerateFiles(dir, "*.json"))
        {
            try
            {
                var record = JsonSerializer.Deserialize(File.ReadAllText(meta), RunLogJson.Default.RunRecord);
                if (record is null) continue;
                // The folder may have moved with the instance: point at the log next to the meta file.
                result.Add(record with { LogPath = Path.ChangeExtension(meta, ".log") });
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        }
        return [.. result.OrderByDescending(r => r.StartedAt)];
    }

    /// <summary>Reads a saved log; an unreadable line becomes a plain Info line.</summary>
    public static IReadOnlyList<StoredLogLine> Read(string logPath)
    {
        var lines = new List<StoredLogLine>();
        using var reader = new StreamReader(new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        while (reader.ReadLine() is { } line) lines.Add(Parse(line));
        return lines;
    }

    /// <summary>"2026-11-09T20:14:05.123+13:00 ERROR message" or "… JAY message". Newlines inside a message are escaped.</summary>
    internal static string Format(StoredLogLine line) =>
        line.Time.ToString("O", CultureInfo.InvariantCulture) + " " + (line.FromJay ? "JAY" : line.Level.ToString().ToUpperInvariant()) + " "
        + line.Message.Replace("\\", "\\\\").Replace("\r", "").Replace("\n", "\\n");

    internal static StoredLogLine Parse(string line)
    {
        var first = line.IndexOf(' ');
        var second = first < 0 ? -1 : line.IndexOf(' ', first + 1);
        if (second > 0 && DateTimeOffset.TryParse(line[..first], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time))
        {
            var tag = line[(first + 1)..second];
            var message = Unescape(line[(second + 1)..]);
            if (tag == "JAY") return new StoredLogLine(time, GameLogLevel.Info, message, true);
            if (Enum.TryParse<GameLogLevel>(tag, true, out var level)) return new StoredLogLine(time, level, message, false);
        }
        return new StoredLogLine(DateTimeOffset.MinValue, GameLogLevel.Info, line, false);
    }

    private static string Unescape(string text)
    {
        if (!text.Contains('\\')) return text;
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length)
            {
                var next = text[++i];
                sb.Append(next == 'n' ? '\n' : next);
            }
            else sb.Append(text[i]);
        }
        return sb.ToString();
    }

    private void WriteMeta()
    {
        try
        {
            File.WriteAllText(_metaPath, JsonSerializer.Serialize(_record, RunLogJson.Default.RunRecord), new UTF8Encoding(false));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static void Prune(string dir, int keep)
    {
        try
        {
            var stale = Directory.EnumerateFiles(dir, "*.log").Order(StringComparer.Ordinal).ToList();
            foreach (var log in stale.Take(Math.Max(0, stale.Count - keep)))
            {
                File.Delete(log);
                File.Delete(Path.ChangeExtension(log, ".json"));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(RunRecord))]
[System.Text.Json.Serialization.JsonSourceGenerationOptions(UseStringEnumConverter = true)]
internal sealed partial class RunLogJson : System.Text.Json.Serialization.JsonSerializerContext;
