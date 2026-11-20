namespace Amber.Mobile;

internal sealed record AndroidRuntimeAssets(string DescriptorAsset, string? ArchiveAsset);

/// <summary>Selects ABI-specific Java 25 metadata while keeping the old ARM64/4K bundle isolated.</summary>
internal static class AndroidRuntimeAssetCatalog
{
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
