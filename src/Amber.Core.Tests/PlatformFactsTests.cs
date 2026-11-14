namespace Amber.Core.Tests;

/// <summary>The platform and opt-in test attributes skip exactly where their tests cannot run.</summary>
public class PlatformFactsTests
{
    [Fact]
    public void PlatformAttributesSkipOnOtherSystems()
    {
        Assert.Equal(OperatingSystem.IsWindows(), new WindowsFactAttribute().Skip is null);
        Assert.Equal(OperatingSystem.IsWindows(), new WindowsTheoryAttribute().Skip is null);
        Assert.Equal(!OperatingSystem.IsWindows(), new UnixFactAttribute().Skip is null);
        Assert.Equal(!OperatingSystem.IsWindows(), new UnixTheoryAttribute().Skip is null);
        Assert.Equal(OperatingSystem.IsLinux(), new LinuxFactAttribute().Skip is null);
        Assert.Equal(OperatingSystem.IsMacOS(), new MacFactAttribute().Skip is null);
    }

    [Fact]
    public void EnvFactSkipsUntilTheVariableHasTheValue()
    {
        var name = "JAY_TEST_OPT_IN_" + Guid.NewGuid().ToString("N");
        try
        {
            Assert.NotNull(new EnvFactAttribute(name).Skip);
            Assert.NotNull(new EnvFactAttribute(name, "1").Skip);
            Environment.SetEnvironmentVariable(name, "0");
            Assert.Null(new EnvFactAttribute(name).Skip);
            Assert.NotNull(new EnvFactAttribute(name, "1").Skip);
            Environment.SetEnvironmentVariable(name, "1");
            Assert.Null(new EnvFactAttribute(name, "1").Skip);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }
}
