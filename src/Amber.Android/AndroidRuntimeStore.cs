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
        return File.Exists(directory.VersionJsonPath(versionId)) && directory.ResolveVersion(versionId).JavaVersion?.MajorVersion >= 25;
    }

    public static RuntimeArchiveDescriptor? Descriptor(Context context, bool java25)
    {
        if (!java25) return AndroidRuntimeCatalog.ForCurrentProcess();
        if (RuntimeInformation.ProcessArchitecture != Architecture.Arm64 || Environment.SystemPageSize > 4096) return null;
        if (context.Assets?.List("")?.Contains("jre25.json") != true) return null;
        using var metadata = context.Assets.Open("jre25.json");
        return JsonSerializer.Deserialize(metadata, AndroidDescriptorContext.Default.RuntimeArchiveDescriptor);
    }

    public static bool IsInstalled(Context context, bool java25 = false)
    {
        var root = Root(context, java25);
        var manifestPath = Path.Combine(root, ManifestName);
        if (!File.Exists(manifestPath)) return false;
        try
        {
            var manifest = RuntimeBundleManifest.Read(manifestPath);
            var descriptor = Descriptor(context, java25);
            // Installation already verifies every SHA-256 before activation. Startup only checks
            // the immutable app-private layout so opening the game never hashes the full JRE on
            // Android's UI thread.
            return descriptor is not null &&
                   string.Equals(manifest.Id, descriptor.Id, StringComparison.Ordinal) &&
                   RuntimeBundleVerifier.VerifyLayout(root, manifest, CurrentArchitecture());
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
                    if (java25) return context.Assets!.Open("jre25-arm64-v8a.tar.xz");
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
