using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Amber.Core.Localization;

namespace Amber.Core.Bake;

/// <summary>
/// A problem the loader reports while the game runs, for example a Mixin it had to switch off.
/// Interface 1.3 writes them as JSON objects (schema <c>amber-ul/runtime-problem@1</c>), one per line, to
/// <c>launch.problemsFile</c> and on stdout behind the prefix <c>@amber-ul/problem </c> (the game's logger may put
/// a timestamp in front). Older loaders only print a Chinese text line, which is read as a fallback.
/// </summary>
public sealed partial record BakeRuntimeProblem(string Code, string Severity, string Mod, string Mixin, string Target, string Reason)
{
    public const string Prefix = "@amber-ul/problem ";

    /// <summary>The loader's own message (English), kept for the log.</summary>
    public string Message { get; init; } = "";
    /// <summary>Code-specific detail: the option for a semantic risk, the bake misses for a rebake, the version detail for the bridge.</summary>
    public string Detail { get; init; } = "";
    /// <summary>For dependency codes: the problem object as the bake's problem lines have it, so it reads the same.</summary>
    internal JsonObject? Raw { get; init; }

    /// <summary>The loader switched a Mixin off (or removed a risky injector): the mod runs, maybe without some features.</summary>
    public bool DisablesFeatures => Code is "mixin_incompatible" or "mixin_semantic_risk";
    public bool IsError => Severity == "error";
    /// <summary>The launch had to bake something itself; run <c>warm</c> again afterwards.</summary>
    public bool AsksForWarm => Code == "launch_rebaked";
    /// <summary>Worth telling the player about (not the informational ones).</summary>
    public bool IsNotable => Severity is "error" or "warning";

    /// <summary>The same problem from stdout and from the problems file counts once.</summary>
    public string Key => string.Join('\u001f', Code, Mod, Mixin, Target, Detail);

    /// <summary>
    /// A problem from one line of game output, or null. With <paramref name="legacy"/> (a loader without a problems
    /// file) the old text line is read too; otherwise only the JSON form counts.
    /// </summary>
    public static BakeRuntimeProblem? Parse(string line, bool legacy = false)
    {
        var at = line.IndexOf(Prefix, StringComparison.Ordinal);
        if (at >= 0)
        {
            try { return JsonNode.Parse(line[(at + Prefix.Length)..]) is JsonObject o ? FromJson(o) : null; }
            catch (JsonException) { return null; }
        }
        if (!legacy || !line.Contains("[amber-ul]", StringComparison.Ordinal)) return null;
        var m = LegacyPattern().Match(line);
        return m.Success ? new BakeRuntimeProblem(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value, m.Groups[5].Value, m.Groups[6].Value) : null;
    }

    /// <summary>One object of the problems file; null for the <c>launch</c> header line and anything that is not a problem.</summary>
    public static BakeRuntimeProblem? FromJson(JsonObject o)
    {
        if (Str(o, "type") != "problem" || Str(o, "code") is not { Length: > 0 } code) return null;
        var mixin = Str(o, "mixin") ?? Str(o, "mixins") ?? "";
        var detail = code switch
        {
            "mixin_semantic_risk" => Str(o, "handler") ?? Str(o, "option") ?? "",
            "launch_rebaked" => Str(o, "bakeMisses") ?? Num(o, "bakeMisses") ?? "",
            "fabric_api_bridge_version" => Str(o, "detail") ?? "",
            "fabric_api_bridged" => Str(o, "platform") ?? "",
            _ => "",
        };
        return new BakeRuntimeProblem(code, Str(o, "severity") ?? "warning", Str(o, "modId") ?? Str(o, "file") ?? "", mixin,
            Str(o, "target") ?? "", Str(o, "category") ?? Str(o, "action") ?? "")
        {
            Message = Str(o, "message") ?? "",
            Detail = detail,
            Raw = o,
        };
    }

    /// <summary>Every problem in a problems file (written line by line while the game runs, so it survives a crash).</summary>
    public static IReadOnlyList<BakeRuntimeProblem> ReadFile(string path)
    {
        var list = new List<BakeRuntimeProblem>();
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (reader.ReadLine() is { } line)
            {
                if (line.Length == 0 || line[0] != '{') continue;
                try { if (JsonNode.Parse(line) is JsonObject o && FromJson(o) is { } problem) list.Add(problem); }
                catch (JsonException) { }   // a line cut off by a crash
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return list;
    }

    /// <summary>Localized one-liner for the log and notices.</summary>
    public string Describe() => Code switch
    {
        "mixin_incompatible" => L.F("core.bake.runtime.mixin", Mod, Mixin, Target),
        "mixin_semantic_risk" => L.F("core.bake.runtime.semantic_risk", Mod, Detail.Length > 0 ? Detail : Mixin),
        "fabric_api_bridged" => L.F("core.bake.runtime.bridged", Mod),
        "fabric_api_bridge_version" => L.F("core.bake.runtime.bridge_version", Mod, Detail),
        "fabric_init_not_run" => L.F("core.bake.runtime.init_not_run", Mod),
        "launch_rebaked" => L.F("core.bake.runtime.rebaked", Detail.Length > 0 ? Detail : "?"),
        _ when Raw is not null => BakeService.DescribeProblem(Raw),
        _ => Mod.Length > 0 ? $"{Mod}: {(Message.Length > 0 ? Message : Code)}" : (Message.Length > 0 ? Message : Code),
    };

    private static string? Str(JsonObject o, string key) => o[key] switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonArray a => string.Join(", ", a.Select(n => n?.ToString()).Where(t => !string.IsNullOrEmpty(t))),
        _ => null,
    };

    private static string? Num(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<long>(out var n) ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;

    [GeneratedRegex(@"\[amber-ul\] \S+ ([a-z_]+) \((error|warning)\) \S+?=(\S+) mixin=(\S+) \S+?=(\S+) \S+?=(\S+)")]
    private static partial Regex LegacyPattern();
}
