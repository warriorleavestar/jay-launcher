using Android.App;
using Android.Runtime;
using Android.Widget;
using Java.Interop;

namespace Amber.Android;

[Register("nz/work/minecraft/jay/SdlEntry")]
public sealed class SdlEntry : Java.Lang.Object
{
    // SDL invokes this on SDLThread once the Android surface has been created.
    [Export("startGame")]
    public static void StartGame(Activity activity)
    {
        try
        {
            var intent = activity.Intent ?? throw new InvalidOperationException("Missing game request.");
            var version = intent.GetStringExtra(GameActivity.VersionIdExtra) ?? throw new InvalidOperationException("Missing game version.");
            var name = intent.GetStringExtra(GameActivity.PlayerNameExtra) ?? throw new InvalidOperationException("Missing player.");
            var prepared = AndroidEmbeddedGame.PrepareAsync(activity, version, name,
                intent.GetStringExtra(GameActivity.ServerAddressExtra), null, CancellationToken.None).GetAwaiter().GetResult();
            activity.RunOnUiThread(() =>
            {
                var overlay = new TouchControlOverlay(activity, new SdlTouchInput(activity));
                activity.AddContentView(overlay, new global::Android.Views.ViewGroup.LayoutParams(-1, -1));
            });
            AndroidEmbeddedGame.Run(prepared);
        }
        catch (Exception error)
        {
            global::Android.Util.Log.Error("JayGame", error.ToString());
            activity.RunOnUiThread(() => Toast.MakeText(activity, error.Message, ToastLength.Long)?.Show());
        }
    }
}
