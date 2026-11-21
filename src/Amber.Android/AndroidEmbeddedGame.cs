using Android.Content;
using Amber.Core.Auth;
using Amber.Core.Game;
using Amber.Core.Install;
using Amber.Core.Launch;
using Amber.Core.Localization;
using Amber.Core.Net;
using Amber.Mobile;
using System.Runtime.InteropServices;

namespace Amber.Android;

internal readonly record struct AndroidGameInstallProgress(InstallStage Stage, int Completed, int Total, long Bytes, IReadOnlyList<string>? ActiveFiles);

internal sealed record AndroidPreparedGame(
    string RuntimeRoot,
    string WorkingDirectory,
    string NativeRoot,
    bool Sdl,
    EmbeddedJavaInvocation Invocation);

/// <summary>Installs Mojang files and prepares the same launch command used by desktop Jay for an embedded JVM.</summary>
internal static class AndroidEmbeddedGame
{
    /// <summary>
    /// Fetches the small Mojang version metadata before Android chooses which embedded Java
    /// runtime to install. A fresh profile has no local version JSON yet, so treating a missing
    /// file as Java 21 would install the wrong runtime for Java 25 clients.
    /// </summary>
    public static async Task<int> PrepareVersionMetadataAsync(
        Context context,
        string versionId,
        IProgress<AndroidGameInstallProgress>? progress,
        CancellationToken ct)
    {
        versionId = GameDirectory.CheckId(versionId);
        var gameDirectory = new GameDirectory(Path.Combine(context.FilesDir!.AbsolutePath, "minecraft"));
        using var http = Downloader.CreateHttpClient();
        var downloader = new Downloader(http, SourceChain.Default()) { MaxConcurrency = 8, MaxChunksPerFile = 4 };
        var installer = new VanillaInstaller(downloader, gameDirectory) { Rules = AndroidRules() };
        var forwardedProgress = new Progress<InstallProgress>(value =>
            progress?.Report(new(value.Stage, value.Files.CompletedFiles, value.Files.TotalFiles,
                value.Files.BytesDownloaded, value.Files.ActiveFiles)));
        await installer.PrepareVersionAsync(versionId, forwardedProgress, ct);
        return gameDirectory.ResolveVersion(versionId).JavaVersion?.MajorVersion ?? 8;
    }

    public static bool UsesSdl(Context context, string versionId)
    {
        var directory = new GameDirectory(Path.Combine(context.FilesDir!.AbsolutePath, "minecraft"));
        return directory.ResolveVersion(versionId).Libraries.Any(library =>
            library.Name.StartsWith("org.lwjgl:lwjgl-sdl:", StringComparison.Ordinal));
    }

