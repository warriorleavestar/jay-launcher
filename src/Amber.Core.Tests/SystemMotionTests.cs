using Amber.App.Services;

namespace Amber.Core.Tests;

/// <summary>Reduced motion: a theme pack applied at startup doesn't read the system setting on the UI thread.</summary>
public class SystemMotionTests
{
    [Fact]
    public void ReapplyBeforeStartDoesNotReadTheSystemSetting() => UiTestThread.Run(() =>
    {
        // The test process never calls SystemMotion.Start (no app is running), which is the state at startup when
        // ThemeService.ApplyAtStartup runs before Start.
        var before = SystemMotion.SystemQueries;
        SystemMotion.Reapply();
        Assert.Equal(before, SystemMotion.SystemQueries);
    });
}
