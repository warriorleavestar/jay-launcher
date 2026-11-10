namespace Amber.Core;

/// <summary>
/// Writes files that hold secrets (keys, tokens, password hashes) so they are never readable by other users, not even
/// for a moment: on macOS and Linux the file is created 0600 from the start instead of being written with the default
/// mode and tightened afterwards. Writing goes to a temporary file that then replaces the target, so a crash leaves
/// either the old file or the new one.
/// </summary>
public static class PrivateFile
{
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode OwnerOnlyDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    public static void WriteAllText(string path, string text)
    {
        var full = Path.GetFullPath(path);
        EnsureDirectory(Path.GetDirectoryName(full)!);
        var temp = full + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = OwnerOnly;
        try
        {
            using (var stream = new FileStream(temp, options))
            using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
                writer.Write(text);
            File.Move(temp, full, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>Creates the directory owner-only (0700) on macOS and Linux when it doesn't exist yet. Existing directories are left as they are.</summary>
    public static void EnsureDirectory(string dir)
    {
        if (Directory.Exists(dir)) return;
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(dir);
        else Directory.CreateDirectory(dir, OwnerOnlyDirectory);
    }
}
