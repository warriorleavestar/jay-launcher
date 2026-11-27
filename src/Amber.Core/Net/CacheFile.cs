namespace Amber.Core.Net;

/// <summary>
/// Saves a downloaded file into a cache under a fixed name, safely when several callers fetch the same file at once.
/// </summary>
internal static class CacheFile
{
    /// <summary>
    /// Writes <paramref name="bytes"/> to a temp file next to <paramref name="target"/> and moves it into place without
    /// replacing anything. When another caller got there first and its file passes <paramref name="accept"/>, that file
    /// is kept: replacing a file that someone else is reading or moving fails on Windows. Only a file that does not pass
    /// is replaced. Files longer than <paramref name="maxBytes"/> never pass. The temp file is always removed.
    /// </summary>
    /// <exception cref="IOException">The file could not be put in place.</exception>
    /// <exception cref="UnauthorizedAccessException">The file could not be put in place.</exception>
    public static async Task SaveAsync(string target, byte[] bytes, Func<byte[], bool> accept, long maxBytes, CancellationToken ct = default)
    {
        var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temp, bytes, ct);
            try
            {
                File.Move(temp, target, overwrite: false);
                return;
            }
            catch (IOException)
            {
                // Usually the target already exists because a concurrent caller saved it first.
                if (Accepts(target, accept, maxBytes)) return;
            }
            File.Move(temp, target, overwrite: true);
        }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static bool Accepts(string path, Func<byte[], bool> accept, long maxBytes)
    {
        try
        {
            // Let others read, replace or delete it meanwhile: this check must not get in anyone's way.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > maxBytes) return false;
            using var copy = new MemoryStream((int)stream.Length);
            stream.CopyTo(copy);
            return copy.Length <= maxBytes && accept(copy.ToArray());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
