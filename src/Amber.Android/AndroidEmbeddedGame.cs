using Android.Content;
using Amber.Core.Auth;
using Amber.Core.Game;
using Amber.Core.Install;
using Amber.Core.Launch;
using Amber.Core.Net;
using System.Runtime.InteropServices;

namespace Amber.Android;

internal readonly record struct AndroidGameInstallProgress(InstallStage Stage, int Completed, int Total, long Bytes);

internal sealed record AndroidPreparedGame(
    string RuntimeRoot,
    string WorkingDirectory,
    EmbeddedJavaInvocation Invocation);

/// <summary>Installs Mojang files and prepares the same launch command used by desktop Jay for an embedded JVM.</summary>
internal static class AndroidEmbeddedGame
{
    // Newest release that uses Java 21 and LWJGL 3.3.3/GLFW. The 26.x line uses Java 25 and SDL3.
    public const string FirstPlayableVersion = "1.21.11";

    public static async Task<AndroidPreparedGame> PrepareAsync(
        Context context,
        string playerName,
        string? serverAddress,
        IProgress<AndroidGameInstallProgress>? progress,
        CancellationToken ct)
    {
        playerName = playerName.Trim();
        if (playerName.Length is < 1 or > 16 || playerName.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '_')))
            throw new ArgumentException("Minecraft names must use 1-16 letters, numbers or underscores.");
        if (!AndroidRuntimeStore.IsInstalled(context)) throw new InvalidOperationException("The Android runtime is not installed.");

        var gameRoot = Path.Combine(context.FilesDir!.AbsolutePath, "minecraft");
        var gameDirectory = new GameDirectory(gameRoot);
        using var http = Downloader.CreateHttpClient();
        var downloader = new Downloader(http, SourceChain.Default()) { MaxConcurrency = 8, MaxChunksPerFile = 4 };
        var rules = AndroidRules();
        var installer = new VanillaInstaller(downloader, gameDirectory) { Rules = rules };
        var forwardedProgress = new Progress<InstallProgress>(value =>
            progress?.Report(new(value.Stage, value.Files.CompletedFiles, value.Files.TotalFiles, value.Files.BytesDownloaded)));
        await installer.InstallAsync(FirstPlayableVersion, forwardedProgress, ct);
        await CreateMobileOptionsIfMissingAsync(gameRoot, ct);

        var runtimeRoot = AndroidRuntimeStore.Root(context);
        var nativeRoot = Path.Combine(runtimeRoot, "native");
        RequireNativeFiles(nativeRoot, ["liblwjgl.so", "liblwjgl_opengl.so", "liblwjgl_stb.so", "libfreetype.so", "libopenal.so", "libmobileglues.so"]);
        var glfw = Path.Combine(nativeRoot, "libglfw.so");
        if (!File.Exists(glfw)) glfw = Path.Combine(context.ApplicationInfo!.NativeLibraryDir!, "libglfw.so");
        if (!File.Exists(glfw)) throw new FileNotFoundException("The Android GLFW bridge is missing.", glfw);

        var cache = Path.Combine(context.CacheDir!.AbsolutePath, "game");
        Directory.CreateDirectory(cache);
        var extraJvm = new List<string>
        {
            "-Djava.home=" + runtimeRoot,
            "-Djava.io.tmpdir=" + cache,
            "-Duser.home=" + context.FilesDir.AbsolutePath,
            "-Dorg.lwjgl.system.allocator=system",
            "-Dorg.lwjgl.librarypath=" + nativeRoot,
            "-Dorg.lwjgl.libname=" + Path.Combine(nativeRoot, "liblwjgl.so"),
            "-Dorg.lwjgl.glfw.libname=" + glfw,
            "-Dorg.lwjgl.opengl.explicitInit=true",
            "-Dorg.lwjgl.freetype.libname=" + Path.Combine(nativeRoot, "libfreetype.so"),
            "-Dorg.lwjgl.openal.libname=" + Path.Combine(nativeRoot, "libopenal.so"),
        };

        var launcher = new GameLauncher(gameDirectory) { BaseRules = rules };
        var invocation = launcher.BuildEmbeddedInvocation(new LaunchOptions
        {
            VersionId = FirstPlayableVersion,
            JavaPath = Path.Combine(runtimeRoot, "bin", "java"),
            Session = OfflineAccount.Create(playerName),
            WorkingDirectory = gameRoot,
            MaxMemoryMb = 1024,
            MinMemoryMb = 256,
            WindowWidth = 1280,
            WindowHeight = 720,
            Fullscreen = true,
            JoinServer = string.IsNullOrWhiteSpace(serverAddress) ? null : serverAddress,
            ExtraJvmArgs = extraJvm,
            LauncherName = "Jay Android",
        });
        var bootstrap = Path.Combine(cache, "jay-runtime-probe.jar");
        await CopyAssetAsync(context, "jay-runtime-probe.jar", bootstrap, ct);
        var vmOptions = invocation.VmOptions.Select(value =>
            value.StartsWith("-Djava.class.path=", StringComparison.Ordinal)
                ? value + Path.PathSeparator + bootstrap
                : value).ToArray();
        invocation = new EmbeddedJavaInvocation(
            vmOptions,
            "nz.work.minecraft.jay.AndroidGameBootstrap",
            [invocation.MainClass, .. invocation.Arguments],
            invocation.TransientDirectory);
        return new AndroidPreparedGame(runtimeRoot, gameRoot, invocation);
    }

    public static void Run(AndroidPreparedGame game)
    {
        var nativeRoot = Path.Combine(game.RuntimeRoot, "native");
        var mobileGluesRoot = Path.Combine(game.WorkingDirectory, "mobileglues");
        Directory.CreateDirectory(mobileGluesRoot);
        var mobileGlues = Path.Combine(nativeRoot, "libmobileglues.so");
        foreach (var (name, value) in new (string Name, string Value)[]
        {
            ("MG_DIR_PATH", mobileGluesRoot),
            ("MG_ANGLE_DIR", ""),
            ("JAY_EGL_LIBRARY", mobileGlues),
            ("JAY_GL_LIBRARY", mobileGlues),
        })
        {
            Environment.SetEnvironmentVariable(name, value);
            NativeJvmHost.SetEnvironmentVariable(name, value);
        }
        Directory.SetCurrentDirectory(game.WorkingDirectory);

        var error = NativeJvmHost.LoadRuntime(Path.Combine(game.RuntimeRoot, "lib", "server", "libjvm.so"));
        if (error is not null) throw new InvalidOperationException(error);
        foreach (var library in new[] { "libjava.so", "libnet.so", "libnio.so" })
        {
            var path = Path.Combine(game.RuntimeRoot, "lib", library);
            error = NativeJvmHost.Preload(path);
            if (error is not null) throw new InvalidOperationException($"Could not load {library}: {error}");
        }
        foreach (var library in new[] { "libmobileglues.so" })
        {
            var path = Path.Combine(nativeRoot, library);
            error = NativeJvmHost.PreloadLocal(path);
            if (error is not null) throw new InvalidOperationException($"Could not load {library}: {error}");
        }
        var (_, environment) = NativeJvmHost.Create(game.Invocation.VmOptions);
        NativeJvmHost.RunMain(environment, game.Invocation.MainClass, game.Invocation.Arguments);
    }

    private static RuleEvaluator AndroidRules() => new()
    {
        OsName = "linux",
        Arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x86_64",
    };

    private static void RequireNativeFiles(string root, IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            var path = Path.Combine(root, name);
            if (!File.Exists(path)) throw new FileNotFoundException("The Android game runtime is incomplete.", path);
        }
    }

    private static async Task CopyAssetAsync(Context context, string asset, string destination, CancellationToken ct)
    {
        await using var input = context.Assets!.Open(asset);
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 32 * 1024, true);
        await input.CopyToAsync(output, ct);
        await output.FlushAsync(ct);
    }

    private static async Task CreateMobileOptionsIfMissingAsync(string gameRoot, CancellationToken ct)
    {
        var path = Path.Combine(gameRoot, "options.txt");
        if (File.Exists(path)) return;

        const string defaults = """
            version:4671
            ao:true
            autoJump:true
            biomeBlendRadius:0
            enableVsync:true
            entityDistanceScaling:0.75
            entityShadows:false
            graphicsPreset:"fast"
            guiScale:3
            maxFps:60
            mipmapLevels:2
            onboardAccessibility:false
            particles:1
            pauseOnLostFocus:false
            rawMouseInput:false
            renderClouds:"false"
            renderDistance:8
            simulationDistance:6
            touchscreen:false
            """;
        await File.WriteAllTextAsync(path, defaults + Environment.NewLine, ct);
    }
}