    public static async Task<AndroidPreparedGame> PrepareAsync(
        Context context,
        string versionId,
        string playerName,
        string? serverAddress,
        IProgress<AndroidGameInstallProgress>? progress,
        CancellationToken ct)
    {
        versionId = GameDirectory.CheckId(versionId);
        playerName = playerName.Trim();
        if (playerName.Length is < 1 or > 16 || playerName.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '_')))
            throw new ArgumentException("Minecraft names must use 1-16 letters, numbers or underscores.");
        var java25 = AndroidRuntimeStore.RequiresJava25(context, versionId);
        if (!AndroidRuntimeStore.IsInstalled(context, java25)) throw new InvalidOperationException("The Android runtime is not installed.");

        var gameRoot = Path.Combine(context.FilesDir!.AbsolutePath, "minecraft");
        var gameDirectory = new GameDirectory(gameRoot);
        using var http = Downloader.CreateHttpClient();
        var downloader = new Downloader(http, SourceChain.Default()) { MaxConcurrency = 8, MaxChunksPerFile = 4 };
        var rules = AndroidRules();
        var installer = new VanillaInstaller(downloader, gameDirectory) { Rules = rules };
        var forwardedProgress = new Progress<InstallProgress>(value =>
            progress?.Report(new(value.Stage, value.Files.CompletedFiles, value.Files.TotalFiles, value.Files.BytesDownloaded, value.Files.ActiveFiles)));
        var runtimeRoot = AndroidRuntimeStore.Root(context, java25);
        await installer.PrepareVersionAsync(versionId, forwardedProgress, ct);
        var requiredJava = gameDirectory.ResolveVersion(versionId).JavaVersion?.MajorVersion ?? 8;
        var installedJava = JavaRuntimeRelease.ReadMajor(runtimeRoot)
            ?? throw new InvalidOperationException(L.T("mobile.runtime.invalid_release"));
        if (installedJava < requiredJava)
            throw new InvalidOperationException(L.F("mobile.runtime.too_old",
                versionId, requiredJava, installedJava));

        await installer.CompleteAsync(versionId, forwardedProgress, ct);
        await CreateMobileOptionsIfMissingAsync(gameRoot, ct);

        var sdl = UsesSdl(context, versionId);
        var packagedNativeRoot = context.ApplicationInfo!.NativeLibraryDir!;
        var nativeRoot = AndroidNativeLibraries.ResolveRoot(runtimeRoot, packagedNativeRoot,
            requiredJava, allowPackagedFallback: sdl);
        RequireNativeFiles(nativeRoot, AndroidNativeLibraries.RequiredForJava(requiredJava));
        var sdlLibrary = sdl
            ? AndroidNativeLibraries.ResolveLibrary("libSDL3.so", nativeRoot, packagedNativeRoot)
            : null;
        if (sdlLibrary is not null && !File.Exists(sdlLibrary))
            throw new FileNotFoundException("The Android SDL bridge is missing.", sdlLibrary);
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
            "-Dorg.lwjgl.freetype.libname=" + Path.Combine(nativeRoot, "libfreetype.so"),
            "-Dorg.lwjgl.openal.libname=" + Path.Combine(nativeRoot, "libopenal.so"),
            "-Djay.gl.library=" + Path.Combine(nativeRoot, "libmobileglues.so"),
            "-Djay.minecraft.version=" + versionId,
        };

        if (sdl)
        {
            extraJvm.Add("-Dorg.lwjgl.sdl.libname=" + sdlLibrary);
            extraJvm.Add("-Dorg.lwjgl.vulkan.libname=/system/lib64/libvulkan.so");
            extraJvm.Add("-Djna.boot.library.path=" + nativeRoot);
            // Mojang's desktop JNA jar contains a glibc-linked Linux native. Android must
            // use the matching libjnidispatch.so staged in our verified runtime instead.
            extraJvm.Add("-Djna.nounpack=true");
            extraJvm.Add("-Dorg.lwjgl.spvc.libname=" + Path.Combine(nativeRoot, AndroidNativeLibraries.SpirvCross));
            extraJvm.Add("-Dorg.lwjgl.shaderc.libname=" + Path.Combine(nativeRoot, AndroidNativeLibraries.Shaderc));
            extraJvm.Add("-Dorg.lwjgl.opengl.libname=" + Path.Combine(nativeRoot, "libmobileglues.so"));
        }
        else extraJvm.Add("-Dorg.lwjgl.opengl.explicitInit=true");
        var launcher = new GameLauncher(gameDirectory) { BaseRules = rules };
        var invocation = launcher.BuildEmbeddedInvocation(new LaunchOptions
        {
            VersionId = versionId,
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
        bool namedSdlInput = sdl && invocation.MainClass == "net.minecraft.client.main.Main" && versionId == "26.3";
        if (sdl && !namedSdlInput) return new AndroidPreparedGame(runtimeRoot, gameRoot, nativeRoot, true, invocation);
        var bootstrap = Path.Combine(cache, "jay-runtime-probe.jar");
        await CopyAssetAsync(context, "jay-runtime-probe.jar", bootstrap, ct);
        var vmOptions = invocation.VmOptions.Select(value =>
            value.StartsWith("-Djava.class.path=", StringComparison.Ordinal)
                ? value + Path.PathSeparator + bootstrap
                : value).ToArray();
        invocation = new EmbeddedJavaInvocation(
            vmOptions,
            namedSdlInput ? "nz.work.minecraft.jay.SdlMenuBootstrap" : "nz.work.minecraft.jay.AndroidGameBootstrap",
            [invocation.MainClass, .. invocation.Arguments],
            invocation.TransientDirectory);
        return new AndroidPreparedGame(runtimeRoot, gameRoot, nativeRoot, sdl, invocation);
    }

    public static void Run(AndroidPreparedGame game)
    {
        var nativeRoot = game.NativeRoot;
        var mobileGluesRoot = Path.Combine(game.WorkingDirectory, "mobileglues");
        Directory.CreateDirectory(mobileGluesRoot);
        var mobileGlues = Path.Combine(nativeRoot, "libmobileglues.so");
        bool emulatorAngle = game.Sdl && global::Android.OS.Build.Hardware is ("ranchu" or "goldfish") &&
            File.Exists(Path.Combine(nativeRoot, "libEGL_angle.so"));
        foreach (var (name, value) in new (string Name, string Value)[]
        {
            ("MG_DIR_PATH", mobileGluesRoot),
            ("MG_ANGLE_DIR", emulatorAngle ? nativeRoot : ""),
            ("JAY_EMULATOR_ANGLE", emulatorAngle ? "1" : "0"),
            ("JAY_EGL_LIBRARY", mobileGlues),
            ("JAY_GL_LIBRARY", mobileGlues),
            ("SDL_OPENGL_LIBRARY", mobileGlues),
        })
        {
            Environment.SetEnvironmentVariable(name, value);
            NativeJvmHost.SetEnvironmentVariable(name, value);
        }
        if (game.Sdl)
        {
            // SDL and LWJGL must resolve GL entry points through the very same provider.
            SdlTouchInput.SetHint("SDL_OPENGL_LIBRARY", mobileGlues);
            SdlTouchInput.SetHint("SDL_EGL_LIBRARY", mobileGlues);
        }
        Directory.SetCurrentDirectory(game.WorkingDirectory);

        var cpp = Path.Combine(game.RuntimeRoot, "lib", "libc++_shared.so");
        if (File.Exists(cpp) && NativeJvmHost.Preload(cpp) is { } dependencyError)
            throw new InvalidOperationException($"Could not load libc++_shared.so: {dependencyError}");
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
        if (File.Exists(path))
        {
            var existing = await File.ReadAllTextAsync(path, ct);
            if (existing.Split('\n').Any(line =>
                    line.Trim().StartsWith("lang:", StringComparison.Ordinal) &&
                    line.Trim().Length > "lang:".Length)) return;
            await File.AppendAllTextAsync(path,
                (existing.Length > 0 && !existing.EndsWith('\n') ? Environment.NewLine : "") +
                "lang:zh_cn" + Environment.NewLine, ct);
            return;
        }

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
            lang:zh_cn
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
