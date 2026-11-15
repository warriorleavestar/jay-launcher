using System.Text.Json.Nodes;
using Amber.Core.Bake;
using Amber.Core.Diagnostics;

namespace Amber.Core.Tests;

/// <summary>Forge 1.12.2 through the universal loader: what it reported for a start that ended early, as last-session causes with fixes.</summary>
public sealed class LegacyLaunchProblemsTests
{
    private static BakeRuntimeProblem P(string code, string severity, string? detail = null)
    {
        var o = new JsonObject { ["type"] = "problem", ["code"] = code, ["severity"] = severity };
        if (detail is not null) o["rootCause"] = detail;
        return BakeRuntimeProblem.FromJson(o)!;
    }

    private static readonly string Src = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    [Fact]
    public void ErrorsComeFirst_EachWithItsFix()
    {
        var causes = LegacyLaunchProblems.Causes([
            P("legacy_cache_disabled", "warning"),
            P("legacy_quickplay_failed", "warning"),
            P("legacy_launch_failed", "error", "java.lang.NoClassDefFoundError: x"),
            P("legacy_java_unsupported", "error"),
            P("mixin_incompatible", "error"),
        ]);
        Assert.Equal(["legacy.failed", "legacy.java", "legacy.cache", "legacy.quickplay"], causes.Select(c => c.Finding.Key));
        Assert.Equal([CrashFixKind.LaunchWithoutLoader, CrashFixKind.ViewLog], causes[0].Fixes.Select(f => f.Kind));
        Assert.Equal([CrashFixKind.ChooseJava], causes[1].Fixes.Select(f => f.Kind));
        Assert.Equal([CrashFixKind.TransformerCacheOff], causes[2].Fixes.Select(f => f.Kind));
        Assert.Empty(causes[3].Fixes);
        Assert.Equal(CrashSeverity.Possible, causes[2].Finding.Severity);
    }

    [Fact]
    public void TheSameProblemTwiceIsOneCause()
    {
        var causes = LegacyLaunchProblems.Causes([P("legacy_launchwrapper_missing", "error"), P("legacy_launchwrapper_missing", "error")]);
        var only = Assert.Single(causes);
        Assert.Equal([CrashFixKind.RepairFiles], only.Fixes.Select(f => f.Kind));
    }

    [Fact]
    public void TitlesAndFixLabelsExistInEveryLanguage()
    {
        var keys = new[] { "java", "launchwrapper", "failed", "cache", "quickplay" }.Select(k => "instance.lastrun.legacy." + k)
            .Concat([new CrashFix(CrashFixKind.TransformerCacheOff).LabelKey, new CrashFix(CrashFixKind.LaunchWithoutLoader).LabelKey,
                "core.bake.runtime.legacy_quickplay", "instance.lastrun.cache_off_done"]);
        foreach (var file in Directory.GetFiles(Path.Combine(Src, "Amber.Core", "Localization"), "*.json"))
        {
            var table = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
            foreach (var key in keys) Assert.True(table.ContainsKey(key), $"{Path.GetFileName(file)}: {key}");
            if (Path.GetFileName(file).StartsWith("zh") || Path.GetFileName(file).StartsWith("lzh"))
                foreach (var key in keys) Assert.DoesNotContain("烘焙", table[key]!.GetValue<string>());
        }
    }
}
