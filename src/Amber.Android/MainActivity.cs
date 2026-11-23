using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Views;
using Android.Views.InputMethods;
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
        Amber.App.App.MobileTextInputFocusChanged = SetLauncherKeyboardVisible;
    }

    protected override void OnPause()
    {
        Amber.App.App.MobileTextInputFocusChanged = null;
        base.OnPause();
    }

    private void SetLauncherKeyboardVisible(bool visible)
    {
        var target = CurrentFocus ?? Window?.DecorView;
        if (target is null) return;

        target.PostDelayed(() =>
        {
            if (IsFinishing || IsDestroyed) return;
            var input = (InputMethodManager?)GetSystemService(Context.InputMethodService);
            if (visible)
            {
                // Avalonia's text client already has the caret and composition state. Restarting
                // the native connection makes Android notice it even on tablets/emulators that
                // advertise a hardware keyboard, then Forced honours the explicit screen tap.
                target.RequestFocus();
                input?.RestartInput(target);
                input?.ShowSoftInput(target, ShowFlags.Forced);
            }
            else
            {
                input?.HideSoftInputFromWindow(target.WindowToken, HideSoftInputFlags.None);
            }
        }, 100);
    }
}
