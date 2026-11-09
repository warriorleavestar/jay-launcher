using Amber.Core.Game;
using Amber.Core.GameLibrary;
using Amber.Core.Install;

namespace Amber.Core.Tests;

/// <summary>New-instance panel (official design §6.11): default names, the inline name check and loader availability.</summary>
public sealed class NewInstanceNameTests
{
    [Fact]
    public void DefaultNameIsVersionAndLoader()
    {
        Assert.Equal("1.21.1-Fabric", NewInstanceNames.Default("1.21.1", LoaderKind.Fabric, []));
        Assert.Equal("1.21.1", NewInstanceNames.Default("1.21.1", null, []));
    }

    [Fact]
    public void DuplicateNamesGetNextFreeNumber()
    {
        Assert.Equal("1.21.1-Fabric-2", NewInstanceNames.Default("1.21.1", LoaderKind.Fabric, ["1.21.1-Fabric"]));
        Assert.Equal("1.20.1-Forge-4", NewInstanceNames.Default("1.20.1", LoaderKind.Forge, ["1.20.1-forge", "1.20.1-Forge-2", "1.20.1-Forge-3"]));
        Assert.Equal("1.21.1-2", NewInstanceNames.Default("1.21.1", null, ["1.21.1"]));
    }

    [Fact]
    public void OtherLoadersDoNotTakeTheName() =>
        Assert.Equal("1.21.1-NeoForge", NewInstanceNames.Default("1.21.1", LoaderKind.NeoForge, ["1.21.1-Fabric", "1.21.1"]));

    [Theory]
    [InlineData("My pack", InstanceNameProblem.None)]
    [InlineData("1.21.1 Fabric 优化", InstanceNameProblem.None)]
    [InlineData("", InstanceNameProblem.Empty)]
    [InlineData("   ", InstanceNameProblem.Empty)]
    [InlineData("a/b", InstanceNameProblem.InvalidCharacters)]
    [InlineData("a\\b", InstanceNameProblem.InvalidCharacters)]
    [InlineData("what?", InstanceNameProblem.InvalidCharacters)]
    [InlineData("a:b", InstanceNameProblem.InvalidCharacters)]
    [InlineData("..", InstanceNameProblem.InvalidCharacters)]
    [InlineData("pack.", InstanceNameProblem.TrailingDotOrSpace)]
    [InlineData("pack ", InstanceNameProblem.TrailingDotOrSpace)]
    [InlineData(" pack", InstanceNameProblem.TrailingDotOrSpace)]
    [InlineData("CON", InstanceNameProblem.Reserved)]
    [InlineData("nul.txt", InstanceNameProblem.Reserved)]
    [InlineData("com1", InstanceNameProblem.Reserved)]
    [InlineData("console", InstanceNameProblem.None)]
    [InlineData("existing", InstanceNameProblem.Taken)]
    [InlineData("EXISTING", InstanceNameProblem.Taken)]
    public void NameCheck(string name, InstanceNameProblem expected) =>
        Assert.Equal(expected, NewInstanceNames.Check(name, ["existing"]));

    [Fact]
    public void NamesThatPassAreValidIds()
    {
        foreach (var name in new[] { "My pack", "1.21.1-Fabric-2", "整合包 · 二" })
        {
            Assert.Equal(InstanceNameProblem.None, NewInstanceNames.Check(name, []));
            Assert.Equal(name, GameDirectory.CheckId(name));
        }
    }

    [Theory]
    [InlineData("1.21.4", "release", LoaderKind.Fabric, LoaderBlock.None)]
    [InlineData("1.13.2", "release", LoaderKind.Fabric, LoaderBlock.TooOld)]
    [InlineData("1.14", "release", LoaderKind.Fabric, LoaderBlock.None)]
    [InlineData("1.12.2", "release", LoaderKind.Quilt, LoaderBlock.TooOld)]
    [InlineData("1.12.2", "release", LoaderKind.Forge, LoaderBlock.None)]
    [InlineData("1.20", "release", LoaderKind.NeoForge, LoaderBlock.TooOld)]
    [InlineData("1.20.1", "release", LoaderKind.NeoForge, LoaderBlock.None)]
    [InlineData("1.21", "release", LoaderKind.NeoForge, LoaderBlock.None)]
    [InlineData("24w14a", "snapshot", LoaderKind.Fabric, LoaderBlock.None)]
    [InlineData("24w14a", "snapshot", LoaderKind.Forge, LoaderBlock.Snapshot)]
    [InlineData("24w14a", "snapshot", LoaderKind.OptiFine, LoaderBlock.Snapshot)]
    [InlineData("b1.7.3", "old_beta", LoaderKind.Forge, LoaderBlock.AncientVersion)]
    [InlineData("a1.0.4", "old_alpha", LoaderKind.Fabric, LoaderBlock.AncientVersion)]
    [InlineData("1.21-pre1", "release", LoaderKind.NeoForge, LoaderBlock.None)]
    public void LoaderAvailability(string version, string type, LoaderKind kind, LoaderBlock expected) =>
        Assert.Equal(expected, LoaderSupport.Check(version, type, kind));
}
