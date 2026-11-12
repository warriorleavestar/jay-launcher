using System.Runtime.InteropServices;
using Amber.Core.Launch;

namespace Amber.Core.Tests;

/// <summary>The strong sandbox's inbound loopback filters: struct layouts match the 64-bit WFP headers, and the check needs no admin.</summary>
public sealed class WindowsLoopbackInboundTests
{
    [Fact]
    public void StructLayoutsMatchWfpHeaders()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Equal(16, Marshal.SizeOf<WindowsLoopbackInbound.FWP_VALUE0>());
        Assert.Equal(40, Marshal.SizeOf<WindowsLoopbackInbound.FWPM_FILTER_CONDITION0>());
        Assert.Equal(72, Marshal.SizeOf<WindowsLoopbackInbound.FWPM_SUBLAYER0>());
        Assert.Equal(200, Marshal.SizeOf<WindowsLoopbackInbound.FWPM_FILTER0>());
        Assert.Equal(152, (int)Marshal.OffsetOf<WindowsLoopbackInbound.FWPM_FILTER0>(nameof(WindowsLoopbackInbound.FWPM_FILTER0.rawContext)));
    }

    [Fact]
    public void PresenceCheckRunsWithoutAdmin()
    {
        if (!OperatingSystem.IsWindows()) return;
        // True or false depends on this machine (whether the admin step ran); with the filter engine running it must
        // be a definite answer, never "unknown", and never need elevation.
        WindowsLoopbackInbound.Invalidate();
        var state = WindowsLoopbackInbound.Check();
        Assert.NotNull(state);
        Assert.Equal(state == true, WindowsLoopbackInbound.Present());
    }
}

public sealed class WindowsLoopbackInboundClassifyTests
{
    [Fact]
    public void LookupAnswersAreReadStrictly()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.True(WindowsLoopbackInbound.Classify(0));
        Assert.True(WindowsLoopbackInbound.Classify(5));
        Assert.True(WindowsLoopbackInbound.Classify(0x80070005));
        Assert.False(WindowsLoopbackInbound.Classify(0x80320003));
        Assert.False(WindowsLoopbackInbound.Classify(0x80320007));
        Assert.Null(WindowsLoopbackInbound.Classify(0x800706BA)); // RPC server unavailable
        Assert.Null(WindowsLoopbackInbound.Classify(0x80320009));
    }

    [Fact]
    public void HelperNamesTheUserOnlyWhenSettingUpLoopback()
    {
        if (!OperatingSystem.IsWindows()) return;
        var user = new System.Security.Principal.SecurityIdentifier("S-1-5-21-1-2-3-1001");
        Assert.Equal(["--jay-sandbox-setup", "--loopback", "--user=S-1-5-21-1-2-3-1001", @"C:\g"],
            WindowsSandboxSetup.Arguments(false, true, [@"C:\g"], user));
        Assert.Equal(["--jay-sandbox-undo", "--loopback", @"C:\g"], WindowsSandboxSetup.Arguments(true, true, [@"C:\g"], user));
        Assert.Equal(["--jay-sandbox-setup", "--no-loopback", @"C:\g"], WindowsSandboxSetup.Arguments(false, false, [@"C:\g"], user));
    }

    [Fact]
    public void HelperAcceptsOnlyRealAccountSids()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.NotNull(WindowsSandboxSetup.ParseUser("--user=S-1-5-21-1-2-3-1001"));
        Assert.Null(WindowsSandboxSetup.ParseUser("--user=S-1-1-0")); // Everyone
        Assert.Null(WindowsSandboxSetup.ParseUser("--user=S-1-5-18")); // SYSTEM
        Assert.Null(WindowsSandboxSetup.ParseUser("--user=S-1-15-2-1")); // ALL APPLICATION PACKAGES
        Assert.Null(WindowsSandboxSetup.ParseUser("--user=nonsense"));
        Assert.Null(WindowsSandboxSetup.ParseUser("S-1-5-21-1-2-3-1001"));
        // Setting up loopback without a user, a user with --no-loopback, or a user on undo is refused.
        Assert.Equal(2, WindowsSandboxSetup.Run(["--jay-sandbox-setup", "--loopback"]));
        Assert.Equal(2, WindowsSandboxSetup.Run(["--jay-sandbox-setup", "--no-loopback", "--user=S-1-5-21-1-2-3-1001"]));
        Assert.Equal(2, WindowsSandboxSetup.Run(["--jay-sandbox-undo", "--loopback", "--user=S-1-5-21-1-2-3-1001"]));
    }

    [Fact]
    public void RemoteUserDescriptorGrantsOnlyThatUser()
    {
        if (!OperatingSystem.IsWindows()) return;
        var user = new System.Security.Principal.SecurityIdentifier("S-1-5-21-1-2-3-1001");
        var sd = new System.Security.AccessControl.RawSecurityDescriptor(WindowsLoopbackInbound.UserDescriptor(user), 0);
        var ace = Assert.IsType<System.Security.AccessControl.CommonAce>(Assert.Single(sd.DiscretionaryAcl!));
        Assert.Equal(user, ace.SecurityIdentifier);
        Assert.Equal(1, ace.AccessMask);
        Assert.Equal(System.Security.AccessControl.AceQualifier.AccessAllowed, ace.AceQualifier);
    }
}
