using Amber.Core.Localization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Amber.Core.Net;

namespace Amber.Core.Java;

/// <summary>从 Mojang 的 Java 运行时清单下载 JRE 到 &lt;游戏目录&gt;/runtime。</summary>
public sealed class JavaRuntimeInstaller(Downloader downloader, string gameRoot)
{
    private const string ManifestUrl = "https://piston-meta.mojang.com/v1/products/java-runtime/2ec0cc96c44e5a76b9c8b7c39df7210883d12871/all.json";

    public static string Platform
    {
        get
        {
            var arch = RuntimeInformation.OSArchitecture;
            if (OperatingSystem.IsWindows())
                return arch switch { Architecture.Arm64 => "windows-arm64", Architecture.X86 => "windows-x86", _ => "windows-x64" };
            if (OperatingSystem.IsMacOS())
                return arch == Architecture.Arm64 ? "mac-os-arm64" : "mac-os";
            // Mojang 目前只发布 Linux x86/x64 运行时；ARM Linux 从 Adoptium 下 Temurin（见 TemurinInstaller），
            // 不能把 x64 包误装到 ARM 机器上。
            return arch switch
            {
                Architecture.X64 => "linux",
                Architecture.X86 => "linux-i386",
                Architecture.Arm64 => "linux-arm64",
                _ => "linux-" + arch.ToString().ToLowerInvariant(),
            };
        }
    }

    /// <summary>
    /// 依次试的 Mojang 平台：ARM 的 Mac 和 Windows 上 Mojang 不提供老 Java（8、16），退回 x64 版
    /// （Mac 经 Rosetta 2、Windows 经系统自带的 x64 仿真运行；老版本 Minecraft 的本地库本来也只有 x64 的，必须用 x64 Java）。
    /// </summary>
    internal static IReadOnlyList<string> PlatformCandidates(string platform) => platform switch
    {
        "mac-os-arm64" => ["mac-os-arm64", "mac-os"],
        "windows-arm64" => ["windows-arm64", "windows-x64"],
        _ => [platform],
    };

    /// <summary>安装运行时并返回 java 可执行文件路径。component 为空或当前平台没有时按主版本号找；Mojang 完全不提供的平台（ARM Linux）从 Adoptium 下 Temurin。</summary>
    public async Task<string> InstallAsync(string? component, int majorVersion, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        using var all = JsonDocument.Parse(await downloader.GetStringAsync(ManifestUrl, ct));
        foreach (var candidate in PlatformCandidates(Platform))
        {
            if (!all.RootElement.TryGetProperty(candidate, out var platform) || FindComponent(platform, component, majorVersion) is not { } found) continue;
            if (candidate == "mac-os" && Platform == "mac-os-arm64" && !RosettaInstalled())
                throw new PlatformNotSupportedException(L.F("core.java.install.needrosetta", majorVersion));
            return await InstallMojangAsync(candidate, found.Name, found.Url, progress, ct);
        }
        if (TemurinInstaller.Supports(Platform))
            return await new TemurinInstaller(downloader, gameRoot).InstallAsync(majorVersion, progress, ct);
        if (!all.RootElement.TryGetProperty(Platform, out _))
            throw new PlatformNotSupportedException(L.F("core.java.install.unsupportedplatform", Platform));
        throw new InvalidOperationException(L.F("core.java.install.unavailableversion", majorVersion));
    }

    /// <summary>Apple 芯片上 x64 程序要 Rosetta 2；没装时运行 x64 Java 会直接报“Bad CPU type”。</summary>
    private static bool RosettaInstalled() =>
        File.Exists("/Library/Apple/usr/share/rosetta/rosetta") || Directory.Exists("/Library/Apple/usr/libexec/oah");

    private async Task<string> InstallMojangAsync(string platformName, string name, string manifestUrl, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        using var manifest = JsonDocument.Parse(await downloader.GetStringAsync(manifestUrl, ct));
        // 组件名来自版本文件，文件名来自运行时清单：拼出来的路径都必须在 runtime 目录里。
        Game.GameDirectory.CheckId(name);
        var home = Path.Combine(gameRoot, "runtime", name, platformName, name);
        var items = new List<DownloadItem>();
        var executableFiles = new List<string>();
        foreach (var file in manifest.RootElement.GetProperty("files").EnumerateObject())
        {
            var local = Game.GameDirectory.Contained(home, file.Name);
            switch (file.Value.GetProperty("type").GetString())
            {
                case "directory":
                    Directory.CreateDirectory(local);
                    break;
                case "file":
                    var raw = file.Value.GetProperty("downloads").GetProperty("raw");
                    items.Add(new DownloadItem(raw.GetProperty("url").GetString()!, local, raw.GetProperty("sha1").GetString(), raw.GetProperty("size").GetInt64(), Within: home));
                    if (file.Value.TryGetProperty("executable", out var executable) && executable.GetBoolean())
                        executableFiles.Add(local);
                    break;
            }
        }
        await downloader.DownloadAllAsync(items, progress, ct);

        var exe = Path.Combine(home, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java");
        if (OperatingSystem.IsMacOS()) exe = Path.Combine(home, "jre.bundle", "Contents", "Home", "bin", "java");
        if (!File.Exists(exe)) throw new FileNotFoundException(L.T("core.java.install.missingexecutable"), exe);
        if (!OperatingSystem.IsWindows())
        {
            executableFiles.Add(exe);
            foreach (var path in executableFiles.Distinct())
                File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
        return exe;
    }

    private static (string Name, string Url)? FindComponent(JsonElement platform, string? component, int major)
    {
        if (component is not null && Url(platform, component) is { } direct) return (component, direct);
        foreach (var entry in platform.EnumerateObject())
        {
            if (entry.Name == "minecraft-java-exe" || entry.Value.GetArrayLength() == 0) continue;
            var versionName = entry.Value[0].GetProperty("version").GetProperty("name").GetString() ?? "";
            var parsed = versionName.StartsWith("8u", StringComparison.Ordinal) ? 8 : JavaFinder.ParseMajor(versionName);
            if (parsed == major && Url(platform, entry.Name) is { } url) return (entry.Name, url);
        }
        return null;

        static string? Url(JsonElement p, string key) =>
            p.TryGetProperty(key, out var arr) && arr.GetArrayLength() > 0 ? arr[0].GetProperty("manifest").GetProperty("url").GetString() : null;
    }
}
