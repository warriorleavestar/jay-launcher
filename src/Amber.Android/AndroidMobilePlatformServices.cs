using Android.App;
using Android.Content;
using Android.Content.PM;
using Amber.Mobile;
using System.Runtime.Versioning;

namespace Amber.Android;

public sealed class AndroidMobilePlatformServices(Context context) : IMobilePlatformServices
{
    private readonly Context _context = context.ApplicationContext!;

    public bool CanOpenGameLauncher => true;
    public string GameLauncherName => "Jay";

    public Task<MobileGameLauncherResult> OpenGameLauncherAsync(string? serverAddress)
    {
        if (!string.IsNullOrWhiteSpace(serverAddress))
        {
            var clipboard = (ClipboardManager?)_context.GetSystemService(Context.ClipboardService);
            if (clipboard is not null)
                clipboard.PrimaryClip = ClipData.NewPlainText("Jay Server", serverAddress);
        }

        var gameIntent = new Intent(_context, typeof(GameActivity));
        gameIntent.AddFlags(ActivityFlags.NewTask);
        if (!string.IsNullOrWhiteSpace(serverAddress))
            gameIntent.PutExtra(GameActivity.ServerAddressExtra, serverAddress);
        _context.StartActivity(gameIntent);
        return Task.FromResult(AndroidRuntimeStore.IsInstalled(_context)
            ? MobileGameLauncherResult.Launched
            : MobileGameLauncherResult.RuntimeSetupOpened);
    }

    public void SetGatewayActive(bool active)
    {
        var intent = new Intent(_context, typeof(GatewayKeepAliveService));
        if (!active)
        {
            _context.StopService(intent);
            return;
        }

        if (OperatingSystem.IsAndroidVersionAtLeast(33)) RequestNotificationPermissionOnce();
        // The gateway runs whether or not the notification may be shown.
        if (OperatingSystem.IsAndroidVersionAtLeast(26))
            _context.StartForegroundService(intent);
        else
            _context.StartService(intent);
    }

    /// <summary>
    /// Android 13 and later hide the gateway's ongoing notification until the user allows
    /// notifications. Ask once, the first time the gateway starts, and never again after that.
    /// </summary>
    [SupportedOSPlatform("android33.0")]
    private void RequestNotificationPermissionOnce()
    {
        const string AskedKey = "notifications-asked";
        if (_context.CheckSelfPermission(global::Android.Manifest.Permission.PostNotifications) == Permission.Granted) return;
        var preferences = _context.GetSharedPreferences("jay-permissions", FileCreationMode.Private);
        if (preferences is null || preferences.GetBoolean(AskedKey, false)) return;
        if (MainActivity.Resumed is not { } activity) return;
        preferences.Edit()?.PutBoolean(AskedKey, true)?.Apply();
        activity.RequestPermissions([global::Android.Manifest.Permission.PostNotifications], NotificationPermissionRequest);
    }

    private const int NotificationPermissionRequest = 4108;
}
