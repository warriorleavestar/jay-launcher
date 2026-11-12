using Amber.Core.Activity;
using Amber.Core.Launch;
using Amber.Core.Localization;
using Avalonia.Threading;

namespace Amber.App.Services;

/// <summary>
/// The automatic update check (Settings › About › "Check for a new Jay once a day"). A minute after start and then once
/// an hour it looks whether a day has passed since the last check; if so it asks the update feed (the official releases
/// unless the user set another address). A newer version is put on the request bar only when 2 of the 3 Jay trustees
/// signed it; unsigned or failed checks stay silent (Settings › About › Check for updates says why). The notice only
/// opens the release page: Jay never downloads or installs an update by itself.
/// </summary>
internal sealed class UpdateNotifier
{
    internal const string RequestKind = "update";
    private const string DedupeKey = "jay-update";
    private static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Tick = TimeSpan.FromHours(1);

    private readonly LauncherService _service;
    private readonly Func<DateTimeOffset> _now;
    private readonly HttpClient _http;
    private readonly string _dataDir;
    private readonly TrustList? _trust;
    private bool _running;
    private string? _offeredUrl;
    private string? _offeredVersion;

    /// <param name="http">Tests pass a fake feed; Jay uses the service's API client.</param>
    /// <param name="trust">Tests pass their own trustees; Jay uses the built-in list.</param>
    internal UpdateNotifier(LauncherService service, Func<DateTimeOffset>? now = null, HttpClient? http = null, string? dataDir = null, TrustList? trust = null)
    {
        _service = service;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _http = http ?? service.Http;
        _dataDir = dataDir ?? AppSettings.DataDir;
        _trust = trust;
        service.Requests.Resolved += OnResolved;
    }

    /// <summary>Starts the timer. Not on phones and tablets (the stores and the preview channel update those).</summary>
    public static UpdateNotifier? Start(LauncherService service)
    {
        if (OperatingSystem.IsAndroid() || OperatingSystem.IsIOS()) return null;
        var notifier = new UpdateNotifier(service);
        DispatcherTimer.RunOnce(() =>
        {
            _ = notifier.CheckIfDueAsync();
            var timer = new DispatcherTimer { Interval = Tick };
            timer.Tick += (_, _) => _ = notifier.CheckIfDueAsync();
            timer.Start();
        }, FirstDelay);
        return notifier;
    }

    /// <summary>Runs the check when it is on and a day has passed. Returns whether a notice was posted.</summary>
    internal async Task<bool> CheckIfDueAsync(CancellationToken ct = default)
    {
        var settings = _service.Settings;
        if (_running || !settings.AutoUpdateCheck || !AutoUpdateCheck.IsDue(settings.LastUpdateCheck, _now())) return false;
        _running = true;
        try
        {
            VerifiedUpdate? update = null;
            try
            {
                update = await new UpdateCheck(_http, _dataDir, _trust)
                    .CheckAsync(AutoUpdateCheck.FeedFor(settings.UpdateFeedUrl), UpdateChecker.CurrentVersion, ct);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidDataException) { }
            // Count failed checks too, so an offline computer doesn't retry every hour.
            settings.LastUpdateCheck = _now();
            Save();
            if (update is not { IsVerified: true } || update.Info.Url is null) return false;
            if (!AutoUpdateCheck.ShouldNotify(update.Info.Version, settings.SkippedUpdateVersion)) return false;
            Offer(update.Info.Version, update.Info.Url);
            return true;
        }
        finally
        {
            _running = false;
        }
    }

    private void Offer(string version, string url)
    {
        _offeredVersion = version;
        _offeredUrl = url;
        _service.Requests.Post(new RequestSpec(RequestKind, L.F("update.notice.title", version),
            L.F("update.notice.body", UpdateChecker.CurrentVersion),
            [
                new("open", L.T("tools.update.open_page"), IsDefault: true),
                new("later", L.T("update.notice.later")),
                new("skip", L.T("update.notice.skip")),
            ], DedupeKey: DedupeKey));
    }

    private void OnResolved(RequestHandle request)
    {
        if (request.Spec.Kind != RequestKind || request.ResolvedBy == RequestSource.Program) return;
        switch (request.Choice)
        {
            case "open" when _offeredUrl is { } url && UpdateChecker.TryGetSafeUpdateUrl(url, out var uri):
                if (ShellOpen.TryOpen(uri!.AbsoluteUri, createFolder: false) is { } error)
                    Toasts.Show(L.F("update.notice.open_failed", error.Message), Avalonia.Controls.Notifications.NotificationType.Error);
                break;
            case "skip" when _offeredVersion is { } version:
                _service.Settings.SkippedUpdateVersion = version;
                Save();
                break;
        }
    }

    private void Save()
    {
        try { _service.Settings.Save(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
