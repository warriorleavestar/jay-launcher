using Amber.App.Services;
using Amber.Core.Content;
using Amber.Core.Localization;

namespace Amber.App.ViewModels;

/// <summary>
/// Shader packs need Iris, Oculus or OptiFine (old-spec gap N3). Shared by the Discover page (install a shader)
/// and the instance's shader tab: what to tell the player, and installing the shader mod.
/// </summary>
internal static class ShaderRenderer
{
    public static ShaderReadiness ReadinessOf(GameInstance instance)
    {
        IEnumerable<string?> ids;
        try { ids = new ModManager(instance.ModsDir).List().Where(m => m.Enabled).Select(m => m.ModId).ToList(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { ids = []; }
        return ShaderSupport.Check(instance.Loader, instance.McVersion, ids);
    }

    /// <summary>Iris or Oculus, by readiness.</summary>
    public static string NameFor(ShaderReadiness state) => state == ShaderReadiness.NeedsOculus ? "Oculus" : "Iris";

    /// <summary>The warning under the shader list or above the install button; null when shaders will work.</summary>
    public static string? NoteFor(ShaderReadiness state, string instanceId) => state switch
    {
        ShaderReadiness.Vanilla => L.F("content.shader.vanilla", instanceId),
        ShaderReadiness.NeedsIris or ShaderReadiness.NeedsOculus => L.F("content.shader.needs_renderer", instanceId, NameFor(state)),
        ShaderReadiness.Unsupported => L.F("content.shader.unsupported", instanceId),
        _ => null,
    };

    /// <summary>Installs Iris or Oculus (with Sodium and other required dependencies) into the instance's mods folder.</summary>
    /// <returns>False when no build fits this game version and loader.</returns>
    public static async Task<bool> InstallAsync(LauncherService service, GameInstance instance, ShaderReadiness state, CancellationToken ct)
    {
        // A dependency that is already there (same file name, for example Sodium) is verified and skipped by the downloader.
        var plan = await ShaderSupport.PlanAsync(instance, state, service.Modrinth, service.ContentInstaller, null, ct);
        if (plan is null) return false;
        Directory.CreateDirectory(instance.ModsDir);
        await service.ContentInstaller.InstallAsync(plan, instance.ModsDir, null, ct);
        service.NotifyContentChanged();
        return true;
    }
}
