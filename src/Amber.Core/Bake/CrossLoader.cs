using System.Text.Json.Nodes;
using Amber.Core.Content;

namespace Amber.Core.Bake;

/// <summary>
/// Mods from other ecosystems that the universal loader runs on an instance ("mixed packs"): for example Fabric and
/// Quilt mods on NeoForge 1.21.1, Forge 1.21.1 mods on NeoForge, NeoForge 1.20.1 mods on Forge 1.20.1.
/// <para>
/// What the loader accepts comes from the loader itself: the package's <c>accepts</c> table (interface 1.3.1) when
/// it has one, otherwise it is inferred from the platforms and the bridge jars the package ships (interface 1.3):
/// a platform runs Fabric/Quilt mods only with its Fabric bridge, Forge mods on NeoForge only with the Forge
/// compatibility layer. Ecosystems are Modrinth loader names (<c>fabric</c>, <c>quilt</c>, <c>forge</c>, <c>neoforge</c>).
/// Everything here is pure, so it is unit-tested.
/// </para>
/// </summary>
public static class CrossLoader
{
    public static readonly string[] Ecosystems = ["fabric", "quilt", "forge", "neoforge"];

    /// <summary>What a loader package runs: its platforms (game versions), bridge jars and optional accepts table.</summary>
    public sealed record Capabilities(IReadOnlyDictionary<string, IReadOnlyList<string>> Platforms, IReadOnlyCollection<string> Bridges, AcceptTable? Accepts = null)
    {
        public static Capabilities Of(BakePackage package) => new(package.Platforms, package.Bridges, package.Accepts);

        /// <summary>
        /// What the pinned release (jay-loader v0.3.0) runs, from its manifest. Used only to tell the player what turning
        /// on baking would allow before the loader is downloaded; an installed package always speaks for itself.
        /// </summary>
        public static Capabilities PinnedRelease { get; } = new(
            new Dictionary<string, IReadOnlyList<string>>
            {
                [BakePlatform.Vanilla] = ["1.21.1"],
                [BakePlatform.NeoForge] = ["1.21.1"],
                [BakePlatform.Forge] = ["1.20.1"],
            },
            ["fmlbridge.jar", "fabricbridge.jar", "fmlbridge-forge.jar", "fabricbridge-forge.jar", "forgecompat.jar"]);
    }

    /// <summary>
    /// The <c>accepts</c> table: platform → game version → ecosystems. The loader's form (interface revision 1.3.1) is an
    /// array of <c>{"platform","mc","available","accepts":[…],"missingBridges":[…]}</c>; an entry with
    /// <c>available: false</c> (a core-only package) accepts nothing, and its missing bridges are kept for the prompt.
    /// Two older map forms are also read: <c>{"neoforge":{"1.21.1":[…]}}</c> and, for every game version of a
    /// platform, <c>{"neoforge":[…]}</c> (stored under <c>*</c>).
    /// </summary>
    public sealed record AcceptTable(IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>> Platforms)
    {
        public const string AnyVersion = "*";

        /// <summary>Per platform and game version, the bridge jars the package lacks to run it (from <c>missingBridges</c>).</summary>
        public IReadOnlyDictionary<(string Platform, string Mc), IReadOnlyList<string>> MissingBridges { get; init; } =
            new Dictionary<(string, string), IReadOnlyList<string>>();

        public static AcceptTable? Parse(JsonNode? node)
        {
            if (node is JsonArray entries) return ParseEntries(entries);
            if (node is not JsonObject obj) return null;
            var table = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>>(StringComparer.Ordinal);
            foreach (var (platform, value) in obj)
            {
                var versions = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
                if (value is JsonArray flat) versions[AnyVersion] = Names(flat);
                else if (value is JsonObject byVersion)
                    foreach (var (mc, list) in byVersion)
                        if (list is JsonArray a) versions[mc] = Names(a);
                table[platform] = versions;
            }
            return new AcceptTable(table);
        }

        private static AcceptTable ParseEntries(JsonArray entries)
        {
            var table = new Dictionary<string, Dictionary<string, IReadOnlyList<string>>>(StringComparer.Ordinal);
            var missing = new Dictionary<(string, string), IReadOnlyList<string>>();
            foreach (var entry in entries.OfType<JsonObject>())
            {
                if (Text(entry["platform"]) is not { Length: > 0 } platform) continue;
                var mc = Text(entry["mc"]) is { Length: > 0 } m ? m : AnyVersion;
                // Missing "available" means available; only an explicit false turns the entry off.
                var available = entry["available"] is not JsonValue a || !a.TryGetValue<bool>(out var on) || on;
                if (!table.TryGetValue(platform, out var versions)) table[platform] = versions = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
                versions[mc] = available && entry["accepts"] is JsonArray list ? Names(list) : [];
                if (entry["missingBridges"] is JsonArray bridges && bridges.Count > 0)
                    missing[(platform, mc)] = bridges.Select(Text).OfType<string>().Where(b => b.Length > 0).ToList();
            }
            return new AcceptTable(table.ToDictionary(p => p.Key, p => (IReadOnlyDictionary<string, IReadOnlyList<string>>)p.Value, StringComparer.Ordinal))
            {
                MissingBridges = missing,
            };
        }

        private static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : null;

        /// <summary>The bridges the package lacks for this platform and game version (empty when none or unknown).</summary>
        public IReadOnlyList<string> Missing(string platform, string mc) =>
            MissingBridges.TryGetValue((platform, mc), out var exact) ? exact : MissingBridges.TryGetValue((platform, AnyVersion), out var any) ? any : [];

