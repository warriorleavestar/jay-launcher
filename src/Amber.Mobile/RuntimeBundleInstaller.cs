using System.Buffers;
using System.Security.Cryptography;

namespace Amber.Mobile;

/// <summary>
/// Installs a runtime into a same-volume staging directory, verifies every byte and only
/// replaces the active runtime after the complete bundle is valid.
/// </summary>
public static class RuntimeBundleInstaller
{
    public static async Task InstallAsync(
        string destination,
        RuntimeBundleManifest manifest,
        string architecture,
        Func<RuntimeBundleFile, CancellationToken, Task<Stream>> openSource,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(openSource);

        var errors = manifest.Validate();
        if (errors.Count != 0) throw new InvalidDataException(string.Join(" ", errors));
        if (!manifest.MatchesArchitecture(architecture))
            throw new InvalidDataException($"Runtime architecture {manifest.Architecture} does not match {architecture}.");

        var target = Path.GetFullPath(destination);
        if (File.Exists(target)) throw new IOException("The runtime destination is a file.");
        var parent = Directory.GetParent(target)?.FullName
            ?? throw new InvalidDataException("The runtime destination has no parent directory.");
        Directory.CreateDirectory(parent);

        var suffix = Guid.NewGuid().ToString("N");
        var staging = target + ".install-" + suffix;
        var backup = target + ".backup-" + suffix;
        Directory.CreateDirectory(staging);

        try
        {
            foreach (var file in manifest.Files)
            {
                ct.ThrowIfCancellationRequested();
                var outputPath = Path.Combine(staging, file.Path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                await using var input = await openSource(file, ct);
                await WriteVerifiedAsync(input, outputPath, file, ct);
            }

            manifest.Write(Path.Combine(staging, "runtime-manifest.json"));
            if (!await RuntimeBundleVerifier.VerifyAsync(staging, manifest, architecture, ct))
                throw new InvalidDataException("The staged Android runtime did not pass final verification.");

            ActivateStaged(target, staging, backup);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }

    internal static void ActivateStaged(string target, string staging, string backup)
    {
        if (Directory.Exists(target)) Directory.Move(target, backup);
        try
        {
            Directory.Move(staging, target);
        }
        catch
        {
            if (!Directory.Exists(target) && Directory.Exists(backup)) Directory.Move(backup, target);
            throw;
        }

        if (Directory.Exists(backup)) Directory.Delete(backup, true);
    }

    private static async Task WriteVerifiedAsync(
        Stream input,
        string outputPath,
        RuntimeBundleFile file,
        CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var output = new FileStream(
                outputPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 128 * 1024,
                useAsync: true);
            long written = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
                if (read == 0) break;
                written += read;
                if (written > file.Size) throw new InvalidDataException($"Runtime file is larger than declared: {file.Path}");
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
            }
            await output.FlushAsync(ct);

            var digest = Convert.ToHexString(hash.GetHashAndReset());
            if (written != file.Size || !digest.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Runtime file verification failed: {file.Path}");
        }
        catch
        {
            File.Delete(outputPath);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
