namespace Amber.Core.Multiplayer;

/// <summary>
/// Reading and replacing the small JSON lists people build up over time (contacts, trusted members), so that a moment
/// when the file can't be read never ends with the list written over as empty.
/// </summary>
internal static class ListFile
{
    private const int ReadAttempts = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// The file's text, or null when there is no file. A file another program holds for a moment (a virus scanner, a
    /// backup tool, another Jay replacing it) is tried again briefly; when it stays unreadable this throws
    /// <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/>.
    /// </summary>
    public static string? Read(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { return File.Exists(path) ? File.ReadAllText(path) : null; }
            catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && e is not FileNotFoundException && attempt < ReadAttempts)
            {
                Thread.Sleep(RetryDelay);
            }
            catch (FileNotFoundException) { return null; }
        }
    }

    /// <summary>Keeps a copy of a file that could not be understood (as name.broken) before it is replaced.</summary>
    public static void KeepBroken(string path)
    {
        try
        {
            if (File.Exists(path)) File.Copy(path, path + ".broken", overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
