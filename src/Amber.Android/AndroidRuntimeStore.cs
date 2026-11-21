using Android.Content;
using Amber.Core.Net;
using Amber.Mobile;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Amber.Android;

/// <summary>
/// Owns Jay's in-app Android runtime. Runtime components are kept separate from the APK so
/// their licenses, sources and hashes can be shown before the user-initiated download.
/// </summary>
internal static class AndroidRuntimeStore
{
    private const string ManifestName = "runtime-manifest.json";
    private static readonly HttpClient RuntimeClient = CreateRuntimeClient();

    public static string Root(Context context, bool java25 = false) =>
        Path.Combine(context.FilesDir!.AbsolutePath, java25 ? "game-runtime-25" : "game-runtime");

    public static bool RequiresJava25(Context context, string versionId)
    {
        var directory = new Amber.Core.Game.GameDirectory(Path.Combine(context.FilesDir!.AbsolutePath, "minecraft"));
        return File.Exists(directory.VersionJsonPath(versionId)) &&
               AndroidRuntimeChoice.UsesJava25(directory.ResolveVersion(versionId).JavaVersion?.MajorVersion ?? 8);
    }

    public static RuntimeArchiveDescriptor? Descriptor(Context context, bool java25)
    {
        if (!java25) return AndroidRuntimeCatalog.ForCurrentProcess();
        var assets = Java25Assets(context);
        if (assets is null) return null;
        using var metadata = context.Assets!.Open(assets.DescriptorAsset);
        var descriptor = JsonSerializer.Deserialize(metadata, AndroidDescriptorContext.Default.RuntimeArchiveDescriptor);
        return descriptor is not null && descriptor.Architecture == CurrentArchitecture() && descriptor.Validate().Count == 0
            ? descriptor
            : null;
    }

    public static bool IsInstalled(Context context, bool java25 = false)
    {
        var root = Root(context, java25);
        var manifestPath = Path.Combine(root, ManifestName);
        if (!File.Exists(manifestPath)) return false;
        try
        {
            var manifest = RuntimeBundleManifest.Read(manifestPath);
            var java25Assets = java25 ? Java25Assets(context) : null;
            var descriptor = Descriptor(context, java25);
            // Installation already verifies every SHA-256 before activation. Startup only checks
            // the immutable app-private layout so opening the game never hashes the full JRE on
            // Android's UI thread. PR/debug APKs intentionally omit the Java 25 download catalog;
            // that must not invalidate a runtime which a previous signed build already installed.
            // If this APK does carry a catalog, keep enforcing its exact runtime generation.
            var catalogPresent = !java25 || java25Assets is not null;
            var accepted = AndroidRuntimeAssetCatalog.AcceptsInstalledRuntime(
                manifest.Id, descriptor?.Id, catalogPresent);
            var layoutValid = RuntimeBundleVerifier.VerifyLayout(root, manifest, CurrentArchitecture());
            return accepted && layoutValid;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or CryptographicException)
        {
            return false;
        }
    }

    public static async Task<RuntimeBundleManifest> InstallAsync(
        Context context,
        RuntimeArchiveDescriptor descriptor,
        bool java25,
        IProgress<RuntimeInstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        HttpResponseMessage? response = null;
        try
        {
            var manifest = await RuntimeArchiveInstaller.InstallAsync(
                Root(context, java25),
                descriptor,
                async (archive, token) =>
                {
                    var embeddedArchive = java25 ? Java25Assets(context)?.ArchiveAsset : null;
                    if (embeddedArchive is not null) return context.Assets!.Open(embeddedArchive);
                    response = await RuntimeClient.GetAsync(archive.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token);
                    response.EnsureSuccessStatusCode();
                    if (response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps)
                        throw new HttpRequestException("The Android runtime download was redirected away from HTTPS.");
                    if (response.Content.Headers.ContentLength is { } length && length != archive.ArchiveSize)
                        throw new InvalidDataException("The Android runtime server returned an unexpected file size.");
                    return await response.Content.ReadAsStreamAsync(token);
                },
                progress,
                ct);
            MakeRuntimeCodeReadOnly(context, manifest, java25);
            return manifest;
        }
        finally
        {
            response?.Dispose();
        }
    }

    private static string CurrentArchitecture() => RuntimeInformation.ProcessArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.Arm64 => "arm64-v8a",
        System.Runtime.InteropServices.Architecture.X64 => "x86_64",
        _ => "unsupported"
    };

    private static AndroidRuntimeAssets? Java25Assets(Context context) =>
        AndroidRuntimeAssetCatalog.SelectJava25(
            CurrentArchitecture(),
            Environment.SystemPageSize,
            context.Assets?.List("") ?? []);

    private static HttpClient CreateRuntimeClient() =>
        Downloader.CreateHttpClient(category: NetworkCategory.Download);

    private static void MakeRuntimeCodeReadOnly(Context context, RuntimeBundleManifest manifest, bool java25)
    {
        var root = Root(context, java25);
        foreach (var file in manifest.Files)
        {
            var mode = UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
            if (file.Path.StartsWith("bin/", StringComparison.Ordinal) || file.Path == "lib/jspawnhelper")
                mode |= UnixFileMode.UserExecute;
            File.SetUnixFileMode(Path.Combine(root, file.Path), mode);
        }
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(RuntimeArchiveDescriptor))]
internal sealed partial class AndroidDescriptorContext : System.Text.Json.Serialization.JsonSerializerContext;
