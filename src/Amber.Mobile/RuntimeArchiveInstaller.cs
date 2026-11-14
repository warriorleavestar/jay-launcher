using System.Buffers;
using System.Security.Cryptography;
using SharpCompress.Readers;

namespace Amber.Mobile;

public sealed record RuntimeArchiveDescriptor(
    int Format,
    string Id,
    string Architecture,
    long ArchiveSize,
    string ArchiveSha256,
    long MaximumExtractedSize,
    string License,
    string SourceUrl,
    string DownloadUrl)
{
    public const int CurrentFormat = 1;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (Format != CurrentFormat) errors.Add($"Unsupported runtime archive format {Format}.");
        if (string.IsNullOrWhiteSpace(Id)) errors.Add("The runtime archive ID is missing.");
        if (Architecture is not ("arm64-v8a" or "x86_64")) errors.Add("The runtime archive architecture is not supported.");
        if (ArchiveSize <= 0) errors.Add("The runtime archive size is invalid.");
        if (MaximumExtractedSize < ArchiveSize || MaximumExtractedSize > 1024L * 1024 * 1024)
            errors.Add("The runtime extracted-size limit is invalid.");
        if (ArchiveSha256.Length != 64 || ArchiveSha256.Any(c => !Uri.IsHexDigit(c)))
            errors.Add("The runtime archive SHA-256 is invalid.");
        if (string.IsNullOrWhiteSpace(License)) errors.Add("The runtime archive license is missing.");
        if (!IsHttps(SourceUrl)) errors.Add("The runtime source URL is invalid.");
        if (!IsHttps(DownloadUrl)) errors.Add("The runtime download URL is invalid.");
        return errors;
    }

    private static bool IsHttps(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
}

public enum RuntimeInstallPhase { Downloading, Extracting, Verifying, Activating }

public readonly record struct RuntimeInstallProgress(RuntimeInstallPhase Phase, long Completed, long Total);

