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
    public void KeysAreDistinctAndFixed()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Equal(3, new[] { WindowsLoopbackInbound.SubLayerKey, WindowsLoopbackInbound.FilterV4Key, WindowsLoopbackInbound.FilterV6Key }.Distinct().Count());
        Assert.Equal(new Guid("5a8f3c1e-7d2b-4e69-9a41-6c0b2f7e9d13"), WindowsLoopbackInbound.SubLayerKey);
    }

    [Fact]
    public void PresenceCheckRunsWithoutAdmin()
    {
        if (!OperatingSystem.IsWindows()) return;
        // Either answer is fine (it depends on this machine); it must not throw or need elevation.
        _ = WindowsLoopbackInbound.Present();
    }
}
