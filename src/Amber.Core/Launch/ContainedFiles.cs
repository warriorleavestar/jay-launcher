using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Amber.Core.Localization;
using Microsoft.Win32.SafeHandles;

namespace Amber.Core.Launch;

/// <summary>
/// Folders and small files inside an instance that Jay itself writes before a launch, where the game (which can write
/// its own instance) may have left a link. Every level below the trusted root is opened without following links and
/// kept open until the work is done, so a link cannot be put in between the check and the write:
/// <list type="bullet">
/// <item>Windows: each level is opened as itself (not what it points to), refused when it is a reparse point
/// (symbolic link, junction), and held without delete sharing, so it cannot be renamed or replaced meanwhile.</item>
/// <item>Linux and macOS: each level is opened with <c>openat(…, O_NOFOLLOW)</c> on the level above; a file is
/// written to a new file in the root and moved into place with <c>renameat</c>, which replaces a link instead of
/// writing through it.</item>
/// </list>
/// A link anywhere below the root is an <see cref="IOException"/>; nothing outside the root is read or written.
/// </summary>
public static partial class ContainedFiles
{
    /// <summary>The largest file <see cref="UpdateText"/> reads (config files are a few KB).</summary>
    public const int MaxTextBytes = 4 * 1024 * 1024;

    /// <summary>Creates <paramref name="folders"/> under <paramref name="root"/>, one level at a time.</summary>
    public static void CreateDirectory(string root, IReadOnlyList<string> folders)
    {
        using var chain = Chain.Open(root, folders, create: true);
    }

    /// <summary>True when one of the existing levels of <paramref name="folders"/> under <paramref name="root"/> is a link.</summary>
    public static bool HasLink(string root, IReadOnlyList<string> folders)
    {
        var path = root;
        foreach (var name in folders)
        {
            CheckName(name);
            path = Path.Combine(path, name);
            var info = new DirectoryInfo(path);
            if (info.LinkTarget is not null || info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0) return true;
            if (!info.Exists) return false;
        }
        return false;
    }

    /// <summary>
    /// Reads <paramref name="file"/> in <paramref name="folders"/> under <paramref name="root"/> ("" when it does not
    /// exist), and writes <paramref name="update"/>'s result back when it differs (UTF-8, no BOM).
    /// </summary>
    public static void UpdateText(string root, IReadOnlyList<string> folders, string file, Func<string, string> update)
    {
        CheckName(file);
        using var chain = Chain.Open(root, folders, create: true);
        if (OperatingSystem.IsWindows()) Windows.UpdateText(chain, file, update);
        else Unix.UpdateText(chain, root, file, update);
    }

    private static void CheckName(string name)
    {
        if (name.Length == 0 || name is "." or ".." || name.IndexOfAny(['/', '\\', '\0']) >= 0 || Path.IsPathRooted(name))
            throw new ArgumentException("Not a single folder or file name.", nameof(name));
    }

    internal static IOException Blocked(string path) => new(L.F("core.launch.link_blocked", path));

    private static string Decode(byte[] bytes, int length)
    {
        var text = new UTF8Encoding(false).GetString(bytes, 0, length);
        return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
    }

