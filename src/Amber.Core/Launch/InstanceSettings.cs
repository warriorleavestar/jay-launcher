using Amber.Core;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Amber.Core.Auth;
using Amber.Core.Game;

namespace Amber.Core.Launch;

/// <summary>Why <see cref="InstanceSettings.Load"/> left out commands, the Java path and arguments.</summary>
public enum InstanceSettingsDrop
{
    None,

    /// <summary>Jay did not save these values (something else wrote the file).</summary>
    NotSavedByJay,

    /// <summary>Jay could not read its record just then, so it could not check them; trying again may load them.</summary>
    RecordUnavailable,
}

/// <summary>全局和单个版本共用的启动设置。版本设置里为 null 的项沿用全局值。</summary>
public sealed class InstanceSettings
{
    public bool? Isolate { get; set; }
    public string? JavaPath { get; set; }
    public bool? AutoMemory { get; set; }
    public int? MaxMemoryMb { get; set; }
    public string? JvmArgs { get; set; }
    public string? GameArgs { get; set; }
    public int? WindowWidth { get; set; }
    public int? WindowHeight { get; set; }
    public bool? Fullscreen { get; set; }
    public string? PreLaunchCommand { get; set; }
    public string? PostExitCommand { get; set; }
    public string? JoinServer { get; set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = AmberJsonContext.Resolver,
    };

    public const string FileName = "amber-instance.json";

    /// <summary>
    /// True when the file held a command, Java path or argument that Jay did not write (the sandboxed game can write
    /// the instance folder), or that Jay could not check. Those values were dropped; the rest of the settings load as
    /// usual. <see cref="DropReason"/> says which. See <see cref="InstanceSettingsTrust"/>.
    /// </summary>
    [JsonIgnore]
    public bool DroppedUnknownValues => DropReason != InstanceSettingsDrop.None;

    /// <summary>Why <see cref="Load"/> dropped values, if it did.</summary>
    [JsonIgnore]
    public InstanceSettingsDrop DropReason { get; private set; }

    /// <summary>
    /// True when the instance folder holds settings that a save replaced before Jay could confirm them (see
    /// <see cref="UnconfirmedCopies"/>). They are never used on their own; only the player can take them back.
    /// </summary>
    [JsonIgnore]
    public bool HasUnconfirmedValues { get; private set; }

    private const int MaxUnconfirmedCopies = 5;

    /// <summary>
    /// The files whose values <see cref="Load"/> dropped, by path, with the fingerprint dropped. A save that would
    /// replace those values keeps a copy first, even when the record lists them by then (it could not be read at load
    /// time): the player never saw them, so replacing them was not their choice.
    /// </summary>
    private static readonly ConcurrentDictionary<string, string> DroppedOnLoad = new(StringComparer.Ordinal);

    public static InstanceSettings Load(GameDirectory dir, string id)
    {
        var path = Path.Combine(dir.VersionDir(id), FileName);
        if (!File.Exists(path))
        {
            // Nothing to check, but the folder is still recorded as seen now, before a game from it can write here.
            try { InstanceSettingsTrust.EnsureAdopted(dir, InstanceSettingsTrust.QuickWait); } catch (IOException) { }
            return new InstanceSettings { HasUnconfirmedValues = UnconfirmedCopies(dir, id).Count > 0 };
        }
        var settings = ReadFile(path) ?? new InstanceSettings();
        settings.HasUnconfirmedValues = UnconfirmedCopies(dir, id).Count > 0;
        var verdict = InstanceSettingsTrust.Check(dir, settings);
        if (verdict != InstanceSettingsTrust.Verdict.Trusted)
        {
            DroppedOnLoad[Path.GetFullPath(path)] = InstanceSettingsTrust.Fingerprint(settings)!;
            settings.PreLaunchCommand = null;
            settings.PostExitCommand = null;
            settings.JavaPath = null;
            settings.JvmArgs = null;
            settings.GameArgs = null;
            settings.DropReason = verdict == InstanceSettingsTrust.Verdict.RecordUnavailable
                ? InstanceSettingsDrop.RecordUnavailable
                : InstanceSettingsDrop.NotSavedByJay;
        }
        return settings;
    }

