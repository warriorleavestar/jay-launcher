using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Android.Views.InputMethods;
using Avalonia.Android;
using Amber.App.Services;
using Amber.Core.Localization;

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
        LauncherUpdate.AndroidInstall = InstallUpdate;
    }

    protected override void OnPause()
    {
        LauncherUpdate.AndroidInstall = null;
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

    private void InstallUpdate(string path)
    {
        var manager = (ActivityManager?)GetSystemService(ActivityService);
        if (manager?.AppTasks?.Any(task =>
            task.TaskInfo?.TopActivity?.ClassName is { } name &&
            (name.EndsWith(".GameActivity", StringComparison.Ordinal) || name.EndsWith(".JaySdlActivity", StringComparison.Ordinal))) == true)
            throw new InvalidOperationException(L.T("tools.update.finish_game"));
        var pm = PackageManager!;
        // Check the installed identity before opening the installer. Android enforces the
        // signing relationship and downgrade policy again when the user confirms.
#pragma warning disable CS0618, CA1422
        var candidate = pm.GetPackageArchiveInfo(path, PackageInfoFlags.Signatures);
        var current = pm.GetPackageInfo(PackageName!, PackageInfoFlags.Signatures);
        var signers = current?.Signatures;
        if (candidate?.PackageName != PackageName || signers is not { Count: > 0 }
            || candidate?.Signatures is not { Count: > 0 } offered
            || !signers.Select(s => s.ToCharsString()).Order().SequenceEqual(offered.Select(s => s.ToCharsString()).Order())
            || (OperatingSystem.IsAndroidVersionAtLeast(28) ? candidate.LongVersionCode <= current!.LongVersionCode : candidate.VersionCode <= current!.VersionCode))
            throw new InvalidDataException(L.T("tools.update.identity"));
#pragma warning restore CS0618, CA1422
        if (!pm.CanRequestPackageInstalls())
        {
            StartActivity(new Intent(global::Android.Provider.Settings.ActionManageUnknownAppSources,
                global::Android.Net.Uri.Parse("package:" + PackageName)));
            throw new InvalidOperationException(L.T("tools.update.permission"));
        }
        var uri = AndroidX.Core.Content.FileProvider.GetUriForFile(this, PackageName + ".updates", new Java.IO.File(path));
        var intent = new Intent(Intent.ActionView);
        intent.SetDataAndType(uri, "application/vnd.android.package-archive");
        intent.AddFlags(ActivityFlags.GrantReadUriPermission);
        StartActivity(intent);
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
