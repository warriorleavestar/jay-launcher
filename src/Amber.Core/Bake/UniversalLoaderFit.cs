using Amber.Core.Content;
using Amber.Core.Localization;

namespace Amber.Core.Bake;

/// <summary>
/// What the instance's "Universal loader" card and the new-instance tick box say: whether the loader runs this
/// instance (and in plain words why not), and how many of its mods run only through it. Pure, so it is unit-tested.
/// </summary>
public static class UniversalLoaderFit
{
    /// <summary>
    /// Null when the universal loader runs an instance with this loader and game version; otherwise a localized reason.
    /// <paramref name="caps"/> is what the installed loader runs, else the pinned release's; null when neither is available.
    /// </summary>
    public static string? Unsupported(CrossLoader.Capabilities? caps, string? instanceLoader, string? mcVersion)
    {
        if (instanceLoader is null) return L.T("core.bake.fit.no_loader");
        if (CrossLoader.PlatformOf(instanceLoader) is not { } platform) return L.F("core.bake.unsupported_loader", instanceLoader);
        if (caps is null) return L.T("core.bake.fit.unavailable");
        var versions = caps.Platforms.GetValueOrDefault(platform) ?? [];
        if (string.IsNullOrEmpty(mcVersion) || !versions.Contains(mcVersion))
            return L.F("core.bake.unsupported_version", mcVersion ?? "", BakePlatform.DisplayName(platform),
                versions.Count == 0 ? "-" : string.Join(L.T("core.mp.format.listseparator"), versions));
        return null;
    }

    /// <summary>The enabled mods that run here only through the universal loader (a Fabric mod on NeoForge, say).</summary>
    public static int CrossMods(IEnumerable<LocalMod> mods, string? instanceLoader, IReadOnlyList<string> extras) =>
        extras.Count == 0 ? 0 : mods.Count(m => m.Enabled && m.Requirements is { Loaders.Count: > 0 } r && CrossLoader.IsCross(instanceLoader, r.Loaders, extras));

    /// <summary>"Vanilla base 1.21.1 · NeoForge 1.21.1 · Forge 1.20.1": the platforms and game versions a loader runs, for the settings card.</summary>
    public static string Supported(CrossLoader.Capabilities caps) =>
        string.Join(" · ", caps.Platforms.Where(p => p.Value.Count > 0)
            .OrderBy(p => p.Key == BakePlatform.Vanilla ? 0 : p.Key == BakePlatform.NeoForge ? 1 : 2)
            .Select(p => BakePlatform.DisplayName(p.Key) + " " + string.Join(L.T("core.mp.format.listseparator"), p.Value)));

    /// <summary>The bytes under a folder (0 when it is missing); files that vanish or cannot be read meanwhile are skipped.</summary>
    public static long FolderSize(string folder)
    {
        if (!Directory.Exists(folder)) return 0;
        long total = 0;
        foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            try { total += file.Length; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return total;
    }
}
