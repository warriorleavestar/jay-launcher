using System.Globalization;
using Amber.App.Services;
using Amber.Core.Localization;
using Amber.Core.Multiplayer.Public;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

/// <summary>
/// Volunteer nodes. Every member asks the directory for a few volunteers after connecting, adds them as extra peers and
/// remembers them for next time. A member who opts in also listens on a public port and registers itself, so other
/// players can get into the public network through this computer.
/// </summary>
public partial class MultiplayerViewModel
{
    private CancellationTokenSource? _volunteerCts;

    [ObservableProperty] private bool _volunteerEnabled;
    [ObservableProperty] private string _volunteerAddress = "";
    [ObservableProperty] private string _volunteerPort = VolunteerNodes.DefaultPort.ToString(CultureInfo.InvariantCulture);
    [ObservableProperty] private string _volunteerLimit = "1024";
    [ObservableProperty] private string _volunteerStatus = "";

    /// <summary>The current network takes volunteers (its config can turn them off).</summary>
    public bool VolunteersAllowed => NetworkPolicy.Volunteers;

    private VolunteerCache VolunteerCache => new(Path.Combine(AppSettings.DataDir, "volunteer-nodes.json"));

    private void InitVolunteer()
    {
        var s = _service.Settings;
        VolunteerEnabled = s.VolunteerNode;
        VolunteerAddress = s.VolunteerAddress ?? "";
        VolunteerPort = s.VolunteerPort.ToString(CultureInfo.InvariantCulture);
        VolunteerLimit = s.VolunteerLimitKBps.ToString(CultureInfo.InvariantCulture);
        VolunteerStatus = VolunteerEnabled ? L.T("multiplayer.volunteer.status_next_connect") : "";
    }

    /// <summary>The volunteer settings to start EasyTier with, or null when off / not allowed / not valid.</summary>
    private VolunteerSettings? VolunteerFor(PublicNetworkProfile profile)
    {
        var s = _service.Settings;
        if (!s.VolunteerNode || !VolunteersAllowed || string.IsNullOrWhiteSpace(s.VolunteerAddress)) return null;
        var settings = new VolunteerSettings(s.VolunteerAddress, s.VolunteerPort, s.VolunteerLimitKBps);
        try { _ = settings.Url; return settings; }
        catch (ArgumentException) { return null; }
    }

    /// <summary>Volunteers remembered for this network, tried as extra peers on connect.</summary>
    private IReadOnlyList<string> RememberedVolunteers(PublicNetworkProfile profile) =>
        VolunteersAllowed ? VolunteerCache.For(profile.Id) : [];

    [RelayCommand]
    private void SaveVolunteer()
    {
        if (!int.TryParse(VolunteerPort, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1024 or > 65535)
        {
            SetStatus(StatusMessage.Error("multiplayer.volunteer.bad_port"));
            return;
        }
        if (!int.TryParse(VolunteerLimit, NumberStyles.None, CultureInfo.InvariantCulture, out var limit) || limit is < 0 or > 1_000_000)
        {
            SetStatus(StatusMessage.Error("multiplayer.volunteer.bad_limit"));
            return;
        }
        if (VolunteerEnabled)
        {
            try { _ = new VolunteerSettings(VolunteerAddress, port, limit).Url; }
            catch (ArgumentException e)
            {
                SetStatus(new StatusMessage("common.raw", [e.Message], StatusKind.Error));
                return;
            }
        }
        var s = _service.Settings;
        var changed = s.VolunteerNode != VolunteerEnabled || s.VolunteerAddress != VolunteerAddress.Trim() || s.VolunteerPort != port || s.VolunteerLimitKBps != limit;
        s.VolunteerNode = VolunteerEnabled;
        s.VolunteerAddress = VolunteerAddress.Trim();
        s.VolunteerPort = port;
        s.VolunteerLimitKBps = limit;
        s.Save();
        if (!VolunteerEnabled) StopVolunteering();
        VolunteerStatus = VolunteerEnabled ? L.T("multiplayer.volunteer.status_next_connect") : "";
        // Listening needs a restart of the public-network EasyTier, which happens on the next connect.
        SetStatus(StatusMessage.Success(changed && IsPublicConnected ? "multiplayer.volunteer.saved_reconnect" : "multiplayer.volunteer.saved"));
    }

    /// <summary>After connecting: pick up volunteers from the directory and, when volunteering, start registering.</summary>
    private void StartVolunteerWork(PublicNetworkSession network, PublicNetworkProfile profile)
    {
        StopVolunteering();
        var cts = new CancellationTokenSource();
        _volunteerCts = cts;
        _ = UseVolunteersAsync(network, profile, cts.Token);
        if (network.Options.Volunteer is { } me) _ = VolunteerLoopAsync(network, me.Url, cts.Token);
    }

    private void StopVolunteering()
    {
        _volunteerCts?.Cancel();
        _volunteerCts?.Dispose();
        _volunteerCts = null;
    }

    private async Task UseVolunteersAsync(PublicNetworkSession network, PublicNetworkProfile profile, CancellationToken ct)
    {
        if (!VolunteersAllowed) return;
        try
        {
            var nodes = await DirectoryClient(network).NodesAsync(ct);
            if (nodes.Count == 0) return;
            VolunteerCache.Remember(profile.Id, nodes);
            var have = new HashSet<string>(profile.Nodes.Concat(network.Options.ExtraPeers ?? []), StringComparer.OrdinalIgnoreCase);
            if (network.Options.Volunteer is { } me) have.Add(me.Url);
            var added = 0;
            foreach (var node in nodes.Where(n => !have.Contains(n)))
            {
                if (added >= VolunteerNodes.ConnectTo) break;
                if (await network.AddPeerAsync(node, ct)) added++;
            }
            if (added > 0) _diag.Add(L.T("multiplayer.public.diagnostics_category"), L.F("multiplayer.volunteer.added_peers", added));
        }
        catch (Exception e) when (e is IOException or InvalidDataException or OperationCanceledException or DirectoryRejectedException) { }
    }

    private async Task VolunteerLoopAsync(PublicNetworkSession network, string url, CancellationToken ct)
    {
        var client = DirectoryClient(network);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var listed = await client.VolunteerAsync(url, ct);
                VolunteerStatus = listed ? L.F("multiplayer.volunteer.status_listed", url) : L.F("multiplayer.volunteer.status_checking", url);
            }
            catch (DirectoryRejectedException e)
            {
                VolunteerStatus = e.Message;
                if (e.Code is "off" or "banned" or "bad") return;
            }
            catch (Exception e) when (e is IOException or InvalidDataException) { VolunteerStatus = L.T("multiplayer.volunteer.status_retry"); }
            catch (OperationCanceledException) { return; }
            // Shortly after the first registration the directory has probed us; renew then so we show up sooner.
            var wait = VolunteerStatus.Length > 0 && !VolunteerStatus.Equals(L.F("multiplayer.volunteer.status_listed", url), StringComparison.Ordinal)
                ? TimeSpan.FromSeconds(20) : VolunteerNodes.RenewEvery;
            try { await Task.Delay(wait, ct); }
            catch (OperationCanceledException) { return; }
        }
    }
}
