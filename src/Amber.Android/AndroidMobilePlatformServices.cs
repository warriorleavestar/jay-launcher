using Android.App;
using Android.Content;
using Amber.Mobile;

namespace Amber.Android;

public sealed class AndroidMobilePlatformServices(Context context) : IMobilePlatformServices
{
    private const string FclPackage = "com.tungsten.fcl";
    private const string FclDebugPackage = "com.tungsten.fcl.debug";
    private const string FclReleases = "https://github.com/FCL-Team/FoldCraftLauncher/releases/latest";
    private readonly Context _context = context.ApplicationContext!;

    public bool CanOpenGameLauncher => true;
    public string GameLauncherName => "FCL";

    public Task<MobileGameLauncherResult> OpenGameLauncherAsync(string? serverAddress)
    {
        if (!string.IsNullOrWhiteSpace(serverAddress))
        {
            var clipboard = (ClipboardManager?)_context.GetSystemService(Context.ClipboardService);
            if (clipboard is not null)
                clipboard.PrimaryClip = ClipData.NewPlainText("Jay Server", serverAddress);
        }

        foreach (var packageName in new[] { FclPackage, FclDebugPackage })
        {
            var launchIntent = _context.PackageManager?.GetLaunchIntentForPackage(packageName);
            if (launchIntent is null) continue;
            launchIntent.AddFlags(ActivityFlags.NewTask);
            _context.StartActivity(launchIntent);
            return Task.FromResult(MobileGameLauncherResult.Launched);
        }

        var releaseIntent = new Intent(Intent.ActionView, global::Android.Net.Uri.Parse(FclReleases));
        releaseIntent.AddFlags(ActivityFlags.NewTask);
        _context.StartActivity(releaseIntent);
        return Task.FromResult(MobileGameLauncherResult.InstallPageOpened);
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
