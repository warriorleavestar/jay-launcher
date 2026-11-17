using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Amber.Core;

/// <summary>
/// Writes files that hold secrets (keys, tokens, password hashes) so they are never readable by other users, not even
/// for a moment: on macOS and Linux the file is created 0600 from the start instead of being written with the default
/// mode and tightened afterwards; on Windows it is created with its own protected ACL (this user, SYSTEM and
/// Administrators, the same people who can read a 0600 file elsewhere) instead of inheriting the folder's, which may let
/// every user read it. Writing goes to a temporary file that then replaces the target, so a crash leaves either the old
/// file or the new one.
/// </summary>
public static class PrivateFile
{
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode OwnerOnlyDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const string LocalSystem = "S-1-5-18";
    private const string Administrators = "S-1-5-32-544";

    public static void WriteAllText(string path, string text) =>
        Write(path, stream =>
        {
            using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false), leaveOpen: true);
            writer.Write(text);
        });

    /// <summary>Like <see cref="WriteAllText"/>, for binary secrets.</summary>
    public static void WriteAllBytes(string path, byte[] bytes) => Write(path, stream => stream.Write(bytes));

    private static void Write(string path, Action<FileStream> write)
    {
        var full = Path.GetFullPath(path);
        EnsureDirectory(Path.GetDirectoryName(full)!);
        var temp = full + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            using (var stream = CreateNew(temp))
            {
                write(stream);
                stream.Flush(true);
            }
            File.Move(temp, full, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    private static FileStream CreateNew(string path)
    {
        if (OperatingSystem.IsWindows())
            return new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.Write | FileSystemRights.Synchronize, FileShare.None, 4096, FileOptions.None, LockedFile());
        return new FileStream(path, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, UnixCreateMode = OwnerOnly });
    }

    /// <summary>
    /// Creates the directory owner-only when it doesn't exist yet: 0700 on macOS and Linux, a protected ACL for this user,
    /// SYSTEM and Administrators on Windows. Existing directories are left as they are.
    /// </summary>
    public static void EnsureDirectory(string dir)
    {
        if (Directory.Exists(dir)) return;
        if (OperatingSystem.IsWindows()) new DirectoryInfo(dir).Create(LockedDirectory());
        else Directory.CreateDirectory(dir, OwnerOnlyDirectory);
    }

    /// <summary>The SIDs a private file or folder grants full control to on Windows.</summary>
    [SupportedOSPlatform("windows")]
    internal static SecurityIdentifier[] Owners()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return [identity.User!, new SecurityIdentifier(LocalSystem), new SecurityIdentifier(Administrators)];
    }

    [SupportedOSPlatform("windows")]
    private static FileSecurity LockedFile()
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in Owners()) security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        return security;
    }

    [SupportedOSPlatform("windows")]
    private static DirectorySecurity LockedDirectory()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in Owners())
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }
}
