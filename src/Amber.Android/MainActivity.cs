using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
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
    protected override void OnResume()
    {
        base.OnResume();
        AndroidGameRetry.RetryColdStartIfNeeded(this);
    }
}

/// <summary>
/// A cold Android emulator/device can terminate the isolated embedded-JVM process during its
/// first few seconds while native loaders are being warmed. The launcher remains healthy in its
/// own process, so retry that one narrowly identified early return once and never loop.
/// </summary>
internal static class AndroidGameRetry
{
    private const long MinimumFailureMs = 1_000;
    private const long MaximumFailureMs = 7_000;
    private static readonly object Gate = new();
    private static Intent? _launchIntent;
    private static long _launchedAt;
    private static bool _retryUsed;

    public static void Arm(Intent intent)
    {
        lock (Gate)
        {
            _launchIntent?.Dispose();
            _launchIntent = new Intent(intent);
            _launchedAt = SystemClock.ElapsedRealtime();
            _retryUsed = false;
        }
    }

    public static void RetryColdStartIfNeeded(Activity activity)
    {
        Intent? retry = null;
        lock (Gate)
        {
            if (_launchIntent is null || _retryUsed) return;
            var elapsed = SystemClock.ElapsedRealtime() - _launchedAt;
            if (elapsed < MinimumFailureMs) return;
            if (elapsed > MaximumFailureMs)
            {
                _launchIntent.Dispose();
                _launchIntent = null;
                return;
            }

            _retryUsed = true;
            retry = new Intent(_launchIntent);
            _launchIntent.Dispose();
            _launchIntent = null;
        }

        if (retry is null) return;
        var retryIntent = retry;
        activity.Window?.DecorView.PostDelayed(() =>
        {
            if (!activity.IsFinishing && !activity.IsDestroyed)
                activity.StartActivity(retryIntent);
            retryIntent.Dispose();
        }, 350);
    }
}
