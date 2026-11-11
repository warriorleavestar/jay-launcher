using System.Security.AccessControl;
using Amber.Core.Launch;
using static Amber.Core.Launch.PrivateFolder;

// FileSystemRights values are plain flags here; the checks themselves are platform-neutral.
#pragma warning disable CA1416

namespace Amber.Core.Tests;

/// <summary>
/// Junction folders for paths the system code page can't write (UI security review D): a shared place like %PUBLIC% or
/// the drive root is only used through a per-user folder that nobody else can change or take away.
/// </summary>
public sealed class PrivateFolderTests
{
    private const string Me = "S-1-5-21-1-2-3-1001";
    private const string Other = "S-1-5-21-1-2-3-1002";
    private const string Users = "S-1-5-32-545";
    private const string AuthenticatedUsers = "S-1-5-11";
    private const string Interactive = "S-1-5-4";

    private static readonly Rule[] Locked =
    [
        new(Me, FileSystemRights.FullControl, true, false),
        new(LocalSystem, FileSystemRights.FullControl, true, false),
        new(Administrators, FileSystemRights.FullControl, true, false),
    ];

    [Fact]
    public void TheLockedFolderIsPrivate()
    {
        Assert.True(IsPrivate(Me, true, Locked, Me));
        Assert.True(IsPrivate(Administrators, true, Locked, Me));
        Assert.True(IsPrivate(Me, true, [.. Locked, new(Users, FileSystemRights.ReadAndExecute, true, false)], Me));
    }

    [Fact]
    public void AFolderSomeoneElseMadeOrCanChangeIsNot()
    {
        // Made first by another user (they own it and can change its ACL whenever they like).
        Assert.False(IsPrivate(Other, true, Locked, Me));
        Assert.False(IsPrivate(null, true, Locked, Me));
        // Still inheriting from %PUBLIC% or the drive root.
        Assert.False(IsPrivate(Me, false, Locked, Me));
        // Anyone else who may write, delete or re-point what is in it.
        foreach (var rights in new[] { FileSystemRights.Modify, FileSystemRights.WriteData, FileSystemRights.AppendData, FileSystemRights.Delete,
                     FileSystemRights.DeleteSubdirectoriesAndFiles, FileSystemRights.ChangePermissions, FileSystemRights.TakeOwnership })
        {
            Assert.False(IsPrivate(Me, true, [.. Locked, new(Users, rights, true, false)], Me));
            Assert.False(IsPrivate(Me, true, [.. Locked, new(Other, rights, true, false)], Me));
        }
        // A deny rule never makes it less private.
        Assert.True(IsPrivate(Me, true, [.. Locked, new(Other, FileSystemRights.FullControl, false, false)], Me));
    }

    [Fact]
    public void TheParentMustNotLetOthersTakeTheFolderAway()
    {
        // The drive root: Authenticated Users may create folders there, and modify only what they made (inherit-only).
        Rule[] root =
        [
            new(LocalSystem, FileSystemRights.FullControl, true, false),
            new(Administrators, FileSystemRights.FullControl, true, false),
            new(AuthenticatedUsers, FileSystemRights.AppendData, true, false),
            new(AuthenticatedUsers, FileSystemRights.Modify, true, true),
            new(Users, FileSystemRights.ReadAndExecute, true, false),
        ];
        Assert.True(KeepsChildren(TrustedInstaller, root, Me));
        Assert.True(KeepsChildren(LocalSystem, [.. root, new(Interactive, FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories, true, false)], Me));
        // Someone who may delete or rename what is in it, or owns it (and can give themselves that right).
        Assert.False(KeepsChildren(TrustedInstaller, [.. root, new(Users, FileSystemRights.DeleteSubdirectoriesAndFiles, true, false)], Me));
        Assert.False(KeepsChildren(TrustedInstaller, [.. root, new(Interactive, FileSystemRights.FullControl, true, false)], Me));
        Assert.False(KeepsChildren(Other, root, Me));
        Assert.False(KeepsChildren(null, root, Me));
    }

    /// <summary>On Windows, for real: a new folder gets the locked ACL; one that inherits its parent's is refused.</summary>
    [Fact]
    public void EnsureLocksANewFolderAndRefusesAnOpenOne()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "jay-private-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            var made = Ensure(Path.Combine(root, "locked"));
            Assert.NotNull(made);
            var security = new DirectoryInfo(made).GetAccessControl();
            Assert.True(security.AreAccessRulesProtected);
            Assert.Equal(made, Ensure(made));

            // Created the ordinary way first (inherits the temp folder's ACL): not ours to trust.
            var open = Directory.CreateDirectory(Path.Combine(root, "open")).FullName;
            Assert.Null(Ensure(open));
            // A link is refused even when it points at a private folder.
            var link = Path.Combine(root, "link");
            Directory.CreateSymbolicLink(link, made);
            Assert.Null(Ensure(link));
        }
        catch (UnauthorizedAccessException) { /* no symlink right on this machine: the link case can't be set up */ }
        finally { Directory.Delete(root, true); }
    }
}
