using Android.App;
using Android.Content;
using Android.Runtime;
using Amber.App.Services;
using Amber.Core.Install;
using Amber.Core.Localization;
using Amber.Mobile;
using Avalonia;
using Avalonia.Android;
using Avalonia.Media;

namespace Amber.Android;

[Application]
public sealed class Application : AvaloniaAndroidApplication<Amber.App.App>
{
    public Application(nint javaReference, JniHandleOwnership transfer) : base(javaReference, transfer) { }

    public override void OnCreate()
    {
        AppContext.SetData(Amber.Core.Multiplayer.EasyTierInstaller.AndroidNativeDirectoryKey, ApplicationInfo!.NativeLibraryDir);
        var files = FilesDir!.AbsolutePath;
        Environment.SetEnvironmentVariable("AMBER_DATA_DIR", Path.Combine(files, "amber"));
        // Before anything reads the device identity or saved servers: seal their wrap keys in the Keystore.
        Amber.Core.Auth.LocalKeyProtection.Protector = new AndroidKeystoreKeyProtector();
        // The dedicated :game process does not create Avalonia's launcher view model, so it
        // never reaches App.OnFrameworkInitializationCompleted where the saved language is
        // normally applied. Load it at process startup as well, otherwise every in-game touch
        // setting remains in the fallback language after the player changes Jay's language.
        var settings = AppSettings.Load();
        L.SetLanguage(settings.Language);
        Amber.App.App.MobileGameDirectory = Path.Combine(files, "minecraft");
        Amber.App.App.MobileLaunchRequested = async (options, progress, ct) =>
        {
            if (MainActivity.FindGameTask(this) is not null)
                throw new InvalidOperationException(L.T("mobile.game.already_running"));
            var versionId = options.VersionId;
            var gameProgress = new Progress<AndroidGameInstallProgress>(value =>
            {
                var fraction = value.Total <= 0 ? null : (double?)value.Completed / value.Total;
                var detail = value.Stage switch
                {
                    InstallStage.Manifest or InstallStage.VersionJson => L.T("mobile.game.fetching_manifest"),
                    InstallStage.AssetIndex => L.T("mobile.game.fetching_assets"),
                    InstallStage.Files => L.F("mobile.game.downloading", value.Completed, value.Total),
                    _ => L.T("mobile.game.preparing")
                };
                progress?.Report(new Amber.App.MobileLaunchProgress(fraction, detail, value.Bytes, value.ActiveFiles));
            });

            // A fresh Android profile has no local version JSON. Fetch that small metadata first
            // so a Java 25 game never causes the legacy runtime to be installed by mistake.
            var requiredJava = await AndroidEmbeddedGame.PrepareVersionMetadataAsync(
                this, versionId, gameProgress, ct);
            if (!AndroidRuntimeChoice.IsSupported(requiredJava))
                throw new InvalidOperationException(L.T("mobile.runtime.unsupported"));
            var java25 = AndroidRuntimeChoice.UsesJava25(requiredJava);
            if (!AndroidRuntimeStore.IsInstalled(this, java25))
            {
                var descriptor = AndroidRuntimeStore.Descriptor(this, java25)
                    ?? throw new InvalidOperationException(L.T(
                        AndroidRuntimeStore.IsCurrentArchitectureSupported
                            ? "mobile.runtime.catalog_missing"
                            : "mobile.runtime.unsupported"));
                await AndroidRuntimeStore.InstallAsync(this, descriptor, java25, new Progress<RuntimeInstallProgress>(value =>
                {
                    var fraction = value.Total <= 0 ? null : (double?)value.Completed / value.Total;
                    var detail = value.Phase switch
                    {
                        RuntimeInstallPhase.Downloading => L.F("mobile.runtime.downloading", (int)((fraction ?? 0) * 100)),
                        RuntimeInstallPhase.Extracting => L.F("mobile.runtime.extracting", (int)((fraction ?? 0) * 100)),
                        RuntimeInstallPhase.Verifying => L.F("mobile.runtime.verifying", (int)((fraction ?? 0) * 100)),
                        _ => L.T("mobile.runtime.activating")
                    };
                    progress?.Report(new Amber.App.MobileLaunchProgress(fraction, detail, value.Completed,
                        value.Phase == RuntimeInstallPhase.Downloading ? [Path.GetFileName(new Uri(descriptor.DownloadUrl).AbsolutePath)] : []));
                }), ct);
            }

            var prepared = await AndroidEmbeddedGame.PrepareAsync(this, options, gameProgress, ct);
            ct.ThrowIfCancellationRequested();
            var intent = new Intent(this, typeof(GameActivity));
            if (prepared.Sdl)
                intent.SetClassName(this, "nz.work.minecraft.jay.JaySdlActivity");
            intent.AddFlags(ActivityFlags.NewTask);
            GameLaunchProvider.Stage(intent, prepared);
            StartActivity(intent);
        };
        base.OnCreate();
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder) =>
        base.CustomizeAppBuilder(builder).WithInterFont().With(new FontManagerOptions
        {
            FontFallbacks =
            [
                new FontFallback
                {
                    FontFamily = new FontFamily("avares://Jay/Assets/Fonts#Noto Sans SC")
                }
            ]
        });
}
