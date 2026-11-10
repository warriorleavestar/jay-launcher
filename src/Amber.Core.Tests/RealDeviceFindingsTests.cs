using Amber.Core;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Public;

namespace Amber.Core.Tests;

/// <summary>Regressions from the 2026-11-10 real-device test run.</summary>
public class RealDeviceFindingsTests
{
    [Fact]
    public void SpecialFolderUsesTheReportedPathWhenItIsRooted()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cfg"));
        Assert.Equal(root, SpecialFolders.Resolve(Environment.SpecialFolder.ApplicationData, ".config", "XDG_CONFIG_HOME",
            (_, _) => root, _ => null, "/home/x"));
    }

    [Fact]
    public void MissingConfigFolderFallsBackToXdgThenHomeInsteadOfARelativePath()
    {
        // .NET returns "" for ApplicationData when ~/.config doesn't exist (and can't be created).
        var xdg = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "xdg"));
        Assert.Equal(xdg, SpecialFolders.Resolve(Environment.SpecialFolder.ApplicationData, ".config", "XDG_CONFIG_HOME",
            (_, _) => "", v => v == "XDG_CONFIG_HOME" ? xdg : null, "/home/x"));

        var home = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "home"));
        var resolved = SpecialFolders.Resolve(Environment.SpecialFolder.ApplicationData, ".config", "XDG_CONFIG_HOME",
            (_, _) => "", v => v == "XDG_CONFIG_HOME" ? "relative/xdg" : null, home);
        Assert.Equal(Path.Combine(home, ".config"), resolved);
        Assert.True(Path.IsPathRooted(resolved));
    }

    [Fact]
    public void NoHomeAtAllIsAnErrorNotTheCurrentDirectory()
    {
        Assert.Throws<InvalidOperationException>(() => SpecialFolders.Resolve(Environment.SpecialFolder.ApplicationData, ".config", "XDG_CONFIG_HOME",
            (_, _) => "", _ => null, ""));
    }

    [Fact]
    public void RealApplicationDataIsAlwaysRooted()
    {
        Assert.True(Path.IsPathRooted(SpecialFolders.ApplicationData()));
        Assert.True(Path.IsPathRooted(SpecialFolders.LocalApplicationData()));
    }

    [Theory]
    [InlineData("10.235.0.2", true)]
    [InlineData("10.235.17.9", true)]
    [InlineData("10.235.0.1", false)] // main directory
    [InlineData("10.235.255.254", false)] // backup directory 2
    [InlineData("10.235.255.252", false)] // backup directory 4
    [InlineData("10.126.126.5", false)] // EasyTier's placeholder before it sees the network
    public void MembersDontCountAsConnectedOnADirectoryAddress(string ip, bool member) =>
        Assert.Equal(member, PublicNetworkSession.IsMemberAddress(ip));

    [Fact]
    public void IdentityStillLoadsWhenKeyProtectionIsUnavailable()
    {
        var dir = Path.Combine(Path.GetTempPath(), "jay-id-" + Guid.NewGuid().ToString("N"));
        try
        {
            MemberIdentity.DpapiUnavailable = true;
            using var first = MemberIdentity.LoadOrCreate(dir);
            Assert.True(first.IsPersisted);
            Assert.Null(first.Warning);
            using var again = MemberIdentity.LoadOrCreate(dir);
            Assert.Equal(first.Fingerprint, again.Fingerprint);
        }
        finally
        {
            MemberIdentity.DpapiUnavailable = false;
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }
}
