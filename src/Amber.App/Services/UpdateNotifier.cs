using Amber.Core.Activity;
using Amber.Core.Launch;
using Amber.Core.Localization;
using Avalonia.Threading;

namespace Amber.App.Services;

/// <summary>
/// The automatic update check (Settings › About › "Check for a new Jay once a day"). A minute after start and then once
/// an hour it looks whether a day has passed since the last check; if so it asks the update feed (the official releases
/// unless the user set another address). A newer version is put on the request bar only when enough Jay release keys
/// signed it; unsigned or failed checks stay silent (Settings › About › Check for updates says why). The notice only
/// opens the release page: Jay never downloads or installs an update by itself.
/// </summary>
internal sealed class UpdateNotifier : IDisposable
{
    internal const string RequestKind = "update";
    private static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Tick = TimeSpan.FromHours(1);

    private readonly LauncherService _service;
    private readonly Func<DateTimeOffset> _now;
    private readonly HttpClient _http;
    private readonly string _dataDir;
    private readonly TrustList? _trust;
    private readonly Func<string, Exception?> _open;
    private bool _running;
    /// <summary>The notices on the request bar, by dedupe key: their version and the page to open (null: none).</summary>
    private readonly Dictionary<string, (string Version, string? Page)> _offers = [];

    /// <param name="http">Tests pass a fake feed; Jay uses the service's API client.</param>
    /// <param name="trust">Tests pass their own trustees; Jay uses the built-in list.</param>
    /// <param name="open">Opens the release page and returns the error, or null; tests record it instead.</param>
    internal UpdateNotifier(LauncherService service, Func<DateTimeOffset>? now = null, HttpClient? http = null, string? dataDir = null, TrustList? trust = null,
        Func<string, Exception?>? open = null)
    {
        _open = open ?? (url => ShellOpen.TryOpen(url, createFolder: false));
        _service = service;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _http = http ?? service.Http;
        _dataDir = dataDir ?? AppSettings.DataDir;
        _trust = trust;
        service.Requests.Resolved += OnResolved;
        Switched += OnSwitched;
    }

    /// <summary>Stops listening to the request bar and the settings switch (Jay keeps its one notifier for the whole run).</summary>
    public void Dispose()
    {
        _service.Requests.Resolved -= OnResolved;
        Switched -= OnSwitched;
    }

    /// <summary>Settings › About's checkbox changed (raised by the settings page).</summary>
    internal static event Action<bool>? Switched;

    internal static void RaiseSwitched(bool on) => Switched?.Invoke(on);

    /// <summary>Turning the daily check off also takes down a notice that is still waiting.</summary>
    private void OnSwitched(bool on)
    {
        if (on) return;
        foreach (var key in _offers.Keys.ToList())
        {
            _offers.Remove(key);
            _service.Requests.Withdraw(key);
        }
    }

    /// <summary>Starts the timer. Not on phones and tablets (the stores and the preview channel update those).</summary>
    public static UpdateNotifier? Start(LauncherService service)
    {
        if (!AutoUpdateCheck.IsSupported) return null;
        // Self-checks and page snapshots must not reach the network or put a notice on the request bar.
        if (Environment.GetEnvironmentVariable("AMBER_SELFTEST") is { Length: > 0 } || Environment.GetEnvironmentVariable("AMBER_SNAPSHOT_DIR") is { Length: > 0 }) return null;
        var notifier = new UpdateNotifier(service);
        DispatcherTimer.RunOnce(() =>
        {
            _ = notifier.RunAsync();
            var timer = new DispatcherTimer { Interval = Tick };
            timer.Tick += (_, _) => _ = notifier.RunAsync();
            timer.Start();
        }, FirstDelay);
        return notifier;
    }

    /// <summary>The timer's entry: nothing a check throws may go unobserved or reach the UI thread.</summary>
    private async Task RunAsync()
    {
        try { await CheckIfDueAsync(); }
        catch (Exception e) { System.Diagnostics.Trace.WriteLine($"update check: {e.GetType().Name}: {e.Message}"); }
    }

    /// <summary>Runs the check when it is on and a day has passed. Returns whether a notice was posted.</summary>
    internal async Task<bool> CheckIfDueAsync(CancellationToken ct = default)
    {
        var settings = _service.Settings;
        if (_running || !settings.AutoUpdateCheck || !AutoUpdateCheck.IsDue(settings.LastUpdateCheck, _now())) return false;
        _running = true;
        var feed = AutoUpdateCheck.FeedFor(settings.UpdateFeedUrl);
        try
        {
            VerifiedUpdate? update = null;
            try
            {
                update = await new UpdateCheck(_http, _dataDir, _trust).CheckAsync(feed, UpdateChecker.CurrentVersion, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // Offline, a wrong address (abc, ftp://…), a broken feed: stay quiet. Settings › About › Check for updates says why.
                System.Diagnostics.Trace.WriteLine($"update check: {e.GetType().Name}: {e.Message}");
            }
            finally
            {
                // Count failed checks too, so an offline computer or a bad address isn't retried every hour.
                settings.LastUpdateCheck = _now();
                Save();
            }
            if (update is not { IsVerified: true }) return false;
            if (!AutoUpdateCheck.ShouldNotify(update.Info.Version, settings.SkippedUpdateVersion)) return false;
            // The switch may have been turned off while the feed was answering.
            if (!settings.AutoUpdateCheck) return false;
            Offer(update.Info.Version, AutoUpdateCheck.ReleasePage(feed, update));
            return true;
        }
        finally
        {
            _running = false;
        }
    }

    /// <param name="page">A page Jay can vouch for (<see cref="AutoUpdateCheck.ReleasePage"/>); without one there is no open button.</param>
    private void Offer(string version, string? page)
    {
        // A newer version replaces an older notice that is still waiting.
        foreach (var older in _offers.Keys.ToList())
        {
            _offers.Remove(older);
            _service.Requests.Withdraw(older);
        }
        var key = "jay-update-" + version;
        _offers[key] = (version, page);
        List<RequestChoice> choices = page is null ? [] : [new("open", L.T("tools.update.open_page"), IsDefault: true)];
        choices.Add(new("later", L.T("update.notice.later"), IsDefault: page is null));
        choices.Add(new("skip", L.T("update.notice.skip")));
        _service.Requests.Post(new RequestSpec(RequestKind, L.F("update.notice.title", version),
            L.F(page is null ? "update.notice.body_no_page" : "update.notice.body", UpdateChecker.CurrentVersion), choices, DedupeKey: key));
    }

    private void OnResolved(RequestHandle request)
    {
        if (request.Spec.Kind != RequestKind || request.Spec.DedupeKey is not { } key || !_offers.Remove(key, out var offer)) return;
        if (request.ResolvedBy == RequestSource.Program) return;
        switch (request.Choice)
        {
            case "open" when offer.Page is { } url && UpdateChecker.TryGetSafeUpdateUrl(url, out var uri):
                if (_open(uri!.AbsoluteUri) is { } error)
                    Toasts.Show(L.F("update.notice.open_failed", error.Message), Avalonia.Controls.Notifications.NotificationType.Error);
                break;
            case "skip":
                _service.Settings.SkippedUpdateVersion = offer.Version;
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
