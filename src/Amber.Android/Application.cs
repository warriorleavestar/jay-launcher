using Android.App;
using Android.Content;
using Android.Runtime;
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
        var files = FilesDir!.AbsolutePath;
        Environment.SetEnvironmentVariable("AMBER_DATA_DIR", Path.Combine(files, "amber"));
        Amber.App.App.MobileGameDirectory = Path.Combine(files, "minecraft");
        Amber.App.App.MobileLaunchRequested = async (versionId, playerName, serverAddress, progress, ct) =>
        {
            if (!AndroidRuntimeStore.IsInstalled(this))
            {
                var descriptor = AndroidRuntimeCatalog.ForCurrentProcess()
                    ?? throw new PlatformNotSupportedException(L.T("mobile.runtime.unsupported"));
                await AndroidRuntimeStore.InstallAsync(this, descriptor, new Progress<RuntimeInstallProgress>(value =>
                {
                    var fraction = value.Total <= 0 ? null : (double?)value.Completed / value.Total;
                    var detail = value.Phase switch
                    {
                        RuntimeInstallPhase.Downloading => L.F("mobile.runtime.downloading", (int)((fraction ?? 0) * 100)),
                        RuntimeInstallPhase.Extracting => L.F("mobile.runtime.extracting", (int)((fraction ?? 0) * 100)),
                        RuntimeInstallPhase.Verifying => L.F("mobile.runtime.verifying", (int)((fraction ?? 0) * 100)),
                        _ => L.T("mobile.runtime.activating")
                    };
                    progress?.Report(new Amber.App.MobileLaunchProgress(fraction, detail, value.Completed));
                }), ct);
            }

            // Complete the selected instance before changing Activity. GameActivity repeats only the cheap invocation
            // construction in its isolated process; all network work stays visible in the launcher's task line.
            await AndroidEmbeddedGame.PrepareAsync(this, versionId, playerName, serverAddress,
                new Progress<AndroidGameInstallProgress>(value =>
                {
                    var fraction = value.Total <= 0 ? null : (double?)value.Completed / value.Total;
                    var detail = value.Stage switch
                    {
                        InstallStage.Manifest or InstallStage.VersionJson => L.T("mobile.game.fetching_manifest"),
                        InstallStage.AssetIndex => L.T("mobile.game.fetching_assets"),
                        InstallStage.Files => L.F("mobile.game.downloading", value.Completed, value.Total),
                        _ => L.T("mobile.game.preparing")
                    };
                    progress?.Report(new Amber.App.MobileLaunchProgress(fraction, detail, value.Bytes));
                }), ct);

            ct.ThrowIfCancellationRequested();
            var intent = new Intent(this, typeof(GameActivity));
            intent.AddFlags(ActivityFlags.NewTask);
            intent.PutExtra(GameActivity.VersionIdExtra, versionId);
            intent.PutExtra(GameActivity.PlayerNameExtra, playerName);
            if (!string.IsNullOrWhiteSpace(serverAddress))
                intent.PutExtra(GameActivity.ServerAddressExtra, serverAddress);
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
