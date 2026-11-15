using Amber.Core.Diagnostics;
using Amber.Core.Localization;

namespace Amber.Core.Bake;

/// <summary>
/// What the universal loader reported for a Forge 1.12.2 start that ended early, as causes for the instance's "last
/// session" panel, each with the fix that goes with it (no dialogs). Errors come first.
/// </summary>
public static class LegacyLaunchProblems
{
    public static IReadOnlyList<(CrashFinding Finding, IReadOnlyList<CrashFix> Fixes)> Causes(IEnumerable<BakeRuntimeProblem> problems)
    {
        var causes = new List<(CrashFinding, IReadOnlyList<CrashFix>, bool)>();
        foreach (var p in problems.DistinctBy(p => p.Code))
        {
            var evidence = p.Detail.Length > 0 ? p.Detail : null;
            switch (p.Code)
            {
                case "legacy_java_unsupported":
                    causes.Add((Finding("java", L.T("instance.lastrun.legacy.java"), p, CrashSeverity.Likely, evidence), [new CrashFix(CrashFixKind.ChooseJava)], true));
                    break;
                case "legacy_launchwrapper_missing":
                    causes.Add((Finding("launchwrapper", L.T("instance.lastrun.legacy.launchwrapper"), p, CrashSeverity.Likely, null), [new CrashFix(CrashFixKind.RepairFiles)], true));
                    break;
                case "legacy_launch_failed":
                    causes.Add((Finding("failed", L.T("instance.lastrun.legacy.failed"), p, CrashSeverity.Likely, evidence),
                        [new CrashFix(CrashFixKind.LaunchWithoutLoader), new CrashFix(CrashFixKind.ViewLog)], true));
                    break;
                case "legacy_cache_disabled":
                    causes.Add((Finding("cache", L.T("instance.lastrun.legacy.cache"), p, CrashSeverity.Possible, evidence), [new CrashFix(CrashFixKind.TransformerCacheOff)], false));
                    break;
                case "legacy_quickplay_failed":
                    causes.Add((Finding("quickplay", L.T("instance.lastrun.legacy.quickplay"), p, CrashSeverity.Possible, evidence), [], false));
                    break;
            }
        }
        return [.. causes.OrderByDescending(c => c.Item3).Select(c => (c.Item1, c.Item2))];
    }

    private static CrashFinding Finding(string key, string title, BakeRuntimeProblem problem, CrashSeverity severity, string? evidence) =>
        new(title, problem.Describe(), severity, evidence) { Key = "legacy." + key };
}
