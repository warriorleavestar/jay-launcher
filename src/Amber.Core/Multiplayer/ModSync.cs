namespace Amber.Core.Multiplayer;

/// <summary>A mod that both sides have, in different versions.</summary>
public sealed record ModVersionPair(ModManifestEntry Mine, ModManifestEntry Host);

/// <summary>How an instance's mods differ from the host's.</summary>
public sealed record ModDiff(IReadOnlyList<ModManifestEntry> Missing, IReadOnlyList<ModVersionPair> Different, IReadOnlyList<ModManifestEntry> Extra, int Same)
{
    /// <summary>Nothing missing and nothing in a different version. Extra mods on my side don't count (they are usually client-only).</summary>
    public bool Matches => Missing.Count == 0 && Different.Count == 0;

    /// <summary>Exactly the same files on both sides.</summary>
    public bool Identical => Matches && Extra.Count == 0;

    /// <summary>Extra mods that are not known to be client-only: these may stop me from joining.</summary>
    public IEnumerable<ModManifestEntry> RiskyExtra => Extra.Where(m => !ModSync.IsClientOnly(m));
}

/// <summary>Where a file for the sync comes from.</summary>
public enum ModSyncSource
{
    /// <summary>Modrinth or CurseForge know this exact file (by SHA-1): download it from there.</summary>
    Public,
    /// <summary>Not on a public site (a private or custom build): ask the host to send it over the room's encrypted file transfer.</summary>
    Host,
}

public sealed record ModSyncStep(ModManifestEntry File, ModSyncSource Source, ModManifestEntry? Replaces = null);

/// <summary>
/// What a sync will do. <see cref="IntoNewInstance"/> (the default) copies my instance first, so my own instance is never
/// touched; otherwise <see cref="Replaced"/> go to Recently deleted, from where they can be restored.
/// </summary>
public sealed record ModSyncPlan(IReadOnlyList<ModSyncStep> Steps, IReadOnlyList<ModManifestEntry> Replaced, IReadOnlyList<ModManifestEntry> Kept, bool IntoNewInstance)
{
    public int FromPublic => Steps.Count(s => s.Source == ModSyncSource.Public);
    public int FromHost => Steps.Count(s => s.Source == ModSyncSource.Host);
    public long Bytes => Steps.Sum(s => s.File.Size);
}

/// <summary>Compares mod lists and plans a sync (official §6.14 “check the mods”).</summary>
public static class ModSync
{
    /// <summary>Mods that only change the client (maps, HUD, menus, performance, shaders). Having them when the host doesn't is fine.</summary>
    private static readonly HashSet<string> ClientOnlyIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "journeymap", "xaerominimap", "xaeroworldmap", "voxelmap", "modmenu", "betterf3", "controlling", "mousetweaks",
        "sodium", "iris", "oculus", "embeddium", "rubidium", "lambdynlights", "dynamiclights", "entityculling", "zoomify", "okzoomer",
        "logical_zoom", "betterpingdisplay", "appleskin", "jade", "wthit", "hwyla", "inventoryprofilesnext", "itemscroller",
        "litematica", "minihud", "tweakeroo", "malilib", "continuity", "indium", "lithium", "ferritecore", "modernfix",
        "immediatelyfast", "notenoughanimations", "skinlayers3d", "3dskinlayers", "betterthirdperson", "freecam", "replaymod",
        "fpsreducer", "dynamic_fps", "dynamicfps", "cullleaves", "borderlessmining", "fullscreenwindowed", "chat_heads", "chatheads",
    };

    public static bool IsClientOnly(ModManifestEntry mod) => mod.ModId is { } id && ClientOnlyIds.Contains(id);

    /// <summary>File name without extension and without the version tail: “sodium-fabric-0.5.8+mc1.20.1.jar” → “sodium-fabric”.</summary>
    public static string StemOf(string fileName)
    {
        var name = fileName.ToLowerInvariant();
        if (name.EndsWith(".disabled", StringComparison.Ordinal)) name = name[..^".disabled".Length];
        if (name.EndsWith(".jar", StringComparison.Ordinal)) name = name[..^4];
        for (var i = 1; i < name.Length - 1; i++)
            if (name[i] is '-' or '_' or '+' && char.IsAsciiDigit(name[i + 1])) return name[..i];
        return name;
    }

    public static ModDiff Compare(ModManifest mine, ModManifest host)
    {
        // Same file (by hash) under any name is the same mod.
        var mineLeft = mine.Mods.ToList();
        var hostLeft = new List<ModManifestEntry>();
        var same = 0;
        foreach (var h in host.Mods)
        {
            var at = mineLeft.FindIndex(m => string.Equals(m.Sha1, h.Sha1, StringComparison.OrdinalIgnoreCase));
            if (at >= 0)
            {
                mineLeft.RemoveAt(at);
                same++;
            }
            else hostLeft.Add(h);
        }

        var mineByIdentity = mineLeft.GroupBy(m => m.Identity).ToDictionary(g => g.Key, g => new Queue<ModManifestEntry>(g));
        var missing = new List<ModManifestEntry>();
        var different = new List<ModVersionPair>();
        foreach (var h in hostLeft)
        {
            if (mineByIdentity.TryGetValue(h.Identity, out var queue) && queue.TryDequeue(out var m)) different.Add(new(m, h));
            else missing.Add(h);
        }
        var extra = mineByIdentity.Values.SelectMany(q => q).ToList();
        return new ModDiff(Sorted(missing), [.. different.OrderBy(p => p.Host.DisplayName, StringComparer.CurrentCultureIgnoreCase)], Sorted(extra), same);
    }

    /// <param name="publicSha1s">The files Modrinth / CurseForge know (looked up by SHA-1 when the user starts the sync).</param>
    public static ModSyncPlan Plan(ModDiff diff, IReadOnlySet<string> publicSha1s, bool intoNewInstance = true)
    {
        ModSyncSource SourceOf(ModManifestEntry e) => publicSha1s.Contains(e.Sha1) ? ModSyncSource.Public : ModSyncSource.Host;
        var steps = diff.Missing.Select(m => new ModSyncStep(m, SourceOf(m)))
            .Concat(diff.Different.Select(p => new ModSyncStep(p.Host, SourceOf(p.Host), p.Mine)))
            .ToList();
        // Extra mods stay unless they are risky and we write into a new instance (a copy made for this room).
        var replaced = diff.Different.Select(p => p.Mine).ToList();
        var kept = diff.Extra.ToList();
        if (intoNewInstance)
        {
            replaced.AddRange(diff.Extra.Where(m => !IsClientOnly(m)));
            kept = [.. diff.Extra.Where(IsClientOnly)];
        }
        return new ModSyncPlan(steps, replaced, kept, intoNewInstance);
    }

    private static List<ModManifestEntry> Sorted(IEnumerable<ModManifestEntry> mods) =>
        [.. mods.OrderBy(m => m.DisplayName, StringComparer.CurrentCultureIgnoreCase)];
}
