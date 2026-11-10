using System.Security.Cryptography;
using Amber.Core.Localization;
using Amber.Core.Multiplayer;
using Amber.Core.Net;

namespace Amber.Core.Bake;

/// <summary>
/// The universal loader package this Jay downloads: one zip attached to a release on Jay's own GitHub repository,
/// pinned by size and SHA-256. A zip that does not match the pin is never unpacked; one that does then goes through
/// the same install as "Install loader from zip…" (every file checked against the package's manifest).
/// The zip is built by the loader project and attached to the release by hand (scripts/attach-loader.ps1); a new
/// loader build means a new pin here.
/// </summary>
public sealed record BakeLoaderRelease(string LoaderVersion, string Tag, string AssetName, string Sha256, long Size)
{
    public const string ReleasesBase = "https://github.com/warriorleavestar/jay-launcher/releases/download/";

    public static BakeLoaderRelease Pinned { get; } = new(
        "0.3.0-proto",
        "universal-loader-0.3.0-proto",
        "amber-ul-0.3.0-proto.zip",
        "8bf1f785211121c843499c70e139fb86bbc881e04d6fa0b8321a5f112cfb6319",
        4_837_788);

    public string Url => ReleasesBase + Tag + "/" + AssetName;

    /// <summary>GitHub first, then the same GitHub download mirrors as the multiplayer component (mirrors first when Jay prefers mirrors). The pin decides, not the host.</summary>
    public IReadOnlyList<(string Name, string Url)> Sources(bool mirrorsFirst)
    {
        var official = ("GitHub", Url);
        var mirrors = EasyTierRelease.Mirrors.Select(m => (m.Name, m.Prefix + Url)).ToList();
        return mirrorsFirst ? [.. mirrors, official] : [official, .. mirrors];
    }

    /// <summary>Throws <see cref="InvalidDataException"/> unless the file has the pinned size and SHA-256.</summary>
    public void Check(string zipPath)
    {
        var length = new FileInfo(zipPath).Length;
        if (length != Size) throw new InvalidDataException(L.F("core.bake.download_size", AssetName, Size, length));
        string hash;
        using (var stream = File.OpenRead(zipPath)) hash = Convert.ToHexStringLower(SHA256.HashData(stream));
        if (!string.Equals(hash, Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(L.F("core.bake.download_hash", AssetName, hash));
    }
}

/// <summary>
/// Fetches the pinned loader zip with Jay's downloader: each source in turn, resumable (.part) within the same
/// source chain, a wrong file thrown away so no other source resumes from it. Returns the checked zip; installing
/// it is the caller's (the same path as a zip the player picked).
/// </summary>
public sealed class BakeLoaderDownloader(Downloader downloader, string installRoot, BakeLoaderRelease? release = null, IReadOnlyList<(string Name, string Url)>? sources = null)
{
    public BakeLoaderRelease Release { get; } = release ?? BakeLoaderRelease.Pinned;

    public IReadOnlyList<(string Name, string Url)> Sources => sources ?? Release.Sources(SourceChain.PrefersMirrors());

    /// <summary>Where the zip is downloaded (outside the version folders the install writes).</summary>
    public string ZipPath => Path.Combine(installRoot, ".download", Release.AssetName);

    /// <summary>progress: CurrentFile is the source being tried, BytesDownloaded the bytes so far of <see cref="BakeLoaderRelease.Size"/>.</summary>
    public async Task<string> FetchAsync(IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        var zip = ZipPath;
        if (File.Exists(zip))
        {
            try { Release.Check(zip); return zip; }
            catch (InvalidDataException) { Discard(zip); }
        }
        var failures = new List<string>();
        foreach (var (name, url) in Sources)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new DownloadProgress(0, 1, 0, name));
            try
            {
                await downloader.DownloadOneAsync(new DownloadItem(url, zip, null, Release.Size),
                    new Progress<long>(b => progress?.Report(new DownloadProgress(0, 1, b, name))), ct);
                Release.Check(zip);
                progress?.Report(new DownloadProgress(1, 1, Release.Size, name));
                return zip;
            }
            catch (Exception e) when (!ct.IsCancellationRequested && e is not OutOfMemoryException)
            {
                failures.Add(L.F("core.mp.component.sourcefailed", name, EasyTierInstaller.Describe(e)));
                if (e is InvalidDataException) Discard(zip);
            }
        }
        throw new BakeException(L.F("core.bake.download_failed", Release.AssetName, string.Join(Environment.NewLine, failures)), failures, "download_failed");
    }

    /// <summary>A wrong file and its partial download go, so the next source starts clean.</summary>
    public static void Discard(string zip)
    {
        foreach (var path in new[] { zip, zip + ".part", zip + ".part.state" })
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
