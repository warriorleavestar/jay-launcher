namespace Amber.Mobile;

internal sealed record AndroidRuntimeAssets(string DescriptorAsset, string? ArchiveAsset);

/// <summary>Selects ABI-specific Java 25 metadata while keeping the old ARM64/4K bundle isolated.</summary>
internal static class AndroidRuntimeAssetCatalog
{
    /// <summary>
    /// An already verified app-private runtime remains usable when this APK intentionally omits the downloadable
    /// Java 25 catalog (for example a PR/debug build). When a catalog is present, its ID still pins the exact
    /// runtime generation and a missing, malformed or mismatched descriptor must force reinstallation.
    /// </summary>
    public static bool AcceptsInstalledRuntime(string manifestId, string? descriptorId, bool catalogPresent) =>
        !string.IsNullOrWhiteSpace(manifestId) &&
        (!catalogPresent || descriptorId is not null && string.Equals(manifestId, descriptorId, StringComparison.Ordinal));

    public static AndroidRuntimeAssets? SelectJava25(
        string architecture,
        int pageSize,
        IReadOnlyCollection<string> rootAssets)
    {
        if (architecture is not ("arm64-v8a" or "x86_64")) return null;

        var descriptor = $"runtime25-{architecture}.json";
        if (rootAssets.Contains(descriptor, StringComparer.Ordinal))
        {
            var archive = $"jay-android-runtime25-{architecture}.tar.xz";
            return new(descriptor,
                rootAssets.Contains(archive, StringComparer.Ordinal) ? archive : null);
        }

        // The original local experiment was ARM64-only and contained 4 KiB-aligned code.
        // Never expose it to x86_64 or a 16 KiB-page device.
        const string legacyDescriptor = "jre25.json";
        const string legacyArchive = "jre25-arm64-v8a.tar.xz";
        if (architecture == "arm64-v8a" && pageSize <= 4096 &&
            rootAssets.Contains(legacyDescriptor, StringComparer.Ordinal) &&
            rootAssets.Contains(legacyArchive, StringComparer.Ordinal))
            return new(legacyDescriptor, legacyArchive);

        return null;
    }
}
