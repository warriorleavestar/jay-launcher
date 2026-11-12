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
