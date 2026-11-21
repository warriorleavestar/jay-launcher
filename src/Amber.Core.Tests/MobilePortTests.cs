using Amber.Mobile.ViewModels;
using Amber.Mobile;
using System.Reflection;
using Amber.Mobile.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using System.Security.Cryptography;
using System.Formats.Tar;
using System.IO.Compression;
using System.Collections.Concurrent;

namespace Amber.Core.Tests;

public sealed class MobilePortTests
{
    [Theory]
    [InlineData("25.0.5-internal", 25)]
    [InlineData("21.0.7+6-LTS", 21)]
    [InlineData("1.8.0_462", 8)]
    [InlineData("17", 17)]
    public void AndroidRuntimeReleaseParsesModernAndLegacyJavaVersions(string version, int expected)
    {
        Assert.Equal(expected, JavaRuntimeRelease.ParseMajor(version));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-java")]
    [InlineData("1")]
    [InlineData("0.0")]
    public void AndroidRuntimeReleaseRejectsInvalidVersions(string version)
    {
        Assert.Null(JavaRuntimeRelease.ParseMajor(version));
    }

    [Fact]
    public void AndroidJava21RuntimeDoesNotRequireJava25GraphicsLibraries()
    {
        var required = AndroidNativeLibraries.RequiredForJava(21);

        Assert.Contains("libmobileglues.so", required);
        Assert.Contains("liblwjgl_opengl.so", required);
        Assert.DoesNotContain("liblwjgl_vma.so", required);
        Assert.DoesNotContain("libshaderc.so", required);
        Assert.DoesNotContain("libspirv-cross.so", required);
    }

    [Fact]
    public void AndroidJava25RuntimeRequiresExperimentalGraphicsLibraries()
    {
        var required = AndroidNativeLibraries.RequiredForJava(25);

        Assert.Contains("liblwjgl_vma.so", required);
        Assert.Contains("libjnidispatch.so", required);
        Assert.Contains(AndroidNativeLibraries.Shaderc, required);
        Assert.Contains(AndroidNativeLibraries.SpirvCross, required);
        Assert.Equal("libshaderc.so", AndroidNativeLibraries.Shaderc);
        Assert.Equal("libspirv-cross.so", AndroidNativeLibraries.SpirvCross);
    }

    [Fact]
    public void AndroidSdlUsesCompleteDownloadedNativesAndOnlyFallsBackForLegacyPackages()
    {
        var root = Path.Combine(Path.GetTempPath(), "jay-native-layout-" + Guid.NewGuid().ToString("N"));
        var runtime = Path.Combine(root, "runtime");
        var native = Path.Combine(runtime, "native");
        var packaged = Path.Combine(root, "packaged");
        Directory.CreateDirectory(native);
        Directory.CreateDirectory(packaged);
        try
        {
            Assert.Equal(packaged, AndroidNativeLibraries.ResolveRoot(runtime, packaged, 25, true));
            foreach (var name in AndroidNativeLibraries.RequiredForJava(25))
                File.WriteAllBytes(Path.Combine(native, name), [1]);

            Assert.Equal(native, AndroidNativeLibraries.ResolveRoot(runtime, packaged, 25, true));
            Assert.Equal(native, AndroidNativeLibraries.ResolveRoot(runtime, packaged, 25, false));

            var packagedSdl = Path.Combine(packaged, "libSDL3.so");
            File.WriteAllBytes(packagedSdl, [1]);
            Assert.Equal(packagedSdl, AndroidNativeLibraries.ResolveLibrary("libSDL3.so", native, packaged));
            var runtimeSdl = Path.Combine(native, "libSDL3.so");
            File.WriteAllBytes(runtimeSdl, [1]);
            Assert.Equal(runtimeSdl, AndroidNativeLibraries.ResolveLibrary("libSDL3.so", native, packaged));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void AndroidJava25CatalogSelectsBothModernAbisAndQuarantinesTheLegacyBundle()
    {
        var armDescriptor = "runtime25-arm64-v8a.json";
        var armArchive = "jay-android-runtime25-arm64-v8a.tar.xz";
        var arm = AndroidRuntimeAssetCatalog.SelectJava25("arm64-v8a", 16_384,
            [armDescriptor, armArchive]);
        Assert.Equal(new AndroidRuntimeAssets(armDescriptor, armArchive), arm);

        var x64Descriptor = "runtime25-x86_64.json";
        var x64 = AndroidRuntimeAssetCatalog.SelectJava25("x86_64", 16_384,
            [x64Descriptor]);
        Assert.Equal(new AndroidRuntimeAssets(x64Descriptor, null), x64);

        Assert.Null(AndroidRuntimeAssetCatalog.SelectJava25("x86_64", 4096,
            ["jre25.json", "jre25-arm64-v8a.tar.xz"]));
        Assert.Null(AndroidRuntimeAssetCatalog.SelectJava25("arm64-v8a", 16_384,
            ["jre25.json", "jre25-arm64-v8a.tar.xz"]));
        Assert.Equal(new AndroidRuntimeAssets("jre25.json", "jre25-arm64-v8a.tar.xz"),
            AndroidRuntimeAssetCatalog.SelectJava25("arm64-v8a", 4096,
                ["jre25.json", "jre25-arm64-v8a.tar.xz"]));
    }

    [Fact]
    public void AndroidRuntimeReleaseReadsTheStandardReleaseFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "jay-java-release-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "release"),
                "IMPLEMENTOR=\"Jay test\"\nJAVA_VERSION=\"25.0.5-internal\"\n");
            Assert.Equal(25, JavaRuntimeRelease.ReadMajor(root));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

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
            Assert.True(File.Exists(Path.Combine(root, Amber.Core.Multiplayer.MemberIdentity.FileName)));
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
            var sidebar = view.FindControl<Border>("SidebarHost")!;
            var bottom = view.FindControl<Border>("BottomNav")!;
            var content = view.FindControl<Grid>("ContentHost")!;
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
                Assert.Equal(width >= 600, sidebar.IsVisible);
                Assert.Equal(width < 600, bottom.IsVisible);
                Assert.Equal(width < 600 ? 0 : 1, Grid.GetColumn(content));
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
        var profile = new TouchControlProfile();
        var controls = profile.Controls;
        Assert.Contains(controls, control => control.Action == TouchControlAction.Move);
        Assert.Contains(controls, control => control.Action == TouchControlAction.Jump);
        Assert.DoesNotContain(controls, control => control.Action == TouchControlAction.PrimaryAction);
        Assert.Contains(controls, control => control.Action == TouchControlAction.Sneak);
        Assert.DoesNotContain(controls, control => control.Action == TouchControlAction.Sprint);
        Assert.DoesNotContain(controls, control => control.Action == TouchControlAction.PreviousSlot);
        Assert.DoesNotContain(controls, control => control.Action == TouchControlAction.NextSlot);

        foreach (var control in controls)
        {
            var halfWidth = control.Action == TouchControlAction.Inventory
                ? control.Radius * 0.82 / profile.ViewportAspectRatio
                : control.Radius / profile.ViewportAspectRatio;
            var halfHeight = control.Action == TouchControlAction.Inventory
                ? control.Radius * 0.82
                : control.Radius;
            Assert.InRange(control.CenterX - halfWidth, 0, 1);
            Assert.InRange(control.CenterX + halfWidth, 0, 1);
            Assert.InRange(control.CenterY - halfHeight, 0, 1);
            Assert.InRange(control.CenterY + halfHeight, 0, 1.001);
            if (control.GameplayVisible)
            {
                if (control.Action == TouchControlAction.Move)
                    Assert.Equal(TouchControlAction.Move,
                        TouchControlLayout.HitTest(control.CenterX +
                            control.Radius * 0.65 / profile.ViewportAspectRatio, control.CenterY)!.Action);
                else
                    Assert.Equal(control.Action, TouchControlLayout.HitTest(control.CenterX, control.CenterY)!.Action);
            }
            if (control.MenuVisible)
                Assert.Equal(control.Action, TouchControlLayout.HitTest(control.CenterX, control.CenterY,
                    gameplayControlsActive: false)!.Action);
        }

        var move = TouchControlLayout.Default.Single(control => control.Action == TouchControlAction.Move);
        var right = TouchControlLayout.JoystickVector(move.CenterX +
            move.Radius * 2 / profile.ViewportAspectRatio, move.CenterY);
        Assert.Equal(1, right.X, 6);
        Assert.Equal(0, right.Y, 6);
        Assert.DoesNotContain(TouchControlLayout.Default, control => control.Label is "Q" or "E" or "Run" or "Use");
        Assert.Equal("背包", TouchControlLayout.Default.Single(control => control.Action == TouchControlAction.Inventory).Label);
        Assert.DoesNotContain(TouchControlLayout.Default, control => control.Action == TouchControlAction.Drop);

        var menu = TouchControlLayout.Default.Single(control => control.Action == TouchControlAction.Menu);
        var movePad = TouchControlLayout.Default.Single(control => control.Action == TouchControlAction.Move);
        var inventory = TouchControlLayout.Default.Single(control => control.Action == TouchControlAction.Inventory);
        var jump = TouchControlLayout.Default.Single(control => control.Action == TouchControlAction.Jump);
        var sneak = TouchControlLayout.Default.Single(control => control.Action == TouchControlAction.Sneak);
        Assert.InRange(menu.CenterX, 0.47, 0.53);
        Assert.True(movePad.CenterX < 0.25);
        Assert.InRange(movePad.CenterY, 0.79, 0.81);
        Assert.InRange(inventory.CenterX, 0.60, 0.61);
        Assert.InRange(inventory.CenterY, 0.98, 0.982);
        Assert.InRange(inventory.Radius, 0.023, 0.025);
        Assert.True(jump.CenterY > 0.78);
        Assert.Equal(movePad.CenterX, sneak.CenterX, 6);
        Assert.Equal(movePad.CenterY, sneak.CenterY, 6);
        Assert.True(jump.Radius >= 0.05 && sneak.Radius >= 0.035);
        Assert.DoesNotContain(TouchControlLayout.Default,
            control => control.Action == TouchControlAction.SecondaryAction);
    }

    [Fact]
    public void TouchProfilesExposeFourDistinctMobileSchemes()
    {
        var profile = new TouchControlProfile();
        Assert.DoesNotContain(profile.Controls, control => control.Action == TouchControlAction.PrimaryAction);

        profile.Mode = TouchControlMode.DPadAndTap;
        var move = profile.Controls.Single(control => control.Action == TouchControlAction.Move);
        Assert.Equal(TouchControlAction.Sneak,
            profile.HitTest(move.CenterX, move.CenterY)!.Action);
        var direction = profile.MovementVector(move.CenterX +
            move.Radius / profile.ViewportAspectRatio, move.CenterY + move.Radius * 0.15);
        Assert.Equal(1, direction.X, 6);
        Assert.Equal(0, direction.Y, 6);

        profile.Mode = TouchControlMode.JoystickAndCrosshair;
        Assert.DoesNotContain(profile.Controls, control => control.Action == TouchControlAction.PrimaryAction);
        Assert.DoesNotContain(profile.Controls, control =>
            control.Action == TouchControlAction.SecondaryAction && control.GameplayVisible);

        profile.Mode = TouchControlMode.ExternalKeyboardMouse;
        Assert.Equal(2, profile.Controls.Count);
        Assert.Contains(profile.Controls, control =>
            control.Action == TouchControlAction.Settings && control.GameplayVisible && control.MenuVisible);
        Assert.Contains(profile.Controls, control =>
            control.Action == TouchControlAction.Close && !control.GameplayVisible && control.MenuVisible);
    }

    [Fact]
    public void TouchControlsUseSquarePixelGeometryOnLandscapeTablets()
    {
        var profile = new TouchControlProfile
        {
            Mode = TouchControlMode.JoystickAndTap,
            ViewportAspectRatio = 1.6
        };
        var move = profile.Controls.Single(control => control.Action == TouchControlAction.Move);
        var jump = profile.Controls.Single(control => control.Action == TouchControlAction.Jump);

        // One rendered radius is radius/aspect in normalised X but radius in normalised Y.
        Assert.Equal(TouchControlAction.Move, profile.HitTest(
            move.CenterX + move.Radius * 0.99 / profile.ViewportAspectRatio,
            move.CenterY)!.Action);
        Assert.Null(profile.HitTest(
            move.CenterX + move.Radius * 1.01 / profile.ViewportAspectRatio,
            move.CenterY));
        Assert.Equal(TouchControlAction.Jump, profile.HitTest(
            jump.CenterX + jump.Radius * 0.99 / profile.ViewportAspectRatio,
            jump.CenterY)!.Action);
        Assert.Null(profile.HitTest(
            jump.CenterX + jump.Radius * 1.01 / profile.ViewportAspectRatio,
            jump.CenterY));

        var rightEdge = profile.MovementVector(
            move.CenterX + move.Radius / profile.ViewportAspectRatio, move.CenterY);
        Assert.Equal(1, rightEdge.X, 6);
        Assert.Equal(0, rightEdge.Y, 6);
    }

    [Fact]
    public void TouchGestureThresholdsUseSquarePixelGeometryOnLandscapeTablets()
    {
        var profile = new TouchControlProfile { ViewportAspectRatio = 1.6 };
        var sink = new RecordingTouchSink();
        var router = new TouchInputRouter(sink, profile);

        Assert.True(router.Press(1, 0.50, 0.40));
        Assert.True(router.Move(1, 0.50 + 0.014 / profile.ViewportAspectRatio, 0.40));
        Assert.True(router.Release(1));
        Assert.Single(sink.Pointers, pointer => pointer.Click);

        Assert.True(router.Press(2, 0.50, 0.40));
        Assert.True(router.Move(2, 0.50 + 0.016 / profile.ViewportAspectRatio, 0.40));
        Assert.True(router.Release(2));
        Assert.Single(sink.Pointers, pointer => pointer.Click);
    }

    [Fact]
    public void TouchMenuClassifiesDragDirectionInPhysicalPixels()
    {
        var profile = new TouchControlProfile { ViewportAspectRatio = 1.6 };
        var sink = new RecordingTouchSink();
        var router = new TouchInputRouter(sink, profile);

        Assert.True(router.Press(1, 0.35, 0.45, controlsEnabled: false));
        Assert.True(router.Move(1, 0.40, 0.52));
        Assert.Contains((TouchControlAction.PrimaryAction, true), sink.Actions);
        Assert.DoesNotContain(sink.Actions,
            action => action.Action is TouchControlAction.PreviousSlot or TouchControlAction.NextSlot);
        Assert.True(router.Release(1));
        Assert.Contains((TouchControlAction.PrimaryAction, false), sink.Actions);
    }

    [Fact]
    public void TouchProfileAppliesScaleAndCustomPositionsToHitTesting()
    {
        var profile = new TouchControlProfile { Scale = 1.2 };
        profile.SetPosition(TouchControlAction.Jump, 0.80, 0.70);
        var jump = profile.Controls.Single(control => control.Action == TouchControlAction.Jump);

        Assert.Equal(0.80, jump.CenterX, 6);
        Assert.Equal(0.70, jump.CenterY, 6);
        Assert.Equal(TouchControlAction.Jump, profile.HitTest(0.80, 0.70)!.Action);
        profile.ResetPositions();
        Assert.NotEqual(0.80, profile.Controls.Single(control => control.Action == TouchControlAction.Jump).CenterX);
    }

    [Fact]
    public void TouchHudKeepsInventoryAttachedToHotbarAcrossTabletAspectRatios()
    {
        var phone = TouchHudGeometry.ForAspectRatio(20d / 9d);
        var tablet = TouchHudGeometry.ForAspectRatio(1.6);
        Assert.True(phone.InventoryX > phone.HotbarRight);
        Assert.True(tablet.InventoryX > tablet.HotbarRight);
        Assert.True(tablet.HotbarRight - tablet.HotbarLeft > phone.HotbarRight - phone.HotbarLeft);
        Assert.True(tablet.TryGetSlot(tablet.HotbarLeft + 0.001, 0.90, out var first));
        Assert.Equal(0, first);
        Assert.True(tablet.TryGetSlot(tablet.HotbarRight - 0.001, 0.99, out var last));
        Assert.Equal(8, last);

        var profile = new TouchControlProfile { ViewportAspectRatio = 1.6 };
        var inventory = profile.Controls.Single(control => control.Action == TouchControlAction.Inventory);
        Assert.Equal(tablet.InventoryX, inventory.CenterX, 6);
        Assert.Equal(tablet.CenterY, inventory.CenterY, 6);
        Assert.InRange(inventory.CenterY, 0.98, 0.982);
        Assert.Equal(tablet.HotbarRight,
            inventory.CenterX - inventory.Radius * 0.82 / 1.6, 6);
        Assert.Null(profile.HitTest(tablet.HotbarRight - 0.001, tablet.CenterY));
        Assert.Equal(TouchControlAction.Inventory,
            profile.HitTest(inventory.CenterX, inventory.CenterY)!.Action);

        profile.Scale = 1.35;
        inventory = profile.Controls.Single(control => control.Action == TouchControlAction.Inventory);
        var enlarged = TouchHudGeometry.ForAspectRatio(1.6, inventory.Radius);
        Assert.Equal(enlarged.InventoryX, inventory.CenterX, 6);
        Assert.Equal(tablet.HotbarRight,
            inventory.CenterX - inventory.Radius * 0.82 / 1.6, 6);
        Assert.True(inventory.CenterY + inventory.Radius * 0.82 <= 1);
        Assert.Null(profile.HitTest(tablet.HotbarRight - 0.001, enlarged.CenterY));
        Assert.Equal(TouchControlAction.Inventory,
            profile.HitTest(inventory.CenterX, inventory.CenterY)!.Action);
    }

    [Fact]
    public void TouchProfileOffersIndependentAttackAreas()
    {
        var profile = new TouchControlProfile { ViewportAspectRatio = 1.6 };
        Assert.True(profile.AllowsAttackAt(0.15, 0.25));
        profile.AttackArea = TouchAttackArea.RightSide;
        Assert.False(profile.AllowsAttackAt(0.20, 0.50));
        Assert.True(profile.AllowsAttackAt(0.80, 0.50));
        profile.AttackArea = TouchAttackArea.Crosshair;
        Assert.True(profile.AllowsAttackAt(0.50, 0.50));
        Assert.False(profile.AllowsAttackAt(0.75, 0.50));
    }

    [Fact]
    public void TouchSettingsRemainAvailableWhileMinecraftMenusAreOpen()
    {
        var sink = new RecordingTouchSink();
        var profile = new TouchControlProfile();
        var router = new TouchInputRouter(sink, profile);
        var settings = profile.Controls.Single(control => control.Action == TouchControlAction.Settings);

        Assert.True(router.Press(61, settings.CenterX, settings.CenterY, controlsEnabled: false));
        Assert.Contains((TouchControlAction.Settings, true), sink.Actions);
        Assert.True(router.Release(61));
        Assert.Contains((TouchControlAction.Settings, false), sink.Actions);
    }

    [Fact]
    public void TouchInputRouterKeepsSimultaneousMovementAndJumpIndependent()
    {
        var sink = new RecordingTouchSink();
        var profile = new TouchControlProfile();
        var router = new TouchInputRouter(sink, profile);
        var move = profile.Controls.Single(control => control.Action == TouchControlAction.Move);
        var jump = profile.Controls.Single(control => control.Action == TouchControlAction.Jump);

        Assert.True(router.Press(10, move.CenterX + move.Radius * 0.65 / profile.ViewportAspectRatio, move.CenterY));
        Assert.True(router.Move(10, move.CenterX + move.Radius / profile.ViewportAspectRatio, move.CenterY));
        Assert.True(router.Press(11, jump.CenterX, jump.CenterY));
        Assert.Contains((TouchControlAction.Jump, true), sink.Actions);
        Assert.Equal(1, sink.LastMovement.X, 6);

        Assert.True(router.Release(11));
        Assert.Contains((TouchControlAction.Jump, false), sink.Actions);
        Assert.Equal(1, sink.LastMovement.X, 6);
        Assert.True(router.Release(10));
        Assert.Equal((0d, 0d), sink.LastMovement);
    }

    [Fact]
    public void TouchInputRouterRestoresFirstMovementFingerWhenSecondOneEnds()
    {
        var sink = new RecordingTouchSink();
        var profile = new TouchControlProfile { Mode = TouchControlMode.JoystickAndTap };
        var router = new TouchInputRouter(sink, profile);
        var move = profile.Controls.Single(control => control.Action == TouchControlAction.Move);

        Assert.True(router.Press(101, move.CenterX + move.Radius * 0.8 / profile.ViewportAspectRatio, move.CenterY));
        Assert.Equal(0.8, sink.LastMovement.X, 6);
        Assert.True(router.Press(102, move.CenterX - move.Radius * 0.8 / profile.ViewportAspectRatio, move.CenterY));
        Assert.Equal(-0.8, sink.LastMovement.X, 6);

        Assert.True(router.Release(102));
        Assert.Equal(0.8, sink.LastMovement.X, 6);
        Assert.True(router.Release(101));
        Assert.Equal((0d, 0d), sink.LastMovement);
    }

    [Fact]
    public void TouchInputRouterAutoSprintsWhenTheJoystickIsPushedFullyForward()
    {
        var sink = new RecordingTouchSink();
        var router = new TouchInputRouter(sink);
        var move = TouchControlLayout.Default.Single(control => control.Action == TouchControlAction.Move);

        Assert.True(router.Press(50, move.CenterX, move.CenterY - move.Radius * 0.65));
        Assert.True(router.Move(50, move.CenterX, move.CenterY - move.Radius));
        Assert.True(router.SprintLocked);
        Assert.True(router.IsPressed(TouchControlAction.Sprint));
        Assert.Contains((TouchControlAction.Sprint, true), sink.Actions);
        Assert.True(router.Release(50));
        Assert.False(router.SprintLocked);
        Assert.Contains((TouchControlAction.Sprint, false), sink.Actions);
    }

    [Fact]
    public void TouchInputRouterTurnsAStationaryLongPressIntoMining()
    {
        var sink = new RecordingTouchSink();
        var router = new TouchInputRouter(sink);

        Assert.True(router.Press(12, 0.55, 0.42));
        Assert.True(router.IsLongPressCandidate(12));
        Assert.True(router.BeginLongPress(12));
        Assert.True(router.IsLongPress(12));
        Assert.Contains((TouchControlAction.PrimaryAction, true), sink.Actions);
        Assert.True(router.Release(12));
        Assert.Contains((TouchControlAction.PrimaryAction, false), sink.Actions);
        Assert.DoesNotContain(sink.Pointers, pointer => pointer.Click);
    }

    [Fact]
    public void TouchInputRouterCancelsMiningWhenTheFingerTurnsIntoALookDrag()
    {
        var sink = new RecordingTouchSink();
        var router = new TouchInputRouter(sink);

        Assert.True(router.Press(15, 0.55, 0.42));
        Assert.True(router.BeginLongPress(15));
        Assert.True(router.Move(15, 0.62, 0.37));
        Assert.Contains(TouchControlAction.PrimaryAction, sink.CanceledActions);
        Assert.False(router.IsLongPress(15));
        Assert.Equal(0.07 * 1.65, sink.LastLook.X, 6);
        Assert.Equal(-0.05 * 1.65, sink.LastLook.Y, 6);
        Assert.True(router.Release(15));
    }

    [Fact]
    public void TouchInputRouterLetsSecondFingerAimWhileMovementStaysHeld()
    {
        var sink = new RecordingTouchSink();
        var profile = new TouchControlProfile();
        var router = new TouchInputRouter(sink, profile);
        var move = profile.Controls.Single(control => control.Action == TouchControlAction.Move);

        Assert.True(router.Press(13, move.CenterX +
            move.Radius * 0.65 / profile.ViewportAspectRatio, move.CenterY));
        Assert.True(router.Move(13, move.CenterX +
            move.Radius / profile.ViewportAspectRatio, move.CenterY));
        Assert.Equal(1, sink.LastMovement.X, 6);

        Assert.True(router.Press(14, 0.55, 0.38));
        Assert.True(router.Move(14, 0.63, 0.32));
        Assert.Equal(0.08 * 1.65, sink.LastLook.X, 6);
        Assert.Equal(-0.06 * 1.65, sink.LastLook.Y, 6);
        Assert.Equal(1, sink.LastMovement.X, 6);

        Assert.True(router.Release(14));
        Assert.Equal(1, sink.LastMovement.X, 6);
        Assert.True(router.Release(13));
    }

    [Fact]
    public void TouchInputRouterTurnsUnoccupiedScreenDragsIntoCameraLook()
    {
        var sink = new RecordingTouchSink();
        var router = new TouchInputRouter(sink);

        Assert.True(router.Press(20, 0.52, 0.45));
        Assert.True(router.Move(20, 0.60, 0.39));
        Assert.Equal(0.08 * 1.65, sink.LastLook.X, 6);
        Assert.Equal(-0.06 * 1.65, sink.LastLook.Y, 6);
        Assert.True(router.Release(20));
        Assert.Empty(sink.Actions);
        Assert.Equal((0.60, 0.39, false), sink.LastPointer);
        Assert.DoesNotContain(sink.Pointers, pointer => pointer.Click);
    }

    [Fact]
    public void TouchInputRouterTurnsGameplayTapsIntoOneClick()
    {
        var sink = new RecordingTouchSink();
        var router = new TouchInputRouter(sink);

        Assert.True(router.Press(23, 0.52, 0.45));
        Assert.True(router.Release(23));
        Assert.Single(sink.Pointers, pointer => pointer.Click);
    }

    [Fact]
    public void TouchInputRouterAppliesSensitivityAndAttackAreaWithoutDisablingLook()
    {
        var sink = new RecordingTouchSink();
        var profile = new TouchControlProfile
        {
            AttackArea = TouchAttackArea.RightSide,
            LookSensitivity = 2
        };
        var router = new TouchInputRouter(sink, profile);

        Assert.True(router.Press(70, 0.20, 0.40));
        Assert.True(router.Release(70));
        Assert.DoesNotContain(sink.Pointers, pointer => pointer.Click);

        Assert.True(router.Press(71, 0.20, 0.40));
        Assert.True(router.Move(71, 0.25, 0.35));
        Assert.Equal(0.10, sink.LastLook.X, 6);
        Assert.Equal(-0.10, sink.LastLook.Y, 6);
        Assert.True(router.Release(71));

        Assert.True(router.Press(72, 0.75, 0.40));
        Assert.True(router.Release(72));
        Assert.Single(sink.Pointers, pointer => pointer.Click);
    }

    [Fact]
    public void TouchInputRouterBypassesControlsWhenGameplayOverlayIsDisabled()
    {
        var sink = new RecordingTouchSink();
        var router = new TouchInputRouter(sink);
        var jump = TouchControlLayout.Default.Single(control => control.Action == TouchControlAction.Jump);

        Assert.True(router.Press(21, jump.CenterX, jump.CenterY, controlsEnabled: false));
        Assert.DoesNotContain(sink.Actions, action => action.Action == TouchControlAction.Jump);
        Assert.Equal((jump.CenterX, jump.CenterY, false), sink.LastPointer);
        Assert.True(router.Release(21));
        Assert.Single(sink.Pointers, pointer => pointer.Click);

        var close = TouchControlLayout.Default.Single(control => control.Action == TouchControlAction.Close);
        Assert.True(router.Press(22, close.CenterX, close.CenterY, controlsEnabled: false));
        Assert.Contains((TouchControlAction.Close, true), sink.Actions);
    }

    [Fact]
    public void TouchInputRouterHoldsMenuPointerForSliders()
    {
        var sink = new RecordingTouchSink();
        var router = new TouchInputRouter(sink);

        Assert.True(router.Press(30, 0.35, 0.55, controlsEnabled: false));
        Assert.DoesNotContain(sink.Actions, action => action.Action == TouchControlAction.PrimaryAction);
        Assert.True(router.Move(30, 0.70, 0.55));
        Assert.Contains((TouchControlAction.PrimaryAction, true), sink.Actions);
        Assert.True(router.Release(30));
        Assert.Contains((TouchControlAction.PrimaryAction, false), sink.Actions);
        Assert.Equal((0.70, 0.55, false), sink.LastPointer);
    }

    [Fact]
    public void TouchInputRouterTurnsMenuSwipesIntoScrollEvents()
    {
        var sink = new RecordingTouchSink();
        var router = new TouchInputRouter(sink);

        Assert.True(router.Press(31, 0.50, 0.70, controlsEnabled: false));
        Assert.True(router.Move(31, 0.50, 0.55));
        Assert.True(router.Move(31, 0.50, 0.40));
        Assert.True(router.Release(31));
        Assert.DoesNotContain(sink.Actions, action => action.Action == TouchControlAction.PrimaryAction);
        Assert.Contains((TouchControlAction.NextSlot, true), sink.Actions);
        Assert.Contains((TouchControlAction.NextSlot, false), sink.Actions);
    }

    [Fact]
    public void TouchInputRouterKeepsKeyboardAvailableInMenus()
    {
        var sink = new RecordingTouchSink();
        var router = new TouchInputRouter(sink);
        var keyboard = TouchControlLayout.Default.Single(control => control.Action == TouchControlAction.Keyboard);

        Assert.True(router.Press(32, keyboard.CenterX, keyboard.CenterY, controlsEnabled: false));
        Assert.Contains((TouchControlAction.Keyboard, true), sink.Actions);
        Assert.True(router.Release(32));
        Assert.Contains((TouchControlAction.Keyboard, false), sink.Actions);
    }

    [Fact]
    public void TouchInputRouterDoubleTapUsesMinecraftsNativeDoubleClick()
    {
        var sink = new RecordingTouchSink();
        var router = new TouchInputRouter(sink);

        Assert.True(router.Press(40, 0.48, 0.36, controlsEnabled: false));
        Assert.True(router.Release(40));
        Assert.Single(sink.Pointers, pointer => pointer.Click);
        Assert.Empty(sink.Actions);

        Assert.True(router.Press(41, 0.48, 0.36, controlsEnabled: false));
        Assert.Empty(sink.Actions);
        Assert.True(router.Release(41));
        Assert.Empty(sink.Actions);
        Assert.Equal(2, sink.Pointers.Count(pointer => pointer.Click));
        Assert.DoesNotContain(TouchControlLayout.Default,
            control => control.Action == TouchControlAction.SecondaryAction);
    }

    [Fact]
    public void TouchInputRouterLongPressSelectsOneInventoryItem()
    {
        var sink = new RecordingTouchSink();
        var router = new TouchInputRouter(sink, inventoryStackCount: (_, _) => 64);

        Assert.True(router.Press(42, 0.48, 0.36, controlsEnabled: false));
        Assert.True(router.BeginLongPress(42));
        Assert.True(router.Release(42));

        Assert.Equal([(0.48, 0.36, 1)], sink.InventorySplits);
        Assert.DoesNotContain(sink.Pointers, pointer => pointer.Click);
    }

    [Fact]
    public void TouchInputRouterLongPressSwipeSelectsExactInventoryQuantity()
    {
        var sink = new RecordingTouchSink();
        var router = new TouchInputRouter(sink, inventoryStackCount: (_, _) => 64);

        Assert.True(router.Press(44, 0.42, 0.40, controlsEnabled: false));
        Assert.True(router.BeginLongPress(44));
        Assert.True(router.Move(44, 0.42 + 0.24 * 31 / 63 / router.Profile.ViewportAspectRatio, 0.40));
        Assert.Equal(32, Assert.Single(router.InventorySplitSelections).Count);
        Assert.True(router.Release(44));
        Assert.Equal([(0.42, 0.40, 32)], sink.InventorySplits);
        Assert.DoesNotContain(sink.Pointers, pointer => pointer.Click);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DraggingAnOccupiedSlotMovesTheStackOnlyOnRelease(bool cancelled)
    {
        var sink = new RecordingTouchSink();
        var router = new TouchInputRouter(sink, inventoryStackCount: (_, y) => y < 0.5 ? 5 : 0);
        router.Press(1, 0.4, 0.4, controlsEnabled: false);
        router.Move(1, 0.4, 0.6);
        router.Release(1, cancelled);

        if (cancelled)
        {
            Assert.Empty(sink.InventorySplits);
            Assert.DoesNotContain(sink.Pointers, point => point.Click);
        }
        else
        {
            Assert.Equal([(0.4, 0.4, 5)], sink.InventorySplits);
            Assert.Contains((0.4, 0.6, true), sink.Pointers);
        }
    }

    [Fact]
    public void DraggingAnOccupiedSlotOutsideTheInventoryDoesNotDropTheStack()
    {
        var sink = new RecordingTouchSink();
        var router = new TouchInputRouter(sink,
            inventoryStackCount: (_, y) => y < 0.5 ? 5 : -1);

        Assert.True(router.Press(2, 0.4, 0.4, controlsEnabled: false));
        Assert.True(router.Move(2, 0.4, 0.7));
        Assert.True(router.Release(2));

        Assert.Empty(sink.InventorySplits);
        Assert.DoesNotContain(sink.Pointers, point => point.Click);
    }

    [Fact]
    public void TouchInputRouterCraftingDragHoldsOnePrimaryButton()
    {
        var sink = new RecordingTouchSink();
        var router = new TouchInputRouter(sink);

        Assert.True(router.Press(43, 0.40, 0.40, controlsEnabled: false));
        Assert.True(router.Move(43, 0.46, 0.40));
        Assert.True(router.Move(43, 0.52, 0.40));
        Assert.True(router.Release(43));

        Assert.Equal(1, sink.Actions.Count(action =>
            action == (TouchControlAction.PrimaryAction, true)));
        Assert.Equal(1, sink.Actions.Count(action =>
            action == (TouchControlAction.PrimaryAction, false)));
        Assert.DoesNotContain(sink.Pointers, pointer => pointer.Click);
    }

    [Fact]
    public void TouchInputRouterCancelDoesNotTurnPendingTapIntoClick()
    {
        var sink = new RecordingTouchSink();
        var router = new TouchInputRouter(sink);

        Assert.True(router.Press(80, 0.52, 0.45));
        router.CancelAll();

        Assert.DoesNotContain(sink.Pointers, pointer => pointer.Click);
        Assert.False(router.Release(80));
    }

    [Fact]
    public void TouchInputRouterCancelDoesNotTransferSelectedInventoryItems()
    {
        var sink = new RecordingTouchSink();
        var router = new TouchInputRouter(sink, inventoryStackCount: (_, _) => 64);

        Assert.True(router.Press(81, 0.42, 0.40, controlsEnabled: false));
        Assert.True(router.BeginLongPress(81));
        Assert.True(router.Move(81, 0.42 + 0.24 * 31 / 63 / router.Profile.ViewportAspectRatio, 0.40));
        Assert.Equal(32, Assert.Single(router.InventorySplitSelections).Count);

        router.CancelAll();

        Assert.Empty(sink.InventorySplits);
        Assert.Empty(router.InventorySplitSelections);
        Assert.False(router.Release(81));
    }

    [Fact]
    public void CancelledReleaseDoesNotCommitDeferredMenuGestures()
    {
        var tapSink = new RecordingTouchSink();
        var tapRouter = new TouchInputRouter(tapSink);
        Assert.True(tapRouter.Press(86, 0.52, 0.45, controlsEnabled: false));
        Assert.True(tapRouter.Release(86, cancelled: true));
        Assert.DoesNotContain(tapSink.Pointers, pointer => pointer.Click);

        var splitSink = new RecordingTouchSink();
        var splitRouter = new TouchInputRouter(splitSink, inventoryStackCount: (_, _) => 64);
        Assert.True(splitRouter.Press(87, 0.42, 0.40, controlsEnabled: false));
        Assert.True(splitRouter.BeginLongPress(87));
        Assert.True(splitRouter.Move(87,
            0.42 + 0.24 * 31 / 63 / splitRouter.Profile.ViewportAspectRatio, 0.40));
        Assert.Equal(32, Assert.Single(splitRouter.InventorySplitSelections).Count);
        Assert.True(splitRouter.Release(87, cancelled: true));
        Assert.Empty(splitSink.InventorySplits);
    }

    [Fact]
    public void CancelledReleaseUsesCancellationEdgesForHeldActions()
    {
        var sink = new RecordingTouchSink();
        var profile = new TouchControlProfile { Mode = TouchControlMode.JoystickAndTap };
        var router = new TouchInputRouter(sink, profile);
        var jump = profile.Controls.Single(control => control.Action == TouchControlAction.Jump);

        Assert.True(router.Press(88, jump.CenterX, jump.CenterY));
        Assert.True(router.Release(88, cancelled: true));
        Assert.Contains(TouchControlAction.Jump, sink.CanceledActions);
        Assert.DoesNotContain((TouchControlAction.Jump, false), sink.Actions);

        Assert.True(router.Press(89, 0.52, 0.45));
        Assert.True(router.BeginLongPress(89));
        Assert.True(router.Release(89, cancelled: true));
        Assert.Contains(TouchControlAction.PrimaryAction, sink.CanceledActions);
        Assert.DoesNotContain((TouchControlAction.PrimaryAction, false), sink.Actions);
    }

    [Fact]
    public void TouchInputRouterCancelReleasesMovementAndMining()
    {
        var sink = new RecordingTouchSink();
        var profile = new TouchControlProfile();
        var router = new TouchInputRouter(sink, profile);
        var move = profile.Controls.Single(control => control.Action == TouchControlAction.Move);

        Assert.True(router.Press(82, move.CenterX +
            move.Radius * 0.65 / profile.ViewportAspectRatio, move.CenterY));
        Assert.True(router.Move(82, move.CenterX +
            move.Radius / profile.ViewportAspectRatio, move.CenterY));
        Assert.True(router.Press(83, 0.55, 0.42));
        Assert.True(router.BeginLongPress(83));
        Assert.Equal(1, sink.LastMovement.X, 6);
        Assert.True(router.IsPressed(TouchControlAction.PrimaryAction));

        router.CancelAll();

        Assert.Equal((0d, 0d), sink.LastMovement);
        Assert.False(router.IsPressed(TouchControlAction.PrimaryAction));
        Assert.Contains(TouchControlAction.PrimaryAction, sink.CanceledActions);
        Assert.False(router.Release(82));
        Assert.False(router.Release(83));
    }

    [Fact]
    public void TouchInputRouterCancelUsesNonTappingActionEdge()
    {
        var sink = new RecordingTouchSink();
        var profile = new TouchControlProfile { Mode = TouchControlMode.JoystickAndTap };
        var router = new TouchInputRouter(sink, profile);
        var jump = profile.Controls.Single(control => control.Action == TouchControlAction.Jump);

        Assert.True(router.Press(85, jump.CenterX, jump.CenterY));
        router.CancelAll();

        Assert.Contains((TouchControlAction.Jump, true), sink.Actions);
        Assert.DoesNotContain((TouchControlAction.Jump, false), sink.Actions);
        Assert.Contains(TouchControlAction.Jump, sink.CanceledActions);
    }

    [Fact]
    public void TouchInputRouterRepeatedPointerDownCancelsTheStaleGestureWithoutClicking()
    {
        var sink = new RecordingTouchSink();
        var router = new TouchInputRouter(sink, inventoryStackCount: (_, _) => 64);

        Assert.True(router.Press(84, 0.42, 0.40, controlsEnabled: false));
        Assert.True(router.BeginLongPress(84));
        Assert.True(router.Move(84, 0.42 + 0.24 * 31 / 63 / router.Profile.ViewportAspectRatio, 0.40));
        Assert.Equal(32, Assert.Single(router.InventorySplitSelections).Count);

        Assert.True(router.Press(84, 0.70, 0.55, controlsEnabled: false));

        Assert.Empty(sink.InventorySplits);
        Assert.DoesNotContain(sink.Pointers, pointer => pointer.Click);
        Assert.Empty(router.InventorySplitSelections);
        Assert.True(router.Release(84));
        Assert.Single(sink.Pointers, pointer => pointer.Click);
    }

    [Fact]
    public void HardwareTextInputGateSurvivesRecreatedInputConnections()
    {
        var gate = new HardwareTextInputGate();

        Assert.True(gate.Accept(45, downTime: 1000, repeatCount: 0));
        Assert.False(gate.Accept(45, downTime: 1000, repeatCount: 0));
        Assert.True(gate.Accept(45, downTime: 1002, repeatCount: 0));
        Assert.True(gate.Accept(45, downTime: 1200, repeatCount: 0));
        Assert.True(gate.Accept(45, downTime: 1200, repeatCount: 1));
        Assert.False(gate.Accept(45, downTime: 1200, repeatCount: 1));
        Assert.True(gate.Accept(46, downTime: 1252, repeatCount: 0));
    }

    [Fact]
    public void HardwareTextInputGateSuppressesImeCommitForPhysicalKeyOnly()
    {
        var gate = new HardwareTextInputGate();

        Assert.True(gate.AcceptHardwareCodePoint(45, 'q', 500, 1_000, 0));
        Assert.False(gate.AcceptCommittedCodePoint('q', 1_030));
        Assert.True(gate.AcceptCommittedCodePoint('q', 1_040));
        Assert.True(gate.AcceptCommittedCodePoint('q', 1_501));
        Assert.True(gate.AcceptHardwareCodePoint(51, 'w', 700, 2_000, 0));
        Assert.True(gate.AcceptCommittedCodePoint('e', 2_020));
        Assert.True(gate.AcceptCommittedCodePoint('e', 2_021));
        Assert.True(gate.AcceptCommittedCodePoint('e', 2_142));
    }

    [Fact]
    public void HardwareTextInputGateAllowsRapidRepeatedCharacters()
    {
        var gate = new HardwareTextInputGate();

        Assert.True(gate.AcceptCommittedCodePoint('o', 1_000));
        Assert.True(gate.AcceptCommittedCodePoint('o', 1_015));
        Assert.True(gate.Accept(43, downTime: 2_000, repeatCount: 0));
        Assert.True(gate.Accept(43, downTime: 2_030, repeatCount: 0));
    }

    [Fact]
    public void HardwareTextInputGateKeepsSdlSendKeyTextExactlyOnce()
    {
        var gate = new HardwareTextInputGate();

        // The same Android key can arrive through Activity.DispatchKeyEvent and the active
        // InputConnection. SDL still receives its printable character, but only once.
        Assert.True(gate.AcceptHardwareCodePoint(29, 'a', 4_000, 5_000, 0));
        Assert.False(gate.AcceptHardwareCodePoint(29, 'a', 4_000, 5_001, 0));
        Assert.False(gate.AcceptCommittedCodePoint('a', 5_020));
        Assert.True(gate.AcceptCommittedCodePoint('a', 5_021));
    }

    [Fact]
    public void HardwareTextInputGateConsumesPendingEchoAcrossMultiCharacterCommit()
    {
        var gate = new HardwareTextInputGate();

        Assert.True(gate.AcceptHardwareCodePoint(30, 'b', 6_000, 7_000, 0));
        var committed = "book".EnumerateRunes()
            .Where(rune => gate.AcceptCommittedCodePoint(rune.Value, 7_020))
            .Select(rune => rune.ToString());

        Assert.Equal("ook", string.Concat(committed));
        Assert.True(gate.AcceptCommittedCodePoint('b', 7_030));
    }

    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(2, 0, 2, 0)]
    [InlineData(0, 3, 0, 3)]
    [InlineData(-1, -2, 0, 0)]
    public void AndroidTextDeletionPlanPreservesImeDirectionAndNoOps(
        int beforeLength, int afterLength, int backspaces, int forwardDeletes)
    {
        var plan = AndroidTextDeletionPlan.Create(beforeLength, afterLength);
        Assert.Equal(backspaces, plan.Backspaces);
        Assert.Equal(forwardDeletes, plan.ForwardDeletes);
    }

    [Theory]
    [InlineData(1, false, true)]
    [InlineData(0, false, false)]
    [InlineData(0, true, true)]
    [InlineData(-1, false, false)]
    [InlineData(-1, true, true)]
    public void SoftKeyboardInputPolicyKeepsExplicitManualFallback(
        int focusState, bool manuallyRequested, bool expected)
    {
        Assert.Equal(expected,
            SoftKeyboardInputPolicy.AcceptsConnection(focusState, manuallyRequested));
    }

    [Fact]
    public void OrdinarySignEditorCanOpenKeyboardWithoutAFocusedWidget()
    {
        Assert.True(SoftKeyboardInputPolicy.AcceptsConnection(
            focusState: 0, manuallyRequested: true));
    }

    [Fact]
    public void HangingSignEditorCanOpenKeyboardWithoutAFocusedWidget()
    {
        Assert.True(SoftKeyboardInputPolicy.AcceptsConnection(
            focusState: 0, manuallyRequested: true));
    }

    [Fact]
    public void KeyboardOverlayReleaseDoesNotResyncTextFocusAtItsButtonCoordinates()
    {
        var sink = new RecordingTouchSink();
        var profile = new TouchControlProfile();
        var router = new TouchInputRouter(sink, profile);
        var keyboard = profile.Controls.Single(control =>
            control.Action == TouchControlAction.Keyboard);

        Assert.True(router.Press(90, keyboard.CenterX, keyboard.CenterY,
            controlsEnabled: false));
        Assert.False(router.TargetsGameSurface(90));
        Assert.True(router.Release(90));

        Assert.True(router.Press(91, 0.5, 0.5, controlsEnabled: false));
        Assert.True(router.TargetsGameSurface(91));
        Assert.True(router.Release(91));
    }

    [Fact]
    public void GameActivityHandlesPhysicalKeyboardChangesWithoutKillingTheWorld()
    {
        var repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", ".."));
        var activity = File.ReadAllText(Path.Combine(repository,
            "src", "Amber.Android", "GameActivity.cs"));

        Assert.Contains("ConfigChanges.Keyboard", activity, StringComparison.Ordinal);
        Assert.Contains("ConfigChanges.KeyboardHidden", activity, StringComparison.Ordinal);
        Assert.Contains("ConfigChanges.Navigation", activity, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AndroidRuntimeManifestRequiresSafeLicensedVerifiedFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "jay-runtime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "lib"));
        try
        {
            var bytes = "verified runtime"u8.ToArray();
            var path = Path.Combine(root, "lib", "libjvm.so");
            await File.WriteAllBytesAsync(path, bytes);
            var file = new RuntimeBundleFile(
                "lib/libjvm.so",
                bytes.Length,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                "GPL-2.0-with-classpath-exception",
                "https://example.invalid/openjdk-source");
            var manifest = new RuntimeBundleManifest(1, "test-runtime", "arm64-v8a", [file]);

            Assert.Empty(manifest.Validate());
            Assert.True(await RuntimeBundleVerifier.VerifyAsync(root, manifest, "arm64-v8a"));
            Assert.True(RuntimeBundleVerifier.VerifyLayout(root, manifest, "arm64-v8a"));
            Assert.False(await RuntimeBundleVerifier.VerifyAsync(root, manifest, "x86_64"));
            Assert.False(RuntimeBundleVerifier.VerifyLayout(root, manifest, "x86_64"));

            await File.AppendAllTextAsync(path, "tampered");
            Assert.False(RuntimeBundleVerifier.VerifyLayout(root, manifest, "arm64-v8a"));
            Assert.False(await RuntimeBundleVerifier.VerifyAsync(root, manifest, "arm64-v8a"));
            Assert.NotEmpty((manifest with { Files = [file with { Path = "../libjvm.so" }] }).Validate());
            Assert.NotEmpty((manifest with { Files = [file with { License = "" }] }).Validate());
            Assert.NotEmpty((manifest with { Files = [file with { SourceUrl = "http://example.invalid/source" }] }).Validate());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task AndroidRuntimeInstallerActivatesOnlyACompleteVerifiedBundle()
    {
        var parent = Path.Combine(Path.GetTempPath(), "jay-runtime-install-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(parent, "active");
        try
        {
            var bytes = "independent android jvm"u8.ToArray();
            var file = RuntimeFile("lib/server/libjvm.so", bytes);
            var manifest = new RuntimeBundleManifest(1, "test-runtime", "arm64-v8a", [file]);

            await RuntimeBundleInstaller.InstallAsync(
                target,
                manifest,
                "arm64-v8a",
                (_, _) => Task.FromResult<Stream>(new MemoryStream(bytes, writable: false)));

            Assert.True(File.Exists(Path.Combine(target, "runtime-manifest.json")));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(target, "lib", "server", "libjvm.so")));
            Assert.True(await RuntimeBundleVerifier.VerifyAsync(target, RuntimeBundleManifest.Read(Path.Combine(target, "runtime-manifest.json")), "arm64-v8a"));
        }
        finally
        {
            if (Directory.Exists(parent)) Directory.Delete(parent, true);
        }
    }

    [Fact]
    public async Task AndroidRuntimeInstallerKeepsActiveRuntimeWhenDownloadIsInvalid()
    {
        var parent = Path.Combine(Path.GetTempPath(), "jay-runtime-install-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(parent, "active");
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, "keep.txt"), "old runtime");
        try
        {
            var expected = "expected"u8.ToArray();
            var tampered = "tampered"u8.ToArray();
            var manifest = new RuntimeBundleManifest(1, "test-runtime", "arm64-v8a", [RuntimeFile("lib/libjvm.so", expected)]);

            await Assert.ThrowsAsync<InvalidDataException>(() => RuntimeBundleInstaller.InstallAsync(
                target,
                manifest,
                "arm64-v8a",
                (_, _) => Task.FromResult<Stream>(new MemoryStream(tampered, writable: false))));

            Assert.Equal("old runtime", await File.ReadAllTextAsync(Path.Combine(target, "keep.txt")));
            Assert.Equal([target], Directory.GetDirectories(parent));
        }
        finally
        {
            Directory.Delete(parent, true);
        }
    }

    [Fact]
    public async Task AndroidRuntimeArchiveInstallerExtractsAndActivatesVerifiedJre()
    {
        var parent = Path.Combine(Path.GetTempPath(), "jay-runtime-archive-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(parent, "active");
        try
        {
            var archive = Convert.FromBase64String(RuntimeXzFixture);
            var descriptor = RuntimeArchive(archive);
            var manifest = await RuntimeArchiveInstaller.InstallAsync(
                target,
                descriptor,
                (_, _) => Task.FromResult<Stream>(new MemoryStream(archive, writable: false)));

            Assert.Contains(manifest.Files, file => file.Path == "lib/server/libjvm.so");
            Assert.True(await RuntimeBundleVerifier.VerifyAsync(target, manifest, "arm64-v8a"));
            Assert.False(File.Exists(Path.Combine(parent, "escape")));
        }
        finally
        {
            if (Directory.Exists(parent)) Directory.Delete(parent, true);
        }
    }

    [Fact]
    public async Task AndroidRuntimeArchiveInstallerRejectsTraversalWithoutReplacingRuntime()
    {
        var parent = Path.Combine(Path.GetTempPath(), "jay-runtime-archive-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(parent, "active");
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, "keep.txt"), "old runtime");
        try
        {
            var archive = CreateRuntimeArchive(("../escape", "bad"u8.ToArray()));
            await Assert.ThrowsAsync<InvalidDataException>(() => RuntimeArchiveInstaller.InstallAsync(
                target,
                RuntimeArchive(archive),
                (_, _) => Task.FromResult<Stream>(new MemoryStream(archive, writable: false))));

            Assert.Equal("old runtime", await File.ReadAllTextAsync(Path.Combine(target, "keep.txt")));
            Assert.False(File.Exists(Path.Combine(parent, "escape")));
        }
        finally
        {
            Directory.Delete(parent, true);
        }
    }

    [Fact]
    public async Task AndroidRuntimeArchiveInstallerExtractsOffTheCallingThread()
    {
        var parent = Path.Combine(Path.GetTempPath(), "jay-runtime-archive-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(parent, "active");
        var archive = Convert.FromBase64String(RuntimeXzFixture);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var ui = new SingleThreadSynchronizationContext();
        var extractThread = 0;
        SynchronizationContext? extractContext = null;
        var held = 0;
        var started = new TaskCompletionSource<Task<RuntimeBundleManifest>>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            // Start the install from a single-threaded "UI" context, the way the Android activity does.
            ui.Post(_ =>
            {
                try
                {
                    started.SetResult(RuntimeArchiveInstaller.InstallCoreAsync(
                        target,
                        RuntimeArchive(archive),
                        (_, _) => Task.FromResult<Stream>(new MemoryStream(archive, writable: false)),
                        progress: null,
                        CancellationToken.None,
                        beforeExtractEntry: () =>
                        {
                            if (Interlocked.Exchange(ref held, 1) != 0) return;
                            extractThread = Environment.CurrentManagedThreadId;
                            extractContext = SynchronizationContext.Current;
                            entered.Set();
                            release.Wait();
                        }));
                }
                catch (Exception e)
                {
                    started.SetException(e);
                }
            }, null);

            Assert.True(entered.Wait(TimeSpan.FromSeconds(30)), "Extraction never started.");
            Assert.NotEqual(ui.ThreadId, extractThread);
            Assert.NotSame(ui, extractContext);

            // While extraction is held, the call has already handed back an unfinished task...
            var install = await started.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.False(install.IsCompleted);
            // ...and the UI thread still runs other work.
            var probe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ui.Post(_ => probe.SetResult(), null);
            await probe.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.False(install.IsCompleted);

            release.Set();
            var manifest = await install.WaitAsync(TimeSpan.FromSeconds(60));
            Assert.True(await RuntimeBundleVerifier.VerifyAsync(target, manifest, "arm64-v8a"));
        }
        finally
        {
            release.Set();
            if (Directory.Exists(parent)) Directory.Delete(parent, true);
        }
    }

    [Fact]
    public async Task AndroidRuntimeArchiveInstallerCancelsDuringExtraction()
    {
        var parent = Path.Combine(Path.GetTempPath(), "jay-runtime-archive-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(parent, "active");
        var archive = CreateRuntimeArchive();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var cts = new CancellationTokenSource();
        var held = 0;
        try
        {
            var install = RuntimeArchiveInstaller.InstallCoreAsync(
                target,
                RuntimeArchive(archive),
                (_, _) => Task.FromResult<Stream>(new MemoryStream(archive, writable: false)),
                progress: null,
                cts.Token,
                beforeExtractEntry: () =>
                {
                    if (Interlocked.Exchange(ref held, 1) != 0) return;
                    entered.Set();
                    release.Wait();
                });

            Assert.True(entered.Wait(TimeSpan.FromSeconds(30)), "Extraction never started.");
            cts.Cancel();
            release.Set();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => install.WaitAsync(TimeSpan.FromSeconds(60)));
            Assert.False(Directory.Exists(target));
            Assert.Empty(Directory.GetFileSystemEntries(parent));
        }
        finally
        {
            release.Set();
            if (Directory.Exists(parent)) Directory.Delete(parent, true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AndroidRuntimeArchiveInstallerKeepsPreviousRuntimeAfterCancelOrFailure(bool cancel)
    {
        var parent = Path.Combine(Path.GetTempPath(), "jay-runtime-archive-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(parent, "active");
        try
        {
            var previousArchive = CreateRuntimeArchive();
            var previous = await RuntimeArchiveInstaller.InstallAsync(
                target,
                RuntimeArchive(previousArchive),
                (_, _) => Task.FromResult<Stream>(new MemoryStream(previousArchive, writable: false)));

            var updateArchive = CreateRuntimeArchive(("lib/extra.so", "update"u8.ToArray()));
            using var cts = new CancellationTokenSource();
            var install = RuntimeArchiveInstaller.InstallCoreAsync(
                target,
                RuntimeArchive(updateArchive),
                (_, _) => Task.FromResult<Stream>(new MemoryStream(updateArchive, writable: false)),
                progress: null,
                cts.Token,
                beforeExtractEntry: () =>
                {
                    if (cancel) cts.Cancel();
                    else throw new IOException("Simulated disk failure during extraction.");
                });

            if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => install);
            else await Assert.ThrowsAsync<IOException>(() => install);

            // No staging, download or backup leftovers next to the runtime.
            Assert.Equal([target], Directory.GetFileSystemEntries(parent));
            // The previous runtime is untouched and still passes full verification.
            var active = RuntimeBundleManifest.Read(Path.Combine(target, "runtime-manifest.json"));
            Assert.Equal(previous.Files.Select(file => file.Path).Order(), active.Files.Select(file => file.Path).Order());
            Assert.False(File.Exists(Path.Combine(target, "lib", "extra.so")));
            Assert.Equal("jvm"u8.ToArray(), await File.ReadAllBytesAsync(Path.Combine(target, "lib", "server", "libjvm.so")));
            Assert.True(await RuntimeBundleVerifier.VerifyAsync(target, active, "arm64-v8a"));
        }
        finally
        {
            if (Directory.Exists(parent)) Directory.Delete(parent, true);
        }
    }

    private static RuntimeBundleFile RuntimeFile(string path, byte[] bytes) => new(
        path,
        bytes.Length,
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
        "GPL-2.0-with-classpath-exception",
        "https://example.invalid/openjdk-source");

    private static RuntimeArchiveDescriptor RuntimeArchive(byte[] archive) => new(
        1,
        "jay-jre21-test",
        "arm64-v8a",
        archive.Length,
        Convert.ToHexString(SHA256.HashData(archive)),
        1024 * 1024,
        "GPL-2.0-with-classpath-exception",
        "https://example.invalid/openjdk-source",
        "https://example.invalid/jre.tar.xz");

    private static byte[] CreateRuntimeArchive(params (string Path, byte[] Data)[] extra)
    {
        var entries = new (string Path, byte[] Data)[]
        {
            ("lib/server/libjvm.so", "jvm"u8.ToArray()),
            ("lib/modules", "modules"u8.ToArray()),
            ("release", "JAVA_VERSION=21"u8.ToArray()),
            ("legal/java.base/LICENSE", "GPL-2.0"u8.ToArray()),
            ("legal/java.base/ASSEMBLY_EXCEPTION", "Classpath Exception"u8.ToArray())
        }.Concat(extra);
        using var archive = new MemoryStream();
        using (var gzip = new GZipStream(archive, CompressionLevel.SmallestSize, leaveOpen: true))
        using (var writer = new TarWriter(gzip, leaveOpen: false))
            foreach (var (path, data) in entries)
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, path)
                {
                    DataStream = new MemoryStream(data, writable: false)
                });
        return archive.ToArray();
    }

    private const string RuntimeXzFixture = """
        /Td6WFoAAATm1rRGAgAhARYAAAB0L+Wj4HH/A1VdABcXwcAN4XBkd8ji0xzZjqGzadoY40D8VACY44SciLdE0kUWxrzJNdZQh6Re
        RJw7pSaEXmof08wj5TrCFFRSiAe4d1/7GMwhiAHCmm5yuDd6kczORFYTx744AGRGQ8kuOfLCoTYRgNybThb1uEujoKUgyv3ilZXB
        YmKuCRWFh47pFQfY1096CTnovrMxPsGaYNFTngP0h2byTEHmYllYSyUBOrrxwNY1LpzCaXV8JtCyeUQlcLu/o/jL7siUmxm8T+8r
        kcsPfp4R29iHPTipLm/uVjZf99c8JaEWUCFkXoiqyg55qqW26rBaoPC3eCZ7BkbmEAD5kKk6TZOaAoyhymHMs/+uWYeTZ/qYAyty
        Nt0osVbsGcAYGsuYxIZILrkYxQpjNUxRTInp5VMihwH4meta4SBBrXW1BnhEIaItgJuxUZcVKjlYmhSgc/urFZ+kmi2E+BcxBkpz
        WB6mqi5+PT+8+thHG+LUNkqMZVrofjBgsk9Z9JiM8Lse9aKXO9rE8ax/9WwpGhVRVWxt93mIgTRjw4ufeUhYH84pn69cl60k7k6G
        LMSNEaPM9qp0mvPegoH02JtNgH7XT4IexIHUEabBull09w7AqTkRUqi9yj6GFeWmpzU7E5kMtDtR2HYyl8iCkmH6AabA4YsXv9HY
        38LjVEniBB0UmRCtPRwseA/1lWjuMYDFMewIsZ7nBiKNDM4bpirXZlzHf4DMLlHohaKH6AgZ2JUopRHoR+znADrN60ot2ebw5N2t
        F13aCaMculBKKuxG9F2JkH7xZu3t3wnswnvr8LN9TVI6S7lIM7D1r6UVQg8arxWsezBj2SpE/+RxtXsgVi01et7meEOQYE8qI+ma
        Q3MgChfTN08adqUdHRrEgm9BC5RvNZh6x3qhxHP7AKxNjS13yrdAaxH63C9wktVusqnE9puxmoRK4Ux7hNc1PPyy2lSIKv8hkg7W
        WuHyp9URPhgavb6eJvCxZX3NBOXXKOYOXL/AwTljExBcf0vZ8/Br5/ZkyxIKSF2HxaNQEzbQn7RcW2xFBeB9CKH90/WEmDHJwn4D
        uOSkvqrb56xEuy4315U0RexbEnxSs1NeGvEtAHrr4MdiD+fnhi1ZfdCIMM9cNZzJAKFIrqxwOpQl9QAAAAAAqDkUZuAiHJcAAfEG
        gOQBAMcUcsixxGf7AgAAAAAEWVo=
        """;

    /// <summary>A minimal UI-like context: one thread that runs posted work in order.</summary>
    private sealed class SingleThreadSynchronizationContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
        private readonly Thread _thread;

        public SingleThreadSynchronizationContext()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "test-ui" };
            _thread.Start();
        }

        public int ThreadId => _thread.ManagedThreadId;

        public override void Post(SendOrPostCallback d, object? state)
        {
            try
            {
                _queue.Add((d, state));
            }
            catch (InvalidOperationException)
            {
                // Disposed: finish stray continuations on the pool instead.
                ThreadPool.QueueUserWorkItem(_ => d(state));
            }
        }

        public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();

        public override SynchronizationContext CreateCopy() => this;

        public void Dispose() => _queue.CompleteAdding();

        private void Run()
        {
            SetSynchronizationContext(this);
            foreach (var (callback, state) in _queue.GetConsumingEnumerable()) callback(state);
        }
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
        public List<TouchControlAction> CanceledActions { get; } = [];
        public (double X, double Y) LastMovement { get; private set; }
        public (double X, double Y) LastLook { get; private set; }
        public (double X, double Y, bool Click) LastPointer { get; private set; }
        public List<(double X, double Y, bool Click)> Pointers { get; } = [];
        public List<(double X, double Y, int Count)> InventorySplits { get; } = [];

        public void SetAction(TouchControlAction action, bool pressed) => Actions.Add((action, pressed));
        public void CancelAction(TouchControlAction action) => CanceledActions.Add(action);
        public void SetMovement(double x, double y) => LastMovement = (x, y);
        public void AddLook(double x, double y) => LastLook = (x, y);
        public void SetPointer(double x, double y, bool click)
        {
            LastPointer = (x, y, click);
            Pointers.Add((x, y, click));
        }
        public void SplitInventoryStack(double x, double y, int count) =>
            InventorySplits.Add((x, y, count));
    }

}
