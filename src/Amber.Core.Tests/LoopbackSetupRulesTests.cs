using Amber.Core.Launch;

namespace Amber.Core.Tests;

/// <summary>The strong sandbox's loopback bookkeeping: readiness, what there is to undo, the record, exit codes.</summary>
public sealed class LoopbackSetupRulesTests
{
    [Theory]
    // exempt, filters, recorded -> ready, has setup
    [InlineData(false, false, false, false, false)]
    [InlineData(false, true, false, false, true)]
    [InlineData(false, null, false, false, false)]
    [InlineData(false, null, true, false, true)]
    [InlineData(true, false, false, false, true)]
    [InlineData(true, false, true, false, true)]
    [InlineData(true, true, false, true, true)]
    [InlineData(true, null, false, false, true)]
    [InlineData(true, null, true, true, true)]
    public void ReadyAndHasSetup(bool exempt, bool? filters, bool recorded, bool ready, bool hasSetup)
    {
        Assert.Equal(ready, LoopbackSetupRules.Ready(exempt, filters, recorded));
        Assert.Equal(hasSetup, LoopbackSetupRules.HasSetup(exempt, filters, recorded));
    }

    [Theory]
    // recorded, undo, loopback, exit -> recorded after
    [InlineData(false, false, true, 0, true)]
    [InlineData(false, false, true, 1, false)]
    [InlineData(true, false, true, 1, true)]
    [InlineData(false, false, false, 0, false)]
    [InlineData(true, false, false, 0, true)]
    [InlineData(true, true, true, 0, false)]
    [InlineData(true, true, true, LoopbackSetupRules.ExitFiltersNotConfirmed, true)]
    [InlineData(true, true, true, 1, true)]
    [InlineData(true, true, false, 0, true)]
    public void RecordFollowsTheHelper(bool recorded, bool undo, bool loopback, int exit, bool after) =>
        Assert.Equal(after, LoopbackSetupRules.RecordedAfter(recorded, undo, loopback, exit));

    [Fact]
    public void ExitCodesAreDescribedHonestly()
    {
        Assert.Equal("WFP 0x8032000A", LoopbackSetupRules.DescribeExit(unchecked((int)0x8032000A)));
        Assert.Equal("WFP 0x80070005", LoopbackSetupRules.DescribeExit(unchecked((int)LoopbackSetupRules.AsHResult(5))));
        Assert.Equal("WFP 0x800706BA", LoopbackSetupRules.DescribeExit(unchecked((int)LoopbackSetupRules.AsHResult(1722))));
        // A crash code is not a filter-engine error.
        Assert.Equal($"exit {unchecked((int)0xE0434352)}", LoopbackSetupRules.DescribeExit(unchecked((int)0xE0434352)));
        Assert.Equal("exit 1", LoopbackSetupRules.DescribeExit(1));
        Assert.Equal(0u, LoopbackSetupRules.AsHResult(0));
        Assert.Equal(0x80320003u, LoopbackSetupRules.AsHResult(0x80320003));
    }
}
