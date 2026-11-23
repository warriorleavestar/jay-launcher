using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
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
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        if (Intent?.GetBooleanExtra(GameActivity.TouchSettingsSelfTestExtra, false) == true)
        {
            var selfTest = new Intent(this, typeof(GameActivity));
            selfTest.PutExtra(GameActivity.TouchSettingsSelfTestExtra, true);
            selfTest.PutExtra(GameActivity.PseudoLocalizationSelfTestExtra,
                Intent.GetBooleanExtra(GameActivity.PseudoLocalizationSelfTestExtra, false));
            StartActivity(selfTest);
            Finish();
            return;
        }
        // Avalonia coordinates predictive Back through AndroidX and exposes the result here.
        // Registering a second platform callback races that dispatcher and can background the
        // Activity before the launcher has a chance to close its current page.
        BackRequested += OnLauncherBackRequested;
    }

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

    private static void OnLauncherBackRequested(object? sender, AndroidBackRequestedEventArgs e)
    {
        if (Amber.App.App.MobileBackRequested?.Invoke() == true)
        {
            e.Handled = true;
            return;
        }

        // Avalonia's AndroidX fallback re-enters its own OnBackPressed override on API 33+ and
        // can leave the root page visible. At the launcher root, explicitly perform Android's
        // expected task-to-background action and consume this invocation once.
        if (sender is MainActivity activity)
        {
            activity.MoveTaskToBack(true);
            e.Handled = true;
        }
    }

    protected override void OnDestroy()
    {
        BackRequested -= OnLauncherBackRequested;
        base.OnDestroy();
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
