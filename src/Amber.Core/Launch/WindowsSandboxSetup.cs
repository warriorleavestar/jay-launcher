using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Amber.Core.Launch;

/// <summary>
/// The one-time admin step of the Windows strong sandbox. Inside an AppContainer, Java looks up every folder on the
/// way to a file (HotSpot's path checks, <c>toRealPath</c>, <c>Files.createDirectories</c>), which needs "list folder"
/// on each parent of the game folder up to the drive root, and a drive root's permissions take admin to change. So the
/// same admin prompt that adds the loopback exemption also lets app containers list those parent folders: names only
/// (list, traverse, read attributes), on that folder only, never inherited by anything below it.
/// <para>
/// The entry is for ALL APPLICATION PACKAGES, not Jay's own container: a parent folder whose permissions name one
/// specific container could no longer be listed by low-integrity processes, which would break the standard level
/// (tested). Windows gives the same group read access to its own folders and Program Files already.
/// </para>
/// <para>
/// Jay starts itself elevated with <see cref="SetupArgument"/> (or <see cref="UndoArgument"/>) and the folders on the
/// command line, never through a file a non-admin process could change. Each entry carries an exact access mask
/// (<see cref="ListMask"/>), so undo removes Jay's entries and nothing Windows or the player added.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsSandboxSetup
{
    public const string SetupArgument = "--jay-sandbox-setup";
    public const string UndoArgument = "--jay-sandbox-undo";
    private const string LoopbackOption = "--loopback";
    private const string NoLoopbackOption = "--no-loopback";

    /// <summary>ALL APPLICATION PACKAGES.</summary>
    internal static readonly SecurityIdentifier AllPackages = new("S-1-15-2-1");

    /// <summary>FILE_LIST_DIRECTORY | FILE_TRAVERSE | FILE_READ_ATTRIBUTES | SYNCHRONIZE: names, never contents.</summary>
    internal const int ListMask = 0x1 | 0x20 | 0x80 | 0x100000;

    /// <summary>True when Jay was started as the elevated helper (handled before any window opens).</summary>
    public static bool IsInvocation(string[] args) => args.Length > 0 && (args[0] == SetupArgument || args[0] == UndoArgument);

    /// <summary>
    /// The parents of the sandbox's granted <paramref name="paths"/> (up to the drive root) that app containers cannot
    /// list yet. Folders inside a granted path are left out: the launch grants those to Jay's container itself.
    /// </summary>
    public static IReadOnlyList<string> FoldersToOpen(IEnumerable<string?> paths)
    {
        var granted = paths.Where(p => !string.IsNullOrEmpty(p) && Path.IsPathFullyQualified(p)).Select(p => Path.GetFullPath(p!)).ToList();
        return
        [
            .. granted.SelectMany(Parents).Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(f => !granted.Any(g => GameSandbox.IsInside(f, g)))
                .Where(f => Directory.Exists(f) && !ContainersCanList(f)),
        ];
    }

    /// <summary>
    /// Creates the missing parent folder of each granted path that does not exist yet (as the player, before the admin
    /// step), so <see cref="FoldersToOpen"/> sees it. Otherwise a granted folder that the first launch creates (the
    /// universal loader's runtime folder on a fresh install) leaves its parent closed, and the next launch asks for
    /// the admin step again. Returns the paths that were created.
    /// </summary>
    public static IReadOnlyList<string> CreateMissingParents(IEnumerable<string?> paths)
    {
        var created = new List<string>();
        foreach (var path in paths)
        {
            if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path)) continue;
            var full = Path.GetFullPath(path);
            if (Directory.Exists(full) || File.Exists(full) || Path.GetDirectoryName(full) is not { } parent || Directory.Exists(parent)) continue;
            try
            {
                Directory.CreateDirectory(parent);
                created.Add(parent);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return created;
    }

    /// <summary>Every folder above <paramref name="path"/>, up to and including the drive root.</summary>
    public static IEnumerable<string> Parents(string path)
    {
        for (var parent = Path.GetDirectoryName(Path.GetFullPath(path)); parent is not null; parent = Path.GetDirectoryName(parent))
            yield return parent;
    }

    /// <summary>Whether app containers (all of them, or Jay's) may list <paramref name="folder"/> itself.</summary>
    public static bool ContainersCanList(string folder)
    {
        try
        {
            var container = WindowsSandbox.DerivedContainerSid();
            return new DirectoryInfo(folder).GetAccessControl(AccessControlSections.Access)
                .GetAccessRules(true, true, typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>()
                .Any(r => r.AccessControlType == AccessControlType.Allow && (r.PropagationFlags & PropagationFlags.InheritOnly) == 0
                    && (r.IdentityReference == AllPackages || r.IdentityReference == container)
                    && (r.FileSystemRights & FileSystemRights.ListDirectory) != 0);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or InvalidOperationException) { return false; }
    }

    /// <summary>The elevated helper's command: Jay itself with the folders, run through Windows' admin prompt.</summary>
    public static ProcessStartInfo Command(string jayExe, bool undo, bool loopback, IReadOnlyList<string> folders) =>
        new(jayExe, CommandLine.Join(Arguments(undo, loopback, folders)))
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };

    internal static IReadOnlyList<string> Arguments(bool undo, bool loopback, IReadOnlyList<string> folders) =>
        [undo ? UndoArgument : SetupArgument, loopback ? LoopbackOption : NoLoopbackOption, .. folders];

    /// <summary>
    /// The elevated helper: the loopback exemption (when asked) and the parent folders. 0 when everything was done,
    /// 2 for a command line Jay would not write, 1 when a step failed.
    /// </summary>
    public static int Run(string[] args)
    {
        if (!IsInvocation(args) || args.Length < 2 || args[1] is not (LoopbackOption or NoLoopbackOption)) return 2;
        var undo = args[0] == UndoArgument;
        var folders = args.Skip(2).ToList();
        if (folders.Any(f => !Path.IsPathFullyQualified(f) || !Directory.Exists(f))) return 2;
        var ok = args[1] != LoopbackOption || Loopback(undo);
        foreach (var folder in folders)
        {
            try
            {
                if (undo) RemoveListing(folder);
                else AddListing(folder);
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                ok = false;
            }
        }
        return ok ? 0 : 1;
    }

    private static bool Loopback(bool remove)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "CheckNetIsolation.exe"),
            $"LoopbackExempt {(remove ? "-d" : "-a")} -p={WindowsSandbox.ContainerSid().Value}")
        { UseShellExecute = false, CreateNoWindow = true };
        using var process = Process.Start(psi);
        if (process is null) return false;
        process.WaitForExit();
        return process.ExitCode == 0;
    }

    /// <summary>Adds Jay's list-only entry to this one folder (nothing below it changes).</summary>
    internal static void AddListing(string folder) => Rewrite(folder, aces =>
        aces.Any(IsJays) ? null : [.. aces, new CommonAce(AceFlags.None, AceQualifier.AccessAllowed, ListMask, AllPackages, false, null)]);

    /// <summary>Takes Jay's list-only entry off this one folder again.</summary>
    internal static void RemoveListing(string folder) => Rewrite(folder, aces =>
        aces.Any(IsJays) ? [.. aces.Where(a => !IsJays(a))] : null);

    private static bool IsJays(GenericAce ace) => ace is CommonAce { AceQualifier: AceQualifier.AccessAllowed, IsInherited: false, AccessMask: ListMask } common
        && common.AceFlags == AceFlags.None && common.SecurityIdentifier == AllPackages;

    /// <summary>
    /// Writes one folder's ACL with SetFileSecurity, which (unlike SetNamedSecurityInfo) never walks the folders below:
    /// a drive root stays a single write. The ACL is kept in Windows' canonical order (own deny, own allow, inherited)
    /// and keeps its automatic inheritance, so later changes by Windows or .NET still work.
    /// </summary>
    private static void Rewrite(string folder, Func<List<GenericAce>, List<GenericAce>?> change)
    {
        var raw = new RawSecurityDescriptor(new DirectoryInfo(folder).GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorBinaryForm(), 0);
        var aces = raw.DiscretionaryAcl?.OfType<GenericAce>().ToList() ?? [];
        if (change(aces) is not { } changed) return;
        var ordered = changed.Where(a => !a.IsInherited && a.AceType == AceType.AccessDenied)
            .Concat(changed.Where(a => !a.IsInherited && a.AceType != AceType.AccessDenied))
            .Concat(changed.Where(a => a.IsInherited)).ToList();
        var acl = new RawAcl(raw.DiscretionaryAcl?.Revision ?? GenericAcl.AclRevision, ordered.Count);
        for (var i = 0; i < ordered.Count; i++) acl.InsertAce(i, ordered[i]);
        var flags = raw.ControlFlags | ControlFlags.DiscretionaryAclPresent;
        // Without the "required" bit Windows would drop the auto-inherited mark (seen: .NET then calls the ACL non-canonical).
        if ((flags & ControlFlags.DiscretionaryAclAutoInherited) != 0) flags |= ControlFlags.DiscretionaryAclAutoInheritRequired;
        var descriptor = new RawSecurityDescriptor(flags, null, null, null, acl);
        var bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, 0);
        if (!SetFileSecurityW(folder, DACL_SECURITY_INFORMATION, bytes)) throw new System.ComponentModel.Win32Exception();
    }

    private const uint DACL_SECURITY_INFORMATION = 0x4;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetFileSecurityW(string name, uint info, byte[] descriptor);
}
