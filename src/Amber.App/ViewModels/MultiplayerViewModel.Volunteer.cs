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
    /// <summary>Sign registrations with the bound Jay account (its own switch, off by default; see the privacy text).</summary>
    [ObservableProperty] private bool _volunteerSigned;
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
        VolunteerSigned = s.VolunteerSigned;
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
        // Signing takes effect at the next renewal; it needs no reconnect.
        s.VolunteerSigned = VolunteerSigned;
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

    [ObservableProperty] private bool _isCheckingVolunteer;

    /// <summary>Fills in this computer's public address as EasyTier's STUN check saw it (needs the public network).</summary>
    [RelayCommand]
    private async Task DetectVolunteerAddressAsync()
    {
        if (_public is not { } network)
        {
            SetStatus(StatusMessage.Warning("multiplayer.volunteer.connect_first"));
            return;
        }
        IsCheckingVolunteer = true;
        try
        {
            if (await network.PublicAddressAsync() is { } address)
            {
                VolunteerAddress = address;
                SetStatus(StatusMessage.Success("multiplayer.volunteer.detected", address));
            }
            else SetStatus(StatusMessage.Warning("multiplayer.volunteer.detect_failed"));
        }
        finally { IsCheckingVolunteer = false; }
    }

    /// <summary>
    /// Asks the directory to dial address:port. When this computer isn't already volunteering on that port, it listens
    /// there just for the test so the directory has something to reach.
    /// </summary>
    [RelayCommand]
    private async Task TestVolunteerPortAsync()
    {
        if (_public is not { } network)
        {
            SetStatus(StatusMessage.Warning("multiplayer.volunteer.connect_first"));
            return;
        }
        if (!int.TryParse(VolunteerPort, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1024 or > 65535)
        {
            SetStatus(StatusMessage.Error("multiplayer.volunteer.bad_port"));
            return;
        }
        string url;
        try { url = new VolunteerSettings(VolunteerAddress, port).Url; }
        catch (ArgumentException e)
        {
            SetStatus(new StatusMessage("common.raw", [e.Message], StatusKind.Error));
            return;
        }
        IsCheckingVolunteer = true;
        System.Net.Sockets.TcpListener? listener = null;
        try
        {
            if (network.Options.Volunteer?.Port != port)
            {
                listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Any, port);
                listener.Start();
                _ = AcceptAndDropAsync(listener);
            }
            var reachable = await DirectoryClient(network).ProbeAsync(url, default);
            VolunteerStatus = reachable ? L.F("multiplayer.volunteer.reachable", url) : L.F("multiplayer.volunteer.unreachable", port);
            SetStatus(reachable ? StatusMessage.Success("multiplayer.volunteer.reachable", url) : StatusMessage.Warning("multiplayer.volunteer.unreachable", port));
        }
        catch (System.Net.Sockets.SocketException)
        {
            SetStatus(StatusMessage.Error("multiplayer.volunteer.port_busy", port));
        }
        catch (DirectoryRejectedException e)
        {
            SetStatus(PortTestRejected(e));
        }
        catch (Exception e) when (e is IOException or InvalidDataException)
        {
            SetStatus(StatusMessage.Warning("multiplayer.volunteer.status_retry"));
        }
        finally
        {
            listener?.Stop();
            IsCheckingVolunteer = false;
        }
    }

    /// <summary>
    /// What "Test port" says when the directory refuses the test. Its generic "rate" text talks about publishing rooms,
    /// so a rate-limited port test gets its own line; other refusals keep the directory's text.
    /// </summary>
    internal static StatusMessage PortTestRejected(DirectoryRejectedException e) => e.Code == "rate"
        ? StatusMessage.Warning("multiplayer.volunteer.test_rate_limited")
        : new StatusMessage("common.raw", [e.Message], StatusKind.Warning);

    private static async Task AcceptAndDropAsync(System.Net.Sockets.TcpListener listener)
    {
        try
        {
            while (true)
            {
                using var client = await listener.AcceptTcpClientAsync();
            }
        }
        catch (Exception e) when (e is System.Net.Sockets.SocketException or ObjectDisposedException or InvalidOperationException) { }
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
                // Only with the person's say-so: signing tells the network's operator which Jay account runs this relay.
                var listed = await client.VolunteerAsync(url, ct, _service.Settings.VolunteerSigned ? JayCertificate : null);
                VolunteerStatus = listed ? L.F("multiplayer.volunteer.status_listed", url) : L.F("multiplayer.volunteer.status_checking", url);
            }
            catch (DirectoryRejectedException e)
            {
                // A network that only takes signed relays: say how, rather than repeat the directory's code.
                VolunteerStatus = e.Code == "operator" && !_service.Settings.VolunteerSigned ? L.T("multiplayer.volunteer.needs_signing") : e.Message;
                if (e.Code is "off" or "banned" or "bad") return;
            }
            catch (Exception e) when (e is IOException or InvalidDataException) { VolunteerStatus = L.T("multiplayer.volunteer.status_retry"); }
            catch (OperationCanceledException) { return; }
            // Shortly after the first registration the directory has probed us; renew then so we show up sooner.
            // A network that wants a signature waits for the person: ask again only at the usual renewal.
            var wait = VolunteerStatus.Length > 0 && !VolunteerStatus.Equals(L.F("multiplayer.volunteer.status_listed", url), StringComparison.Ordinal)
                && !VolunteerStatus.Equals(L.T("multiplayer.volunteer.needs_signing"), StringComparison.Ordinal)
                ? TimeSpan.FromSeconds(20) : VolunteerNodes.RenewEvery;
            try { await Task.Delay(wait, ct); }
            catch (OperationCanceledException) { return; }
        }
    }
}
