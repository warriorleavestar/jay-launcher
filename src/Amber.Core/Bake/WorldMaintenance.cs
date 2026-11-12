using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Amber.Core.Bake;

/// <summary>
/// Prepare one singleplayer world without playing it (bake interface 1.3.3, "maintenance mode"): the game client
/// upgrades it if it was saved by an older version, opens it, generates the chunks around its spawn and quits.
/// </summary>
public sealed record WorldMaintenance(string World, bool Upgrade, int PregenRadius)
{
    /// <summary>The loader's limit for <see cref="PregenRadius"/> (chunks).</summary>
    public const int MaxRadius = 512;

    /// <summary>The spawn area the loader always generates: 8 chunks around the world spawn.</summary>
    public const int SpawnRadius = 8;

    /// <summary>The radii offered in the pre-generate drawer (chunks).</summary>
    public static readonly IReadOnlyList<int> Radii = [64, 128, 256];

    public static WorldMaintenance UpgradeOnly(string world) => new(world, true, 0);

    public static WorldMaintenance Pregen(string world, int radius) => new(world, true, Math.Clamp(radius, 0, MaxRadius));

    /// <summary>The request field for bake-instance.</summary>
    public JsonObject ToRequest() => new() { ["world"] = World, ["upgrade"] = Upgrade, ["pregenRadius"] = PregenRadius };

    /// <summary>Chunks in the square the loader walks (the spawn area included).</summary>
    public static long Chunks(int radius)
    {
        var side = 2L * Math.Max(radius, SpawnRadius) + 1;
        return side * side;
    }

    /// <summary>Blocks from the spawn to the edge of the square.</summary>
    public static int Blocks(int radius) => radius * 16;

    // A rough range for the time it takes: an integrated server generates roughly 40 to 150 new chunks a second,
    // depending on the computer, the mods and the terrain. Said as a range, never as a promise.
    private const double SlowChunksPerSecond = 40, FastChunksPerSecond = 150;

    // About 8 KB per generated chunk on disk (region files, compressed); heavily modded terrain takes more.
    private const long BytesPerChunk = 8 * 1024;

    public static (TimeSpan Low, TimeSpan High) Duration(int radius, long alreadyDone = 0)
    {
        var left = Math.Max(0, Chunks(radius) - alreadyDone);
        return (TimeSpan.FromSeconds(left / FastChunksPerSecond), TimeSpan.FromSeconds(left / SlowChunksPerSecond));
    }

    public static long DiskBytes(int radius, long alreadyDone = 0) => Math.Max(0, Chunks(radius) - alreadyDone) * BytesPerChunk;
}

/// <summary>The loader's echo in the launch block (<c>launch.maintenance</c>), with where to find progress and how to cancel.</summary>
public sealed record MaintenanceLaunch(string World, string Upgrade, int SpawnRadius, int PregenRadius, string? ProgressFile, string? CancelFile)
{
    public static MaintenanceLaunch? FromLaunch(JsonNode? node)
    {
        if (node is not JsonObject o || Str(o, "world") is not { Length: > 0 } world) return null;
        return new MaintenanceLaunch(world, Str(o, "upgrade") ?? "", Int(o, "spawnRadius") ?? WorldMaintenance.SpawnRadius, Int(o, "pregenRadius") ?? 0,
            Str(o, "progressFile"), Str(o, "cancelFile"));
    }

    public JsonObject ToJson()
    {
        var o = new JsonObject { ["world"] = World, ["upgrade"] = Upgrade, ["spawnRadius"] = SpawnRadius, ["pregenRadius"] = PregenRadius };
        if (ProgressFile is not null) o["progressFile"] = ProgressFile;
        if (CancelFile is not null) o["cancelFile"] = CancelFile;
        return o;
    }

    /// <summary>Asks the running game to stop at the next batch (it saves its place and quits normally).</summary>
    public bool RequestCancel()
    {
        if (CancelFile is not { Length: > 0 } file) return false;
        try
        {
            if (Path.GetDirectoryName(file) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
            File.WriteAllText(file, "cancel");
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    internal static string? Str(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    internal static int? Int(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;
}

/// <summary>One progress line of a maintenance run (<c>@amber-ul/progress {…}</c> on the game's stdout).</summary>
public sealed record MaintenanceProgress(string Step, long Done, long Total, string? World, string? Message)
{
    public const string Prefix = "@amber-ul/progress ";
    public const string Schema = "amber-ul/maintenance-progress@1";

    public static MaintenanceProgress? Parse(string line)
    {
        var at = line.IndexOf(Prefix, StringComparison.Ordinal);
        if (at < 0) return null;
        try
        {
            if (JsonNode.Parse(line[(at + Prefix.Length)..]) is not JsonObject o) return null;
            if (MaintenanceLaunch.Str(o, "schema") != Schema || MaintenanceLaunch.Str(o, "step") is not { Length: > 0 } step) return null;
            static long Long(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<long>(out var n) ? n : 0;
            return new MaintenanceProgress(step, Long(o, "done"), Long(o, "total"), MaintenanceLaunch.Str(o, "world"), MaintenanceLaunch.Str(o, "message"));
        }
        catch (JsonException) { return null; }
    }

    /// <summary>The run's last word: done, cancelled or failed (quit follows).</summary>
    public bool IsEnd => Step is "done" or "cancelled" or "failed";

    /// <summary>How far the whole run is, 0–1, or null while it cannot be told (the upgrade's share is not known in advance).</summary>
    public double? Fraction => Step switch
    {
        "pregen" or "spawn" when Total > 0 => Math.Clamp((double)Done / Total, 0, 1),
        "done" => 1,
        _ => null,
    };
}

/// <summary>The loader's resumable place in the spiral (<c>&lt;world&gt;/amber-ul-pregen.json</c>).</summary>
public sealed record PregenState(int Radius, long Done, long Total, bool Complete)
{
    public const string FileName = "amber-ul-pregen.json";

    public static PregenState? Read(string worldDir)
    {
        var path = Path.Combine(worldDir, FileName);
        try
        {
            if (!File.Exists(path) || JsonNode.Parse(File.ReadAllText(path)) is not JsonObject o) return null;
            if (MaintenanceLaunch.Str(o, "schema") != "amber-ul/pregen@1") return null;
            static long Long(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<long>(out var n) ? n : 0;
            return new PregenState((int)Long(o, "radius"), Long(o, "done"), Long(o, "total"), o["complete"] is JsonValue c && c.TryGetValue<bool>(out var b) && b);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>Chunks already generated that a run with this radius does not need to do again (the same spiral continues).</summary>
    public long DoneFor(int radius) => Math.Min(Done, WorldMaintenance.Chunks(radius));

    public string Percent => Total > 0 ? ((double)Done / Total).ToString("P0", CultureInfo.InvariantCulture) : "0%";
}
