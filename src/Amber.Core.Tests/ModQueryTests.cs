using Amber.Core.Content;
using Xunit;

namespace Amber.Core.Tests;

public sealed class ModQueryTests
{
    private static ModFacts M(string name, bool enabled = true, bool update = false, bool problem = false, string? id = null, string? authors = null) =>
        new(name, name.ToLowerInvariant() + "-1.0.jar", id, authors, enabled, update, problem);

    [Fact]
    public void FiltersByChip()
    {
        Assert.True(ModQuery.Matches(M("Sodium"), "", ModFilter.All));
        Assert.False(ModQuery.Matches(M("Sodium"), "", ModFilter.Disabled));
        Assert.True(ModQuery.Matches(M("OptiFine", enabled: false), null, ModFilter.Disabled));
        Assert.True(ModQuery.Matches(M("Iris", update: true), null, ModFilter.Updates));
        Assert.False(ModQuery.Matches(M("Iris"), null, ModFilter.Updates));
        Assert.True(ModQuery.Matches(M("OptiFine", problem: true), null, ModFilter.Problems));
    }

    [Theory]
    [InlineData("sod", true)]
    [InlineData("SODIUM", true)]
    [InlineData("jelly", true)]       // author
    [InlineData("sodium-1.0", true)]  // file name
    [InlineData("render", true)]      // mod id
    [InlineData("sod jelly", true)]   // every word somewhere
    [InlineData("sod iris", false)]
    [InlineData("  ", true)]
    public void SearchMatchesNameFileIdAndAuthors(string search, bool expected) =>
        Assert.Equal(expected, ModQuery.Matches(M("Sodium", id: "render-opt", authors: "jellysquid3"), search, ModFilter.All));

    [Fact]
    public void SearchAndFilterCombine() =>
        Assert.False(ModQuery.Matches(M("Sodium", enabled: true), "sod", ModFilter.Disabled));

    [Theory]
    [InlineData(2, 5, 10, new[] { 2, 3, 4, 5 })]
    [InlineData(5, 2, 10, new[] { 2, 3, 4, 5 })]
    [InlineData(-1, 3, 10, new[] { 3 })]
    [InlineData(3, 3, 10, new[] { 3 })]
    [InlineData(0, 10, 10, new int[0])]
    public void RangeSelectsBetweenAnchorAndTarget(int anchor, int target, int count, int[] expected) =>
        Assert.Equal(expected, ModQuery.Range(anchor, target, count));
}
