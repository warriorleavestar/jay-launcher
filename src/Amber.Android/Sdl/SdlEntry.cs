using Android.App;
using Android.OS;
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
            var prepared = GameLaunchProvider.Read(activity);
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
        finally
        {
            // JaySdlActivity owns a HotSpot VM in the dedicated :sdlgame process. HotSpot cannot
            // create a second VM after main() returns, so retaining this process makes the next
            // launch fail with JNI_EEXIST. Let the Activity finish (and an error toast render)
            // before reclaiming only this isolated game process; the launcher stays alive.
            activity.RunOnUiThread(() =>
            {
                GameActivity.FinishGame(activity);
                var handler = new Handler(Looper.MainLooper!);
                handler.PostDelayed(() =>
                    global::Android.OS.Process.KillProcess(global::Android.OS.Process.MyPid()), 400);
            });
        }
    }
}
