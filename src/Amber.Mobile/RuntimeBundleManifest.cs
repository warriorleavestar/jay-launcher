using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Amber.Mobile;

public sealed record RuntimeBundleFile(
    string Path,
    long Size,
    string Sha256,
    string License,
    string SourceUrl);

public sealed record RuntimeBundleManifest(
    int Format,
    string Id,
    string Architecture,
    IReadOnlyList<RuntimeBundleFile> Files)
{
    public const int CurrentFormat = 1;

    public static RuntimeBundleManifest Read(string path) =>
        JsonSerializer.Deserialize(File.ReadAllText(path), MobileRuntimeJsonContext.Default.RuntimeBundleManifest)
        ?? throw new InvalidDataException("The Android runtime manifest is empty.");

    public void Write(string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(this, MobileRuntimeJsonContext.Default.RuntimeBundleManifest));

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (Format != CurrentFormat) errors.Add($"Unsupported runtime manifest format {Format}.");
        if (string.IsNullOrWhiteSpace(Id)) errors.Add("The runtime ID is missing.");
        if (Architecture is not ("arm64-v8a" or "x86_64")) errors.Add("The runtime architecture is not supported.");
        if (Files.Count == 0) errors.Add("The runtime manifest has no files.");

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Files)
        {
            if (!IsSafeRelativePath(file.Path)) errors.Add($"Unsafe runtime path: {file.Path}");
            else if (!names.Add(file.Path)) errors.Add($"Duplicate runtime path: {file.Path}");
            if (file.Size <= 0) errors.Add($"Invalid size for {file.Path}.");
            if (file.Sha256.Length != 64 || file.Sha256.Any(c => !Uri.IsHexDigit(c)))
                errors.Add($"Invalid SHA-256 for {file.Path}.");
            if (string.IsNullOrWhiteSpace(file.License)) errors.Add($"Missing license for {file.Path}.");
            if (!Uri.TryCreate(file.SourceUrl, UriKind.Absolute, out var source) || source.Scheme != Uri.UriSchemeHttps)
                errors.Add($"Invalid source URL for {file.Path}.");
        }
        return errors;
    }

    public bool MatchesArchitecture(string architecture) =>
        string.Equals(Architecture, architecture, StringComparison.Ordinal);

    internal static bool IsSafeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path)) return false;
        var normalized = path.Replace('\\', '/');
        return normalized.Split('/').All(part => part.Length > 0 && part is not "." and not "..");
    }
}

[JsonSerializable(typeof(RuntimeBundleManifest))]
internal sealed partial class MobileRuntimeJsonContext : JsonSerializerContext;

public static class RuntimeBundleVerifier
{
    public static async Task<bool> VerifyFileAsync(string root, RuntimeBundleFile file, CancellationToken ct = default)
    {
        if (!RuntimeBundleManifest.IsSafeRelativePath(file.Path)) return false;
        var fullRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, file.Path));
        if (!path.StartsWith(fullRoot, StringComparison.Ordinal) || !File.Exists(path)) return false;
        var info = new FileInfo(path);
        if (info.Length != file.Size) return false;
        await using var stream = File.OpenRead(path);
        var digest = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
        return digest.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<bool> VerifyAsync(
        string root,
        RuntimeBundleManifest manifest,
        string architecture,
        CancellationToken ct = default)
    {
        if (manifest.Validate().Count != 0 || !manifest.MatchesArchitecture(architecture)) return false;
        foreach (var file in manifest.Files)
            if (!await VerifyFileAsync(root, file, ct)) return false;
        return true;
    }
}
