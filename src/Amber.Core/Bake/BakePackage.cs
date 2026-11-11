using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amber.Core.Game;
using Amber.Core.Localization;

namespace Amber.Core.Bake;

/// <summary>
/// The universal loader's packaged layout (interface v1.3): one folder with <c>loader.jar</c>, <c>libs/</c>,
/// <c>bridges/</c> and <c>manifest.json</c> (schema <c>amber-ul/package@1</c>). The manifest lists the class path,
/// the Java each platform needs and the sha256 of every file; Jay checks all of them before running anything.
/// </summary>
public sealed record BakePackage(
    string Root,
    string LoaderVersion,
    int Protocol,
    Version Interface,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Platforms,
    IReadOnlyDictionary<string, int> JavaMin,
    IReadOnlyList<string> Classpath,
    IReadOnlyDictionary<string, BakePackage.FileEntry> Files)
{
    public const string ManifestName = "manifest.json";
    public const string Schema = "amber-ul/package@1";

    /// <summary>The bridge jars the manifest lists (file names such as <c>fabricbridge.jar</c>).</summary>
    public IReadOnlyCollection<string> Bridges { get; init; } = [];

    /// <summary>
    /// Interface 1.3.1 <c>accepts</c>: per platform and game version, the mod ecosystems the loader runs there
    /// (Modrinth loader names). Null from older packages: <see cref="CrossLoader"/> then infers it from the bridges.
    /// </summary>
    public CrossLoader.AcceptTable? Accepts { get; init; }

    /// <summary>One file the manifest vouches for (relative path inside the package).</summary>
    public sealed record FileEntry(string Path, string Sha256, long Size);

    /// <summary>Reads and checks the manifest's shape. Throws a localized <see cref="BakeException"/> when it is not a loader package.</summary>
    public static BakePackage Read(string root)
    {
        root = System.IO.Path.GetFullPath(root);
        var path = System.IO.Path.Combine(root, ManifestName);
        JsonObject manifest;
        try
        {
            manifest = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new InvalidDataException();
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
        {
            throw new BakeException(L.F("core.bake.package_bad", root), [], "package_bad");
        }
        if (Str(manifest, "schema") != Schema) throw new BakeException(L.F("core.bake.package_bad", root), [], "package_bad");

        var files = new Dictionary<string, FileEntry>(StringComparer.Ordinal);
        foreach (var (name, node) in manifest["files"] as JsonObject ?? [])
        {
            if (node is not JsonObject entry || Str(entry, "sha256") is not { Length: 64 } sha || entry["size"] is not JsonValue size || !size.TryGetValue<long>(out var bytes))
                throw new BakeException(L.F("core.bake.package_bad", root), [], "package_bad");
            // A name that would leave the folder ("..", rooted, drive letters) makes the whole manifest invalid.
            try { GameDirectory.Contained(root, Normalize(name)); }
            catch (InvalidDataException) { throw new BakeException(L.F("core.bake.package_bad", root), [], "package_bad"); }
            files[Normalize(name)] = new FileEntry(Normalize(name), sha.ToLowerInvariant(), bytes);
        }
        var classpath = new List<string>();
        foreach (var entry in manifest["classpath"] as JsonArray ?? [])
        {
            var relative = Normalize(entry?.GetValue<string>() ?? "");
            // Every class path entry must be one of the files the manifest vouches for (and so inside the folder).
            if (!files.ContainsKey(relative)) throw new BakeException(L.F("core.bake.package_bad", root), [], "package_bad");
            classpath.Add(GameDirectory.Contained(root, relative));
        }
        if (classpath.Count == 0) throw new BakeException(L.F("core.bake.package_bad", root), [], "package_bad");

        var platforms = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var (kind, versions) in manifest["platforms"] as JsonObject ?? [])
            platforms[kind] = versions is JsonArray a ? a.Select(v => v?.GetValue<string>() ?? "").Where(v => v.Length > 0).ToList() : [];
        var javaMin = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (kind, value) in manifest["javaMin"] as JsonObject ?? [])
            if (value is JsonValue v && v.TryGetValue<int>(out var major)) javaMin[kind] = major;

        var protocol = manifest["protocol"] is JsonValue p && p.TryGetValue<int>(out var pr) ? pr : 0;
        var face = Version.TryParse(Str(manifest, "interface"), out var parsed) ? parsed : new Version(1, 0);
        var bridges = new List<string>();
        foreach (var (name, node) in manifest["bridges"] as JsonObject ?? [])
            if (node is JsonValue v && v.TryGetValue<string>(out var bridgePath) && files.ContainsKey(Normalize(bridgePath))) bridges.Add(System.IO.Path.GetFileName(name));
        return new BakePackage(root, Str(manifest, "loaderVersion") ?? "", protocol, face, platforms, javaMin, classpath, files)
        {
            Bridges = bridges,
            Accepts = CrossLoader.AcceptTable.Parse(manifest["accepts"]),
        };
    }

    /// <summary>
    /// Checks every file the manifest lists against its size and sha256. Throws a localized
    /// <see cref="BakeException"/> (code <c>package_mismatch</c>) naming the first file that is missing or different.
    /// </summary>
    public void Verify()
    {
        foreach (var file in Files.Values)
        {
            var path = GameDirectory.Contained(Root, file.Path);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != file.Size || Sha256(path) != file.Sha256)
                throw new BakeException(L.F("core.bake.package_mismatch", file.Path, LoaderVersion), [], "package_mismatch");
        }
    }

    /// <summary>The Java a platform needs, per the manifest. Every loader so far needs 21.</summary>
    public int JavaMinFor(string kind) => JavaMin.TryGetValue(kind, out var major) ? major : JavaMin.Count > 0 ? JavaMin.Values.Max() : BakeService.DefaultJavaMin;

    /// <summary>
    /// Installs a loader package from its zip: the zip must hold one top-level folder with a manifest; it is
    /// unpacked into a temporary folder under <paramref name="installRoot"/>, every file is checked against the
    /// manifest, and only then moved to <c>&lt;installRoot&gt;/&lt;loaderVersion&gt;</c> (replacing an older copy of
    /// the same version). Nothing outside <paramref name="installRoot"/> is written. Returns the installed package.
    /// </summary>
    public static BakePackage InstallFromZip(string zipPath, string installRoot)
    {
        installRoot = System.IO.Path.GetFullPath(installRoot);
        Directory.CreateDirectory(installRoot);
        var temp = System.IO.Path.Combine(installRoot, ".install-" + Guid.NewGuid().ToString("N")[..12]);
        try
        {
            try
            {
                using var zip = ZipFile.OpenRead(zipPath);
                var tops = zip.Entries.Select(e => Normalize(e.FullName).Split('/')[0]).Where(t => t.Length > 0).Distinct(StringComparer.Ordinal).ToList();
                var prefix = tops.Count == 1 && zip.GetEntry(tops[0] + "/" + ManifestName) is not null ? tops[0] + "/" : "";
                if (prefix.Length == 0 && zip.GetEntry(ManifestName) is null)
                    throw new BakeException(L.F("core.bake.package_bad", System.IO.Path.GetFileName(zipPath)), [], "package_bad");
                foreach (var entry in zip.Entries)
                {
                    var name = Normalize(entry.FullName);
                    if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    var relative = name[prefix.Length..];
                    if (relative.Length == 0 || name.EndsWith('/')) continue;
                    // Contained() refuses "..", rooted names and anything else that would leave the folder.
                    var target = GameDirectory.Contained(temp, relative);
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target, false);
                }
            }
            catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                throw new BakeException(L.F("core.bake.package_zip_bad", System.IO.Path.GetFileName(zipPath), e.Message), [], "package_bad");
            }

            var package = Read(temp);
            package.Verify();
            // Only files the manifest vouches for (plus the manifest) may be in the package.
            var extra = Directory.EnumerateFiles(temp, "*", SearchOption.AllDirectories)
                .Select(f => Normalize(System.IO.Path.GetRelativePath(temp, f)))
                .FirstOrDefault(f => f != ManifestName && !package.Files.ContainsKey(f));
            if (extra is not null) throw new BakeException(L.F("core.bake.package_extra", extra), [], "package_mismatch");
            if (package.LoaderVersion.Length == 0 || package.LoaderVersion.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '+')))
                throw new BakeException(L.F("core.bake.package_bad", System.IO.Path.GetFileName(zipPath)), [], "package_bad");

            var final = System.IO.Path.Combine(installRoot, package.LoaderVersion);
            if (Directory.Exists(final)) Directory.Delete(final, true);
            Directory.Move(temp, final);
            return Read(final);
        }
        finally
        {
            if (Directory.Exists(temp)) try { Directory.Delete(temp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>The sha256 of a file, lower-case hex.</summary>
    public static string Sha256(string path)
    {
        using var input = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(input));
    }

    private static string Normalize(string name) => name.Replace('\\', '/');

    private static string? Str(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
