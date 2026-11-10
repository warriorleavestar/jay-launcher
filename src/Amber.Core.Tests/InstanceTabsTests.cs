using Amber.Core.Navigation;

namespace Amber.Core.Tests;

public class InstanceTabsTests
{
    [Theory]
    [InlineData("overview", 0)]
    [InlineData("mods", 1)]
    [InlineData("Worlds", 2)]
    [InlineData("packs", 3)]
    [InlineData("shaders", 4)]
    [InlineData("servers", 5)]
    [InlineData("shots", 6)]
    [InlineData("logs", 7)]
    [InlineData("settings", 8)]
    public void RoutePartsNameTheTabs(string part, int index) => Assert.Equal(index, InstanceTabs.IndexOf(part));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("add")]
    [InlineData("1.21.1")]
    public void OtherPartsKeepTheCurrentTab(string? part) => Assert.Null(InstanceTabs.IndexOf(part));

    [Fact]
    public void TheModsRouteParsesToTheModsTab() => Assert.Equal(1, InstanceTabs.IndexOf(Route.Parse("library/fabric-1.20.1/mods")[2]));
}
