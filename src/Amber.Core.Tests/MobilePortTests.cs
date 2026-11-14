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
using System.Net;
using Amber.Core.Net;

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
        Assert.Contains(TouchControlLayout.Default, control => control.Action == TouchControlAction.Move);
        Assert.Contains(TouchControlLayout.Default, control => control.Action == TouchControlAction.Jump);
        Assert.DoesNotContain(TouchControlLayout.Default, control => control.Action == TouchControlAction.PrimaryAction);
        Assert.Contains(TouchControlLayout.Default, control => control.Action == TouchControlAction.Sneak);
        Assert.DoesNotContain(TouchControlLayout.Default, control => control.Action == TouchControlAction.Sprint);
        Assert.DoesNotContain(TouchControlLayout.Default, control => control.Action == TouchControlAction.PreviousSlot);
        Assert.DoesNotContain(TouchControlLayout.Default, control => control.Action == TouchControlAction.NextSlot);

        foreach (var control in TouchControlLayout.Default)
        {
            Assert.InRange(control.CenterX - control.Radius, 0, 1);
            Assert.InRange(control.CenterX + control.Radius, 0, 1);
            Assert.InRange(control.CenterY - control.Radius, 0, 1);
            Assert.InRange(control.CenterY + control.Radius, 0, 1);
            if (control.GameplayVisible)
                Assert.Equal(control.Action, TouchControlLayout.HitTest(control.CenterX, control.CenterY)!.Action);
            if (control.MenuVisible)
                Assert.Equal(control.Action, TouchControlLayout.HitTest(control.CenterX, control.CenterY,
                    gameplayControlsActive: false)!.Action);
        }

        var move = TouchControlLayout.Default.Single(control => control.Action == TouchControlAction.Move);
        var right = TouchControlLayout.JoystickVector(move.CenterX + move.Radius * 2, move.CenterY);
        Assert.Equal(1, right.X, 6);
        Assert.Equal(0, right.Y, 6);
        Assert.DoesNotContain(TouchControlLayout.Default, control => control.Label is "Q" or "E" or "Run" or "Use");
        Assert.Equal("背包", TouchControlLayout.Default.Single(control => control.Action == TouchControlAction.Inventory).Label);
        Assert.DoesNotContain(TouchControlLayout.Default, control => control.Action == TouchControlAction.Drop);

        var menu = TouchControlLayout.Default.Single(control => control.Action == TouchControlAction.Menu);
        var movePad = TouchControlLayout.Default.Single(control => control.Action == TouchControlAction.Move);
        var split = TouchControlLayout.Default.Single(control => control.Action == TouchControlAction.SecondaryAction);
        var inventory = TouchControlLayout.Default.Single(control => control.Action == TouchControlAction.Inventory);
        var jump = TouchControlLayout.Default.Single(control => control.Action == TouchControlAction.Jump);
        var sneak = TouchControlLayout.Default.Single(control => control.Action == TouchControlAction.Sneak);
        Assert.InRange(menu.CenterX, 0.47, 0.53);
        Assert.True(movePad.CenterX < 0.25 && movePad.CenterY > 0.60);
        Assert.InRange(inventory.CenterX, 0.600, 0.612);
        Assert.True(inventory.CenterY > 0.95);
        Assert.True(jump.CenterY > 0.70 && sneak.CenterY > 0.70);
        Assert.False(split.GameplayVisible);
        Assert.True(split.MenuVisible);
        Assert.Null(TouchControlLayout.HitTest(split.CenterX, split.CenterY));
    }

    [Fact]
    public void TouchProfilesExposeThreeDistinctMobileSchemes()
    {
        var profile = new TouchControlProfile();
        Assert.DoesNotContain(profile.Controls, control => control.Action == TouchControlAction.PrimaryAction);

        profile.Mode = TouchControlMode.DPadAndTap;
        var move = profile.Controls.Single(control => control.Action == TouchControlAction.Move);
        Assert.Equal(TouchControlAction.Sneak,
            profile.HitTest(move.CenterX, move.CenterY)!.Action);
        var direction = profile.MovementVector(move.CenterX + move.Radius, move.CenterY + move.Radius * 0.15);
        Assert.Equal(1, direction.X, 6);
        Assert.Equal(0, direction.Y, 6);

        profile.Mode = TouchControlMode.JoystickAndCrosshair;
        Assert.Contains(profile.Controls, control => control.Action == TouchControlAction.PrimaryAction);
        Assert.Contains(profile.Controls, control => control.Action == TouchControlAction.SecondaryAction && control.GameplayVisible);
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

    [Fact]
    public void TouchInputRouterAutoSprintsWhenTheJoystickIsPushedFullyForward()
    {
        var sink = new RecordingTouchSink();
        var router = new TouchInputRouter(sink);
        var move = TouchControlLayout.Default.Single(control => control.Action == TouchControlAction.Move);

        Assert.True(router.Press(50, move.CenterX, move.CenterY));
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
        Assert.Contains((TouchControlAction.PrimaryAction, false), sink.Actions);
        Assert.False(router.IsLongPress(15));
        Assert.Equal(0.07, sink.LastLook.X, 6);
        Assert.Equal(-0.05, sink.LastLook.Y, 6);
        Assert.True(router.Release(15));
    }

    [Fact]
    public void TouchInputRouterLetsSecondFingerAimWhileMovementStaysHeld()
    {
        var sink = new RecordingTouchSink();
        var router = new TouchInputRouter(sink);
        var move = TouchControlLayout.Default.Single(control => control.Action == TouchControlAction.Move);

        Assert.True(router.Press(13, move.CenterX, move.CenterY));
        Assert.True(router.Move(13, move.CenterX + move.Radius, move.CenterY));
        Assert.Equal(1, sink.LastMovement.X, 6);

        Assert.True(router.Press(14, 0.55, 0.38));
        Assert.True(router.Move(14, 0.63, 0.32));
        Assert.Equal(0.08, sink.LastLook.X, 6);
        Assert.Equal(-0.06, sink.LastLook.Y, 6);
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
        Assert.Equal(0.08, sink.LastLook.X, 6);
        Assert.Equal(-0.06, sink.LastLook.Y, 6);
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
    public void TouchInputRouterBypassesControlsWhenGameplayOverlayIsDisabled()
    {
        var sink = new RecordingTouchSink();
        var router = new TouchInputRouter(sink);
        var jump = TouchControlLayout.Default.Single(control => control.Action == TouchControlAction.Jump);

        Assert.True(router.Press(21, jump.CenterX, jump.CenterY, controlsEnabled: false));
        Assert.DoesNotContain(sink.Actions, action => action.Action == TouchControlAction.Jump);
        Assert.Equal((jump.CenterX, jump.CenterY, false), sink.LastPointer);
        Assert.Contains((TouchControlAction.PrimaryAction, true), sink.Actions);
        Assert.True(router.Release(21));
        Assert.Contains((TouchControlAction.PrimaryAction, false), sink.Actions);
        Assert.DoesNotContain(sink.Pointers, pointer => pointer.Click);

        var menu = TouchControlLayout.Default.Single(control => control.Action == TouchControlAction.Menu);
        Assert.True(router.Press(22, menu.CenterX, menu.CenterY, controlsEnabled: false));
        Assert.Contains((TouchControlAction.Menu, true), sink.Actions);
    }

    [Fact]
    public void TouchInputRouterHoldsMenuPointerForSliders()
    {
        var sink = new RecordingTouchSink();
        var router = new TouchInputRouter(sink);

        Assert.True(router.Press(30, 0.35, 0.55, controlsEnabled: false));
        Assert.Contains((TouchControlAction.PrimaryAction, true), sink.Actions);
        Assert.True(router.Move(30, 0.70, 0.55));
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
        Assert.Contains((TouchControlAction.PrimaryAction, false), sink.Actions);
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
    public void TouchInputRouterUsesMenuSplitAsRightClickModifier()
    {
        var sink = new RecordingTouchSink();
        var router = new TouchInputRouter(sink);
        var split = TouchControlLayout.Default.Single(control => control.Action == TouchControlAction.SecondaryAction);

        Assert.True(router.Press(40, split.CenterX, split.CenterY, controlsEnabled: false));
        Assert.DoesNotContain(sink.Actions, action => action.Action == TouchControlAction.SecondaryAction);

        Assert.True(router.Press(41, 0.48, 0.36, controlsEnabled: false));
        Assert.Equal((0.48, 0.36, false), sink.LastPointer);
        Assert.Contains((TouchControlAction.SecondaryAction, true), sink.Actions);
        Assert.True(router.Move(41, 0.62, 0.54));
        Assert.Equal((0.62, 0.54, false), sink.LastPointer);
        Assert.DoesNotContain((TouchControlAction.SecondaryAction, false), sink.Actions);

        Assert.True(router.Release(41));
        Assert.Contains((TouchControlAction.SecondaryAction, false), sink.Actions);
        Assert.True(router.Release(40));
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
    public async Task AndroidRuntimeArchiveInstallerRemovesLeftoversFromInterruptedInstalls()
    {
        var parent = Path.Combine(Path.GetTempPath(), "jay-runtime-archive-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(parent, "game-runtime");
        var staging = Path.Combine(parent, "game-runtime.install-x");
        var backup = Path.Combine(parent, "game-runtime.backup-y");
        var download = Path.Combine(parent, "game-runtime.download-z");
        var unrelated = Path.Combine(parent, "game-runtime-notes.txt");
        Directory.CreateDirectory(Path.Combine(staging, "lib"));
        Directory.CreateDirectory(backup);
        await File.WriteAllTextAsync(Path.Combine(staging, "lib", "partial"), "partial");
        await File.WriteAllTextAsync(Path.Combine(backup, "release"), "old");
        await File.WriteAllTextAsync(download, "partial archive");
        await File.WriteAllTextAsync(unrelated, "keep");
        try
        {
            var archive = Convert.FromBase64String(RuntimeXzFixture);
            await RuntimeArchiveInstaller.InstallAsync(
                target,
                RuntimeArchive(archive),
                (_, _) => Task.FromResult<Stream>(new MemoryStream(archive, writable: false)));

            Assert.True(Directory.Exists(target));
            Assert.False(Directory.Exists(staging));
            Assert.False(Directory.Exists(backup));
            Assert.False(File.Exists(download));
            Assert.True(File.Exists(unrelated));
            Assert.Equal([target], Directory.GetDirectories(parent));
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
    public async Task AndroidRuntimeArchiveInstallsIntoTheSameFolderRunOneAtATime()
    {
        var parent = Path.Combine(Path.GetTempPath(), "jay-runtime-archive-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(parent, "game-runtime");
        try
        {
            var archive = Convert.FromBase64String(RuntimeXzFixture);
            var descriptor = RuntimeArchive(archive);
            var slow = new GatedStream(archive, archive.Length / 2);
            var firstInstall = RuntimeArchiveInstaller.InstallAsync(target, descriptor, (_, _) => Task.FromResult<Stream>(slow));
            await slow.ReachedGate.WaitAsync(TimeSpan.FromSeconds(10));
            var download = Assert.Single(Directory.GetFiles(parent, "game-runtime.download-*"));
            var staging = Assert.Single(Directory.GetDirectories(parent, "game-runtime.install-*"));

            var secondOpened = false;
            var secondInstall = RuntimeArchiveInstaller.InstallAsync(target, descriptor, (_, _) =>
            {
                secondOpened = true;
                return Task.FromResult<Stream>(new MemoryStream(archive, writable: false));
            });
            await Task.Delay(200);
            Assert.False(secondOpened);
            Assert.True(File.Exists(download));
            Assert.True(Directory.Exists(staging));

            slow.Release();
            var manifests = await Task.WhenAll(firstInstall, secondInstall).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(secondOpened);
            Assert.True(await RuntimeBundleVerifier.VerifyAsync(target, manifests[1], "arm64-v8a"));
            Assert.Equal([target], Directory.GetDirectories(parent));
            Assert.Empty(Directory.GetFiles(parent));
        }
        finally
        {
            if (Directory.Exists(parent)) Directory.Delete(parent, true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AndroidRuntimeArchiveInstallerRestoresTheRuntimeOfAnInterruptedActivation(bool cancelled)
    {
        var parent = Path.Combine(Path.GetTempPath(), "jay-runtime-archive-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(parent, "game-runtime");
        try
        {
            var archive = Convert.FromBase64String(RuntimeXzFixture);
            var descriptor = RuntimeArchive(archive);
            var installed = await RuntimeArchiveInstaller.InstallAsync(
                target, descriptor, (_, _) => Task.FromResult<Stream>(new MemoryStream(archive, writable: false)));
            // The app was killed after the old runtime was moved to its backup and before the new one was moved in.
            Directory.Move(target, target + ".backup-old");
            Directory.CreateDirectory(target + ".install-new");
            await File.WriteAllTextAsync(Path.Combine(target + ".install-new", "partial"), "partial");

            using var cts = new CancellationTokenSource();
            Task<Stream> Open(RuntimeArchiveDescriptor _, CancellationToken token)
            {
                if (!cancelled) throw new HttpRequestException("offline");
                cts.Cancel();
                token.ThrowIfCancellationRequested();
                throw new InvalidOperationException("unreachable");
            }
            if (cancelled)
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RuntimeArchiveInstaller.InstallAsync(target, descriptor, Open, ct: cts.Token));
            else
                await Assert.ThrowsAsync<HttpRequestException>(() => RuntimeArchiveInstaller.InstallAsync(target, descriptor, Open));

            var restored = RuntimeBundleManifest.Read(Path.Combine(target, "runtime-manifest.json"));
            Assert.Equal(installed.Id, restored.Id);
            Assert.True(await RuntimeBundleVerifier.VerifyAsync(target, restored, "arm64-v8a"));
            Assert.Equal([target], Directory.GetDirectories(parent));
        }
        finally
        {
            if (Directory.Exists(parent)) Directory.Delete(parent, true);
        }
    }

    [Fact]
    public async Task MobileSetupKeepsThePageUsableAfterAFailedGameDownloadAndCanStartAgain()
    {
        const string files = "/data/user/0/example.app/files";
        var page = new RecordingSetupPage();
        var attempt = new MobileSetupAttempt(page.Page, [(files + "/game-runtime", "<runtime>"), (files, "<files>")]);

        var result = await attempt.RunAsync(MobileSetupStage.GameInstall, async (_, _) =>
        {
            await Task.Yield();
            throw new DownloadException(
                $"1 file failed: {files}/minecraft/assets/objects/ab/cd",
                [(new DownloadItem("https://example.invalid/cd", files + "/minecraft/assets/objects/ab/cd"),
                  new HttpRequestException("not found", null, HttpStatusCode.NotFound))]);
        });

        Assert.Equal(MobileSetupResult.Failed, result);
        Assert.False(attempt.IsRunning);
        Assert.Equal([true, false], page.Busy);
        Assert.Equal(1, page.PanelShown);
        Assert.Equal(0, page.CancelledCount);
        var shown = Assert.Single(page.Failures);
        Assert.Contains("<files>/minecraft/assets/objects/ab/cd", shown);
        Assert.DoesNotContain(files, shown);
        var line = Assert.Single(page.Logs);
        Assert.StartsWith("setup failed: stage=GameInstall error=DownloadException code=0x", line);
        Assert.Contains("<files>/minecraft/assets", line);
        Assert.DoesNotContain(files, line);
        Assert.DoesNotContain(" at ", line);

        // The launch step fails after the page was taken away: the panel comes back and the log names the stage.
        result = await attempt.RunAsync(MobileSetupStage.GameInstall, (current, _) =>
        {
            current.Stage = MobileSetupStage.GameLaunch;
            throw new FileNotFoundException("The Android GLFW bridge is missing.", files + "/game-runtime/native/libglfw.so");
        });
        Assert.Equal(MobileSetupResult.Failed, result);
        Assert.Equal(2, page.PanelShown);
        Assert.StartsWith("setup failed: stage=GameLaunch error=FileNotFoundException", page.Logs[^1]);
        Assert.DoesNotContain("/data/", page.Logs[^1]);

        var ran = false;
        result = await attempt.RunAsync(MobileSetupStage.GameInstall, (_, _) =>
        {
            ran = true;
            return Task.CompletedTask;
        });
        Assert.Equal(MobileSetupResult.Completed, result);
        Assert.True(ran);
        Assert.Equal([true, false, true, false, true, false], page.Busy);
        Assert.Equal(2, page.Logs.Count);
    }

    [Fact]
    public async Task MobileSetupCancelRestoresTheButtonsAndOnlyTheUsersCancelCountsAsCancel()
    {
        var page = new RecordingSetupPage();
        var attempt = new MobileSetupAttempt(page.Page, []);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = attempt.RunAsync(MobileSetupStage.RuntimeDownload, async (_, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
        });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(attempt.IsRunning);
        Assert.Equal(MobileSetupResult.AlreadyRunning, await attempt.RunAsync(MobileSetupStage.RuntimeDownload, (_, _) => Task.CompletedTask));

        attempt.Cancel();
        Assert.Equal(MobileSetupResult.Cancelled, await run.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.False(attempt.IsRunning);
        Assert.Equal([true, false], page.Busy);
        Assert.Equal(1, page.CancelledCount);
        Assert.Equal(1, page.PanelShown);
        Assert.Empty(page.Logs);
        Assert.Empty(page.Failures);

        // A timeout inside the work is a failure, not the user's cancel.
        var result = await attempt.RunAsync(MobileSetupStage.RuntimeDownload, (_, _) => throw new TaskCanceledException("timed out"));
        Assert.Equal(MobileSetupResult.Failed, result);
        Assert.Equal(1, page.CancelledCount);
        Assert.StartsWith("setup failed: stage=RuntimeDownload error=TaskCanceledException", Assert.Single(page.Logs));
        Assert.Equal([true, false, true, false], page.Busy);
    }

    [Fact]
    public async Task MobileSetupLogsTheFailingStageEvenBeforeThePostedProgressUpdateRuns()
    {
        var page = new RecordingSetupPage();
        var attempt = new MobileSetupAttempt(page.Page, []);
        // Stands in for Progress<T> on the UI thread: reports are queued and their callbacks have not run yet.
        var posted = new PostedProgress<RuntimeInstallProgress>();

        // Activation fails synchronously right after reporting it.
        var result = await attempt.RunAsync(MobileSetupStage.RuntimeDownload, (current, _) =>
        {
            current.TrackRuntimeStages(posted).Report(new(RuntimeInstallPhase.Activating, 1, 1));
            throw new IOException("The runtime folder is in use.");
        });
        Assert.Equal(MobileSetupResult.Failed, result);
        Assert.StartsWith("setup failed: stage=RuntimeActivate error=IOException", page.Logs[^1]);
        Assert.Equal([RuntimeInstallPhase.Activating], posted.Queued.Select(value => value.Phase));

        // The real installer: extraction throws on the first entry, before any per-entry progress.
        var parent = Path.Combine(Path.GetTempPath(), "amber-mobile-stage-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var archive = new MemoryStream();
            using (var gzip = new GZipStream(archive, CompressionLevel.SmallestSize, leaveOpen: true))
            using (var writer = new TarWriter(gzip, leaveOpen: false))
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "../escape")
                {
                    DataStream = new MemoryStream("x"u8.ToArray(), writable: false)
                });
            var bytes = archive.ToArray();
            posted.Queued.Clear();
            result = await attempt.RunAsync(MobileSetupStage.RuntimeDownload, (current, token) =>
                RuntimeArchiveInstaller.InstallAsync(
                    Path.Combine(parent, "game-runtime"),
                    RuntimeArchive(bytes),
                    (_, _) => Task.FromResult<Stream>(new MemoryStream(bytes, writable: false)),
                    current.TrackRuntimeStages(posted),
                    token));
            Assert.Equal(MobileSetupResult.Failed, result);
            Assert.StartsWith("setup failed: stage=RuntimeExtract error=InvalidDataException", page.Logs[^1]);
            Assert.Equal(RuntimeInstallPhase.Extracting, posted.Queued[^1].Phase);
            Assert.False(File.Exists(Path.Combine(parent, "escape")));
        }
        finally
        {
            if (Directory.Exists(parent)) Directory.Delete(parent, true);
        }

        // The next attempt starts again from its first stage, not from where the last one failed.
        result = await attempt.RunAsync(MobileSetupStage.RuntimeDownload, (_, _) => throw new HttpRequestException("offline"));
        Assert.Equal(MobileSetupResult.Failed, result);
        Assert.StartsWith("setup failed: stage=RuntimeDownload error=HttpRequestException", page.Logs[^1]);
        Assert.Equal(3, page.Logs.Count);
    }

    [Fact]
    public async Task MobileSetupPageCallbacksRunInOrderThroughFailureCancelAndRetry()
    {
        var page = new RecordingSetupPage();
        var attempt = new MobileSetupAttempt(page.Page, []);
        attempt.Cancel(); // nothing running: does nothing

        // Failure: busy, then the message, then the panel comes back, and only then are the buttons enabled.
        Assert.Equal(MobileSetupResult.Failed,
            await attempt.RunAsync(MobileSetupStage.GameInstall, (_, _) => throw new InvalidOperationException("broken")));
        Assert.Equal(["busy", "failed", "panel", "idle"], page.Events);

        // Cancel: the user's cancel shows the cancelled text and the panel, then enables the buttons.
        page.Events.Clear();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = attempt.RunAsync(MobileSetupStage.GameInstall, async (_, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
        });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(["busy"], page.Events);
        attempt.Cancel();
        Assert.Equal(MobileSetupResult.Cancelled, await run.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(["busy", "cancelled", "panel", "idle"], page.Events);

        // Retry after the cancel gets a fresh token and completes; a finished attempt does not touch the panel.
        page.Events.Clear();
        var tokenWasCancelled = true;
        Assert.Equal(MobileSetupResult.Completed, await attempt.RunAsync(MobileSetupStage.GameInstall, (_, token) =>
        {
            tokenWasCancelled = token.IsCancellationRequested;
            return Task.CompletedTask;
        }));
        Assert.False(tokenWasCancelled);
        Assert.Equal(["busy", "idle"], page.Events);
        attempt.Cancel(); // after completion: does nothing to the next run

        // The work took the panel away (the game is showing) and then failed: the panel is put back.
        page.Events.Clear();
        Assert.Equal(MobileSetupResult.Failed, await attempt.RunAsync(MobileSetupStage.GameInstall, async (current, _) =>
        {
            current.Stage = MobileSetupStage.GameLaunch;
            await Task.Yield();
            throw new InvalidOperationException("The game stopped.");
        }));
        Assert.Equal(["busy", "failed", "panel", "idle"], page.Events);
        Assert.StartsWith("setup failed: stage=GameLaunch", page.Logs[^1]);
        Assert.False(attempt.IsRunning);
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

    private sealed class RecordingSetupPage
    {
        public List<bool> Busy { get; } = [];
        public List<string> Failures { get; } = [];
        public List<string> Logs { get; } = [];
        public int PanelShown { get; private set; }
        public int CancelledCount { get; private set; }

        /// <summary>Every page call in order: busy, idle, panel, cancelled, failed.</summary>
        public List<string> Events { get; } = [];

        public MobileSetupPage Page => new(
            busy =>
            {
                Busy.Add(busy);
                Events.Add(busy ? "busy" : "idle");
            },
            () =>
            {
                PanelShown++;
                Events.Add("panel");
            },
            () =>
            {
                CancelledCount++;
                Events.Add("cancelled");
            },
            message =>
            {
                Failures.Add(message);
                Events.Add("failed");
            },
            Logs.Add);
    }

    /// <summary>Queues reports like a <see cref="Progress{T}"/> whose UI-thread callbacks have not run yet.</summary>
    private sealed class PostedProgress<T> : IProgress<T>
    {
        public List<T> Queued { get; } = [];
        public void Report(T value) => Queued.Add(value);
    }

    /// <summary>Hands out the first part of the data, then waits until <see cref="Release"/>.</summary>
    private sealed class GatedStream(byte[] data, int gateAt) : Stream
    {
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _position;

        public Task ReachedGate => _reached.Task;
        public void Release() => _release.TrySetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position >= gateAt)
            {
                _reached.TrySetResult();
                await _release.Task.WaitAsync(cancellationToken);
            }
            var limit = _position < gateAt ? gateAt : data.Length;
            var count = Math.Min(buffer.Length, limit - _position);
            data.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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
        public (double X, double Y) LastLook { get; private set; }
        public (double X, double Y, bool Click) LastPointer { get; private set; }
        public List<(double X, double Y, bool Click)> Pointers { get; } = [];

        public void SetAction(TouchControlAction action, bool pressed) => Actions.Add((action, pressed));
        public void SetMovement(double x, double y) => LastMovement = (x, y);
        public void AddLook(double x, double y) => LastLook = (x, y);
        public void SetPointer(double x, double y, bool click)
        {
            LastPointer = (x, y, click);
            Pointers.Add((x, y, click));
        }
    }

}
