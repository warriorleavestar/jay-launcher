using System.Collections.ObjectModel;
using Amber.App.Services;
using Amber.Core.Auth;
using Amber.Core.Localization;
using Amber.Core.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

/// <summary>One line of the network list (official design §8.8): what it is, when it happens and how it stands now.</summary>
public sealed record NetworkRow(NetworkCategory Category, string Title, string When, string Status, string? ChangeRoute, string? LastUsed)
{
    public string Number => ((int)Category).ToString(System.Globalization.CultureInfo.InvariantCulture);
    public bool CanChange => ChangeRoute is not null;
    public bool HasLastUsed => LastUsed is not null;
}

/// <summary>
/// Builds the five-category network list with the current status of each. "About and privacy" and Settings › Privacy
/// show the same list (§6.21, §8.8).
/// </summary>
public static class NetworkList
{
    public static IReadOnlyList<NetworkRow> Build(LauncherService service, MultiplayerViewModel? multiplayer)
    {
        var settings = service.Settings;
        var microsoft = service.Accounts.Store.Accounts.Count(a => a.Kind == AccountKind.Microsoft);
        var external = service.Accounts.Store.Accounts.Count(a => a.Kind == AccountKind.External);
        var login = microsoft == 0 && external == 0 ? L.T("about.net.login.none") : L.F("about.net.login.count", microsoft + external);
        var update = string.IsNullOrWhiteSpace(settings.UpdateFeedUrl) ? L.T("about.net.update.no_feed") : L.T("about.net.update.manual");
        var play = multiplayer is null || multiplayer.IsIdle ? L.T("about.net.play.idle")
            : multiplayer.IsHosting ? L.T("about.net.play.hosting") : L.T("about.net.play.joined");
        return
        [
            Row(NetworkCategory.Download, "about.net.download", "about.net.download.when", L.T("about.net.download.status"), "settings#Source"),
            Row(NetworkCategory.Login, "about.net.login", "about.net.login.when", login, "accounts"),
            Row(NetworkCategory.ContentApi, "about.net.content", "about.net.content.when", L.T("about.net.content.status"), "settings#CurseForgeKey"),
            Row(NetworkCategory.UpdateCheck, "about.net.update", "about.net.update.when", update, "settings#UpdateFeed"),
            Row(NetworkCategory.Multiplayer, "about.net.play", "about.net.play.when", play, "play"),
        ];
    }

    private static NetworkRow Row(NetworkCategory category, string titleKey, string whenKey, string status, string? route) =>
        new(category, L.T(titleKey), L.T(whenKey), status, route,
            NetworkPolicy.LastUsed(category) is { } t ? L.F("about.net.last_used", t.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture)) : null);

    /// <summary>How account sign-ins are stored on this computer.</summary>
    public static string CredentialMethod => OperatingSystem.IsWindows() ? L.T("about.stored.credentials.windows") : L.T("about.stored.credentials.other");
}

/// <summary>The network list with its status, shown by "About and privacy" and Settings › Privacy.</summary>
public sealed partial class NetworkListModel(LauncherService service) : ObservableObject
{
    public ObservableCollection<NetworkRow> Rows { get; } = [];

    /// <summary>Used for the multiplayer status line.</summary>
    internal MultiplayerViewModel? Multiplayer { get; set; }

    /// <summary>Opens a "Change ›" route.</summary>
    public Action<string>? Navigate { get; set; }

    /// <summary>Compact width: the status goes under each item instead of in its own column.</summary>
    [ObservableProperty] public partial bool IsCompact { get; set; }

    public void Refresh()
    {
        Rows.Clear();
        foreach (var row in NetworkList.Build(service, Multiplayer)) Rows.Add(row);
    }

    [RelayCommand]
    private void Change(NetworkRow? row)
    {
        if (row?.ChangeRoute is { } route) Navigate?.Invoke(route);
    }
}

/// <summary>About and privacy (official design §6.21): a locked page that says what Jay does and does not do.</summary>
public sealed partial class AboutViewModel : PageViewModel
{
    private readonly LauncherService _service;

    public AboutViewModel(LauncherService service)
    {
        _service = service;
        List = new NetworkListModel(service);
        Refresh();
    }

    public NetworkListModel List { get; }

    public override string Title => L.T("about.title");
    public override string Icon => "M12 2 4 5v6c0 5 3.4 9.7 8 11 4.6-1.3 8-6 8-11V5z";

    /// <summary>Opens a route (help).</summary>
    public Action<string>? Navigate { get; set; }

    public string StoredText => L.F("about.stored.body", NetworkList.CredentialMethod);
    public string VersionText => L.F("about.version", Amber.Core.Launch.UpdateChecker.CurrentVersion);
    public string DataFolder => Path.GetDirectoryName(AppSettings.FilePath) ?? "";

    public void Refresh()
    {
        List.Refresh();
        OnPropertyChanged(nameof(StoredText));
        OnPropertyChanged(nameof(ChildModeText));
    }

    /// <summary>"For a child: on · last changed …" so a parent can tell whether it was changed.</summary>
    public string ChildModeText => ChildModeStatus.Text(_service.Settings.ChildMode);

    protected override void OnLanguageChanged() => Refresh();

    protected override void OnBecameCurrent() => Refresh();

    [RelayCommand]
    private void OpenChildMode() => Navigate?.Invoke("settings#ChildMode");

    [RelayCommand]
    private void OpenDataFolder()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(DataFolder) { UseShellExecute = true }); }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    /// <summary>The licence note is shown in place (nothing is fetched).</summary>
    [ObservableProperty] public partial bool ShowLicenses { get; set; }

    [RelayCommand]
    private void ToggleLicenses() => ShowLicenses = !ShowLicenses;

    [RelayCommand]
    private void OpenHelp() => Navigate?.Invoke("help");

    /// <summary>The result of "Verify a Jay download"; null before the first check.</summary>
    [ObservableProperty] public partial string? VerifyText { get; set; }

    /// <summary>Whether the last checked file is a release signed by enough Jay trustees.</summary>
    [ObservableProperty] public partial bool VerifyPassed { get; set; }

    /// <summary>
    /// Verify a Jay download (privacy plan item 2): SHA256SUMS.txt, SHA256SUMS.txt.sigs and, when present,
    /// trust-chain.json must sit next to the file. Reads local files only; nothing is fetched.
    /// </summary>
    public async Task VerifyDownloadAsync(string path, Amber.Core.Launch.TrustList? trust = null)
    {
        VerifyPassed = false;
        VerifyText = L.T("about.verify.checking");
        var name = Path.GetFileName(path);
        try
        {
            var check = await Amber.Core.Launch.ReleaseSignature.VerifyPackageAsync(path, own: trust);
            VerifyPassed = check.IsValid;
            VerifyText = check.IsValid ? L.F("about.verify.ok", name, check.Tag ?? "", check.Seq)
                : L.F("about.verify.bad", name, Amber.Core.Launch.UpdateCheck.Explain(check.Verdict));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            VerifyText = L.F("about.verify.bad", name, e.Message);
        }
    }
}

/// <summary>The one-line child-mode status (§9.6).</summary>
public static class ChildModeStatus
{
    public static string Text(Amber.Core.Safety.ChildModeSettings? child)
    {
        var state = child?.Enabled == true ? L.T("settings.child.on") : L.T("settings.child.off");
        return child?.ChangedAt is { } t
            ? L.F("settings.child.status_changed", state, t.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture))
            : L.F("settings.child.status", state);
    }
}
