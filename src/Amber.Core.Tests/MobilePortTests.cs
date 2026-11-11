using Amber.Mobile.ViewModels;
using Amber.Mobile;
using System.Reflection;
using Amber.Mobile.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Amber.Core.Tests;

public sealed class MobilePortTests
{
    [Fact]
    public async Task FreshMobileProfileCreatesPrivateIdentityAndEmptyServerList()
    {
        var root = Path.Combine(Path.GetTempPath(), "jay-mobile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var viewModel = new MobileMainViewModel(root);

            Assert.Matches("^[0-9A-Z]{4}(?:-[0-9A-Z]{4}){3}$", viewModel.DeviceFingerprint);
            Assert.True(viewModel.HasNoServers);
            Assert.Empty(viewModel.Servers);
            var version = typeof(MobileMainViewModel).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
            Assert.Contains(version, viewModel.VersionText);
            Assert.True(File.Exists(Path.Combine(root, "identity.key")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }    
    [Fact]
    public void MobileLayoutReflowsAcrossPhoneAndTabletWidths()
    {
        UiTestThread.Run(() =>
        {
            var view = new MainView();
            var columns = view.FindControl<Grid>("ContentColumns")!;
            var server = view.FindControl<StackPanel>("ServerColumn")!;
            foreach (var (width, height, wide) in new[]
            {
                (320, 568, false), (412, 915, false), (720, 360, false),
                (800, 1280, true), (1280, 800, true), (360, 640, false)
            })
            {
                var size = new Size(width, height);
                view.Measure(size);
                view.Arrange(new Rect(size));
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(wide ? 1 : 0, Grid.GetColumn(server));
                Assert.Equal(wide ? 0 : 1, Grid.GetRow(server));
                Assert.Equal(wide ? 2 : 1, columns.ColumnDefinitions.Count);
            }
        });
    }

    [Fact]
    public async Task AndroidGameRuntimeReceivesTheRunningGatewayAddress()
    {
        var root = Path.Combine(Path.GetTempPath(), "jay-mobile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var platform = new FakeMobilePlatformServices();
            await using var viewModel = new MobileMainViewModel(root, platform);
            Assert.True(viewModel.ShowGameLauncher);

            viewModel.IsConnected = true;
            viewModel.LocalAddress = "127.0.0.1:25565";
            await viewModel.OpenGameLauncherCommand.ExecuteAsync(null);

            Assert.Equal("127.0.0.1:25565", platform.OpenedWithAddress);
            Assert.Contains("Jay", viewModel.Status);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void DefaultTouchControlsStayOnScreenAndHitTheirCenters()
    {
        Assert.Contains(TouchControlLayout.Default, control => control.Action == TouchControlAction.Move);
        Assert.Contains(TouchControlLayout.Default, control => control.Action == TouchControlAction.Jump);
        Assert.Contains(TouchControlLayout.Default, control => control.Action == TouchControlAction.PrimaryAction);

        foreach (var control in TouchControlLayout.Default)
        {
            Assert.InRange(control.CenterX - control.Radius, 0, 1);
            Assert.InRange(control.CenterX + control.Radius, 0, 1);
            Assert.InRange(control.CenterY - control.Radius, 0, 1);
            Assert.InRange(control.CenterY + control.Radius, 0, 1);
            Assert.Equal(control.Action, TouchControlLayout.HitTest(control.CenterX, control.CenterY)!.Action);
        }

        var move = TouchControlLayout.Default.Single(control => control.Action == TouchControlAction.Move);
        var right = TouchControlLayout.JoystickVector(move.CenterX + move.Radius * 2, move.CenterY);
        Assert.Equal(1, right.X, 6);
        Assert.Equal(0, right.Y, 6);
    }

    [Fact]
    public void TouchInputRouterKeepsSimultaneousMovementAndJumpIndependent()
    {
        var sink = new RecordingTouchSink();
        var router = new TouchInputRouter(sink);
        var move = TouchControlLayout.Default.Single(control => control.Action == TouchControlAction.Move);
        var jump = TouchControlLayout.Default.Single(control => control.Action == TouchControlAction.Jump);

        Assert.True(router.Press(10, move.CenterX, move.CenterY));
        Assert.True(router.Move(10, move.CenterX + move.Radius, move.CenterY));
        Assert.True(router.Press(11, jump.CenterX, jump.CenterY));
        Assert.Contains((TouchControlAction.Jump, true), sink.Actions);
        Assert.Equal(1, sink.LastMovement.X, 6);

        Assert.True(router.Release(11));
        Assert.Contains((TouchControlAction.Jump, false), sink.Actions);
        Assert.Equal(1, sink.LastMovement.X, 6);
        Assert.True(router.Release(10));
        Assert.Equal((0d, 0d), sink.LastMovement);
    }

    private sealed class FakeMobilePlatformServices : IMobilePlatformServices
    {
        public bool CanOpenGameLauncher => true;
        public string GameLauncherName => "Jay";
        public string? OpenedWithAddress { get; private set; }

        public Task<MobileGameLauncherResult> OpenGameLauncherAsync(string? serverAddress)
        {
            OpenedWithAddress = serverAddress;
            return Task.FromResult(MobileGameLauncherResult.Launched);
        }

        public void SetGatewayActive(bool active) { }
    }

    private sealed class RecordingTouchSink : ITouchInputSink
    {
        public List<(TouchControlAction Action, bool Pressed)> Actions { get; } = [];
        public (double X, double Y) LastMovement { get; private set; }

        public void SetAction(TouchControlAction action, bool pressed) => Actions.Add((action, pressed));
        public void SetMovement(double x, double y) => LastMovement = (x, y);
    }

}
