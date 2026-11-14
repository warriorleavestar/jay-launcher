using System.IO.Compression;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

namespace Amber.Core.Multiplayer;

/// <summary>
/// The copy of easytier-core the elevated guard (administrator on Windows, root on Mac/Linux) runs. The guard never runs
/// a program from Jay's data folder, which the user's account can change. It gets the path of the pinned package, reads
/// it once into memory, checks size, SHA-256 and the release signature itself, and extracts it into a folder only
/// administrators or root can change:
/// <c>%ProgramFiles%\Jay\easytier</c>, <c>/Library/Application Support/Jay/easytier</c> or <c>/var/lib/jay/easytier</c>.
/// Later starts reuse that copy. Messages here are for developers (the guard has no window), so they are not translated.
/// </summary>
internal static class ElevatedCoreStore
{
    /// <summary>Tests: a temporary folder instead of the system one.</summary>
    internal static string? RootForTest;

    /// <summary>Tests: the package to expect instead of this system's pinned one.</summary>
    internal static EasyTierRelease.Asset? AssetForTest;

    private const UnixFileMode DirMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    private const UnixFileMode LibraryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    /// <summary>Where the protected copies live.</summary>
    internal static string Root => RootForTest ?? DefaultRoot();

    private static string DefaultRoot() =>
        OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Jay", "easytier")
        : OperatingSystem.IsMacOS() ? "/Library/Application Support/Jay/easytier"
        : "/var/lib/jay/easytier";

    /// <summary>Whether a path names a package (what Jay passes) rather than a program.</summary>
    internal static bool IsPackage(string path) => path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the protected easytier-core for the pinned build, extracting it from <paramref name="packagePath"/> first
    /// when it isn't there yet. Throws <see cref="InvalidDataException"/> when the package doesn't match.
    /// </summary>
    internal static string Prepare(string packagePath)
    {
        var asset = AssetForTest ?? EasyTierRelease.Current;
        var root = Root;
        var dir = Path.Combine(root, EasyTierRelease.InstallTag, asset.Platform);
        var core = Path.Combine(dir, asset.CoreName);
        EnsureRoot(root);
        if (!Directory.Exists(dir))
        {
            var package = ReadPackage(packagePath, asset);
            var parent = Path.GetDirectoryName(dir)!;
            CreateProtectedDirectory(parent);
            foreach (var leftover in Directory.EnumerateDirectories(parent, asset.Platform + ".tmp-*")) Directory.Delete(leftover, true);
            var temp = dir + ".tmp-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
            CreateProtectedDirectory(temp);
            try
            {
                using (var zip = new ZipArchive(new MemoryStream(package, writable: false), ZipArchiveMode.Read))
                    EasyTierInstaller.ExtractEntries(zip, temp, asset);
                if (!OperatingSystem.IsWindows())
                {
                    foreach (var file in Directory.EnumerateFiles(temp))
                    {
                        var name = Path.GetFileName(file);
                        File.SetUnixFileMode(file, name == asset.CoreName || name == asset.CliName ? DirMode : LibraryMode);
                    }
                }
                if (!File.Exists(Path.Combine(temp, asset.CoreName))) throw new InvalidDataException("easytier-core missing from the package");
                try { Directory.Move(temp, dir); }
                catch (IOException) when (Directory.Exists(dir)) { /* another guard finished first: use its copy */ }
            }
            finally
            {
                if (Directory.Exists(temp)) Directory.Delete(temp, true);
            }
        }
        CheckTree(root, dir, core);
        return core;
    }

    /// <summary>Reads the package once and checks it; the checked bytes are what gets extracted, so the file can't change in between.</summary>
    internal static byte[] ReadPackage(string path, EasyTierRelease.Asset asset)
    {
        // The size is checked before opening: a pipe or device has none, and opening one could block.
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != asset.Size) throw new InvalidDataException("The multiplayer package does not match the pinned one");
        var bytes = new byte[asset.Size];
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1))
        {
            if (stream.Length != asset.Size) throw new InvalidDataException("The multiplayer package does not match the pinned one");
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1) throw new InvalidDataException("The multiplayer package does not match the pinned one");
        }
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), Convert.FromHexString(asset.Sha256)))
            throw new InvalidDataException("The multiplayer package does not match the pinned one");
        if (!EasyTierRelease.SignatureAllowsInstall) throw new InvalidDataException("The multiplayer package is not signed by Jay's trustees");
        return bytes;
    }

    /// <summary>Creates the root (and missing parents) so that only administrators or root can change what is in it.</summary>
    private static void EnsureRoot(string root)
    {
        if (OperatingSystem.IsWindows())
        {
            var parent = Path.GetDirectoryName(root)!;
            if (!Directory.Exists(parent)) Directory.CreateDirectory(parent);
            if (!Directory.Exists(root)) CreateAdminOnlyDirectory(root);
            return;
        }
        // Walk down from the first folder that exists; every folder made here is 0755 and owned by root (the guard).
        var missing = new Stack<string>();
        for (var d = root; !Directory.Exists(d); d = Path.GetDirectoryName(d)!) missing.Push(d);
        while (missing.Count > 0) Directory.CreateDirectory(missing.Pop(), DirMode);
    }

    private static void CreateProtectedDirectory(string path)
    {
        if (Directory.Exists(path)) return;
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path); // inherits the root's administrators-only list
        else Directory.CreateDirectory(path, DirMode);
    }

    /// <summary>Administrators and SYSTEM may change it; users may only read and run what is in it. Nothing is inherited from above.</summary>
    [SupportedOSPlatform("windows")]
    private static void CreateAdminOnlyDirectory(string path)
    {
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(admins);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).Create(security);
    }

    /// <summary>
    /// Refuses a copy that a non-administrator could have swapped: no link or junction anywhere from the root down, and on
    /// Mac/Linux no folder or file in it that the group or others may write, and no folder above it that others may write.
    /// </summary>
    internal static void CheckTree(string root, string dir, string core)
    {
        if (!File.Exists(core)) throw new IOException("easytier-core missing from the protected folder");
        var folders = new List<string>();
        for (var d = Path.GetFullPath(dir); ; d = Path.GetDirectoryName(d)!)
        {
            folders.Add(d);
            if (string.Equals(d.TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal)) break;
            if (Path.GetDirectoryName(d) is null) throw new IOException("protected folder outside its root");
        }
        foreach (var path in folders.Concat(Directory.EnumerateFileSystemEntries(dir)))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("The protected multiplayer folder contains a link");
            if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
                throw new IOException("The protected multiplayer folder can be changed by other users");
        }
        if (OperatingSystem.IsWindows()) return;
        for (var d = Path.GetDirectoryName(Path.GetFullPath(root)); d is not null; d = Path.GetDirectoryName(d))
        {
            var mode = File.GetUnixFileMode(d);
            if ((mode & UnixFileMode.OtherWrite) != 0 && (mode & UnixFileMode.StickyBit) == 0 && RootForTest is null)
                throw new IOException("A folder above the protected multiplayer folder can be changed by any user");
        }
    }
}
