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
        Assert.Contains(TouchControlLayout.Default, control => control.Action == TouchControlAction.Sneak);
        Assert.Contains(TouchControlLayout.Default, control => control.Action == TouchControlAction.Sprint);
        Assert.Contains(TouchControlLayout.Default, control => control.Action == TouchControlAction.PreviousSlot);
        Assert.Contains(TouchControlLayout.Default, control => control.Action == TouchControlAction.NextSlot);

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
        Assert.Equal((jump.CenterX, jump.CenterY, true), sink.LastPointer);
        Assert.True(router.Release(21));
        Assert.Equal((jump.CenterX, jump.CenterY, true), sink.LastPointer);

        var menu = TouchControlLayout.Default.Single(control => control.Action == TouchControlAction.Menu);
        Assert.True(router.Press(22, menu.CenterX, menu.CenterY, controlsEnabled: false));
        Assert.Contains((TouchControlAction.Menu, true), sink.Actions);
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
