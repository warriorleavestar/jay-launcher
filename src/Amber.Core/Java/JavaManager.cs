using Amber.Core.Localization;
using Amber.Core.Game;
using Amber.Core.Net;

namespace Amber.Core.Java;

/// <summary>按版本要求找 Java：本机有完全匹配的就用，没有就下载 Mojang 运行时，最后才退而用更高版本。</summary>
public sealed class JavaManager(Downloader downloader, GameDirectory dir, string? sharedRoot = null)
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private List<JavaInstall>? _cache;

    /// <summary>是否允许自动下载运行时。关闭后只用本机已有的 Java。</summary>
    public bool AutoDownload { get; init; } = true;

    public IProgress<string>? Status { get; set; }

    public async Task<List<JavaInstall>> ScanAsync(bool refresh = false)
    {
        if (_cache is null || refresh)
            _cache = await Task.Run(() =>
            {
                var found = JavaFinder.FindAll(dir.Root);
                // Jay's runtime cache is shared between game-library folders. Keep scanning the current game folder
                // too so runtimes installed by older builds are reused instead of downloaded again.
                if (!string.IsNullOrWhiteSpace(sharedRoot)
                    && !Path.GetFullPath(sharedRoot).Equals(Path.GetFullPath(dir.Root), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    found.AddRange(JavaFinder.FindAll(sharedRoot));
                return found.DistinctBy(java => Path.GetFullPath(java.JavaPath), OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToList();
            });
        return _cache;
    }

    public Task<string> ForMajorAsync(int major, CancellationToken ct = default) => ResolveAsync(null, major, ct);

    public Task<string> ForVersionAsync(VersionJson version, CancellationToken ct = default) =>
        ResolveAsync(version.JavaVersion?.Component ?? (version.JavaVersion is null ? "jre-legacy" : null), version.JavaVersion?.MajorVersion ?? 8, ct);

    private async Task<string> ResolveAsync(string? component, int major, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var installs = await ScanAsync();
            if (JavaFinder.PickExact(installs, major) is { } exact) return exact.JavaPath;

            if (AutoDownload)
            {
                try
                {
                    Status?.Report(L.F("core.java.manager.downloading", major));
                    var runtime = new JavaRuntimeInstaller(downloader, string.IsNullOrWhiteSpace(sharedRoot) ? dir.Root : sharedRoot);
                    var path = await runtime.InstallAsync(component, major, new Progress<DownloadProgress>(p =>
                        Status?.Report(L.F("core.java.manager.downloadprogress", major, p.CompletedFiles, p.TotalFiles))), ct);
                    await ScanAsync(true);
                    return path;
                }
                catch (Exception e) when (e is not OperationCanceledException && JavaFinder.Pick(installs, major) is not null)
                {
                    Status?.Report(L.F("core.java.manager.downloadfallback", major, e.Message));
                }
            }
            return JavaFinder.Pick(installs, major)?.JavaPath
                   ?? throw new JavaNotFoundException(major, L.F("core.java.manager.notfound", major, major));
        }
        finally
        {
            _lock.Release();
        }
    }
}

/// <summary>找不到（也没能下载）需要的 Java。启动失败卡按它给出【下载 Java N】。</summary>
public sealed class JavaNotFoundException(int major, string message) : InvalidOperationException(message)
{
    public int Major { get; } = major;
}