    /// <summary>
    /// The copies of settings a save replaced before Jay could confirm them, oldest first. They hold what the file held
    /// then, as it was; read one with <see cref="ReadUnconfirmed"/>. Nothing in them is used unless the player saves
    /// it again. They are kept in Jay's own data folder (see <see cref="UnconfirmedFolder"/>), which the game never sees.
    /// </summary>
    public static IReadOnlyList<string> UnconfirmedCopies(GameDirectory dir, string id)
    {
        List<string> copies = [];
        var folder = UnconfirmedFolder(dir, id);
        for (var n = 1; n <= MaxUnconfirmedCopies; n++)
            if (UnconfirmedPath(folder, n) is var copy && File.Exists(copy)) copies.Add(copy);
        return copies;
    }

    /// <summary>The settings in an unconfirmed copy, unchecked (for showing them to the player); null when unreadable.</summary>
    public static InstanceSettings? ReadUnconfirmed(string path) => ReadFile(path);

    /// <summary>
    /// Where the unconfirmed copies of one instance's settings are kept: in Jay's data folder, next to its record of
    /// the settings it saved, named after the instance folder. Never in the instance folder: the game can write there,
    /// so it could put a link where a copy is about to be written.
    /// </summary>
    public static string UnconfirmedFolder(GameDirectory dir, string id) =>
        Path.Combine(InstanceSettingsTrust.DataFolder, "instance-settings-kept", InstanceSettingsTrust.FolderKey(dir.VersionDir(id)));

    private static string UnconfirmedPath(string folder, int n) => Path.Combine(folder, n + ".json");

