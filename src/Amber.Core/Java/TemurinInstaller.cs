using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Amber.Core.Localization;
using Amber.Core.Net;

namespace Amber.Core.Java;

/// <summary>
/// Mojang 不提供 Java 的平台（ARM Linux）：从 Adoptium（Eclipse Temurin）的公开 API 下 JRE，
/// 装到 &lt;游戏目录&gt;/runtime/temurin-N/&lt;平台&gt;/temurin-N（和 Mojang 运行时同样的层数，Java 扫描能找到）。
/// 只在用户要启动、需要这个 Java 时下载；校验 API 给的 SHA-256。
/// </summary>
public sealed class TemurinInstaller(Downloader downloader, string gameRoot)
{
    private const string Api = "https://api.adoptium.net/v3/assets/latest";

    /// <summary>Temurin 发布的版本（LTS + 当前）。要求的版本没有时用比它高的最近一个（比如 16 用 17）。</summary>
    internal static readonly int[] Features = [8, 11, 17, 21, 25];

    public static bool Supports(string mojangPlatform) => ArchOf(mojangPlatform) is not null;

    /// <summary>Mojang 平台名 → Adoptium 的 os / architecture。</summary>
    internal static (string Os, string Arch)? ArchOf(string mojangPlatform) => mojangPlatform switch
    {
        "linux-arm64" => ("linux", "aarch64"),
        "linux-arm" => ("linux", "arm"),
        _ => null,
    };

    internal static int? FeatureFor(int major) => Features.Where(f => f >= major).Select(f => (int?)f).FirstOrDefault();

    internal static string AssetsUrl(int feature, string os, string arch) =>
        $"{Api}/{feature}/hotspot?architecture={arch}&image_type=jre&os={os}&vendor=eclipse";

    /// <summary>测试用：假装是别的平台（在 x64 机器上测下载和解压 ARM 包）。</summary>
    internal string? PlatformOverride { get; init; }

    public async Task<string> InstallAsync(int major, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var platform = PlatformOverride ?? JavaRuntimeInstaller.Platform;
        var (os, arch) = ArchOf(platform) ?? throw new PlatformNotSupportedException(L.F("core.java.install.unsupportedplatform", platform));
        var feature = FeatureFor(major) ?? throw new InvalidOperationException(L.F("core.java.install.unavailableversion", major));
        using var assets = JsonDocument.Parse(await downloader.GetStringAsync(AssetsUrl(feature, os, arch), ct));
        var package = PickPackage(assets.RootElement) ?? throw new InvalidOperationException(L.F("core.java.install.unavailableversion", major));

        var name = $"temurin-{feature}";
        var root = Path.Combine(gameRoot, "runtime", name, platform);
        var home = Path.Combine(root, name);
        Directory.CreateDirectory(root);
        var archive = Path.Combine(root, "download.tar.gz");
        try
        {
            await downloader.DownloadAllAsync([new DownloadItem(package.Url, archive, null, package.Size, Within: root)], progress, ct);
            if (!string.Equals(await Sha256Async(archive, ct), package.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(L.T("core.java.install.checksum"));
            var staging = home + ".tmp";
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            await using (var file = File.OpenRead(archive))
                Extract(file, staging);
            if (Directory.Exists(home)) Directory.Delete(home, true);
            Directory.Move(staging, home);
        }
        finally
        {
            File.Delete(archive);
        }
        var exe = Path.Combine(home, "bin", "java");
        if (!File.Exists(exe)) throw new FileNotFoundException(L.T("core.java.install.missingexecutable"), exe);
        File.SetUnixFileMode(exe, File.GetUnixFileMode(exe) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        return exe;
    }

    internal readonly record struct Package(string Url, string Sha256, long Size);

    /// <summary>API 返回的第一个 tar.gz 包（JRE）。</summary>
    internal static Package? PickPackage(JsonElement assets)
    {
        if (assets.ValueKind != JsonValueKind.Array) return null;
        foreach (var asset in assets.EnumerateArray())
        {
            if (!asset.TryGetProperty("binary", out var binary) || !binary.TryGetProperty("package", out var package)) continue;
            var url = package.TryGetProperty("link", out var l) ? l.GetString() : null;
            var sha = package.TryGetProperty("checksum", out var c) ? c.GetString() : null;
            if (url is null || sha is not { Length: 64 } || !url.StartsWith("https://", StringComparison.Ordinal) || !url.EndsWith(".tar.gz", StringComparison.Ordinal)) continue;
            return new Package(url, sha, package.TryGetProperty("size", out var z) && z.TryGetInt64(out var size) ? size : 0);
        }
        return null;
    }

    /// <summary>
    /// 解开 tar.gz，去掉最外一层目录（jdk8u512-b09-jre/ 之类）。只接受普通文件、目录和指向包内的符号链接；
    /// 任何会落到目标目录外的条目都拒绝（防路径穿越）。
    /// </summary>
    internal static void Extract(Stream gzip, string destination)
    {
        var root = Path.GetFullPath(destination);
        Directory.CreateDirectory(root);
        using var gz = new GZipStream(gzip, CompressionMode.Decompress);
        using var tar = new TarReader(gz);
        while (tar.GetNextEntry() is { } entry)
        {
            var parts = entry.Name.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length <= 1) continue;
            var relative = Path.Combine(parts[1..]);
            var target = Path.GetFullPath(Path.Combine(root, relative));
            if (!Inside(root, target)) throw new InvalidDataException(L.T("core.java.install.badarchive"));
            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    Directory.CreateDirectory(target);
                    break;
                case TarEntryType.RegularFile or TarEntryType.V7RegularFile:
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target, true);
                    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(target, (UnixFileMode)((int)entry.Mode & 0x1FF) | UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    break;
                case TarEntryType.SymbolicLink:
                    var link = entry.LinkName;
                    var resolved = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(target)!, link));
                    if (Path.IsPathRooted(link) || !Inside(root, resolved)) throw new InvalidDataException(L.T("core.java.install.badarchive"));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    if (File.Exists(target) || Directory.Exists(target)) File.Delete(target);
                    File.CreateSymbolicLink(target, link);
                    break;
                // 硬链接、设备文件之类 JRE 里没有，忽略。
            }
        }
    }

    private static bool Inside(string root, string path) =>
        path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) || path == root;

    private static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
    }
}
