using System.Diagnostics;

namespace Amber.App.Services;

/// <summary>
/// Opens a folder, file or URL with the system's default app. Fails softly: on Linux without a desktop opener, for a
/// file that was just deleted, or a folder that can't be created, the caller gets the error to show instead of an
/// exception on the UI thread (which would close Jay).
/// </summary>
public static class ShellOpen
{
    /// <summary>Test hook: replaces the system call.</summary>
    internal static Action<ProcessStartInfo> Start { get; set; } = info => Process.Start(info)?.Dispose();

    /// <summary>Opens <paramref name="path"/>; a folder that doesn't exist yet is created first. Returns the error, or null.</summary>
    public static Exception? TryOpen(string path, bool createFolder = true)
    {
        try
        {
            // A file (a crash report, a screenshot) is opened as it is; only a missing folder is created.
            if (createFolder && !File.Exists(path)) Directory.CreateDirectory(path);
            Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception
                                       or InvalidOperationException or PlatformNotSupportedException)
        {
            return e;
        }
    }
}
