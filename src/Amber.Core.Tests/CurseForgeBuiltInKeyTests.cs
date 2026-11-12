using System.Reflection;
using Amber.Core.Content;

// 只给 Read 的测试用的假值：真 Key 只在官方构建时由 CI 注入 Amber.Core。
[assembly: AssemblyMetadata("Jay.CurseForgeApiKey", " fake-test-key ")]

namespace Amber.Core.Tests;

public sealed class CurseForgeBuiltInKeyTests
{
    [Fact]
    public void ReadsTrimmedMetadata() =>
        Assert.Equal("fake-test-key", CurseForgeBuiltInKey.Read(typeof(CurseForgeBuiltInKeyTests).Assembly));

    [Fact]
    public void MissingMetadataIsNull() =>
        Assert.Null(CurseForgeBuiltInKey.Read(typeof(object).Assembly));

    [Fact]
    public void UserKeyWinsThenEnvironmentThenBuiltIn()
    {
        Assert.Equal("user", CurseForgeBuiltInKey.Resolve("user", "env"));
        Assert.Equal("env", CurseForgeBuiltInKey.Resolve(" ", "env"));
        Assert.Equal(CurseForgeBuiltInKey.Value, CurseForgeBuiltInKey.Resolve(null, ""));
    }

    [Fact]
    public void RepositoryBuildsCarryNoKey() =>
        // CI 只在推到 main 和打标签时注入；PR 和本机构建都不该带 Key。
        Assert.Null(CurseForgeBuiltInKey.Value);
}
