using System.Security.Cryptography;

namespace Amber.Core.Multiplayer;

public sealed record ModSyncProgress(int Done, int Total, string Current);

/// <summary>How a sync went: which files came in, and which could not be fetched (by display name).</summary>
public sealed record ModSyncResult(IReadOnlyList<ModManifestEntry> Added, IReadOnlyList<ModManifestEntry> Failed, ModSyncPlan Plan)
{
    public bool Succeeded => Failed.Count == 0;
}

/// <summary>
/// Carries out a mod sync in one mods folder (official §6.14 “one-click sync”). Files Modrinth knows by SHA-1 come from
/// there (a download the user started); the rest are asked from the host over the room's encrypted channel. Every file
/// is checked against the SHA-1 in the host's list before it goes in. Replaced files go to Recently deleted, never
/// straight to nothing. The caller copies the instance first when syncing into a new one.
/// </summary>
public sealed class ModSyncRunner
{
    /// <summary>SHA-1s → download URL, for the files a public site has.</summary>
    public required Func<IReadOnlyCollection<string>, CancellationToken, Task<IReadOnlyDictionary<string, string>>> FindPublic { get; init; }

    /// <summary>Download a URL to a path.</summary>
    public required Func<string, string, CancellationToken, Task> Download { get; init; }

    /// <summary>Ask the host for one file by SHA-1; where it was saved, or null.</summary>
    public required Func<string, CancellationToken, Task<string?>> FromHost { get; init; }

    /// <summary>Move a file to Recently deleted.</summary>
    public required Action<string> Recycle { get; init; }

    public async Task<ModSyncResult> RunAsync(ModDiff diff, string modsDir, bool intoNewInstance, IProgress<ModSyncProgress>? progress = null, CancellationToken ct = default)
    {
        var wanted = diff.Missing.Concat(diff.Different.Select(p => p.Host)).Select(m => m.Sha1).Distinct().ToList();
        IReadOnlyDictionary<string, string> urls;
        try
        {
            urls = wanted.Count == 0 ? new Dictionary<string, string>() : await FindPublic(wanted, ct);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // Public sites unreachable: everything comes from the host instead.
            urls = new Dictionary<string, string>();
        }
        var plan = ModSync.Plan(diff, urls.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase), intoNewInstance);
        Directory.CreateDirectory(modsDir);

        var added = new List<ModManifestEntry>();
        var failed = new List<ModManifestEntry>();
        var done = 0;
        foreach (var step in plan.Steps)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new ModSyncProgress(done, plan.Steps.Count, step.File.DisplayName));
            var name = SafeFileName(step.File.FileName);
            var temp = Path.Combine(modsDir, name + ".jaysync");
            try
            {
                if (step.Source == ModSyncSource.Public && urls.TryGetValue(step.File.Sha1, out var url))
                {
                    await Download(url, temp, ct);
                }
                else if (await FromHost(step.File.Sha1, ct) is { } received)
                {
                    File.Move(received, temp, overwrite: true);
                }
                else
                {
                    failed.Add(step.File);
                    continue;
                }
                if (!Sha1Matches(temp, step.File.Sha1))
                {
                    File.Delete(temp);
                    failed.Add(step.File);
                    continue;
                }
                // The old version of this mod (and any file already under the new name) goes to Recently deleted.
                if (step.Replaces is { } old && Path.Combine(modsDir, SafeFileName(old.FileName)) is var oldPath && File.Exists(oldPath)) Recycle(oldPath);
                var target = Path.Combine(modsDir, name);
                if (File.Exists(target)) Recycle(target);
                File.Move(temp, target);
                added.Add(step.File);
            }
            catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException || e is TaskCanceledException && !ct.IsCancellationRequested)
            {
                TryDelete(temp);
                failed.Add(step.File);
            }
            finally
            {
                done++;
            }
        }
        // Extra mods that could stop me joining: out of the way (only in a copy made for this room; see ModSync.Plan).
        foreach (var extra in plan.Replaced.Where(m => !plan.Steps.Any(s => s.Replaces == m)))
        {
            var path = Path.Combine(modsDir, SafeFileName(extra.FileName));
            if (File.Exists(path)) Recycle(path);
        }
        progress?.Report(new ModSyncProgress(done, plan.Steps.Count, ""));
        return new ModSyncResult(added, failed, plan);
    }

    /// <summary>A bare file name: no folders, no characters Windows refuses.</summary>
    internal static string SafeFileName(string name) => FileNames.Bare(name, "mod.jar");

    private static bool Sha1Matches(string path, string sha1)
    {
        using var stream = File.OpenRead(path);
        return string.Equals(Convert.ToHexStringLower(SHA1.HashData(stream)), sha1, StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
