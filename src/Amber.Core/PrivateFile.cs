using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Amber.Core;

/// <summary>
/// Writes files that hold secrets (keys, tokens, password hashes) so they are never readable by other users, not even
/// for a moment: on macOS and Linux the file is created 0600 from the start instead of being written with the default
/// mode and tightened afterwards; on Windows it is created with its own access list (this user, SYSTEM and
/// Administrators only, nothing inherited from the folder). Writing goes to a temporary file that then replaces the
/// target, so a crash leaves either the old file or the new one.
/// </summary>
public static class PrivateFile
{
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode OwnerOnlyDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    internal const string LocalSystem = "S-1-5-18";
    internal const string Administrators = "S-1-5-32-544";

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
            // A move on the same volume keeps the temporary file's own access list (Windows) and mode (macOS/Linux).
            File.Move(temp, full, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>
    /// Creates the directory owner-only when it doesn't exist yet: 0700 on macOS and Linux, and so every missing folder
    /// above it (the mode given to <see cref="Directory.CreateDirectory(string, UnixFileMode)"/> covers only the last one);
    /// on Windows an access list for this user, SYSTEM and Administrators that doesn't inherit from the parent.
    /// Existing directories are left as they are.
    /// </summary>
    public static void EnsureDirectory(string dir)
    {
        if (Directory.Exists(dir)) return;
        if (OperatingSystem.IsWindows())
        {
            var full = Path.GetFullPath(dir);
            if (Path.GetDirectoryName(full) is { } parent) Directory.CreateDirectory(parent);
            new DirectoryInfo(full).Create(LockedDirectory());
            return;
        }
        var unixParent = Path.GetDirectoryName(Path.GetFullPath(dir));
        if (unixParent is not null) EnsureDirectory(unixParent);
        Directory.CreateDirectory(dir, OwnerOnlyDirectory);
    }

    /// <summary>A new file (it must not exist yet) that only this user can read, opened for writing.</summary>
    private static FileStream CreateNew(string path)
    {
        if (OperatingSystem.IsWindows())
            return new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.Write | FileSystemRights.Read, FileShare.None, 4096, FileOptions.None, LockedFile());
        return new FileStream(path, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, UnixCreateMode = OwnerOnly });
    }

    /// <summary>The SIDs a private file or folder grants full control to on Windows.</summary>
    [SupportedOSPlatform("windows")]
    internal static SecurityIdentifier[] Owners()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return [identity.User!, new SecurityIdentifier(LocalSystem), new SecurityIdentifier(Administrators)];
    }

    /// <summary>The access list for a secret file on Windows: full control for this user, SYSTEM and Administrators; no inherited rules.</summary>
    [SupportedOSPlatform("windows")]
    internal static FileSecurity LockedFile()
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in Owners())
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        return security;
    }

    /// <summary>The same for a folder, passed on to what is created in it.</summary>
    [SupportedOSPlatform("windows")]
    internal static DirectorySecurity LockedDirectory()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags both = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var sid in Owners())
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, both, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }
}
