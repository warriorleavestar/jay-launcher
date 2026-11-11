using Android.Content;
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

    public static string Root(Context context) => Path.Combine(context.FilesDir!.AbsolutePath, "game-runtime");

    public static bool IsInstalled(Context context)
    {
        var root = Root(context);
        var manifestPath = Path.Combine(root, ManifestName);
        if (!File.Exists(manifestPath)) return false;
        try
        {
            var manifest = RuntimeBundleManifest.Read(manifestPath);
            return RuntimeBundleVerifier.VerifyAsync(root, manifest, CurrentArchitecture()).GetAwaiter().GetResult();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or CryptographicException)
        {
            return false;
        }
    }

    public static async Task<RuntimeBundleManifest> InstallAsync(
        Context context,
        RuntimeArchiveDescriptor descriptor,
        IProgress<RuntimeInstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        HttpResponseMessage? response = null;
        try
        {
            var manifest = await RuntimeArchiveInstaller.InstallAsync(
                Root(context),
                descriptor,
                async (archive, token) =>
                {
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
            MakeRuntimeCodeReadOnly(context, manifest);
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

    private static HttpClient CreateRuntimeClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Jay-Android/1.0");
        return client;
    }

    private static void MakeRuntimeCodeReadOnly(Context context, RuntimeBundleManifest manifest)
    {
        var root = Root(context);
        foreach (var file in manifest.Files)
        {
            var mode = UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
            if (file.Path.StartsWith("bin/", StringComparison.Ordinal) || file.Path == "lib/jspawnhelper")
                mode |= UnixFileMode.UserExecute;
            File.SetUnixFileMode(Path.Combine(root, file.Path), mode);
        }
    }
}
