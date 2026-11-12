namespace Amber.Core.Bake;

/// <summary>
/// Where the universal loader and its caches live: a runtime folder beside Jay's data folder
/// (<c>%APPDATA%\Amber-runtime</c> next to <c>%APPDATA%\Amber</c>), never inside it.
/// <para>
/// The data folder holds settings, accounts, keys and identities. A baked game runs the loader's own launcher and
/// reads the loader package, the bake cache and the mapping/platform data at run time; once the game process runs in
/// an OS sandbox (privacy plan item 15) it must be able to see those files without seeing the data folder. So
/// this folder only ever holds the loader package (<see cref="LoaderRoot"/>), the bake cache (<see cref="CacheRoot"/>)
/// and the downloaded mapping tables and platform installs (<see cref="DataRoot"/>): nothing secret goes here.
/// </para>
/// </summary>
public sealed record BakeRuntime(string Root)
{
    public const string RootVariable = "AMBER_RUNTIME_DIR";

    /// <summary>Installed loader packages: <c>&lt;root&gt;/universal-loader/&lt;version&gt;</c>.</summary>
    public string LoaderRoot => Path.Combine(Root, "universal-loader");

    /// <summary>The loader's cache (baked jars, remapped game, state files). A launch may rebuild an entry ("launch_rebaked").</summary>
    public string CacheRoot => Path.Combine(Root, "bake-cache");

    /// <summary>Mapping tables, mapping cache and NeoForge/Forge platform installs that Jay prepares for the loader.</summary>
    public string DataRoot => Path.Combine(Root, "bake-data");

    /// <summary>The runtime folder for a data folder: <c>AMBER_RUNTIME_DIR</c>, else <c>&lt;data folder&gt;-runtime</c> beside it.</summary>
    public static BakeRuntime For(string dataDir)
    {
        if (Environment.GetEnvironmentVariable(RootVariable) is { Length: > 0 } configured) return new BakeRuntime(Path.GetFullPath(configured));
        var data = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDir));
        return new BakeRuntime(data + "-runtime");
    }

    /// <summary>One folder a sandboxed baked game needs, and whether the game may write there.</summary>
    public sealed record Grant(string Path, bool Writable);

    /// <summary>
    /// What the game sandbox must grant for a baked launch, besides the instance folder, Java, assets and libraries:
    /// the loader package and the platform/mapping data read-only, the cache read-write (a launch may rebake an
    /// entry). Platform installs link to the game directory's libraries, which the sandbox grants anyway.
    /// </summary>
    public IReadOnlyList<Grant> SandboxGrants => SandboxGrantsFor(null);

    /// <summary>
    /// The grants for a launch with loader <paramref name="loaderVersion"/>: a loader that writes only inside the
    /// instance at launch (<see cref="LaunchWritesOnlyInstance"/>) gets the cache read-only too. bake-instance, warm
    /// and gc write the cache, and Jay runs them outside the sandbox.
    /// </summary>
    public IReadOnlyList<Grant> SandboxGrantsFor(string? loaderVersion) =>
        [new(LoaderRoot, false), new(DataRoot, false), new(CacheRoot, !LaunchWritesOnlyInstance(loaderVersion))];

    /// <summary>The last loader whose launch still writes the shared cache (class cache, missing products, isolation files).</summary>
    public static readonly Version LastSharedCacheWriter = new(0, 3, 1);

    /// <summary>
    /// Whether a launch with this loader writes only under <c>&lt;instance&gt;/.amber-ul/</c> (its class cache and a
    /// cache overlay for missing products), so the shared cache can be read-only for the game. Loaders after 0.3.1;
    /// an unknown version (a dev folder without a package) counts as an old one.
    /// </summary>
    public static bool LaunchWritesOnlyInstance(string? loaderVersion)
    {
        if (string.IsNullOrEmpty(loaderVersion)) return false;
        var core = loaderVersion.Split('-', '+')[0];
        return Version.TryParse(core.Count(c => c == '.') == 0 ? core + ".0" : core, out var version)
            && Normalize(version) > LastSharedCacheWriter;
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    /// <summary>
    /// Moves loader packages and caches that older versions kept in the data folder into the runtime folder (once;
    /// a folder that already exists in the runtime folder is left alone and the old one stays). Returns the moved
    /// folders as (old, new). Same volume, so this is a rename: links inside (platform libraries) keep working.
    /// </summary>
    public IReadOnlyList<(string From, string To)> MigrateFrom(string dataDir)
    {
        var moved = new List<(string, string)>();
        foreach (var (name, target) in new[] { ("universal-loader", LoaderRoot), ("bake-cache", CacheRoot), ("bake-data", DataRoot) })
        {
            var old = Path.Combine(dataDir, name);
            if (!Directory.Exists(old) || Directory.Exists(target) || File.Exists(target)) continue;
            try
            {
                Directory.CreateDirectory(Root);
                Directory.Move(old, target);
                moved.Add((old, target));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A busy or cross-volume folder stays where it is; the loader is reinstalled / the cache rebuilt as needed.
            }
        }
        return moved;
    }

    /// <summary>A configured loader path that pointed into the old folder now points to the same place in the runtime folder.</summary>
    public string? RebaseLoaderPath(string? configured, string dataDir)
    {
        if (string.IsNullOrWhiteSpace(configured)) return configured;
        var old = Path.Combine(Path.GetFullPath(dataDir), "universal-loader");
        var full = Path.GetFullPath(configured);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!GameDirectoryPaths.IsWithin(full, old, comparison)) return configured;
        var relative = Path.GetRelativePath(old, full);
        var rebased = relative == "." ? LoaderRoot : Path.Combine(LoaderRoot, relative);
        return Directory.Exists(rebased) || !Directory.Exists(full) ? rebased : configured;
    }
}

internal static class GameDirectoryPaths
{
    public static bool IsWithin(string path, string folder, StringComparison comparison)
    {
        var p = Path.TrimEndingDirectorySeparator(path);
        var f = Path.TrimEndingDirectorySeparator(folder);
        return p.Equals(f, comparison) || p.StartsWith(f + Path.DirectorySeparatorChar, comparison) || p.StartsWith(f + Path.AltDirectorySeparatorChar, comparison);
    }
}
