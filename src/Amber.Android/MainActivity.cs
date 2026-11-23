using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Android.Views.InputMethods;
using Android.Window;
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
    private IOnBackInvokedCallback? _backInvokedCallback;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        RegisterBackInvokedCallback();
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

#pragma warning disable CS0672, CS0618
    public override void OnBackPressed() => HandleActivityBack();
#pragma warning restore CS0672, CS0618

    private void RegisterBackInvokedCallback()
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.Tiramisu) return;
#pragma warning disable CA1416
        _backInvokedCallback = new LauncherBackInvokedCallback(this);
        OnBackInvokedDispatcher.RegisterOnBackInvokedCallback(
            IOnBackInvokedDispatcher.PriorityDefault, _backInvokedCallback);
#pragma warning restore CA1416
    }

    private void HandleActivityBack()
    {
        if (Amber.App.App.MobileBackRequested?.Invoke() == true) return;
        MoveTaskToBack(true);
    }

    protected override void OnDestroy()
    {
        if (_backInvokedCallback is { } callback)
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
            {
#pragma warning disable CA1416
                OnBackInvokedDispatcher.UnregisterOnBackInvokedCallback(callback);
#pragma warning restore CA1416
            }
            callback.Dispose();
            _backInvokedCallback = null;
        }
        base.OnDestroy();
    }

    private sealed class LauncherBackInvokedCallback : Java.Lang.Object, IOnBackInvokedCallback
    {
        private readonly WeakReference<MainActivity> _activity;

        public LauncherBackInvokedCallback(MainActivity activity) =>
            _activity = new WeakReference<MainActivity>(activity);

        public void OnBackInvoked()
        {
            if (_activity.TryGetTarget(out var activity)) activity.HandleActivityBack();
        }
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