    /// <summary>The file as it is, with no check of who wrote it; null when it cannot be read.</summary>
    internal static InstanceSettings? ReadFile(string path)
    {
        try
        {
            return Parse(File.ReadAllText(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The settings in <paramref name="json"/>; null when it is not a settings file.</summary>
    internal static InstanceSettings? Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, Options.Info<InstanceSettings>());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes the settings. Throws <see cref="IOException"/> when Jay cannot record them as its own (see
    /// <see cref="InstanceSettingsTrust"/>); the file is then left as it was.
    /// <para>
    /// When the file holds commands, a Java path or arguments that this save would replace and that the player could not
    /// have seen (the record does not list them, or <see cref="Load"/> dropped them), the file is first copied aside
    /// (see <see cref="UnconfirmedCopies"/>), so they are not lost. They stay unconfirmed: nothing trusts them again.
    /// </para>
    /// </summary>
    public void Save(GameDirectory dir, string id)
    {
        var folder = dir.VersionDir(id);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, FileName);
        var json = JsonSerializer.Serialize(this, Options.Info<InstanceSettings>());
        var print = InstanceSettingsTrust.Fingerprint(this);
        InstanceSettingsTrust.Trust(dir, this, isTrusted =>
        {
            KeepUnconfirmed(UnconfirmedFolder(dir, id), path, print, isTrusted);
            WriteReplacing(folder, path, json);
        });
        DroppedOnLoad.TryRemove(Path.GetFullPath(path), out _);
        DropReason = InstanceSettingsDrop.None;
        HasUnconfirmedValues = UnconfirmedCopies(dir, id).Count > 0;
    }

    /// <summary>
    /// Writes the settings file as a new file next to it, then moves that over it. The game can write the instance
    /// folder, so the file there may be a link (or a hard link) to some other file; writing into it would change that
    /// file. A move replaces only the name in this folder. The new file is created only if nothing has that name yet
    /// (not even a link), so it cannot be redirected either.
    /// </summary>
    private static void WriteReplacing(string folder, string path, string json)
    {
        var temp = Path.Combine(folder, "." + FileName + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                stream.Write(Encoding.UTF8.GetBytes(json));
            InstanceSettingsTrust.MoveOver(temp, path);
        }
        finally
        {
            try { File.Delete(temp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Copies the file aside, into <paramref name="keptFolder"/>, before a save replaces values the player never saw
    /// (see <see cref="Save"/>).
    /// </summary>
    private static void KeepUnconfirmed(string keptFolder, string path, string? newPrint, Func<string, bool> isTrusted)
    {
        // Jay never writes a link here: one in its place was put there by something else and holds nothing of the
        // player's. Reading through it would only copy some other file.
        if (new FileInfo(path).LinkTarget is not null) return;
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return;
        }
        // Any other read error stops the save: the file is left as it was.
        if (Parse(text) is not { } existing || InstanceSettingsTrust.Fingerprint(existing) is not { } oldPrint) return;
        if (oldPrint == newPrint) return;
        var droppedHere = DroppedOnLoad.TryGetValue(Path.GetFullPath(path), out var dropped) && dropped == oldPrint;
        if (isTrusted(oldPrint) && !droppedHere) return;
        Directory.CreateDirectory(keptFolder);
        string? free = null;
        for (var n = 1; n <= MaxUnconfirmedCopies; n++)
        {
            var copy = UnconfirmedPath(keptFolder, n);
            if (!File.Exists(copy))
            {
                free ??= copy;
                continue;
            }
            if (File.ReadAllText(copy) == text) return; // kept already
        }
        // All copies in use: the newest takes the place of the last, so the oldest (likely the player's own) stay.
        File.WriteAllText(free ?? UnconfirmedPath(keptFolder, MaxUnconfirmedCopies), text);
    }

    /// <summary>
    /// 按这份（已合并全局的）设置生成启动参数：内存（含自动内存）、工作目录（版本隔离）、JVM 和游戏参数、窗口。
    /// 启动器的正式启动、Connector 预处理、导出启动脚本和命令行都用它，各处的设置不会对不上。
    /// </summary>
    public LaunchOptions ToLaunchOptions(GameDirectory dir, string id, string javaPath, GameSession session, IReadOnlyList<string> sessionJvmArgs)
    {
        var workDir = Isolate == true ? dir.VersionDir(id) : dir.Root;
        var memory = AutoMemory == true
            ? MemoryPlanner.AutoMemoryMb(new Amber.Core.Content.ModManager(Path.Combine(workDir, "mods")).CountEnabled(), MemoryPlanner.FreePhysicalMb())
            : MaxMemoryMb ?? 4096;
        return new LaunchOptions
        {
            VersionId = id,
            JavaPath = javaPath,
            Session = session,
            ExtraJvmArgs = [.. sessionJvmArgs, .. ArgumentSplitter.Split(JvmArgs)],
            ExtraGameArgs = ArgumentSplitter.Split(GameArgs),
            MaxMemoryMb = memory,
            WorkingDirectory = Isolate == true ? workDir : null,
            WindowWidth = WindowWidth,
            WindowHeight = WindowHeight,
            Fullscreen = Fullscreen == true,
            LauncherVersion = UpdateChecker.CurrentVersion,
        };
    }

    /// <summary>this 是版本设置，fallback 是全局设置。</summary>
    public InstanceSettings Over(InstanceSettings fallback) => new()
    {
        // What Load left out stays known: the merged values below fall back to the global ones for those fields.
        DropReason = DropReason,
        HasUnconfirmedValues = HasUnconfirmedValues,
        Isolate = Isolate ?? fallback.Isolate,
        JavaPath = Empty(JavaPath) ?? fallback.JavaPath,
        AutoMemory = AutoMemory ?? fallback.AutoMemory,
        MaxMemoryMb = MaxMemoryMb ?? fallback.MaxMemoryMb,
        JvmArgs = Empty(JvmArgs) ?? fallback.JvmArgs,
        GameArgs = Empty(GameArgs) ?? fallback.GameArgs,
        WindowWidth = WindowWidth ?? fallback.WindowWidth,
        WindowHeight = WindowHeight ?? fallback.WindowHeight,
        Fullscreen = Fullscreen ?? fallback.Fullscreen,
        PreLaunchCommand = Empty(PreLaunchCommand) ?? fallback.PreLaunchCommand,
        PostExitCommand = Empty(PostExitCommand) ?? fallback.PostExitCommand,
        JoinServer = Empty(JoinServer) ?? fallback.JoinServer,
    };

    private static string? Empty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}

public static class MemoryPlanner
{
    public static long FreePhysicalMb()
    {
        if (OperatingSystem.IsWindows())
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (GlobalMemoryStatusEx(ref status)) return (long)(status.AvailPhys / 1024 / 1024);
        }
        if (OperatingSystem.IsLinux())
        {
            try
            {
                if (ProcMemInfoMb(File.ReadAllText("/proc/meminfo"), "MemAvailable") is { } available) return available;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        if (OperatingSystem.IsMacOS() && MacAvailableMemoryMb() is { } macAvailable) return macAvailable;
        var info = GC.GetGCMemoryInfo();
        return (info.TotalAvailableMemoryBytes - info.MemoryLoadBytes) / 1024 / 1024;
    }

    /// <summary>
    /// The next maximum after an out-of-memory crash: 2 GB more, in 512 MB steps, but never more than three quarters
    /// of the computer's memory. Returns <paramref name="currentMb"/> when it is already at that cap.
    /// </summary>
    public static int RaisedMemoryMb(int currentMb, long totalMb)
    {
        var cap = (int)Math.Max(2048, totalMb * 3 / 4 / 512 * 512);
        var next = (currentMb + 2048) / 512 * 512;
        return Math.Max(currentMb, Math.Min(next, cap));
    }

    public static long TotalPhysicalMb()
    {
        if (OperatingSystem.IsLinux())
        {
            try
            {
                if (ProcMemInfoMb(File.ReadAllText("/proc/meminfo"), "MemTotal") is { } total) return total;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        if (OperatingSystem.IsMacOS() && TrySysctl("hw.memsize", out var bytes)) return (long)(bytes / 1024 / 1024);
        return GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024 / 1024;
    }

    internal static long? ProcMemInfoMb(string text, string key)
    {
        foreach (var line in text.Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon < 0 || !line[..colon].Equals(key, StringComparison.Ordinal)) continue;
            var value = line[(colon + 1)..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
            return long.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var kb)
                ? kb / 1024
                : null;
        }
        return null;
    }

    private static long? MacAvailableMemoryMb()
    {
        if (!TrySysctl("hw.pagesize", out var pageSize) ||
            !TrySysctl("vm.page_free_count", out var free) ||
            !TrySysctl("vm.page_inactive_count", out var inactive)) return null;
        TrySysctl("vm.page_purgeable_count", out var purgeable);
        return checked((long)((free + inactive + purgeable) * pageSize / 1024 / 1024));
    }

    private static bool TrySysctl(string name, out ulong value)
    {
        value = 0;
        if (!OperatingSystem.IsMacOS()) return false;
        nuint length = sizeof(ulong);
        return SysctlByName(name, out value, ref length, IntPtr.Zero, 0) == 0 && length > 0;
    }

    /// <summary>
    /// 自动内存：原版 2.5 GB、加模组后按数量加。留出系统余量，不超过可用内存。
    /// 算出来小于 1 GB 时仍给 1 GB，游戏再少跑不起来。
    /// </summary>
    public static int AutoMemoryMb(int modCount, long freeMb)
    {
        var wanted = modCount switch
        {
            0 => 2560,
            < 30 => 3072,
            < 100 => 4096,
            < 200 => 6144,
            _ => 8192,
        };
        var usable = freeMb - 512;
        return (int)Math.Max(1024, Math.Min(wanted, usable));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("libc", EntryPoint = "sysctlbyname")]
    private static extern int SysctlByName(string name, out ulong oldValue, ref nuint oldLength, IntPtr newValue, nuint newLength);
}

public static class ArgumentSplitter
{
    /// <summary>按空格拆参数，双引号里的空格保留（JVM 参数输入框用）。</summary>
    public static List<string> Split(string? text)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return result;
        var current = new StringBuilder();
        var quoted = false;
        var has = false;
        foreach (var c in text)
        {
            if (c == '"')
            {
                quoted = !quoted;
                has = true;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (has) result.Add(current.ToString());
                current.Clear();
                has = false;
            }
            else
            {
                current.Append(c);
                has = true;
            }
        }
        if (has) result.Add(current.ToString());
        return result;
    }
}
