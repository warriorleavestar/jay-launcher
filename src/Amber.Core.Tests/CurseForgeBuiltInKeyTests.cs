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
    public void RepositoryBuildsCarryNoKey()
    {
        // CI 护栏：只有推到 main 和打标签时才注入，跑测试的构建不该带 Key。
        // MSBuild 会把环境变量当属性读入，本机设了 JAY_CURSEFORGE_API_KEY 时这条不适用，直接跳过。
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("JAY_CURSEFORGE_API_KEY"))) return;
        Assert.Null(CurseForgeBuiltInKey.Value);
    }
}
