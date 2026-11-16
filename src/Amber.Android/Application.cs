using Android.App;
using Android.Content;
using Android.Runtime;
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
        Amber.App.App.MobileLaunchRequested = (versionId, playerName, serverAddress) =>
        {
            var intent = new Intent(this, typeof(GameActivity));
            intent.AddFlags(ActivityFlags.NewTask);
            intent.PutExtra(GameActivity.VersionIdExtra, versionId);
            intent.PutExtra(GameActivity.PlayerNameExtra, playerName);
            if (!string.IsNullOrWhiteSpace(serverAddress))
                intent.PutExtra(GameActivity.ServerAddressExtra, serverAddress);
            StartActivity(intent);
            return Task.CompletedTask;
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
