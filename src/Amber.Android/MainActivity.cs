using Android.App;
using Android.Content.PM;
using Android.Views;
using Avalonia.Android;

namespace Amber.Android;

[Activity(
    Label = "Jay",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@drawable/jay",
    MainLauncher = true,
    WindowSoftInputMode = SoftInput.AdjustResize,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public sealed class MainActivity : AvaloniaMainActivity
{
    private static WeakReference<MainActivity>? s_resumed;

    /// <summary>The launcher screen while it is in the foreground, for system prompts such as permissions.</summary>
    internal static MainActivity? Resumed =>
        s_resumed is not null && s_resumed.TryGetTarget(out var activity) ? activity : null;

    protected override void OnResume()
    {
        base.OnResume();
        s_resumed = new WeakReference<MainActivity>(this);
    }

    protected override void OnPause()
    {
        if (Resumed == this) s_resumed = null;
        base.OnPause();
    }
}