    private static byte[] ReadAll(Stream stream, string path)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int n;
        while ((n = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + n > MaxTextBytes) throw new IOException(L.F("core.launch.file_too_big", path));
            buffer.Write(chunk, 0, n);
        }
        return buffer.ToArray();
    }

    /// <summary>The open levels from the root down; the last one is where the file goes.</summary>
    private sealed class Chain : IDisposable
    {
        public readonly List<SafeHandle> Handles = [];
        public string Path = "";

        public SafeHandle Last => Handles[^1];

        public static Chain Open(string root, IReadOnlyList<string> folders, bool create)
        {
            var chain = new Chain { Path = System.IO.Path.GetFullPath(root) };
            try
            {
                chain.Handles.Add(OperatingSystem.IsWindows() ? Windows.OpenRoot(chain.Path) : Unix.OpenRoot(chain.Path));
                foreach (var name in folders)
                {
                    CheckName(name);
                    var path = System.IO.Path.Combine(chain.Path, name);
                    chain.Handles.Add(OperatingSystem.IsWindows() ? Windows.OpenLevel(path, create) : Unix.OpenLevel(chain.Last, name, path, create));
                    chain.Path = path;
                }
                return chain;
            }
            catch
            {
                chain.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            for (var i = Handles.Count - 1; i >= 0; i--) Handles[i].Dispose();
            Handles.Clear();
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static partial class Windows
    {
        private const uint FileReadAttributes = 0x80, GenericRead = 0x80000000, GenericWrite = 0x40000000;
        private const uint ShareRead = 1, ShareWrite = 2;
        private const uint OpenExisting = 3, OpenAlways = 4;
        private const uint BackupSemantics = 0x02000000, OpenReparsePoint = 0x00200000;
        private const uint AttributeDirectory = 0x10, AttributeReparsePoint = 0x400;
        private const int ErrorFileNotFound = 2, ErrorPathNotFound = 3;

        [StructLayout(LayoutKind.Sequential)]
        private struct AttributeTagInfo
        {
            public uint FileAttributes;
            public uint ReparseTag;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct StandardInfo
        {
            public long AllocationSize;
            public long EndOfFile;
            public uint NumberOfLinks;
            public byte DeletePending;
            public byte Directory;
        }

        [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        private static partial SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetFileInformationByHandleEx(SafeHandle file, int infoClass, out AttributeTagInfo info, uint size);

        [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetStandardInfo(SafeHandle file, int infoClass, out StandardInfo info, uint size);

        private const int FileStandardInfoClass = 1, FileAttributeTagInfoClass = 9;

        // Not shared for delete: while Jay holds a level, it cannot be renamed, removed or replaced by a link.
        private static SafeFileHandle Open(string path, uint access, uint disposition, uint flags) =>
            CreateFile(@"\\?\" + path, access, ShareRead | ShareWrite, 0, disposition, flags | OpenReparsePoint, 0);

        public static SafeHandle OpenRoot(string path)
        {
            // The root itself is trusted (it may be a junction the player set up); only what is below it is checked.
            var handle = CreateFile(@"\\?\" + path, FileReadAttributes, ShareRead | ShareWrite, 0, OpenExisting, BackupSemantics, 0);
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError(), path);
            return handle;
        }

        public static SafeHandle OpenLevel(string path, bool create)
        {
            var handle = Open(path, FileReadAttributes, OpenExisting, BackupSemantics);
            if (handle.IsInvalid && create && Marshal.GetLastPInvokeError() is ErrorFileNotFound or ErrorPathNotFound)
            {
                handle.Dispose();
                // The level above is held, so this creates the folder in the right place.
                Directory.CreateDirectory(path);
                handle = Open(path, FileReadAttributes, OpenExisting, BackupSemantics);
            }
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastPInvokeError();
                handle.Dispose();
                if (error is ErrorFileNotFound or ErrorPathNotFound) throw new DirectoryNotFoundException(path);
                throw new IOException(new Win32Exception(error).Message + ": " + path);
            }
            var attributes = Attributes(handle, path);
            if ((attributes & AttributeReparsePoint) != 0 || (attributes & AttributeDirectory) == 0)
            {
                handle.Dispose();
                throw Blocked(path);
            }
            return handle;
        }

        private static uint Attributes(SafeHandle handle, string path)
        {
            if (!GetFileInformationByHandleEx(handle, FileAttributeTagInfoClass, out var info, (uint)Marshal.SizeOf<AttributeTagInfo>()))
                throw new IOException(new Win32Exception(Marshal.GetLastPInvokeError()).Message + ": " + path);
            return info.FileAttributes;
        }

        public static void UpdateText(Chain chain, string file, Func<string, string> update)
        {
            var path = System.IO.Path.Combine(chain.Path, file);
            using var handle = Open(path, GenericRead | GenericWrite, OpenAlways, 0);
            if (handle.IsInvalid) throw new IOException(new Win32Exception(Marshal.GetLastPInvokeError()).Message + ": " + path);
            // Opened as itself: a link is refused, and a file with a second name elsewhere (a hard link) is not written.
            if ((Attributes(handle, path) & (AttributeReparsePoint | AttributeDirectory)) != 0) throw Blocked(path);
            if (!GetStandardInfo(handle, FileStandardInfoClass, out var standard, (uint)Marshal.SizeOf<StandardInfo>()) || standard.NumberOfLinks > 1)
                throw Blocked(path);
            using var stream = new FileStream(handle, FileAccess.ReadWrite);
            var bytes = ReadAll(stream, path);
            var text = Decode(bytes, bytes.Length);
            var updated = update(text);
            if (updated == text) return;
            stream.Position = 0;
            stream.SetLength(0);
            var output = new UTF8Encoding(false).GetBytes(updated);
            stream.Write(output, 0, output.Length);
            stream.Flush(true);
        }
    }

    private static partial class Unix
    {
        private static bool Mac => OperatingSystem.IsMacOS();
        private static bool Arm => RuntimeInformation.ProcessArchitecture is Architecture.Arm64 or Architecture.Arm;

        // Flag values differ between Linux (x64 and arm) and macOS.
        private static int Directory => Mac ? 0x100000 : Arm ? 0x4000 : 0x10000;
        private static int NoFollow => Mac ? 0x100 : Arm ? 0x8000 : 0x20000;
        private static int CloseOnExec => Mac ? 0x1000000 : 0x80000;
        private const int ReadOnly = 0;
        private static int AtCwd => Mac ? -2 : -100;
        private static int Loop => Mac ? 62 : 40;
        private const int NotFound = 2, Exists = 17, NotDirectory = 20;

        [LibraryImport("libc", EntryPoint = "openat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        private static partial int OpenAt(int dir, string path, int flags);

        [LibraryImport("libc", EntryPoint = "mkdirat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        private static partial int MkdirAt(int dir, string path, uint mode);

        [LibraryImport("libc", EntryPoint = "renameat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        private static partial int RenameAt(int fromDir, string from, int toDir, string to);

        [LibraryImport("libc", EntryPoint = "unlinkat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        private static partial int UnlinkAt(int dir, string path, int flags);

        private static int Fd(SafeHandle handle) => (int)handle.DangerousGetHandle();

        private static SafeFileHandle Wrap(int fd) => new(fd, ownsHandle: true);

        public static SafeHandle OpenRoot(string path)
        {
            var fd = OpenAt(AtCwd, path, ReadOnly | Directory | CloseOnExec);
            if (fd < 0) throw new IOException(L.F("core.launch.open_failed", path, Marshal.GetLastPInvokeError()));
            return Wrap(fd);
        }

        public static SafeHandle OpenLevel(SafeHandle parent, string name, string path, bool create)
        {
            var fd = OpenAt(Fd(parent), name, ReadOnly | Directory | NoFollow | CloseOnExec);
            if (fd < 0 && create && Marshal.GetLastPInvokeError() == NotFound)
            {
                if (MkdirAt(Fd(parent), name, 0x1ED /* 0755 */) != 0 && Marshal.GetLastPInvokeError() != Exists)
                    throw new IOException(L.F("core.launch.open_failed", path, Marshal.GetLastPInvokeError()));
                fd = OpenAt(Fd(parent), name, ReadOnly | Directory | NoFollow | CloseOnExec);
            }
            if (fd >= 0) return Wrap(fd);
            var error = Marshal.GetLastPInvokeError();
            // A link fails with ELOOP (O_NOFOLLOW); a link to a file, or a file, with ENOTDIR.
            if (error == Loop || error == NotDirectory) throw Blocked(path);
            if (error == NotFound) throw new DirectoryNotFoundException(path);
            throw new IOException(L.F("core.launch.open_failed", path, error));
        }

        public static void UpdateText(Chain chain, string root, string file, Func<string, string> update)
        {
            var path = System.IO.Path.Combine(chain.Path, file);
            var text = "";
            var fd = OpenAt(Fd(chain.Last), file, ReadOnly | NoFollow | CloseOnExec);
            if (fd < 0)
            {
                var error = Marshal.GetLastPInvokeError();
                if (error == Loop) throw Blocked(path);
                if (error != NotFound) throw new IOException(L.F("core.launch.open_failed", path, error));
            }
            else
            {
                using var stream = new FileStream(Wrap(fd), FileAccess.Read);
                var bytes = ReadAll(stream, path);
                text = Decode(bytes, bytes.Length);
            }
            var updated = update(text);
            if (updated == text) return;
            // A new file in the root (never through a link: CreateNew fails on an existing name), moved over the old
            // name with renameat on the held folder, which replaces whatever is there instead of writing through it.
            var temp = ".jay-write-" + Guid.NewGuid().ToString("N")[..12] + ".tmp";
            var tempPath = System.IO.Path.Combine(System.IO.Path.GetFullPath(root), temp);
            try
            {
                using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write))
                {
                    var output = new UTF8Encoding(false).GetBytes(updated);
                    stream.Write(output, 0, output.Length);
                    stream.Flush(true);
                }
                if (RenameAt(Fd(chain.Handles[0]), temp, Fd(chain.Last), file) != 0)
                    throw new IOException(L.F("core.launch.open_failed", path, Marshal.GetLastPInvokeError()));
            }
            finally
            {
                UnlinkAt(Fd(chain.Handles[0]), temp, 0);
            }
        }
    }
}
