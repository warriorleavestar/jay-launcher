using Android.App;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Java.Lang.Reflect;
using System.Runtime.InteropServices;

namespace Amber.Android;

/// <summary>Connects Jay's SurfaceView to the ART side of the bundled SDL3 Android driver.</summary>
internal static class AndroidSdlBridge
{
    private const string ClassName = "org.libsdl.app.SDLActivity";

    public static void Load()
    {
        Java.Lang.JavaSystem.LoadLibrary("SDL3");
        // Minecraft 26.3 initializes SDL directly from its Java render thread instead of
        // entering through SDL_main. SDL deliberately rejects SDL_Init until an embedding
        // host makes this promise; without it startup ends with "Application didn't
        // initialize properly" before a window can be created.
        SDL_SetMainReady();
    }

    [DllImport("SDL3", EntryPoint = "SDL_SetMainReady", ExactSpelling = true)]
    private static extern void SDL_SetMainReady();

    public static void Initialize(Activity activity, Surface surface, int width, int height) =>
        Invoke("initialize", [
            Java.Lang.Class.FromType(typeof(Activity)), Java.Lang.Class.FromType(typeof(Surface)),
            Java.Lang.Integer.Type!, Java.Lang.Integer.Type!, Java.Lang.Float.Type!, Java.Lang.Float.Type!
        ], [activity, surface, new Java.Lang.Integer(width), new Java.Lang.Integer(height),
            new Java.Lang.Float(activity.Resources?.DisplayMetrics?.Density ?? 1),
            new Java.Lang.Float(RefreshRate(activity))]);

    public static void Update(Activity activity, Surface surface, int width, int height) =>
        Invoke("updateSurface", [
            Java.Lang.Class.FromType(typeof(Surface)), Java.Lang.Integer.Type!, Java.Lang.Integer.Type!,
            Java.Lang.Float.Type!, Java.Lang.Float.Type!
        ], [surface, new Java.Lang.Integer(width), new Java.Lang.Integer(height),
            new Java.Lang.Float(activity.Resources?.DisplayMetrics?.Density ?? 1),
            new Java.Lang.Float(RefreshRate(activity))]);

    public static void Clear() => Invoke("clearSurface", [], []);

    private static float RefreshRate(Activity activity)
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
            return activity.Display?.RefreshRate ?? 60;
#pragma warning disable CA1422 // Required fallback for Android 8-10, before Context.Display existed.
        return activity.WindowManager?.DefaultDisplay?.RefreshRate ?? 60;
#pragma warning restore CA1422
    }

    private static void Invoke(string name, Java.Lang.Class[] parameters, Java.Lang.Object[] arguments)
    {
        using var type = Java.Lang.Class.ForName(ClassName);
        using Method method = type!.GetDeclaredMethod(name, parameters)!;
        method.Invoke(null, arguments);
    }
}
