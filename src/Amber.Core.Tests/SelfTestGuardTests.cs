using Amber.App.Views;

namespace Amber.Core.Tests;

/// <summary>The GUI self-check refuses to run against the developer's own settings or the real .minecraft.</summary>
public class SelfTestGuardTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void NoTestDataFolderIsRefused(string? dataDir) => Assert.NotNull(SelfTestGuard.DataDirProblem(dataDir));

    [Fact]
    public void ATestDataFolderIsAccepted() => Assert.Null(SelfTestGuard.DataDirProblem(Path.GetTempPath()));

    [Fact]
    public void TheDefaultGameFolderIsRefused()
    {
        var root = Path.Combine(Path.GetTempPath(), "jay-guard", ".minecraft");
        Assert.NotNull(SelfTestGuard.GameDirProblem(root, root));
        Assert.NotNull(SelfTestGuard.GameDirProblem(root + Path.DirectorySeparatorChar, root));
        Assert.NotNull(SelfTestGuard.GameDirProblem("", root));
        Assert.Null(SelfTestGuard.GameDirProblem(Path.Combine(Path.GetTempPath(), "jay-guard", "game"), root));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("content", true)]
    [InlineData("libary", false)]
    [InlineData("shell ", false)]
    public void OnlyTheFullRunAndContentReachTheFullFlow(string? scope, bool full) =>
        Assert.Equal(full, SelfTestGuard.IsFullFlowScope(scope));
}
