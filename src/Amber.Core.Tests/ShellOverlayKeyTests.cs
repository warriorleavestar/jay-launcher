using Amber.App.Views;
using Avalonia.Input;

namespace Amber.Core.Tests;

/// <summary>§8.5: over the first-run wizard and the breaker countdown the shell takes only the global escape keys.</summary>
public class ShellOverlayKeyTests
{
    [Theory]
    [InlineData(Key.T, KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift, true)]
    [InlineData(Key.Q, KeyModifiers.Control | KeyModifiers.Shift, true)]
    [InlineData(Key.V, KeyModifiers.Control, false)]
    [InlineData(Key.D1, KeyModifiers.Control, false)]
    [InlineData(Key.OemComma, KeyModifiers.Control, false)]
    [InlineData(Key.J, KeyModifiers.Control, false)]
    [InlineData(Key.Left, KeyModifiers.Alt, false)]
    [InlineData(Key.Y, KeyModifiers.Alt, false)]
    [InlineData(Key.T, KeyModifiers.Control, false)]
    public void OnlyGlobalEscapeKeysPassTheOverlay(Key key, KeyModifiers modifiers, bool allowed) =>
        Assert.Equal(allowed, MainWindow.AllowedOverBlockingOverlay(key, modifiers));
}
