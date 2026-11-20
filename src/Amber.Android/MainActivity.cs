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
public sealed class MainActivity : AvaloniaMainActivity;
