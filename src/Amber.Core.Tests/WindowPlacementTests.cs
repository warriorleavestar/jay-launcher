using Amber.App.Services;
using Amber.App.Views;

namespace Amber.Core.Tests;

/// <summary>
/// The window's size stays NaN when no screen was found at startup (some Linux setups). Such a placement must not be
/// saved: NaN can't be written as JSON, so closing the window threw and every later settings save failed.
/// </summary>
public sealed class WindowPlacementTests
{
    [Theory]
    [InlineData(1100, 700, true)]
    [InlineData(double.NaN, 700, false)]
    [InlineData(1100, double.NaN, false)]
    [InlineData(double.PositiveInfinity, 700, false)]
    [InlineData(0, 700, false)]
    public void OnlyFinitePositiveSizesAreSaved(double width, double height, bool usable) =>
        Assert.Equal(usable, MainWindow.IsUsable(new WindowPlacement { Width = width, Height = height }));

    [Fact]
    public void ANaNPlacementWouldBreakEverySettingsSave()
    {
        var dir = Path.Combine(Path.GetTempPath(), "jay-placement-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new AppSettings { SavePathForTest = Path.Combine(dir, "settings.json"), LauncherWindow = new WindowPlacement { Width = double.NaN, Height = 700 } };
            Assert.Throws<ArgumentException>(settings.Save);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
