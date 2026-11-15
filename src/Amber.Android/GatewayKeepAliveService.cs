using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.App;
using Amber.Core.Localization;
using System.Runtime.Versioning;

namespace Amber.Android;

// specialUse: the service keeps a loopback gateway open for a game running in the foreground. The
// dataSync type would be stopped by Android 15 after six hours a day, which would end the gateway
// in the middle of a long session.
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeSpecialUse)]
public sealed class GatewayKeepAliveService : Service
{
    private const string ChannelId = "jay_gateway";
    private const int NotificationId = 4107;

    public override IBinder? OnBind(Intent? intent) => null;

    public override void OnCreate()
    {
        base.OnCreate();
        if (OperatingSystem.IsAndroidVersionAtLeast(26)) CreateChannel();
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var openJay = new Intent(this, typeof(MainActivity));
        var pendingIntent = PendingIntent.GetActivity(
            this,
            0,
            openJay,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        var builder = new NotificationCompat.Builder(this, ChannelId);
        builder.SetSmallIcon(Resource.Drawable.jay);
        builder.SetContentTitle(L.T("mobile.gateway.notification.title"));
        builder.SetContentText(L.T("mobile.gateway.notification.body"));
        builder.SetContentIntent(pendingIntent!);
        builder.SetOngoing(true);
        builder.SetSilent(true);
        var notification = builder.Build();

        StartForeground(NotificationId, notification!);
        return StartCommandResult.NotSticky;
    }

    public override void OnDestroy()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(24))
            StopForeground(StopForegroundFlags.Remove);
        else
            StopForeground(true);
        base.OnDestroy();
    }

    [SupportedOSPlatform("android26.0")]
    private void CreateChannel()
    {
        var manager = (NotificationManager?)GetSystemService(NotificationService);
        manager?.CreateNotificationChannel(new NotificationChannel(
            ChannelId,
            L.T("mobile.gateway.notification.channel"),
            NotificationImportance.Low)
        {
            Description = L.T("mobile.gateway.notification.body")
        });
    }
}
