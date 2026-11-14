using Amber.App.Controls.Safety;
using Amber.App.Services;
using Amber.Core.Themes;
using Avalonia.Controls;

namespace Amber.Core.Tests;

/// <summary>The page-enter effect never scales or fades the locked safety surfaces, whatever a theme sets.</summary>
public sealed class Depth3DTests
{
    [Fact]
    public void APageWithASafetyPanelComesInFlat() => UiTestThread.Run(() =>
    {
        var host = new Border { Child = new StackPanel { Children = { new TextBlock { Text = "page" }, new SafetyPanel { Title = "Someone knocks" } } } };
        Depth3D.EnterPage(host, Motion3D.MaxPageDepth * 10, TimeSpan.FromSeconds(30));
        Assert.Null(host.RenderTransform);
        Assert.Equal(1, host.Opacity);
    });

    [Fact]
    public void ASafetyPanelThatShowsUpMidwayStopsTheEffectAtOnce() => UiTestThread.Run(() =>
    {
        var content = new StackPanel { Children = { new TextBlock { Text = "page" } } };
        var host = new Border { Child = content };
        // The deepest a theme may set, and a slow motion far longer than a page is allowed to take.
        Depth3D.EnterPage(host, Motion3D.MaxPageDepth, TimeSpan.FromSeconds(30));
        Assert.NotNull(host.RenderTransform);
        Assert.True(host.Opacity < 1);
        Depth3D.TickForTest(host);
        Assert.True(host.Opacity < 1);

        content.Children.Add(new SafetyButton { Content = "Deny", Kind = SafetyKind.Safe });
        Depth3D.TickForTest(host);
        Assert.Null(host.RenderTransform);
        Assert.Equal(1, host.Opacity);
        // Later frames leave it alone.
        Depth3D.TickForTest(host);
        Assert.Null(host.RenderTransform);
        Assert.Equal(1, host.Opacity);
    });

    [Fact]
    public void APageNeverTakesLongerThanTheCapToComeForward()
    {
        Assert.True(Depth3D.MaxPageEnter <= TimeSpan.FromMilliseconds(400));
        Assert.True(Motion3D.MaxPageDepth <= 0.06);
    }
}

/// <summary>The turning skin figure lets go of a drag it lost, and stops turning by itself when 3D effects go off.</summary>
[Collection("Depth3D")]
public sealed class SkinFigureControlTests
{
    [Fact]
    public void ADragWhosePointerWasTakenAwayEnds() => UiTestThread.Run(() =>
    {
        var figure = new Amber.App.Views.Controls.SkinFigure { Skin = PixelSkin.Generate("Robin") };
        var window = new Avalonia.Controls.Window { Content = figure, Width = 300, Height = 400 };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        var mouse = new Avalonia.Input.Pointer(Avalonia.Input.Pointer.GetNextFreeId(), Avalonia.Input.PointerType.Mouse, true);
        var props = new Avalonia.Input.PointerPointProperties(Avalonia.Input.RawInputModifiers.LeftMouseButton, Avalonia.Input.PointerUpdateKind.LeftButtonPressed);
        figure.RaiseEvent(new Avalonia.Input.PointerPressedEventArgs(figure, mouse, figure, new Avalonia.Point(20, 20), 0, props, Avalonia.Input.KeyModifiers.None));
        figure.RaiseEvent(new Avalonia.Input.PointerEventArgs(Avalonia.Input.InputElement.PointerMovedEvent, figure, mouse, figure, new Avalonia.Point(30, 20), 0, props, Avalonia.Input.KeyModifiers.None));
        var dragged = figure.Angle;
        Assert.NotEqual(SkinTurntable.RestAngle, dragged);

        figure.RaiseEvent(new Avalonia.Input.PointerCaptureLostEventArgs(figure, mouse));
        figure.RaiseEvent(new Avalonia.Input.PointerEventArgs(Avalonia.Input.InputElement.PointerMovedEvent, figure, mouse, figure, new Avalonia.Point(90, 20), 0, props, Avalonia.Input.KeyModifiers.None));
        Assert.Equal(dragged, figure.Angle);
        window.Close();
    });

    [Fact]
    public void TurningEffectsOffStopsAFigureThatTurnsByItself() => UiTestThread.Run(() =>
    {
        var was = Depth3D.Enabled;
        try
        {
            Depth3D.SetEnabled(true);
            var figure = new Amber.App.Views.Controls.SkinFigure { Skin = PixelSkin.Generate("Robin") };
            if (!Depth3D.Current.Turntable) return; // reduced motion on this machine: nothing turns by itself to begin with
            Assert.True(figure.StartSpin());
            Assert.True(figure.Spinning);
            Depth3D.SetEnabled(false);
            figure.OnDepthChanged();
            Assert.False(figure.Spinning);
            Assert.Equal(SkinTurntable.RestAngle, figure.Angle, 3);
        }
        finally
        {
            Depth3D.SetEnabled(was);
        }
    });
}
