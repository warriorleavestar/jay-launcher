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

    [Theory]
    [InlineData(8, false)]
    [InlineData(17, false)]
    [InlineData(21, false)]
    [InlineData(22, true)]
    [InlineData(24, true)]
    [InlineData(25, true)]
    public void AndroidRuntimeChoiceUsesJava25ForEveryCompatiblePost21Instance(
        int requiredJava, bool java25)
    {
        Assert.True(AndroidRuntimeChoice.IsSupported(requiredJava));
        Assert.Equal(java25, AndroidRuntimeChoice.UsesJava25(requiredJava));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(26)]
    [InlineData(99)]
    public void AndroidRuntimeChoiceRejectsUnsupportedMajorsBeforeDownload(int requiredJava)
    {
        Assert.False(AndroidRuntimeChoice.IsSupported(requiredJava));
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

    [Theory]
    [InlineData("jay-runtime-v2-arm64", null, false, true)]
    [InlineData("jay-runtime-v2-arm64", "jay-runtime-v2-arm64", true, true)]
    [InlineData("jay-runtime-v2-arm64", null, true, false)]
    [InlineData("jay-runtime-v2-arm64", "jay-runtime-v3-arm64", true, false)]
    [InlineData("", null, false, false)]
    public void InstalledAndroidRuntimeSurvivesCataloglessDebugBuilds(
        string manifestId, string? descriptorId, bool catalogPresent, bool expected)
    {
        Assert.Equal(expected,
            AndroidRuntimeAssetCatalog.AcceptsInstalledRuntime(manifestId, descriptorId, catalogPresent));
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

        profile.SetPosition(TouchControlAction.Inventory, 0.25, 0.25);
        Assert.False(profile.TryGetPosition(TouchControlAction.Inventory, out _));
        inventory = profile.Controls.Single(control => control.Action == TouchControlAction.Inventory);
        Assert.Equal(enlarged.InventoryX, inventory.CenterX, 6);
        Assert.Equal(enlarged.CenterY, inventory.CenterY, 6);
    }

    [Fact]
    public void LiveMinecraftGuiScaleDrivesHotbarHitTestingAndInventoryAnchor()
    {
        var hud = TouchHudGeometry.ForViewport(2944, 1840, 7);
        Assert.Equal(0.5 - 630d / 2944, hud.HotbarLeft, 6);
        Assert.Equal(0.5 + 630d / 2944, hud.HotbarRight, 6);
        Assert.Equal(0.5 + 707d / 2944, hud.InventoryX, 6);
        Assert.Equal(1 - 77d / 1840, hud.CenterY, 6);
        Assert.True(hud.TryGetSlot(0.30, 0.95, out var first));
        Assert.Equal(0, first);
        Assert.True(hud.TryGetSlot(0.70, 0.95, out var last));
        Assert.Equal(8, last);

        var profile = new TouchControlProfile
        {
            ViewportAspectRatio = 2944d / 1840,
            ViewportWidthPixels = 2944,
            ViewportHeightPixels = 1840,
            MinecraftGuiScale = 7
        };
        var inventory = profile.Controls.Single(control =>
            control.Action == TouchControlAction.Inventory);
        Assert.Equal(hud.InventoryX, inventory.CenterX, 6);
        Assert.Equal(hud.CenterY, inventory.CenterY, 6);
        Assert.Equal(hud.InventoryRadius, inventory.Radius, 6);
    }

    [Fact]
    public void AndroidReadsGuiScaleFromTheWindowWithoutRequiringAnOpenScreen()
    {
        var repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", ".."));
        var bootstrap = File.ReadAllText(Path.Combine(repository, "src", "Amber.Android", "Java",
            "nz", "work", "minecraft", "jay", "AndroidGameBootstrap.java"));

        Assert.Contains("windowGuiScale = optionalMethod(windowType, \"getGuiScale\")", bootstrap,
            StringComparison.Ordinal);
        Assert.Equal(2, bootstrap.Split(
            "windowGuiScale = optionalMethod(windowType, \"s\")", StringSplitOptions.None).Length - 1);
        Assert.Contains("windowGuiScale.invoke(liveWindow)", bootstrap, StringComparison.Ordinal);
        Assert.Contains("if (liveWindow != null && windowGuiScale != null)", bootstrap,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AndroidDropsLegacyInventoryOverridesAndKeepsTheButtonOutOfLayoutEditing()
    {
        var repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", ".."));
        var activity = File.ReadAllText(Path.Combine(repository, "src", "Amber.Android",
            "GameActivity.cs"));

        Assert.Contains("if (layoutVersion < 3)", activity, StringComparison.Ordinal);
        Assert.Contains("editor.PutInt(\"layout-version\", 3)", activity,
            StringComparison.Ordinal);
        Assert.Contains("if (action == TouchControlAction.Inventory) continue;", activity,
            StringComparison.Ordinal);
        Assert.Contains("if (control.Action == TouchControlAction.Inventory) return true;", activity,
            StringComparison.Ordinal);
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
    public void TouchInputRouterKeepsSharedActionPressedUntilTheLastFingerEnds()
    {
        var sink = new RecordingTouchSink();
        var profile = new TouchControlProfile();
        var router = new TouchInputRouter(sink, profile);
        var jump = profile.Controls.Single(control => control.Action == TouchControlAction.Jump);

        Assert.True(router.Press(20, jump.CenterX, jump.CenterY));
        Assert.True(router.Press(21, jump.CenterX, jump.CenterY));
        Assert.Single(sink.Actions, action => action == (TouchControlAction.Jump, true));

        Assert.True(router.Release(20));
        Assert.DoesNotContain((TouchControlAction.Jump, false), sink.Actions);
        Assert.True(router.IsPressed(TouchControlAction.Jump));

        Assert.True(router.Release(21));
        Assert.Single(sink.Actions, action => action == (TouchControlAction.Jump, false));
        Assert.False(router.IsPressed(TouchControlAction.Jump));
    }

    [Fact]
    public void TouchInputRouterKeepsMiningPressedUntilTheLastLongPressEnds()
    {
        var sink = new RecordingTouchSink();
        var router = new TouchInputRouter(sink);

        Assert.True(router.Press(30, 0.60, 0.40));
        Assert.True(router.BeginLongPress(30));
        Assert.True(router.Press(31, 0.70, 0.40));
        Assert.True(router.BeginLongPress(31));
        Assert.Single(sink.Actions,
            action => action == (TouchControlAction.PrimaryAction, true));

        Assert.True(router.Release(30));
        Assert.DoesNotContain((TouchControlAction.PrimaryAction, false), sink.Actions);
        Assert.True(router.IsPressed(TouchControlAction.PrimaryAction));

        Assert.True(router.Release(31));
        Assert.Single(sink.Actions,
            action => action == (TouchControlAction.PrimaryAction, false));
        Assert.False(router.IsPressed(TouchControlAction.PrimaryAction));
    }

    [Fact]
    public void InputActionLatchCombinesTouchKeyboardAndMouseSources()
    {
        var latch = new InputActionLatch();

        Assert.Equal(new(TouchControlAction.Jump, true, false),
            latch.Set(TouchControlAction.Jump, source: 0, pressed: true));
        Assert.Null(latch.Set(TouchControlAction.Jump, source: 100, pressed: true));
        Assert.Null(latch.Set(TouchControlAction.Jump, source: 200, pressed: true));
        Assert.Null(latch.Set(TouchControlAction.Jump, source: 100, pressed: false));
        Assert.Null(latch.Set(TouchControlAction.Jump, source: 0, pressed: false,
            cancelled: true));
        Assert.Equal(new(TouchControlAction.Jump, false, false),
            latch.Set(TouchControlAction.Jump, source: 200, pressed: false));
        Assert.False(latch.IsActive(TouchControlAction.Jump));
    }

    [Fact]
    public void InputActionLatchClearsHardwareWithoutReleasingTouch()
    {
        var latch = new InputActionLatch();
        Assert.NotNull(latch.Set(TouchControlAction.PrimaryAction, 0, pressed: true));
        Assert.Null(latch.Set(TouchControlAction.PrimaryAction, 0x10001, pressed: true));
        Assert.NotNull(latch.Set(TouchControlAction.Sneak, 0x10002, pressed: true));

        var changes = latch.ClearSources(source => source >= 0x10000);

        Assert.Single(changes);
        Assert.Equal(new(TouchControlAction.Sneak, false, false), changes[0]);
        Assert.True(latch.IsActive(TouchControlAction.PrimaryAction));
        Assert.False(latch.IsActive(TouchControlAction.Sneak));
    }

    [Fact]
    public void InputActionLatchCancelsTheLastSourceOnWindowFocusLoss()
    {
        var latch = new InputActionLatch();
        Assert.NotNull(latch.Set(TouchControlAction.PrimaryAction, 0, pressed: true));
        Assert.Null(latch.Set(TouchControlAction.PrimaryAction, 0x20001, pressed: true));
        Assert.Null(latch.Set(TouchControlAction.PrimaryAction, 0, pressed: false,
            cancelled: true));

        var changes = latch.ClearSources(source => source >= 0x10000,
            cancelled: true);

        Assert.Equal([new(TouchControlAction.PrimaryAction, false, true)], changes);
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
    public void TouchInputRouterDoubleTapSplitsTheCarriedStackInHalf()
    {
        var sink = new RecordingTouchSink();
        var count = 64;
        var router = new TouchInputRouter(sink, inventoryStackCount: (_, _) => count);

        Assert.True(router.Press(40, 0.48, 0.36, controlsEnabled: false));
        Assert.True(router.Release(40));
        Assert.Single(sink.Pointers, pointer => pointer.Click);
        count = 0; // The first click now carries the full stack and leaves the slot empty.
        Assert.Empty(sink.Actions);

        Assert.True(router.Press(41, 0.48, 0.36, controlsEnabled: false));
        Assert.Empty(sink.Actions);
        Assert.True(router.Release(41));
        Assert.Empty(sink.Actions);
        Assert.Single(sink.Pointers, pointer => pointer.Click);
        Assert.Equal([(0.48, 0.36, 32)], sink.ReturnedInventoryItems);
        Assert.DoesNotContain(TouchControlLayout.Default,
            control => control.Action == TouchControlAction.SecondaryAction);
    }

    [Fact]
    public void TouchInputRouterDoesNotSplitAcrossAdjacentInventorySlots()
    {
        var sink = new RecordingTouchSink();
        var firstCount = 64;
        var router = new TouchInputRouter(sink,
            inventorySlotState: (x, _) => x < 0.50
                ? new InventorySlotState(17, firstCount)
                : new InventorySlotState(18, 0));

        router.Press(140, 0.49, 0.36, controlsEnabled: false);
        router.Release(140);
        firstCount = 0;
        router.Press(141, 0.51, 0.36, controlsEnabled: false);
        router.Release(141);

        Assert.Empty(sink.ReturnedInventoryItems);
        Assert.Equal(2, sink.Pointers.Count(pointer => pointer.Click));
    }

    [Fact]
    public void OverlayControlInterruptsPendingInventoryDoubleTap()
    {
        var sink = new RecordingTouchSink();
        var count = 64;
        var profile = new TouchControlProfile();
        var router = new TouchInputRouter(sink, profile,
            inventorySlotState: (_, _) => new InventorySlotState(17, count));
        var keyboard = profile.Controls.Single(control =>
            control.Action == TouchControlAction.Keyboard);

        router.Press(150, 0.48, 0.36, controlsEnabled: false);
        router.Release(150);
        count = 0;
        router.Press(151, keyboard.CenterX, keyboard.CenterY, controlsEnabled: false);
        router.Release(151);
        router.Press(152, 0.48, 0.36, controlsEnabled: false);
        router.Release(152);

        Assert.Empty(sink.ReturnedInventoryItems);
        Assert.Equal(2, sink.Pointers.Count(pointer => pointer.Click));
    }

    [Fact]
    public void SystemCancellationInterruptsPendingInventoryDoubleTap()
    {
        var sink = new RecordingTouchSink();
        var count = 64;
        var router = new TouchInputRouter(sink,
            inventorySlotState: (_, _) => new InventorySlotState(17, count));

        router.Press(153, 0.48, 0.36, controlsEnabled: false);
        router.Release(153);
        count = 0;
        router.CancelAll();
        router.Press(154, 0.48, 0.36, controlsEnabled: false);
        router.Release(154);

        Assert.Empty(sink.ReturnedInventoryItems);
        Assert.Equal(2, sink.Pointers.Count(pointer => pointer.Click));
    }

    [Theory]
    [InlineData(-1, -1, -1)]
    [InlineData(0x0001_2340, 0x0122, 64)]
    [InlineData(0x0000_0100, 0, 0)]
    public void PackedInventorySlotStatePreservesIdentityAndCount(
        int packed, int expectedSlot, int expectedCount)
    {
        var state = InventorySlotState.FromPacked(packed);

        Assert.Equal(expectedSlot, state.SlotId);
        Assert.Equal(expectedCount, state.Count);
    }

    [Fact]
    public void CancelledSecondInventoryTapDoesNotSplitTheEarlierStack()
    {
        var sink = new RecordingTouchSink();
        var count = 64;
        var router = new TouchInputRouter(sink, inventoryStackCount: (_, _) => count);

        router.Press(45, 0.48, 0.36, controlsEnabled: false);
        router.Release(45);
        count = 0;
        router.Press(46, 0.48, 0.36, controlsEnabled: false);
        router.Release(46, cancelled: true);
        router.Press(47, 0.48, 0.36, controlsEnabled: false);
        router.Release(47);

        Assert.Empty(sink.ReturnedInventoryItems);
        Assert.Equal(2, sink.Pointers.Count(pointer => pointer.Click));
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
    public void HardwareTextInputGateSuppressesRapidPhysicalKeyEchoesInOrder()
    {
        var gate = new HardwareTextInputGate();

        Assert.True(gate.AcceptHardwareCodePoint(29, 'a', 8_000, 9_000, 0));
        Assert.True(gate.AcceptHardwareCodePoint(30, 'b', 8_010, 9_010, 0));

        Assert.False(gate.AcceptCommittedCodePoint('a', 9_020));
        Assert.False(gate.AcceptCommittedCodePoint('b', 9_021));
        Assert.True(gate.AcceptCommittedCodePoint('a', 9_022));
        Assert.True(gate.AcceptCommittedCodePoint('b', 9_023));
    }

    [Fact]
    public void HardwareTextInputGateAllowsMissingEarlierEchoBeforeLatestEcho()
    {
        var gate = new HardwareTextInputGate();

        Assert.True(gate.AcceptHardwareCodePoint(29, 'a', 10_000, 11_000, 0));
        Assert.True(gate.AcceptHardwareCodePoint(30, 'b', 10_010, 11_010, 0));

        Assert.False(gate.AcceptCommittedCodePoint('b', 11_020));
        Assert.True(gate.AcceptCommittedCodePoint('a', 11_021));
        Assert.True(gate.AcceptCommittedCodePoint('b', 11_022));
    }

    [Fact]
    public void HardwareTextInputGateDoesNotMatchAnArbitraryMiddleCandidate()
    {
        var gate = new HardwareTextInputGate();

        Assert.True(gate.AcceptHardwareCodePoint(29, 'a', 12_000, 13_000, 0));
        Assert.True(gate.AcceptHardwareCodePoint(30, 'b', 12_010, 13_010, 0));
        Assert.True(gate.AcceptHardwareCodePoint(31, 'c', 12_020, 13_020, 0));

        Assert.True(gate.AcceptCommittedCodePoint('b', 13_030));
        Assert.True(gate.AcceptCommittedCodePoint('c', 13_031));
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

    [Fact]
    public void AndroidImeCommitNormalizesSubmitAndTabKeysWithoutLosingText()
    {
        var plan = AndroidTextCommitPlan.Create("say 👋\r\nnext\tfield\n".EnumerateRunes());

        Assert.Equal(
        [
            new(AndroidTextCommitKind.Text, "say 👋"),
            new(AndroidTextCommitKind.Enter),
            new(AndroidTextCommitKind.Text, "next"),
            new(AndroidTextCommitKind.Tab),
            new(AndroidTextCommitKind.Text, "field"),
            new(AndroidTextCommitKind.Enter)
        ], plan);
    }

    [Fact]
    public void AndroidGlfwSnapshotPreservesMixedTextAndKeyOrder()
    {
        var repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", ".."));
        var native = File.ReadAllText(Path.Combine(repository,
            "src", "Amber.Android.Native", "jay_jvm_host.c"));
        var scalarQueue = File.ReadAllText(Path.Combine(repository,
            "src", "Amber.Android.Native", "jay_scalar_queue.c"));
        var nativeBuild = File.ReadAllText(Path.Combine(repository,
            "scripts", "build-android-native.sh"));
        var glfw = File.ReadAllText(Path.Combine(repository,
            "third_party", "android-glfw", "jay-android.patch"));

        Assert.Contains("uint32_t event_types[JAY_EVENT_CAPACITY]", native, StringComparison.Ordinal);
        Assert.Contains("int32_t event_values[JAY_EVENT_CAPACITY]", native, StringComparison.Ordinal);
        Assert.Contains("jay_scalar_queue_push(&g_events, JAY_EVENT_CHAR", native,
            StringComparison.Ordinal);
        Assert.Contains("jay_scalar_queue_push(&g_events, JAY_EVENT_KEY", native,
            StringComparison.Ordinal);
        Assert.Contains("snapshot->event_count = jay_scalar_queue_drain(", native,
            StringComparison.Ordinal);
        Assert.Contains("while (drained < capacity && queue->head != NULL)", scalarQueue,
            StringComparison.Ordinal);
        Assert.Contains("jay_scalar_queue.c", nativeBuild, StringComparison.Ordinal);
        Assert.DoesNotContain("g_event_count < JAY_EVENT_CAPACITY", native,
            StringComparison.Ordinal);
        Assert.DoesNotContain("snapshot->text_count", native, StringComparison.Ordinal);
        Assert.DoesNotContain("snapshot->key_count", native, StringComparison.Ordinal);
        Assert.Contains("input.eventTypes[index] == 1", glfw, StringComparison.Ordinal);
        Assert.Contains("input.eventTypes[index] == 2", glfw, StringComparison.Ordinal);
    }

    [Fact]
    public void AndroidGlfwQueuesPointerAndSplitEventsInOriginalOrder()
    {
        var repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", ".."));
        var native = File.ReadAllText(Path.Combine(repository,
            "src", "Amber.Android.Native", "jay_jvm_host.c"));
        var queue = File.ReadAllText(Path.Combine(repository,
            "src", "Amber.Android.Native", "jay_menu_queue.c"));
        var nativeBuild = File.ReadAllText(Path.Combine(repository,
            "scripts", "build-android-native.sh"));
        var glfw = File.ReadAllText(Path.Combine(repository,
            "third_party", "android-glfw", "jay-android.patch"));

        Assert.Contains("JAY_MENU_EVENT_CAPACITY 32", native, StringComparison.Ordinal);
        Assert.Contains("JAY_MENU_EVENT_POINTER, x, y, 1", native,
            StringComparison.Ordinal);
        Assert.Contains("JAY_MENU_EVENT_SPLIT, x, y, count", native,
            StringComparison.Ordinal);
        Assert.Contains("snapshot->menu_event_count = jay_menu_queue_drain(", native,
            StringComparison.Ordinal);
        Assert.Contains("while (drained < capacity && queue->head != NULL)", queue,
            StringComparison.Ordinal);
        Assert.Contains("jay_menu_queue.c", nativeBuild, StringComparison.Ordinal);
        Assert.DoesNotContain("g_menu_event_count < JAY_MENU_EVENT_CAPACITY", native,
            StringComparison.Ordinal);
        Assert.Contains("for (uint32_t index = 0; index < input.menuEventCount; index++)", glfw,
            StringComparison.Ordinal);
        Assert.Contains(
            "input.eventCount == 0 && input.menuEventCount == 0",
            glfw, StringComparison.Ordinal);
        Assert.Contains("input.menuEventTypes[index] == JAY_MENU_EVENT_POINTER", glfw,
            StringComparison.Ordinal);
        Assert.Contains("input.menuEventTypes[index] == JAY_MENU_EVENT_SPLIT", glfw,
            StringComparison.Ordinal);
        Assert.DoesNotContain("inventorySplitGeneration", glfw, StringComparison.Ordinal);
        Assert.DoesNotContain("pointerClicks", glfw, StringComparison.Ordinal);
    }

    [Fact]
    public void AndroidHeldTouchIndicatorsAnimateAtDisplayRefreshRate()
    {
        var repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", ".."));
        var activity = File.ReadAllText(Path.Combine(repository,
            "src", "Amber.Android", "GameActivity.cs"));

        Assert.Contains("var animatingHoldFeedback = DrawLongPressIndicators(canvas)",
            activity, StringComparison.Ordinal);
        Assert.Contains("if (animatingHoldFeedback || _router.InventorySplitSelections.Count > 0)",
            activity, StringComparison.Ordinal);
        Assert.Contains("return drewAnimatedFeedback", activity, StringComparison.Ordinal);
        Assert.Contains("PostInvalidateOnAnimation()", activity, StringComparison.Ordinal);
        Assert.Contains("PostInvalidateDelayed(100)", activity, StringComparison.Ordinal);
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

    [Theory]
    [InlineData(true, true, false, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, true, false)]
    public void SoftKeyboardOnlyOpensAfterTouchingAnEditorBeforeOrAfterTheClick(
        bool textInputActive, bool touchedFocusedEditor, bool focusedAfterClick, bool expected)
    {
        Assert.Equal(expected,
            SoftKeyboardInputPolicy.ShouldOpenAfterTouch(
                textInputActive, touchedFocusedEditor, focusedAfterClick));
    }

    [Fact]
    public void FinishingAndroidCompositionCommitsItExactlyOnce()
    {
        var composition = new AndroidCompositionBuffer();
        composition.Set("你");

        Assert.Equal("你", composition.Take());
        Assert.Empty(composition.Take());

        composition.Set("old");
        composition.Set("new");
        Assert.Equal("new", composition.Take());
        composition.Set("discarded");
        composition.Clear();
        Assert.Empty(composition.Take());
    }

    [Fact]
    public void OrdinarySignEditorCanOpenKeyboardWithoutAFocusedWidget()
    {
        var session = new SoftKeyboardInputSession();
        session.Request(gameplayActive: false, focusState: 0);

        Assert.True(session.AcceptsConnection(focusState: 0));

        session.Observe(gameplayActive: true, focusState: 0);
        Assert.False(session.AcceptsConnection(focusState: 0));
    }

    [Fact]
    public void HangingSignEditorCanOpenKeyboardWithoutAFocusedWidget()
    {
        var session = new SoftKeyboardInputSession();
        session.Request(gameplayActive: false, focusState: 0);

        Assert.True(session.AcceptsConnection(focusState: 0));

        session.Observe(gameplayActive: true, focusState: 0);
        Assert.False(session.AcceptsConnection(focusState: 0));
    }

    [Fact]
    public void ChatKeyboardFallbackSurvivesOpeningTransitionThenEndsOnGameplayReturn()
    {
        var session = new SoftKeyboardInputSession();
        session.Request(gameplayActive: true, focusState: 0);

        session.Observe(gameplayActive: true, focusState: 0);
        Assert.True(session.AcceptsConnection(focusState: 0));

        session.Observe(gameplayActive: false, focusState: 1);
        Assert.True(session.AcceptsConnection(focusState: 1));

        session.Observe(gameplayActive: true, focusState: 0);
        Assert.False(session.AcceptsConnection(focusState: 0));
    }

    [Fact]
    public void LostChatKeyDoesNotLeaveTheSoftKeyboardArmedDuringGameplay()
    {
        long uptime = 10_000;
        var session = new SoftKeyboardInputSession(() => uptime);
        session.Request(gameplayActive: true, focusState: 0);

        uptime += 1_999;
        session.Observe(gameplayActive: true, focusState: 0);
        Assert.True(session.AcceptsConnection(focusState: 0));

        uptime++;
        session.Observe(gameplayActive: true, focusState: 0);
        Assert.False(session.AcceptsConnection(focusState: 0));
    }

    [Fact]
    public void SoftKeyboardPreviewTracksUnicodeCompositionCommitAndBackspace()
    {
        var preview = new SoftKeyboardPreviewBuffer();
        preview.Begin();

        preview.SetComposingText("你");
        Assert.Equal("你", preview.Text);

        preview.Commit("你好👋".EnumerateRunes());
        Assert.Equal("你好👋", preview.Text);

        preview.Backspace(1);
        Assert.Equal("你好", preview.Text);
        Assert.True(preview.IsActive);
    }

    [Fact]
    public void SoftKeyboardPreviewOmitsSubmitCharactersAndClearsAtSessionEnd()
    {
        var preview = new SoftKeyboardPreviewBuffer();
        preview.Begin();
        preview.Commit("say hi\r\n\t".EnumerateRunes());

        Assert.Equal("say hi", preview.Text);

        preview.End();
        Assert.False(preview.IsActive);
        Assert.Empty(preview.Text);
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
    public void MissingAndroidActionButtonIsRecoveredFromButtonState()
    {
        var tracker = new HardwareMouseButtonTracker();

        Assert.Equal(HardwareMouseButtonTracker.Secondary,
            tracker.Resolve(actionButton: 0, buttonState: HardwareMouseButtonTracker.Secondary,
                pressed: true, primaryFallback: false));
        Assert.Equal(HardwareMouseButtonTracker.Secondary,
            tracker.Resolve(actionButton: 0, buttonState: 0,
                pressed: false, primaryFallback: false));
    }

    [Fact]
    public void DuplicateAndroidMousePacketsDoNotInventAnotherButtonTransition()
    {
        var tracker = new HardwareMouseButtonTracker();

        Assert.Equal(HardwareMouseButtonTracker.Primary,
            tracker.Resolve(actionButton: 0, buttonState: HardwareMouseButtonTracker.Primary,
                pressed: true, primaryFallback: true));
        Assert.Equal(0, tracker.Resolve(actionButton: 0,
            buttonState: HardwareMouseButtonTracker.Primary,
            pressed: true, primaryFallback: false));
        Assert.Equal(HardwareMouseButtonTracker.Primary,
            tracker.Resolve(actionButton: 0, buttonState: 0,
                pressed: false, primaryFallback: true));
        Assert.Equal(0, tracker.Resolve(actionButton: 0, buttonState: 0,
            pressed: false, primaryFallback: false));
    }

    [Fact]
    public void AndroidMiddleMouseKeepsDesktopPickBlockSemantics()
    {
        var repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", ".."));
        var activity = File.ReadAllText(Path.Combine(repository,
            "src", "Amber.Android", "GameActivity.cs"));
        var sdl = File.ReadAllText(Path.Combine(repository,
            "src", "Amber.Android", "Sdl", "SdlTouchInput.cs"));
        var native = File.ReadAllText(Path.Combine(repository,
            "src", "Amber.Android.Native", "jay_jvm_host.c"));
        var glfw = File.ReadAllText(Path.Combine(repository,
            "third_party", "android-glfw", "jay-android.patch"));

        Assert.Equal(12, (int)TouchControlAction.PickBlock);
        Assert.Contains("HardwareMouseButtonTracker.Tertiary => TouchControlAction.PickBlock", activity,
            StringComparison.Ordinal);
        Assert.DoesNotContain("HardwareMouseButtonTracker.Tertiary => TouchControlAction.Inventory", activity,
            StringComparison.Ordinal);
        Assert.Contains("TouchControlAction.PickBlock, false", sdl, StringComparison.Ordinal);
        Assert.Contains("TouchControlAction.PickBlock)", sdl, StringComparison.Ordinal);
        Assert.Contains("#define JAY_ACTION_COUNT 13", native, StringComparison.Ordinal);
        Assert.Contains("GLFW_MOUSE_BUTTON_MIDDLE", glfw, StringComparison.Ordinal);
        Assert.Contains("action <= 2 || action == 12", glfw, StringComparison.Ordinal);
    }

    [Fact]
    public void TouchOverlayReleasesHeldInputAcrossBothGlfwAndSdlWindowFocusChanges()
    {
        var repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", ".."));
        var activity = File.ReadAllText(Path.Combine(repository,
            "src", "Amber.Android", "GameActivity.cs"));
        var overrideStart = activity.IndexOf(
            "public override void OnWindowFocusChanged(bool hasWindowFocus)",
            activity.IndexOf("internal sealed class TouchControlOverlay", StringComparison.Ordinal),
            StringComparison.Ordinal);
        var overrideEnd = activity.IndexOf("public override IInputConnection?", overrideStart,
            StringComparison.Ordinal);

        Assert.True(overrideStart >= 0 && overrideEnd > overrideStart);
        var focusHandler = activity[overrideStart..overrideEnd];
        Assert.Contains("if (!hasWindowFocus) CancelAllInput();", focusHandler, StringComparison.Ordinal);
        Assert.Contains("UpdateHardwareCapture(hasWindowFocus);", focusHandler, StringComparison.Ordinal);
    }

    [Fact]
    public void SdlGameProcessIsReclaimedAfterItsEmbeddedJvmReturns()
    {
        var repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", ".."));
        var manifest = File.ReadAllText(Path.Combine(repository,
            "src", "Amber.Android", "Properties", "AndroidManifest.xml"));
        var entry = File.ReadAllText(Path.Combine(repository,
            "src", "Amber.Android", "Sdl", "SdlEntry.cs"));

        Assert.Contains("android:process=\":sdlgame\"", manifest, StringComparison.Ordinal);
        Assert.Contains("finally", entry, StringComparison.Ordinal);
        Assert.Contains("activity.Finish();", entry, StringComparison.Ordinal);
        Assert.Contains("Process.KillProcess", entry, StringComparison.Ordinal);
        Assert.Contains("Process.MyPid()", entry, StringComparison.Ordinal);
    }

    [Fact]
    public void SdlGameActivitySurvivesExternalKeyboardAndMouseChanges()
    {
        var repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", ".."));
        var manifest = File.ReadAllText(Path.Combine(repository,
            "src", "Amber.Android", "Properties", "AndroidManifest.xml"));

        Assert.Contains(
            "android:configChanges=\"orientation|screenSize|screenLayout|keyboard|keyboardHidden|navigation|uiMode\"",
            manifest, StringComparison.Ordinal);
    }

    [Fact]
    public void AndroidGameLaunchIsSingleInstanceAndPreparationIsDeduplicated()
    {
        var repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", ".."));
        var manifest = File.ReadAllText(Path.Combine(repository,
            "src", "Amber.Android", "Properties", "AndroidManifest.xml"));
        var gameActivity = File.ReadAllText(Path.Combine(repository,
            "src", "Amber.Android", "GameActivity.cs"));
        var home = File.ReadAllText(Path.Combine(repository,
            "src", "Amber.App", "ViewModels", "HomeViewModel.cs"));

        Assert.Contains("android:launchMode=\"singleTask\"", manifest, StringComparison.Ordinal);
        Assert.Contains("LaunchMode = LaunchMode.SingleTask", gameActivity, StringComparison.Ordinal);
        Assert.Contains("Interlocked.CompareExchange(ref _mobileLaunchInProgress, 1, 0)", home,
            StringComparison.Ordinal);
        Assert.Contains("Volatile.Write(ref _mobileLaunchInProgress, 0)", home, StringComparison.Ordinal);
    }

    [Fact]
    public void AndroidInventoryAccessRunsOnTheMinecraftClientThread()
    {
        var repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", ".."));
        var glfw = File.ReadAllText(Path.Combine(repository, "src", "Amber.Android", "Java",
            "nz", "work", "minecraft", "jay", "AndroidGameBootstrap.java"));
        var sdl = File.ReadAllText(Path.Combine(repository, "src", "Amber.Android", "Java",
            "nz", "work", "minecraft", "jay", "SdlMenuBootstrap.java"));

        var glfwSplit = JavaMethod(glfw, "public static boolean dispatchInventorySplit", "/** True only");
        var sdlSplit = JavaMethod(sdl, "public static boolean dispatchInventorySplit", "public static boolean isBlockTarget");
        var glfwCount = JavaMethod(glfw, "public static int inventoryStackCount", "/** Picks an exact quantity");
        var sdlCount = JavaMethod(sdl, "public static int inventoryStackCount", "public static boolean dispatchInventorySplit");
        Assert.Contains("minecraftExecute.invoke", glfwSplit, StringComparison.Ordinal);
        Assert.Contains("dispatchInventorySplitNow", glfwSplit, StringComparison.Ordinal);
        Assert.Contains("((Executor)game).execute", sdlSplit, StringComparison.Ordinal);
        Assert.Contains("minecraftExecute.invoke", glfwCount, StringComparison.Ordinal);
        Assert.Contains("FutureTask<Integer>", glfwCount, StringComparison.Ordinal);
        Assert.Contains("((Executor)game).execute", sdlCount, StringComparison.Ordinal);
        Assert.Contains("FutureTask<Integer>", sdlCount, StringComparison.Ordinal);
        Assert.Contains("get(250, TimeUnit.MILLISECONDS)", glfwCount, StringComparison.Ordinal);
        Assert.Contains("get(250, TimeUnit.MILLISECONDS)", sdlCount, StringComparison.Ordinal);
    }

    [Fact]
    public void NamedAndroidInventoryBridgeInitializesTheMenuSlotIdentity()
    {
        var repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", ".."));
        var bootstrap = File.ReadAllText(Path.Combine(repository, "src", "Amber.Android", "Java",
            "nz", "work", "minecraft", "jay", "AndroidGameBootstrap.java"));
        var named = JavaMethod(bootstrap, "private static void prepareNamedInputBridge",
            "private static void prepareNamedMouseInputFactory");

        Assert.Contains("slotId = accessible(slotType.getDeclaredField(\"index\"))", named,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AndroidNativeAndManagedJvmAbiVersionsMatchTheQueuedInputLayout()
    {
        var repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", ".."));
        var native = File.ReadAllText(Path.Combine(repository,
            "src", "Amber.Android.Native", "jay_jvm_host.c"));
        var managed = File.ReadAllText(Path.Combine(repository,
            "src", "Amber.Android", "NativeJvmHost.cs"));

        Assert.Contains("#define JAY_JVM_ABI_VERSION 20", native, StringComparison.Ordinal);
        Assert.Contains("ExpectedAbiVersion = 20", managed, StringComparison.Ordinal);
    }

    [Fact]
    public void SoftKeyboardPreviewKeepsClampBoundsValidAtTinyVisibleHeights()
    {
        var repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", ".."));
        var activity = File.ReadAllText(Path.Combine(repository,
            "src", "Amber.Android", "GameActivity.cs"));
        var draw = activity[activity.IndexOf("private void DrawSoftKeyboardPreview",
            StringComparison.Ordinal)..activity.IndexOf("private void DrawMovementPad",
            StringComparison.Ordinal)];

        Assert.Contains("if (visibleBottom <= margin * 2", draw, StringComparison.Ordinal);
        Assert.Contains("minimumTextSize = Math.Min(22f, maximumTextSize)", draw,
            StringComparison.Ordinal);
        Assert.Contains("minimumTextSize, maximumTextSize", draw, StringComparison.Ordinal);
    }

    [Fact]
    public void SdlMiningProgressDetectsTheLiveBlockTarget()
    {
        var repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", ".."));
        var sdl = File.ReadAllText(Path.Combine(repository, "src", "Amber.Android", "Java",
            "nz", "work", "minecraft", "jay", "SdlMenuBootstrap.java"));
        var blockTarget = JavaMethod(sdl, "public static boolean isBlockTarget", "// SDLActivity owns");

        Assert.Contains("input.current.invoke", blockTarget, StringComparison.Ordinal);
        Assert.Contains("input.hitResult.get", blockTarget, StringComparison.Ordinal);
        Assert.Contains("input.blockHit.isInstance", blockTarget, StringComparison.Ordinal);
        Assert.DoesNotContain("return false; }", blockTarget, StringComparison.Ordinal);
    }

    [Fact]
    public void FreshAndroidOptionsUseTouchFriendlyAutomaticGuiScale()
    {
        var options = MobileGameOptions.Prepare(existing: null, migrateLegacyScale: true);

        Assert.Contains("guiScale:0", options, StringComparison.Ordinal);
        Assert.Contains("lang:zh_cn", options, StringComparison.Ordinal);
        Assert.DoesNotContain("guiScale:3", options, StringComparison.Ordinal);
    }

    [Fact]
    public void AndroidGuiScaleMigrationRunsOnceAndPreservesPlayerChoices()
    {
        const string legacy = "version:4671\r\nguiScale:3\r\nlang:en_us\r\nmaxFps:90\r\n";
        var migrated = MobileGameOptions.Prepare(legacy, migrateLegacyScale: true);

        Assert.Contains("guiScale:0\r\n", migrated, StringComparison.Ordinal);
        Assert.Contains("lang:en_us\r\n", migrated, StringComparison.Ordinal);
        Assert.Contains("maxFps:90\r\n", migrated, StringComparison.Ordinal);
        Assert.DoesNotContain("lang:zh_cn", migrated, StringComparison.Ordinal);

        var playerSelectedThree = MobileGameOptions.Prepare(
            migrated.Replace("guiScale:0", "guiScale:3", StringComparison.Ordinal),
            migrateLegacyScale: false);
        Assert.Contains("guiScale:3", playerSelectedThree, StringComparison.Ordinal);
    }

    [Fact]
    public void SdlMenuBridgeExposesFocusedEditorHitTestingToAndroid()
    {
        var repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", ".."));
        var sdl = File.ReadAllText(Path.Combine(repository, "src", "Amber.Android", "Java",
            "nz", "work", "minecraft", "jay", "SdlMenuBootstrap.java"));

        Assert.Contains("public static int textInputFocusState()", sdl, StringComparison.Ordinal);
        Assert.Contains("AndroidGameBootstrap.textInputFocusState()", sdl, StringComparison.Ordinal);
        Assert.Contains("public static boolean isTextInputAt(float x, float y)", sdl,
            StringComparison.Ordinal);
        Assert.Contains("AndroidGameBootstrap.isTextInputAt(x, y)", sdl, StringComparison.Ordinal);
    }

    [Fact]
    public void EverySdlInstanceUsesTheTouchMenuBootstrapInsteadOfOneHardCodedVersion()
    {
        var repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", ".."));
        var embedded = File.ReadAllText(Path.Combine(repository, "src", "Amber.Android",
            "AndroidEmbeddedGame.cs"));
        var bootstrap = JavaMethod(embedded, "var bootstrap = Path.Combine", "return new AndroidPreparedGame");

        Assert.Contains("sdl ? \"nz.work.minecraft.jay.SdlMenuBootstrap\"", bootstrap,
            StringComparison.Ordinal);
        Assert.DoesNotContain("versionId == \"26.3\"", embedded, StringComparison.Ordinal);
        Assert.DoesNotContain("if (sdl && !namedSdlInput)", embedded, StringComparison.Ordinal);
    }

    [Fact]
    public void SdlAndroidBackUsesMinecraftEscapeWhileTheGameIsRunning()
    {
        var repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", ".."));
        var activity = File.ReadAllText(Path.Combine(repository, "src", "Amber.Android", "Sdl",
            "JaySdlActivity.java"));
        var back = JavaMethod(activity, "private void handleSystemBack", "super.onBackPressed();");

        Assert.Contains("mSDLThread != null", back, StringComparison.Ordinal);
        Assert.Contains("!mSDLMainFinished", back, StringComparison.Ordinal);
        Assert.Contains("onNativeKeyDown(KeyEvent.KEYCODE_ESCAPE)", back, StringComparison.Ordinal);
        Assert.Contains("onNativeKeyUp(KeyEvent.KEYCODE_ESCAPE)", back, StringComparison.Ordinal);
        Assert.Contains("Build.VERSION.SDK_INT >= 33", activity, StringComparison.Ordinal);
        Assert.Contains("Api33BackHandler.register(this)", activity, StringComparison.Ordinal);
        Assert.Contains("Api33BackHandler.unregister(this, jayBackInvokedCallback)", activity,
            StringComparison.Ordinal);
        Assert.Contains("OnBackInvokedDispatcher.PRIORITY_DEFAULT", activity,
            StringComparison.Ordinal);
        Assert.Contains("public void onBackInvoked()", activity, StringComparison.Ordinal);
        Assert.Contains("activity.handleSystemBack()", activity, StringComparison.Ordinal);
        Assert.Contains("@Override public void onBackPressed()", activity, StringComparison.Ordinal);
        Assert.Contains("handleSystemBack();", activity, StringComparison.Ordinal);
    }

    [Fact]
    public void GlfwAndroidPredictiveBackUsesMinecraftEscapeWhileTheGameIsRunning()
    {
        var repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", ".."));
        var activity = File.ReadAllText(Path.Combine(repository, "src", "Amber.Android",
            "GameActivity.cs"));
        var manifest = File.ReadAllText(Path.Combine(repository, "src", "Amber.Android",
            "Properties", "AndroidManifest.xml"));

        Assert.Contains("BuildVersionCodes.Tiramisu", activity, StringComparison.Ordinal);
        Assert.Contains("RegisterOnBackInvokedCallback", activity, StringComparison.Ordinal);
        Assert.Contains("UnregisterOnBackInvokedCallback", activity, StringComparison.Ordinal);
        Assert.Contains("IOnBackInvokedDispatcher.PriorityDefault", activity,
            StringComparison.Ordinal);
        Assert.Contains("public void OnBackInvoked()", activity, StringComparison.Ordinal);
        Assert.Contains("activity.HandleActivityBack()", activity, StringComparison.Ordinal);
        Assert.Contains("_touchOverlay.HandleSystemBack()", activity, StringComparison.Ordinal);
        Assert.Contains("android:enableOnBackInvokedCallback=\"true\"", manifest,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SdlGameWindowResizesForTheSoftKeyboard()
    {
        var repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", ".."));
        var manifest = File.ReadAllText(Path.Combine(repository, "src", "Amber.Android",
            "Properties", "AndroidManifest.xml"));

        Assert.Contains("android:name=\"nz.work.minecraft.jay.JaySdlActivity\"", manifest,
            StringComparison.Ordinal);
        Assert.Contains("android:windowSoftInputMode=\"adjustResize\"", manifest,
            StringComparison.Ordinal);
        Assert.Contains("android:theme=\"@style/MyTheme.NoActionBar\"", manifest,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Theme.Material.NoActionBar.Fullscreen", manifest,
            StringComparison.Ordinal);
    }

    [Fact]
    public void FocusedSdlOverlayRoutesExternalKeyboardEvents()
    {
        var repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", ".."));
        var overlay = File.ReadAllText(Path.Combine(repository, "src", "Amber.Android",
            "GameActivity.cs"));
        var focusRoute = JavaMethod(overlay, "public override bool OnKeyDown", "public override void OnWindowFocusChanged");

        Assert.Contains("HandleHardwareKey(e) || base.OnKeyDown", focusRoute, StringComparison.Ordinal);
        Assert.Contains("public override bool OnKeyUp", focusRoute, StringComparison.Ordinal);
        Assert.Contains("HandleHardwareKey(e) || base.OnKeyUp", focusRoute, StringComparison.Ordinal);
    }

    [Fact]
    public void FocusedSdlOverlayLeavesAndroidSystemKeysToTheActivity()
    {
        var repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", ".."));
        var overlay = File.ReadAllText(Path.Combine(repository, "src", "Amber.Android",
            "GameActivity.cs"));
        var hardwareKeys = JavaMethod(overlay, "public bool HandleHardwareKey", "public bool HandleHardwareMotion");
        var systemGuard = hardwareKeys.IndexOf("if (e.IsSystem) return false;", StringComparison.Ordinal);
        var sdlRoute = hardwareKeys.IndexOf("if (_sdl is not null)", StringComparison.Ordinal);

        Assert.True(systemGuard >= 0 && systemGuard < sdlRoute,
            "Android system keys must bypass the focused SDL overlay before SDL routing.");
    }

    [Fact]
    public void ExternalMouseWheelReachesMinecraftMenusAsWellAsGameplay()
    {
        var repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", ".."));
        var overlay = File.ReadAllText(Path.Combine(repository, "src", "Amber.Android",
            "GameActivity.cs"));
        var hardwareMotion = JavaMethod(overlay, "public bool HandleHardwareMotion",
            "private static bool IsHardwarePointer");
        var scrollStart = hardwareMotion.IndexOf(
            "if (e.ActionMasked == MotionEventActions.Scroll)", StringComparison.Ordinal);
        var scrollEnd = hardwareMotion.IndexOf(
            "if (e.ActionMasked is MotionEventActions.ButtonPress", scrollStart,
            StringComparison.Ordinal);

        Assert.True(scrollStart >= 0 && scrollEnd > scrollStart);
        var scrollRoute = hardwareMotion[scrollStart..scrollEnd];
        Assert.DoesNotContain("if (!GameplayActive) return false;", scrollRoute,
            StringComparison.Ordinal);
        Assert.Contains("DispatchAction(action, true);", scrollRoute, StringComparison.Ordinal);
        Assert.Contains("DispatchAction(action, false);", scrollRoute, StringComparison.Ordinal);
    }

    [Fact]
    public void AndroidInventoryBridgesPreserveTheCarriedStackReturnOperation()
    {
        var repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", ".."));
        var native = File.ReadAllText(Path.Combine(repository, "src", "Amber.Android.Native",
            "jay_jvm_host.c"));
        var glfw = File.ReadAllText(Path.Combine(repository, "src", "Amber.Android", "Java",
            "nz", "work", "minecraft", "jay", "AndroidGameBootstrap.java"));
        var sdl = File.ReadAllText(Path.Combine(repository, "src", "Amber.Android", "Java",
            "nz", "work", "minecraft", "jay", "SdlMenuBootstrap.java"));

        Assert.Contains("if (count == 0) return;", native, StringComparison.Ordinal);
        Assert.Contains("if (requested < 0)", glfw, StringComparison.Ordinal);
        Assert.Contains("if (requested < 0)", sdl, StringComparison.Ordinal);
        Assert.Contains("for (int returned = 0; returned < -requested; returned++)", sdl,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DownloadableAndroidBuildsRequireBuiltInCurseForgeKeyButPullRequestsStayKeyless()
    {
        var repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", ".."));
        var workflow = File.ReadAllText(Path.Combine(repository,
            ".github", "workflows", "build.yml")).Replace("\r\n", "\n", StringComparison.Ordinal);
        var androidStart = workflow.IndexOf("  android:\n", StringComparison.Ordinal);
        var androidEnd = workflow.IndexOf("\n  android-smoke:\n", androidStart, StringComparison.Ordinal);

        Assert.True(androidStart >= 0 && androidEnd > androidStart);
        var android = workflow[androidStart..androidEnd];
        Assert.Contains("JAY_CURSEFORGE_API_KEY:", android, StringComparison.Ordinal);
        Assert.Contains("secrets.CURSEFORGE_API_KEY", android, StringComparison.Ordinal);
        Assert.Contains("github.event_name == 'push' && github.ref == 'refs/heads/main'", android,
            StringComparison.Ordinal);
        Assert.Contains("if: env.RELEASE_SIGNED_ANDROID == 'true'", android, StringComparison.Ordinal);
        Assert.Contains("test -n \"$ANDROID_RELEASE_KEYSTORE_BASE64\"", android, StringComparison.Ordinal);
        Assert.Contains("if [ -z \"$JAY_CURSEFORGE_API_KEY\" ]", android, StringComparison.Ordinal);
        var ciPublishStart = android.IndexOf("      - name: Publish CI APK", StringComparison.Ordinal);
        var signedPublishStart = android.IndexOf("      - name: Publish release-signed preview APK",
            StringComparison.Ordinal);
        Assert.True(ciPublishStart >= 0 && signedPublishStart > ciPublishStart);
        Assert.DoesNotContain("secrets.CURSEFORGE_API_KEY", android[ciPublishStart..signedPublishStart],
            StringComparison.Ordinal);
        Assert.Contains("JAY_CURSEFORGE_API_KEY: ${{ secrets.CURSEFORGE_API_KEY }}",
            android[signedPublishStart..], StringComparison.Ordinal);
    }

    [Fact]
    public void FreshAndroidLaunchFetchesVersionMetadataBeforeChoosingItsJavaRuntime()
    {
        var repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", ".."));
        var application = File.ReadAllText(Path.Combine(repository,
            "src", "Amber.Android", "Application.cs"));
        var embeddedGame = File.ReadAllText(Path.Combine(repository,
            "src", "Amber.Android", "AndroidEmbeddedGame.cs"));

        var metadataFetch = application.IndexOf("PrepareVersionMetadataAsync(", StringComparison.Ordinal);
        var runtimeSupport = application.IndexOf("AndroidRuntimeChoice.IsSupported(requiredJava)", StringComparison.Ordinal);
        var runtimeChoice = application.IndexOf("AndroidRuntimeChoice.UsesJava25(requiredJava)", StringComparison.Ordinal);
        var runtimeInstall = application.IndexOf("AndroidRuntimeStore.InstallAsync", StringComparison.Ordinal);
        Assert.True(metadataFetch >= 0 && runtimeSupport > metadataFetch && runtimeChoice > runtimeSupport &&
                    runtimeInstall > runtimeChoice);

        var prepareMethod = embeddedGame.IndexOf("PrepareVersionMetadataAsync(", StringComparison.Ordinal);
        var prepareVersion = embeddedGame.IndexOf("installer.PrepareVersionAsync", prepareMethod,
            StringComparison.Ordinal);
        var resolveVersion = embeddedGame.IndexOf("gameDirectory.ResolveVersion(versionId)", prepareMethod,
            StringComparison.Ordinal);
        Assert.True(prepareMethod >= 0 && prepareVersion > prepareMethod && resolveVersion > prepareVersion);
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

    [Fact]
    public async Task AndroidRuntimeArchiveInstallerSerializesConcurrentInstalls()
    {
        var parent = Path.Combine(Path.GetTempPath(), "jay-runtime-archive-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(parent, "active");
        var archive = CreateRuntimeArchive();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var secondOpened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = 0;
        try
        {
            var first = RuntimeArchiveInstaller.InstallCoreAsync(
                target,
                RuntimeArchive(archive),
                (_, _) => Task.FromResult<Stream>(new MemoryStream(archive, writable: false)),
                progress: null,
                CancellationToken.None,
                beforeExtractEntry: () =>
                {
                    if (Interlocked.Exchange(ref held, 1) != 0) return;
                    entered.Set();
                    release.Wait();
                });

            Assert.True(entered.Wait(TimeSpan.FromSeconds(30)), "First extraction never started.");
            var second = RuntimeArchiveInstaller.InstallAsync(
                target,
                RuntimeArchive(archive),
                (_, _) =>
                {
                    secondOpened.TrySetResult();
                    return Task.FromResult<Stream>(new MemoryStream(archive, writable: false));
                },
                reuseInstalled: true);

            await Task.Delay(100);
            Assert.False(secondOpened.Task.IsCompleted);
            release.Set();
            var firstManifest = await first.WaitAsync(TimeSpan.FromSeconds(60));
            var secondManifest = await second.WaitAsync(TimeSpan.FromSeconds(60));
            Assert.False(secondOpened.Task.IsCompleted);
            Assert.Equal(firstManifest.Id, secondManifest.Id);
            Assert.Equal(firstManifest.Files.Select(file => file.Path),
                secondManifest.Files.Select(file => file.Path));
        }
        finally
        {
            release.Set();
            if (Directory.Exists(parent)) Directory.Delete(parent, true);
        }
    }

    [Fact]
    public async Task AndroidRuntimeArchiveInstallerRecoversInterruptedFilesBeforeDownloading()
    {
        var parent = Path.Combine(Path.GetTempPath(), "jay-runtime-archive-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(parent, "active");
        var backup = target + ".backup-crash";
        var staging = target + ".install-crash";
        var download = target + ".download-crash";
        Directory.CreateDirectory(backup);
        Directory.CreateDirectory(staging);
        await File.WriteAllTextAsync(Path.Combine(backup, "keep.txt"), "old runtime");
        await File.WriteAllTextAsync(Path.Combine(staging, "partial.txt"), "partial runtime");
        await File.WriteAllTextAsync(download, "partial archive");
        try
        {
            var archive = CreateRuntimeArchive();
            await Assert.ThrowsAsync<IOException>(() => RuntimeArchiveInstaller.InstallAsync(
                target,
                RuntimeArchive(archive),
                (_, _) => throw new IOException("Simulated download failure.")));

            Assert.Equal("old runtime", await File.ReadAllTextAsync(Path.Combine(target, "keep.txt")));
            Assert.Equal([target], Directory.GetFileSystemEntries(parent));
        }
        finally
        {
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

    private static string JavaMethod(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing Java method marker: {startMarker}");
        var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"Missing Java method end marker: {endMarker}");
        return source[start..end];
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
        public List<(double X, double Y, int Count)> ReturnedInventoryItems { get; } = [];

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
        public void ReturnCarriedInventoryItems(double x, double y, int count) =>
            ReturnedInventoryItems.Add((x, y, count));
    }

}
