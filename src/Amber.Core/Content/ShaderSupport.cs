namespace Amber.Core.Content;

/// <summary>Whether an instance can actually use shader packs.</summary>
public enum ShaderReadiness
{
    /// <summary>Iris, Oculus or OptiFine is installed (or the instance is an OptiFine version).</summary>
    Ready,
    /// <summary>Fabric, Quilt or newer NeoForge without Iris: Iris can be installed alongside.</summary>
    NeedsIris,
    /// <summary>Forge (up to 1.20.1) or older NeoForge without Oculus: Oculus can be installed alongside.</summary>
    NeedsOculus,
    /// <summary>Vanilla: shaders need a loader instance with Iris, or OptiFine.</summary>
    Vanilla,
    /// <summary>A loader with no shader mod Jay can install (for example Forge 1.21+): only OptiFine would work.</summary>
    Unsupported,
}

/// <summary>
/// Shader packs only do something with a shader mod (old-spec gap N3): Iris on Fabric/Quilt/NeoForge,
/// Oculus on Forge, or OptiFine. This checks an instance and plans the matching mod from Modrinth.
/// </summary>
public static class ShaderSupport
{
    /// <summary>Modrinth slugs.</summary>
    public const string IrisProject = "iris";
    public const string OculusProject = "oculus";

    /// <summary>Mod ids that make shader packs work.</summary>
    private static readonly string[] RendererIds = ["iris", "oculus", "optifine", "optifabric"];

    public static bool HasRenderer(IEnumerable<string?> enabledModIds) =>
        enabledModIds.Any(id => id is not null && RendererIds.Contains(id, StringComparer.OrdinalIgnoreCase));

    public static ShaderReadiness Check(string? loader, string mcVersion, IEnumerable<string?> enabledModIds)
    {
        if (loader == "optifine" || HasRenderer(enabledModIds)) return ShaderReadiness.Ready;
        return loader switch
        {
            null => ShaderReadiness.Vanilla,
            "fabric" or "quilt" => ShaderReadiness.NeedsIris,
            "neoforge" => IsAtLeast121(mcVersion) ? ShaderReadiness.NeedsIris : ShaderReadiness.NeedsOculus,
            "forge" => IsAtLeast121(mcVersion) ? ShaderReadiness.Unsupported : ShaderReadiness.NeedsOculus,
            _ => ShaderReadiness.Unsupported,
        };
    }

    /// <summary>The Modrinth project to install for this readiness, or null when there is nothing to install.</summary>
    public static string? ProjectFor(ShaderReadiness readiness) => readiness switch
    {
        ShaderReadiness.NeedsIris => IrisProject,
        ShaderReadiness.NeedsOculus => OculusProject,
        _ => null,
    };

    /// <summary>
    /// Picks the newest compatible Iris or Oculus for the instance and resolves its required dependencies
    /// (Iris needs Sodium). Returns null when nothing fits this game version and loader.
    /// </summary>
    public static async Task<InstallPlan?> PlanAsync(GameInstance instance, ShaderReadiness readiness, IContentProvider modrinth, ContentInstaller installer,
        ISet<string>? installedProjects = null, CancellationToken ct = default)
    {
        if (ProjectFor(readiness) is not { } project) return null;
        var files = await modrinth.GetFilesAsync(project, instance.McVersion, instance.Loader, ct);
        if (files.PickPreferred() is not { } latest) return null;
        return await installer.ResolveAsync(latest, instance, installedProjects, ct);
    }

    /// <summary>
    /// Minecraft 1.21 or later. Year-based versions (26.1) and snapshots that don't parse as 1.x count as new.
    /// </summary>
    internal static bool IsAtLeast121(string mcVersion)
    {
        var parts = mcVersion.Split('.', '-', ' ');
        if (parts.Length < 2 || !int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor)) return true;
        return major > 1 || minor >= 21;
    }
}
