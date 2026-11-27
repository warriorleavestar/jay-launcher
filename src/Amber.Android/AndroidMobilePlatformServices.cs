using Android.App;
using Android.Content;
using Amber.Mobile;

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

        // The launcher owns runtime/game downloads now. Never open the removed full-screen setup Activity directly.
        var launcherIntent = new Intent(_context, typeof(MainActivity));
        launcherIntent.AddFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop);
        _context.StartActivity(launcherIntent);
        return Task.FromResult(MobileGameLauncherResult.Launched);
    }

    public void SetGatewayActive(bool active)
    {
        var intent = new Intent(_context, typeof(GatewayKeepAliveService));
        if (!active)
        {
            _context.StopService(intent);
            return;
        }

        if (OperatingSystem.IsAndroidVersionAtLeast(26))
            _context.StartForegroundService(intent);
        else
            _context.StartService(intent);
    }
}
