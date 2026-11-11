using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

// FileSystemRights values are plain flags; the rule checks run (and are tested) on every platform.
#pragma warning disable CA1416

namespace Amber.Core.Launch;

/// <summary>
/// Windows: a folder only this user (plus SYSTEM and Administrators) can change, for the English-named junctions Jay
/// launches through when the real path can't be written in the system code page. Shared places such as
/// <c>%PUBLIC%</c> and the drive root let every user create folders there. A junction in such a folder could be repointed
/// by another user at their own java.exe or mods, which then run as this user. So a shared place is only used through a
/// per-user folder that is created with a locked ACL and checked again every time before it is used.
/// </summary>
internal static class PrivateFolder
{
    internal const string LocalSystem = "S-1-5-18";
    internal const string Administrators = "S-1-5-32-544";
    internal const string TrustedInstaller = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    /// <summary>Rights that let someone change what is in a folder, or the folder itself.</summary>
    internal const FileSystemRights Changes = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes
        | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.WriteAttributes | FileSystemRights.Delete
        | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    /// <summary>Rights on a parent that let someone delete or rename what is in it, or take the parent over.</summary>
    internal const FileSystemRights TakesChildren = FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions
        | FileSystemRights.TakeOwnership;

    /// <summary>One access rule, as the checks below see it.</summary>
    internal readonly record struct Rule(string Sid, FileSystemRights Rights, bool Allow, bool InheritOnly);

    /// <summary>A per-user folder name in a shared place: the same for this user every time, different for each user.</summary>
    [SupportedOSPlatform("windows")]
    internal static string NameFor(string prefix) =>
        prefix + "-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(WindowsIdentity.GetCurrent().User!.Value)))[..12];

    /// <summary>
    /// <paramref name="path"/> as a private folder: created with the locked ACL when missing, then checked. Null when it
    /// isn't private (someone else made it, its ACL lets others change it, it is a link, or its parent lets others take it away).
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static string? Ensure(string path)
    {
        try
        {
            var me = WindowsIdentity.GetCurrent().User!;
            var info = new DirectoryInfo(Path.GetFullPath(path));
            if (info.Parent is not { Exists: true } parent) return null;
            if (!info.Exists)
            {
                // Created with the ACL in one step. If someone made it first this does nothing, and the checks below refuse it.
                info.Create(Locked(me));
                info.Refresh();
            }
            if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0) return null;
            var security = info.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
            var parentSecurity = parent.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
            return IsPrivate(Owner(security), security.AreAccessRulesProtected, Rules(security), me.Value)
                   && KeepsChildren(Owner(parentSecurity), Rules(parentSecurity), me.Value)
                ? info.FullName
                : null;
        }
        catch (SystemException) { return null; }
    }

    /// <summary>
    /// The folder belongs to me (or Administrators), doesn't inherit from its parent, and no one but me, SYSTEM and
    /// Administrators may change it.
    /// </summary>
    internal static bool IsPrivate(string? owner, bool isProtected, IEnumerable<Rule> rules, string me) =>
        (owner == me || owner == Administrators) && isProtected
        && rules.All(r => !r.Allow || (r.Rights & Changes) == 0 || Trusted(r.Sid, me));

    /// <summary>
    /// The parent's owner is trusted and no one else may delete or rename what is in it. Rules that only pass down to
    /// children don't count: the private folder has its own locked ACL.
    /// </summary>
    internal static bool KeepsChildren(string? owner, IEnumerable<Rule> rules, string me) =>
        owner is not null && (Trusted(owner, me) || owner == TrustedInstaller)
        && rules.All(r => !r.Allow || r.InheritOnly || (r.Rights & TakesChildren) == 0 || Trusted(r.Sid, me) || r.Sid == TrustedInstaller);

    private static bool Trusted(string sid, string me) => sid == me || sid == LocalSystem || sid == Administrators;

    [SupportedOSPlatform("windows")]
    private static DirectorySecurity Locked(SecurityIdentifier me)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags both = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var sid in new[] { me, new SecurityIdentifier(LocalSystem), new SecurityIdentifier(Administrators) })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, both, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    [SupportedOSPlatform("windows")]
    private static string? Owner(DirectorySecurity security) => (security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier)?.Value;

    [SupportedOSPlatform("windows")]
    private static IEnumerable<Rule> Rules(DirectorySecurity security) =>
        security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>()
            .Select(r => new Rule(r.IdentityReference.Value, r.FileSystemRights, r.AccessControlType == AccessControlType.Allow,
                (r.PropagationFlags & PropagationFlags.InheritOnly) != 0));

    /// <summary>The 8.3 short form of an existing path (no link needed), or null when the volume has none.</summary>
    [SupportedOSPlatform("windows")]
    internal static string? ShortPath(string path)
    {
        var buffer = new StringBuilder(1024);
        var length = GetShortPathNameW(path, buffer, buffer.Capacity);
        return length is 0 || length >= buffer.Capacity ? null : buffer.ToString();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathNameW(string longPath, StringBuilder shortPath, int bufferLength);
}
