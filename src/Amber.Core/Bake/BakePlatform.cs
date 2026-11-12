using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Amber.Core.Content;
using Amber.Core.Game;
using Amber.Core.Launch;
using Amber.Core.Localization;
using Amber.Core.Net;

namespace Amber.Core.Bake;

/// <summary>
/// The loader's platforms (interface v1.1+): <c>vanilla</c> (vanilla, Fabric and Quilt instances), <c>neoforge</c>
/// and <c>forge</c>. For the last two Jay prepares the platform install the loader reads (<c>platform.installDir</c>):
/// <code>
///   inst/version.json, inst/install_profile.json, inst/data/client.lzma   (from the official installer)
///   libraries/                                                           (link to the game directory's libraries)
///   platform-cache/&lt;version&gt;-client-jay.json                            (where the installer's processors wrote srg/extra/patched)
/// </code>
/// The processor outputs themselves (the per-version base jars) come from Jay's normal NeoForge/Forge install,
/// which runs the official processors locally. Nothing here is uploaded or shipped: it is the player's own install.
/// </summary>
public static partial class BakePlatform
{
    public const string Vanilla = "vanilla";
    public const string NeoForge = "neoforge";
    public const string Forge = "forge";
    /// <summary>
    /// Forge 1.12.2 (interface v1.3.2): LaunchWrapper and FML coremods, Java 8. The platform folder is Forge's
    /// <c>version.json</c> (the installer's <c>versionInfo</c>) and a <c>libraries/</c> link; nothing else.
    /// </summary>
    public const string ForgeLegacy = "forge-1.12.2";
    public const string ForgeLegacyMc = "1.12.2";

    /// <summary>The loader platform for this instance; null when the loader cannot run it (OptiFine, LiteLoader…).</summary>
    public static string? KindOf(GameInstance instance) => instance.Loader switch
    {
        null or "fabric" or "quilt" => Vanilla,
        "neoforge" => NeoForge,
        "forge" when instance.McVersion == ForgeLegacyMc => ForgeLegacy,
        "forge" => Forge,
        _ => null,
    };

    /// <summary>
    /// The Java a platform's game runs on when Jay knows it without asking the loader: Forge 1.12.2 runs on Java 8
    /// only (LaunchWrapper; most of its coremods break on 9+). Loaders say the same in <c>javaLaunch</c>.
    /// </summary>
    public static JavaRange? KnownJavaLaunch(string kind) => kind == ForgeLegacy ? new JavaRange(8, 8) : null;

    public static string DisplayName(string kind) => L.T(kind switch
    {
        NeoForge => "core.bake.platform.neoforge",
        Forge => "core.bake.platform.forge",
        ForgeLegacy => "core.bake.platform.forge_legacy",
        _ => "core.bake.platform.vanilla",
    });

