using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Amber.Core.Launch;

public static partial class ContainedFiles
{
    /// <summary>
    /// The bytes of a small file the game writes (a status file in its world, say), or null when it is missing, larger
    /// than <paramref name="maxBytes"/>, or not an ordinary file. Never waits: a FIFO or a socket put there is refused at
    /// once, and on Linux and macOS the last name is not followed when it is a link.
    /// </summary>
    public static byte[]? ReadSmall(string path, int maxBytes)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return ReadCapped(handle, maxBytes);
            }
            return Unix.ReadSmall(path, maxBytes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException) { return null; }
    }

    /// <summary>Positioned reads only: a handle that cannot seek fails at once instead of waiting for data.</summary>
    private static byte[]? ReadCapped(SafeFileHandle handle, int maxBytes)
    {
        if (RandomAccess.GetLength(handle) > maxBytes) return null;
        var buffer = new byte[maxBytes + 1];
        var read = 0;
        int n;
        while (read < buffer.Length && (n = RandomAccess.Read(handle, buffer.AsSpan(read), read)) > 0) read += n;
        // It grew while being read.
        return read > maxBytes ? null : buffer[..read];
    }

    private static partial class Unix
    {
        public static byte[]? ReadSmall(string path, int maxBytes)
        {
            var fd = OpenAt(AtCwd, path, ReadOnly | NoFollow | NonBlocking | CloseOnExec);
            if (fd < 0) return null;
            using var handle = Wrap(fd);
            return ReadCapped(handle, maxBytes);
        }
    }
}
