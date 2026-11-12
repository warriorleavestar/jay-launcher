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
        // A runner whose filter engine cannot be opened answers "unknown"; anywhere else the answer must be definite.
        if (!WindowsLoopbackInbound.EngineReachable()) return;
        Assert.True(state is not null, "the filter engine is reachable, so Check() must say present or missing");
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
    public void ControlLookupsGateEverything()
    {
        if (!OperatingSystem.IsWindows()) return;
        uint[] missing = [0x80320007, 0x80320003];
        Assert.True(WindowsLoopbackInbound.Interpret(missing, [5, 5, 5]));
        Assert.True(WindowsLoopbackInbound.Interpret(missing, [0, 0, 0]));
        Assert.False(WindowsLoopbackInbound.Interpret(missing, [5, 0x80320003, 5]));
        Assert.Null(WindowsLoopbackInbound.Interpret(missing, [5, 0x800706BA, 5]));
        // A tightened engine that denies every lookup, even of keys that never exist, tells nothing.
        Assert.Null(WindowsLoopbackInbound.Interpret([5, 5], [5, 5, 5]));
        Assert.Null(WindowsLoopbackInbound.Interpret([0x80320007, 0x80070005], [5, 5, 5]));
    }

    [Fact]
    public void EachUserHasTheirOwnFilterKeys()
    {
        if (!OperatingSystem.IsWindows()) return;
        var a = WindowsLoopbackInbound.KeysFor(new System.Security.Principal.SecurityIdentifier("S-1-5-21-1-2-3-1001"));
        var again = WindowsLoopbackInbound.KeysFor(new System.Security.Principal.SecurityIdentifier("S-1-5-21-1-2-3-1001"));
        var b = WindowsLoopbackInbound.KeysFor(new System.Security.Principal.SecurityIdentifier("S-1-5-21-1-2-3-1002"));
        Assert.Equal(a, again);
        Assert.Equal(4, new[] { a.V4, a.V6, b.V4, b.V6 }.Distinct().Count());
        Assert.DoesNotContain(WindowsLoopbackInbound.SubLayerKey, new[] { a.V4, a.V6, b.V4, b.V6 });
    }
}

/// <summary>The admin helper's command line for the strong sandbox's loopback setup.</summary>
public sealed class WindowsSandboxSetupUserTests
{
    [Fact]
    public void HelperNamesTheUserOnlyWhenSettingUpLoopback()
    {
        if (!OperatingSystem.IsWindows()) return;
        var user = new System.Security.Principal.SecurityIdentifier("S-1-5-21-1-2-3-1001");
        Assert.Equal(["--jay-sandbox-setup", "--loopback", "--user=S-1-5-21-1-2-3-1001", @"C:\g"],
            WindowsSandboxSetup.Arguments(false, true, [@"C:\g"], user));
        Assert.Equal(["--jay-sandbox-undo", "--loopback", "--user=S-1-5-21-1-2-3-1001", @"C:\g"], WindowsSandboxSetup.Arguments(true, true, [@"C:\g"], user));
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
        // Loopback setup or undo without a user, or a user with --no-loopback, is refused.
        Assert.Equal(2, WindowsSandboxSetup.Run(["--jay-sandbox-setup", "--loopback"]));
        Assert.Equal(2, WindowsSandboxSetup.Run(["--jay-sandbox-undo", "--loopback"]));
        Assert.Equal(2, WindowsSandboxSetup.Run(["--jay-sandbox-setup", "--no-loopback", "--user=S-1-5-21-1-2-3-1001"]));
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
