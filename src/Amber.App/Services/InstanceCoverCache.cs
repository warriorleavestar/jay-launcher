using System.Security.Cryptography;
using Amber.Core.Net;
using Avalonia.Media.Imaging;

namespace Amber.App.Services;

/// <summary>Small, persistent cache for modpack artwork shown by the game library.</summary>
internal static class InstanceCoverCache
{
    internal const int MaxBytes = 8 * 1024 * 1024;

    internal static string PathFor(string url)
    {
        var name = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(url)));
        return Path.Combine(AppSettings.DataDir, "covers", name + ".image");
    }

    internal static async Task<Bitmap?> LoadAsync(HttpClient http, string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return null;
        var path = PathFor(uri.AbsoluteUri);
        if (File.Exists(path)) return await DecodeAsync(path, ct);

        byte[] bytes;
        try
        {
            using var scope = NetworkPolicy.Scope(NetworkCategory.ContentApi);
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaxBytes) return null;
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            using var memory = new MemoryStream();
            var buffer = new byte[64 * 1024];
            while (true)
            {
                var read = await source.ReadAsync(buffer, ct);
                if (read == 0) break;
                if (memory.Length + read > MaxBytes) return null;
                await memory.WriteAsync(buffer.AsMemory(0, read), ct);
            }
            bytes = memory.ToArray();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            return null;
        }

        // Decode before committing the cache: an HTML error page or malformed image must never become the cover.
        var bitmap = await DecodeAsync(bytes, ct);
        if (bitmap is null) return null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            await File.WriteAllBytesAsync(temp, bytes, ct);
            File.Move(temp, path, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A read-only cache still shows the image for this run.
        }
        return bitmap;
    }

    private static Task<Bitmap?> DecodeAsync(string path, CancellationToken ct) => Task.Run(() =>
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Bitmap.DecodeToWidth(stream, 512);
        }
        catch (Exception e) when (e is IOException or ArgumentException or InvalidOperationException or NotSupportedException or NullReferenceException)
        {
            try { File.Delete(path); } catch { }
            return null;
        }
    }, ct);

    private static Task<Bitmap?> DecodeAsync(byte[] bytes, CancellationToken ct) => Task.Run(() =>
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            return Bitmap.DecodeToWidth(stream, 512);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or NotSupportedException or NullReferenceException)
        {
            return null;
        }
    }, ct);
}