    /// <summary>The official installer's address (the downloader adds mirrors).</summary>
    public static string InstallerUrl(string kind, string version) => kind switch
    {
        NeoForge => $"https://maven.neoforged.net/releases/net/neoforged/neoforge/{version}/neoforge-{version}-installer.jar",
        Forge or ForgeLegacy => $"https://maven.minecraftforge.net/net/minecraftforge/forge/{version}/forge-{version}-installer.jar",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>
    /// Prepares (or reuses) the platform install for a NeoForge/Forge instance and returns its directory. Returns
    /// null for vanilla. The installer is downloaded once per platform version; the libraries are not copied.
    /// </summary>
    public static async Task<string?> PrepareAsync(GameDirectory dir, GameInstance instance, string dataRoot,
        Func<IReadOnlyCollection<DownloadItem>, CancellationToken, Task>? download, IProgress<BakeEvent>? progress, CancellationToken ct)
    {
        var kind = KindOf(instance);
        if (kind is null or Vanilla) return null;
        var version = instance.LoaderVersion is { Length: > 0 } v && SafeName().IsMatch(v)
            ? v : throw new BakeException(L.F("core.bake.platform_unknown_version", DisplayName(kind)), []);
        // Forge 1.12.2's own version reads "1.12.2-14.23.5.2860"; an instance may know only the build number.
        if (kind == ForgeLegacy && !version.StartsWith(ForgeLegacyMc + "-", StringComparison.Ordinal)) version = ForgeLegacyMc + "-" + version;

        var platforms = Path.Combine(dataRoot, "platforms");
        var installer = Path.Combine(platforms, "installers", $"{kind}-{version}-installer.jar");
        if (!File.Exists(installer))
        {
            if (download is null) throw new BakeException(L.F("core.bake.download_unavailable", Path.GetFileName(installer)), []);
            progress?.Report(new BakeEvent("platform", L.F("core.bake.platform_installer", DisplayName(kind), version), false, false));
            await download([new DownloadItem(InstallerUrl(kind, version), installer, Within: platforms)], ct);
        }

        // One install per platform version and game directory (the libraries link points at that directory).
        var libraries = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir.LibrariesDir));
        var tag = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(OperatingSystem.IsWindows() ? libraries.ToLowerInvariant() : libraries)))[..10];
        var root = Path.Combine(platforms, $"{kind}-{version}-{tag}");
        if (kind == ForgeLegacy) return PrepareLegacy(dir, installer, root, libraries);
        var inst = Path.Combine(root, "inst");
        Directory.CreateDirectory(Path.Combine(inst, "data"));

        JsonObject profile;
        using (var zip = ZipFile.OpenRead(installer))
        {
            Extract(zip, "install_profile.json", Path.Combine(inst, "install_profile.json"));
            profile = JsonNode.Parse(File.ReadAllText(Path.Combine(inst, "install_profile.json")))?.AsObject()
                      ?? throw new InvalidDataException(L.T("core.install.forge.missingprofile"));
            var versionEntry = (profile["json"]?.GetValue<string>() ?? "/version.json").TrimStart('/');
            Extract(zip, versionEntry, Path.Combine(inst, "version.json"));
            if (zip.GetEntry("data/client.lzma") is not null) Extract(zip, "data/client.lzma", Path.Combine(inst, "data", "client.lzma"));
        }

        LinkLibraries(Path.Combine(root, "libraries"), libraries);

        var marker = new JsonObject { ["platform"] = kind, ["side"] = "client", ["writer"] = "jay" };
        var data = profile["data"] as JsonObject;
        var missing = new List<string>();
        foreach (var (key, name, required) in new[] { ("MC_SRG", "srg", true), ("MC_EXTRA", "extra", true), ("PATCHED", "patched", kind == Forge),
                     ("MC_SLIM", "slim", false), ("MERGED_MAPPINGS", "mappings", false) })
        {
            var value = data?[key]?["client"]?.GetValue<string>();
            if (value is null || value.Length < 3 || value[0] != '[' || value[^1] != ']')
            {
                if (required) missing.Add(key);
                continue;
            }
            var path = dir.LibraryPath(MavenCoordinate.Parse(value[1..^1]).RelativePath);
            if (!File.Exists(path))
            {
                if (required) missing.Add(Path.GetFileName(path));
                continue;
            }
            marker[name] = AsciiPaths.MapArgument(path);
        }
        if (missing.Count > 0)
            throw new BakeException(L.F("core.bake.platform_incomplete", DisplayName(kind), string.Join(L.T("core.mp.format.listseparator"), missing)), []);

        var profileVersion = profile["version"]?.GetValue<string>() is { } pv && SafeName().IsMatch(pv) ? pv : $"{kind}-{version}";
        var markerPath = Path.Combine(root, "platform-cache", $"{profileVersion}-client-jay.json");
        var text = marker.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        if (!File.Exists(markerPath) || File.ReadAllText(markerPath) != text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
            var temp = markerPath + ".tmp";
            File.WriteAllText(temp, text, new UTF8Encoding(false));
            File.Move(temp, markerPath, true);
        }
        return root;
    }

    /// <summary>
    /// forge-1.12.2's platform folder: <c>version.json</c> is the installer's <c>versionInfo</c> (LaunchWrapper's
    /// profile) without the server-only libraries, as Jay's own Forge install writes it, and <c>libraries/</c> links
    /// to the game directory's libraries, which must hold every library it lists (the universal jar included). Jay's
    /// normal Forge 1.12.2 install puts them there; anything missing is named here, before the loader runs.
    /// </summary>
    private static string PrepareLegacy(GameDirectory dir, string installer, string root, string libraries)
    {
        Directory.CreateDirectory(root);
        JsonObject info;
        using (var zip = ZipFile.OpenRead(installer))
        {
            var entry = zip.GetEntry("install_profile.json") ?? throw new InvalidDataException(L.T("core.install.forge.missingprofile"));
            using var stream = entry.Open();
            info = (JsonNode.Parse(stream) as JsonObject)?["versionInfo"] as JsonObject
                   ?? throw new BakeException(L.F("core.bake.platform_incomplete", DisplayName(ForgeLegacy), "versionInfo"), []);
        }
        info = (JsonObject)info.DeepClone();
        if (info["libraries"] is JsonArray libs)
            for (var i = libs.Count - 1; i >= 0; i--)
                if (libs[i]?["clientreq"] is JsonValue v && v.TryGetValue<bool>(out var client) && !client) libs.RemoveAt(i);

        var missing = new List<string>();
        foreach (var lib in info["libraries"] as JsonArray ?? [])
        {
            if (lib?["name"]?.GetValue<string>() is not { Length: > 0 } name) continue;
            string relative;
            try { relative = MavenCoordinate.Parse(name).RelativePath; }
            catch (Exception e) when (e is FormatException or ArgumentException or InvalidDataException) { missing.Add(name); continue; }
            if (!File.Exists(dir.LibraryPath(relative))) missing.Add(Path.GetFileName(relative));
        }
        if (missing.Count > 0)
            throw new BakeException(L.F("core.bake.platform_incomplete", DisplayName(ForgeLegacy), string.Join(L.T("core.mp.format.listseparator"), missing.Take(5))), []);

        var text = info.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        var versionJson = Path.Combine(root, "version.json");
        if (!File.Exists(versionJson) || File.ReadAllText(versionJson) != text)
        {
            var temp = versionJson + ".tmp";
            File.WriteAllText(temp, text, new UTF8Encoding(false));
            File.Move(temp, versionJson, true);
        }
        LinkLibraries(Path.Combine(root, "libraries"), libraries);
        return root;
    }

    /// <summary>Writes an installer entry unless the same bytes are already there (keeps modification times stable).</summary>
    private static void Extract(ZipArchive zip, string entryName, string target)
    {
        var entry = zip.GetEntry(entryName) ?? throw new InvalidDataException(L.F("core.install.forge.missingentry", entryName));
        if (File.Exists(target) && new FileInfo(target).Length == entry.Length) return;
        var temp = target + ".tmp";
        entry.ExtractToFile(temp, true);
        File.Move(temp, target, true);
    }

    /// <summary>A junction (Windows) or symbolic link (macOS/Linux) to the game directory's libraries.</summary>
    internal static void LinkLibraries(string link, string target)
    {
        if (!Directory.Exists(target)) throw new DirectoryNotFoundException(target);
        var info = new DirectoryInfo(link);
        if (info.Exists || info.LinkTarget is not null)
        {
            if (info.LinkTarget is null) throw new IOException(L.F("core.bake.platform_link_blocked", link));
            var current = Path.IsPathFullyQualified(info.LinkTarget) ? info.LinkTarget : Path.GetFullPath(info.LinkTarget, Path.GetDirectoryName(link)!);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (Path.TrimEndingDirectorySeparator(current.Replace(@"\\?\", "").Replace(@"\??\", "")).Equals(target, comparison)) return;
            info.Delete();   // only the link itself, never its target
        }
        if (OperatingSystem.IsWindows()) WindowsJunction.Create(link, target);
        else Directory.CreateSymbolicLink(link, target);
    }

    /// <summary>
    /// Forge checks for updates online at every start unless <c>config/fml.toml</c> says <c>versionCheck = false</c>.
    /// The loader's interface asks the launcher to turn it off for baked Forge launches.
    /// </summary>
    public static void DisableForgeUpdateCheck(string workDir)
    {
        var path = Path.Combine(workDir, "config", "fml.toml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path))
        {
            File.WriteAllText(path, "versionCheck = false\n", new UTF8Encoding(false));
            return;
        }
        var text = File.ReadAllText(path);
        var updated = VersionCheckLine().IsMatch(text) ? VersionCheckLine().Replace(text, "versionCheck = false") : text.TrimEnd() + "\nversionCheck = false\n";
        if (updated != text) File.WriteAllText(path, updated, new UTF8Encoding(false));
    }

    /// <summary>
    /// Forge 1.12.2 asks its update server at every start unless <c>config/forge.cfg</c> turns version checking off
    /// (<c>version_checking { B:Global=false }</c>). Jay turns it off for baked launches, as for Forge 1.20.1.
    /// Other settings in the file are kept; Forge fills in the rest of its defaults on the first start.
    /// </summary>
    public static void DisableLegacyForgeUpdateCheck(string workDir)
    {
        var path = Path.Combine(workDir, "config", "forge.cfg");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var text = File.Exists(path) ? File.ReadAllText(path) : "";
        string updated;
        if (LegacyGlobalCheck().IsMatch(text)) updated = LegacyGlobalCheck().Replace(text, m => m.Groups[1].Value + "false");
        else if (LegacyCheckCategory().Match(text) is { Success: true } category)
            updated = text.Insert(category.Index + category.Length, "\n    B:Global=false");
        else updated = (text.Length == 0 ? "" : text.TrimEnd() + "\n\n") + "version_checking {\n    B:Global=false\n}\n";
        if (updated != text) File.WriteAllText(path, updated, new UTF8Encoding(false));
    }

    [GeneratedRegex(@"(?m)^([ \t]*B:Global[ \t]*=[ \t]*)[^\r\n]*")]
    private static partial Regex LegacyGlobalCheck();

    [GeneratedRegex(@"(?m)^[ \t]*version_checking[ \t]*\{[ \t]*\r?$")]
    private static partial Regex LegacyCheckCategory();

    [GeneratedRegex(@"^[A-Za-z0-9._+-]+$")]
    private static partial Regex SafeName();

    [GeneratedRegex(@"(?m)^[ \t]*versionCheck[ \t]*=.*$")]
    private static partial Regex VersionCheckLine();
}