/// <summary>Downloads, bounds, extracts and atomically activates a self-contained Android JRE archive.</summary>
public static class RuntimeArchiveInstaller
{
    public static async Task<RuntimeBundleManifest> InstallAsync(
        string destination,
        RuntimeArchiveDescriptor descriptor,
        Func<RuntimeArchiveDescriptor, CancellationToken, Task<Stream>> openArchive,
        IProgress<RuntimeInstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(openArchive);
        var errors = descriptor.Validate();
        if (errors.Count != 0) throw new InvalidDataException(string.Join(" ", errors));

        var target = Path.GetFullPath(destination);
        if (File.Exists(target)) throw new IOException("The runtime destination is a file.");
        var parent = Directory.GetParent(target)?.FullName
            ?? throw new InvalidDataException("The runtime destination has no parent directory.");
        Directory.CreateDirectory(parent);

        var suffix = Guid.NewGuid().ToString("N");
        var staging = target + ".install-" + suffix;
        var backup = target + ".backup-" + suffix;
        var archivePath = target + ".download-" + suffix;
        Directory.CreateDirectory(staging);

        try
        {
            await using (var input = await openArchive(descriptor, ct))
                await WriteArchiveAsync(input, archivePath, descriptor, progress, ct);

            // Decompressing and hashing the runtime takes a while; keep it off the caller's (UI) thread.
            var files = await Task.Run(() => ExtractVerifiedFiles(archivePath, staging, descriptor, progress, ct), ct);
            RequireRuntimeFiles(files);
            var manifest = new RuntimeBundleManifest(
                RuntimeBundleManifest.CurrentFormat,
                descriptor.Id,
                descriptor.Architecture,
                files);
            manifest.Write(Path.Combine(staging, "runtime-manifest.json"));
            progress?.Report(new(RuntimeInstallPhase.Verifying, 0, files.Count));
            if (!await RuntimeBundleVerifier.VerifyAsync(staging, manifest, descriptor.Architecture, ct))
                throw new InvalidDataException("The extracted Android runtime did not pass final verification.");

            progress?.Report(new(RuntimeInstallPhase.Activating, files.Count, files.Count));
            RuntimeBundleInstaller.ActivateStaged(target, staging, backup);
            return manifest;
        }
        finally
        {
            if (File.Exists(archivePath)) File.Delete(archivePath);
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }

    private static async Task WriteArchiveAsync(
        Stream input,
        string outputPath,
        RuntimeArchiveDescriptor descriptor,
        IProgress<RuntimeInstallProgress>? progress,
        CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true);
            long written = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
                if (read == 0) break;
                written += read;
                if (written > descriptor.ArchiveSize) throw new InvalidDataException("The runtime archive is larger than declared.");
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                progress?.Report(new(RuntimeInstallPhase.Downloading, written, descriptor.ArchiveSize));
            }
            await output.FlushAsync(ct);
            var digest = Convert.ToHexString(hash.GetHashAndReset());
            if (written != descriptor.ArchiveSize || !digest.Equals(descriptor.ArchiveSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The runtime archive failed size or SHA-256 verification.");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static IReadOnlyList<RuntimeBundleFile> ExtractVerifiedFiles(
        string archivePath,
        string staging,
        RuntimeArchiveDescriptor descriptor,
        IProgress<RuntimeInstallProgress>? progress,
        CancellationToken ct)
    {
        var files = new List<RuntimeBundleFile>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        long extracted = 0;
        using var reader = ReaderFactory.OpenReader(archivePath);
        while (reader.MoveToNextEntry())
        {
            ct.ThrowIfCancellationRequested();
            var entry = reader.Entry;
            var relative = (entry.Key ?? "").Replace('\\', '/');
            while (relative.StartsWith("./", StringComparison.Ordinal)) relative = relative[2..];
            if (relative.Length == 0 || entry.IsDirectory) continue;
            if (!RuntimeBundleManifest.IsSafeRelativePath(relative) || !names.Add(relative))
                throw new InvalidDataException($"Unsafe or duplicate runtime archive path: {entry.Key}");
            if (entry.LinkTarget is not null)
                throw new InvalidDataException($"Runtime archive links are not allowed: {relative}");
            if (entry.IsEncrypted || entry.Size <= 0)
                throw new InvalidDataException($"Invalid runtime archive entry: {relative}");
            extracted = checked(extracted + entry.Size);
            if (extracted > descriptor.MaximumExtractedSize)
                throw new InvalidDataException("The runtime archive exceeds its extracted-size limit.");

            var outputPath = Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            using var input = reader.OpenEntryStream();
            using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
            try
            {
                long written = 0;
                while (true)
                {
                    var read = input.Read(buffer, 0, buffer.Length);
                    if (read == 0) break;
                    written += read;
                    if (written > entry.Size) throw new InvalidDataException($"Runtime entry is larger than declared: {relative}");
                    hash.AppendData(buffer, 0, read);
                    output.Write(buffer, 0, read);
                    progress?.Report(new(RuntimeInstallPhase.Extracting, extracted - entry.Size + written, descriptor.MaximumExtractedSize));
                }
                if (written != entry.Size) throw new InvalidDataException($"Runtime entry is truncated: {relative}");
                files.Add(new RuntimeBundleFile(
                    relative,
                    written,
                    Convert.ToHexString(hash.GetHashAndReset()),
                    descriptor.License,
                    descriptor.SourceUrl));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        return files;
    }

    private static void RequireRuntimeFiles(IReadOnlyList<RuntimeBundleFile> files)
    {
        var paths = files.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
        foreach (var required in new[]
        {
            "lib/server/libjvm.so",
            "lib/modules",
            "release",
            "legal/java.base/LICENSE",
            "legal/java.base/ASSEMBLY_EXCEPTION"
        })
            if (!paths.Contains(required)) throw new InvalidDataException($"The runtime archive is missing {required}.");
    }
}
