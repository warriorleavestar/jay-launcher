using Amber.Core.Layout;

namespace Amber.Core.Tests;

public class DensityTests
{
    [Theory]
    [InlineData(null, "standard")]
    [InlineData("", "standard")]
    [InlineData("COMPACT", "compact")]
    [InlineData("weird", "standard")]
    public void Normalizes(string? mode, string expected) => Assert.Equal(expected, Density.Normalize(mode));

    [Fact]
    public void StandardOverridesNothingCompactTightens()
    {
        Assert.Empty(Density.Overrides(Density.Standard));
        var compact = Density.Overrides(Density.Compact);
        Assert.Equal(30, compact["ControlHeight"]);
        Assert.Equal(32, Density.RowHeight(Density.Compact));
        Assert.Equal(40, Density.RowHeight(Density.Standard));
        // Still big enough to hit: never below 24 px.
        Assert.All(compact.Values, v => Assert.True(v >= 24));
    }
}