        private static List<string> Names(JsonArray array) =>
            array.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim().ToLowerInvariant() : "").Where(s => s.Length > 0).Distinct().ToList();

        public IReadOnlyList<string>? For(string platform, string mc) =>
            Platforms.TryGetValue(platform, out var versions)
                ? versions.TryGetValue(mc, out var exact) ? exact : versions.TryGetValue(AnyVersion, out var any) ? any : []
                : null;
    }

    /// <summary>The ecosystems an instance loads by itself. A Quilt instance also runs Fabric mods.</summary>
    public static IReadOnlyList<string> Native(string? instanceLoader) => instanceLoader switch
    {
        null => [],
        "quilt" => ["quilt", "fabric"],
        var loader => [loader],
    };

    /// <summary>
    /// The ecosystems this instance runs only through the universal loader, in the order Jay prefers them. Empty when
    /// the loader does not run this instance (game version or platform not supported, no loader, plain vanilla).
    /// </summary>
    public static IReadOnlyList<string> Extras(Capabilities? caps, string? instanceLoader, string? mcVersion)
    {
        if (caps is null || instanceLoader is null || string.IsNullOrEmpty(mcVersion)) return [];
        var platform = PlatformOf(instanceLoader);
        if (platform is null || !caps.Platforms.TryGetValue(platform, out var versions) || !versions.Contains(mcVersion)) return [];

        var native = Native(instanceLoader);
        IEnumerable<string> accepted;
        if (caps.Accepts?.For(platform, mcVersion) is { } declared) accepted = declared;
        else
        {
            bool Has(string bridge) => caps.Bridges.Contains(bridge, StringComparer.OrdinalIgnoreCase);
            accepted = platform switch
            {
                // The vanilla base runs Fabric mods and, through the loader's Quilt layer, Quilt mods.
                BakePlatform.Vanilla => ["fabric", "quilt"],
                BakePlatform.NeoForge => [.. Has("fabricbridge.jar") ? new[] { "fabric", "quilt" } : [], .. Has("forgecompat.jar") ? new[] { "forge" } : []],
                // NeoForge 1.20.1 is Forge 47.1 forked: the loader reads its mods as Forge mods.
                BakePlatform.Forge => [.. Has("fabricbridge-forge.jar") ? new[] { "fabric", "quilt" } : [], "neoforge"],
                _ => [],
            };
        }
        return Ecosystems.Where(e => accepted.Contains(e) && !native.Contains(e)).ToList();
    }

    /// <summary>The loader platform that runs an instance with this loader (null: none).</summary>
    public static string? PlatformOf(string? instanceLoader) => instanceLoader switch
    {
        "fabric" or "quilt" => BakePlatform.Vanilla,
        "neoforge" => BakePlatform.NeoForge,
        "forge" => BakePlatform.Forge,
        _ => null,
    };

    /// <summary>
    /// The bridge jars the installed package says it lacks to run this instance's platform (a core-only package);
    /// empty when nothing is missing or the package does not say.
    /// </summary>
    public static IReadOnlyList<string> MissingBridges(Capabilities? caps, string? instanceLoader, string? mcVersion) =>
        caps?.Accepts is { } table && PlatformOf(instanceLoader) is { } platform && !string.IsNullOrEmpty(mcVersion) ? table.Missing(platform, mcVersion) : [];

    /// <summary>The ecosystem a file would run as here: a native one if it has one, else the first extra it ships for; null when neither.</summary>
    public static string? EcosystemOf(string? instanceLoader, IEnumerable<string> fileLoaders, IReadOnlyList<string> extras)
    {
        var loaders = fileLoaders.Select(l => l.ToLowerInvariant()).ToList();
        var native = Native(instanceLoader);
        if (native.FirstOrDefault(loaders.Contains) is { } n) return n;
        return extras.FirstOrDefault(loaders.Contains);
    }

    /// <summary>True when the file (or project) runs here only through the universal loader.</summary>
    public static bool IsCross(string? instanceLoader, IEnumerable<string> loaders, IReadOnlyList<string> extras)
    {
        var e = EcosystemOf(instanceLoader, loaders, extras);
        return e is not null && !Native(instanceLoader).Contains(e);
    }

    /// <summary>
    /// The loaders to look up a cross-ecosystem mod's dependencies with: the mod's own ecosystem first (Fabric API for a
    /// Fabric mod on NeoForge), then the instance's own, then the other extras.
    /// </summary>
    public static IReadOnlyList<string?> DependencyOrder(string? instanceLoader, string modEcosystem, IReadOnlyList<string> extras)
    {
        var order = new List<string?> { modEcosystem };
        if (modEcosystem == "quilt") order.Add("fabric");
        foreach (var e in Native(instanceLoader).Concat(extras))
            if (!order.Contains(e)) order.Add(e);
        return order;
    }

    /// <summary>
    /// Files to show for a project: native files first, then files that run through the loader; each group newest
    /// first (the provider's order). Files for neither are dropped.
    /// </summary>
    public static IReadOnlyList<ContentFile> Usable(IEnumerable<ContentFile> files, string? instanceLoader, IReadOnlyList<string> extras)
    {
        var native = Native(instanceLoader);
        var list = files.ToList();
        var mine = list.Where(f => f.Loaders.Any(l => native.Contains(l.ToLowerInvariant()))).ToList();
        var cross = list.Where(f => !mine.Contains(f) && f.Loaders.Any(l => extras.Contains(l.ToLowerInvariant()))).ToList();
        return [.. mine, .. cross];
    }
}
