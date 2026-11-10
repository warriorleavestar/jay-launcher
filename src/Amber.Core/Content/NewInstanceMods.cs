using Amber.Core.Install;
using Amber.Core.Localization;

namespace Amber.Core.Content;

/// <summary>
/// "Add mods while you're at it" in the new-instance panel (official design §6.11, journey J3): the mods picked
/// before the instance exists are resolved against it once it is installed and go in as part of the same task.
/// </summary>
public static class NewInstanceMods
{
    /// <summary>The loader name content sites use, or null when the choice can't take mods (vanilla, OptiFine).</summary>
    public static string? LoaderId(LoaderKind? kind) => kind switch
    {
        LoaderKind.Fabric => "fabric",
        LoaderKind.Quilt => "quilt",
        LoaderKind.Forge => "forge",
        LoaderKind.NeoForge => "neoforge",
        _ => null,
    };

    /// <summary>
    /// One plan for every pick: each project's preferred file for the instance's game version and loader, plus its
    /// required dependencies, merged so shared dependencies come once. Picks with no matching file go to Problems.
    /// </summary>
    public static async Task<InstallPlan> PlanAsync(IContentProvider provider, ContentInstaller installer,
        IEnumerable<ContentProject> picks, GameInstance instance, CancellationToken ct = default)
    {
        var plans = new List<InstallPlan>();
        var problems = new List<string>();
        foreach (var pick in picks)
        {
            ct.ThrowIfCancellationRequested();
            var files = await provider.GetFilesAsync(pick.Id, instance.McVersion, instance.Loader, ct);
            if (files.PickPreferred() is not { } file)
            {
                problems.Add(L.F("core.content.new_instance_mods.no_file", pick.Title, instance.McVersion));
                continue;
            }
            plans.Add(await installer.ResolveAsync(file, instance, null, ct));
        }
        var merged = InstallQueue.Merge(plans);
        return problems.Count == 0 ? merged : merged with { Problems = [.. merged.Problems, .. problems] };
    }
}
