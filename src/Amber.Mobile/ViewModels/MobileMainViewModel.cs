using System.Collections.ObjectModel;
using System.Reflection;
using System.Security.Cryptography;
using Amber.Core.Localization;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Gate;
using Amber.Core.Multiplayer.Transport;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.Mobile.ViewModels;

public sealed partial class MobileServerItem : ObservableObject
{
    private readonly MobileMainViewModel _owner;

    public MobileServerItem(JayServerList.Entry entry, MobileMainViewModel owner)
    {
        Entry = entry;
        _owner = owner;
        ConnectCommand = new AsyncRelayCommand(() => _owner.ConnectAsync(this));
        RemoveCommand = new AsyncRelayCommand(() => _owner.RemoveAsync(this));
    }

    public JayServerList.Entry Entry { get; }
    public string Title => Entry.DisplayName;
    public string Detail => L.F("multiplayer.jayserver.item_detail", Entry.Username, Entry.Host, Entry.Port);
    public string ConnectText => IsConnected ? L.T("mobile.server.connected") : L.T("mobile.server.connect");
    public string RemoveText => L.T("common.remove");
    public IAsyncRelayCommand ConnectCommand { get; }
    public IAsyncRelayCommand RemoveCommand { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectText))]
    public partial bool IsConnected { get; set; }
}

public sealed partial class MobileMainViewModel : ObservableObject, IAsyncDisposable
{
    private static readonly string AppVersion = typeof(MobileMainViewModel).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
    private readonly string _dataDirectory;
    private readonly JayServerList _serverList;
    private readonly MemberIdentity _identity;
    private readonly IMobilePlatformServices _platformServices;
    private TunnelClient? _tunnel;
    private MobileServerItem? _connected;

    public MobileMainViewModel(string? dataDirectory = null, IMobilePlatformServices? platformServices = null)
    {
        _platformServices = platformServices ?? NoMobilePlatformServices.Instance;
        _dataDirectory = dataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jay");
        Directory.CreateDirectory(_dataDirectory);
        _serverList = new JayServerList(_dataDirectory);
        _identity = MemberIdentity.LoadOrCreate(_dataDirectory);
        DeviceFingerprint = _identity.Fingerprint;
        if (_identity.Warning is { Length: > 0 } warning) Status = warning;
        ReloadServers();
    }

    public ObservableCollection<MobileServerItem> Servers { get; } = [];

    public string Title => "Jay";
    private static string PlatformName => OperatingSystem.IsIOS()
        ? L.T("mobile.platform.ios")
        : OperatingSystem.IsAndroid()
            ? L.T("mobile.platform.android")
            : L.T("mobile.platform.mobile");

    private static string PlatformString(string key) =>
        L.T(OperatingSystem.IsIOS() ? key + ".ios" : OperatingSystem.IsAndroid() ? key + ".android" : key + ".mobile");

    public string VersionText => L.F("mobile.version", PlatformName, AppVersion);
    public string ExperimentalText => L.T("mobile.experimental");
    public string ScopeTitle => L.T("mobile.scope.title");
    public string ScopeDescription => PlatformString("mobile.scope.description");
    public string DeviceTitle => L.T("mobile.device.title");
    public string DeviceHint => L.T("mobile.device.hint");
    public string EnrollTitle => L.T("mobile.enroll.title");
    public string EnrollHint => L.T("mobile.enroll.hint");
    public string EnrollButtonText => L.T("mobile.enroll.button");
    public string TunnelTitle => L.T("mobile.tunnel.title");
    public string TunnelHint => PlatformString("mobile.tunnel.hint");
    public string DisconnectButtonText => L.T("mobile.tunnel.disconnect");
    public bool ShowGameLauncher => _platformServices.CanOpenGameLauncher;
    public string GameLauncherTitle => L.T("mobile.game.title");
    public string GameLauncherHint => IsConnected
        ? L.F("mobile.game.hint.ready", _platformServices.GameLauncherName, LocalAddress)
        : L.F("mobile.game.hint.idle", _platformServices.GameLauncherName);
    public string OpenGameLauncherText => L.F("mobile.game.open", _platformServices.GameLauncherName);
    public string SavedServersTitle => L.T("mobile.servers.title");
    public string NoServersText => L.T("mobile.servers.empty");
    public bool HasNoServers => Servers.Count == 0;
    public bool HasStatus => !string.IsNullOrWhiteSpace(Status);

    [ObservableProperty] public partial string DeviceFingerprint { get; set; } = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InviteIsSealed))]
    public partial string InviteText { get; set; } = "";
    /// <summary>The passphrase of a sealed invite (a server with no domain).</summary>
    [ObservableProperty] public partial string InvitePassphrase { get; set; } = "";
    public bool InviteIsSealed => GateInvite.IsSealed(InviteText);
    public string PassphrasePlaceholder => L.T("multiplayer.jayserver.passphrase_placeholder");
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    public partial string Status { get; set; } = "";
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GameLauncherHint))]
    public partial bool IsConnected { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GameLauncherHint))]
    public partial string LocalAddress { get; set; } = "";

    [RelayCommand]
    private async Task EnrollAsync()
    {
        var text = InviteText;
        var passphrase = InvitePassphrase;
        IsBusy = true;
        // A sealed invite (a server with no domain) needs its passphrase; opening it runs Argon2id, off the UI thread.
        var (invite, error) = await Task.Run(() => (GateInvite.TryOpen(text, passphrase, out var e), e));
        IsBusy = false;
        if (invite is null)
        {
            Status = error ?? L.T("mobile.enroll.invalid");
            return;
        }
        if (invite.Locator is not null)
        {
            // Looking the address up means joining a public network, which the phone app doesn't do yet.
            Status = L.T("core.gate.record.mobile");
            return;
        }

        IsBusy = true;
        Status = L.F("multiplayer.jayserver.enrolling", invite.Host);
        try
        {
            var membership = await GateClient.EnrollAsync(invite, _identity);
            var entry = _serverList.Add(membership);
            InviteText = "";
            InvitePassphrase = "";
            ReloadServers();
            Status = L.F("multiplayer.jayserver.enrolled", entry.DisplayName, membership.Username);
        }
        catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or CryptographicException or GateRejectedException)
        {
            Status = L.F("multiplayer.jayserver.enroll_failed", e.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    internal async Task ConnectAsync(MobileServerItem item)
    {
        if (item.IsConnected) return;
        await DisconnectCoreAsync();
        try
        {
            var membership = _serverList.ToMembership(item.Entry);
            var entryId = item.Entry.Id;
            _tunnel = new TunnelClient(0, GateClient.Opener(membership, _identity,
                network => _serverList.UpdateNetwork(entryId, network)),
                line => Dispatcher.UIThread.Post(() => Status = line));
            _connected = item;
            item.IsConnected = true;
            IsConnected = true;
            LocalAddress = $"127.0.0.1:{_tunnel.Port}";
            _platformServices.SetGatewayActive(true);
            Status = L.F("mobile.tunnel.ready", item.Title, LocalAddress);
        }
        catch (Exception e) when (e is IOException or FormatException or CryptographicException or System.Net.Sockets.SocketException)
        {
            Status = L.F("multiplayer.jayserver.enroll_failed", e.Message);
        }
    }

    [RelayCommand]
    private async Task OpenGameLauncherAsync()
    {
        var address = IsConnected ? LocalAddress : null;
        try
        {
            var result = await _platformServices.OpenGameLauncherAsync(address);
            Status = result switch
            {
                MobileGameLauncherResult.Launched when address is not null =>
                    L.F("mobile.game.launched_ready", _platformServices.GameLauncherName, address),
                MobileGameLauncherResult.Launched =>
                    L.F("mobile.game.launched_idle", _platformServices.GameLauncherName),
                MobileGameLauncherResult.InstallPageOpened =>
                    L.F("mobile.game.install", _platformServices.GameLauncherName),
                _ => L.F("mobile.game.unavailable", _platformServices.GameLauncherName)
            };
        }
        catch (Exception e)
        {
            Status = L.F("mobile.game.failed", e.Message);
        }
    }

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        await DisconnectCoreAsync();
        Status = L.T("multiplayer.jayserver.disconnected");
    }

    internal async Task RemoveAsync(MobileServerItem item)
    {
        if (ReferenceEquals(item, _connected)) await DisconnectCoreAsync();
        _serverList.Remove(item.Entry.Id);
        Servers.Remove(item);
        OnPropertyChanged(nameof(HasNoServers));
    }

    private async Task DisconnectCoreAsync()
    {
        if (_tunnel is not null)
        {
            await _tunnel.DisposeAsync();
            _tunnel = null;
        }
        if (_connected is not null) _connected.IsConnected = false;
        _connected = null;
        IsConnected = false;
        LocalAddress = "";
        _platformServices.SetGatewayActive(false);
    }

    private void ReloadServers()
    {
        Servers.Clear();
        foreach (var entry in _serverList.Load()) Servers.Add(new MobileServerItem(entry, this));
        OnPropertyChanged(nameof(HasNoServers));
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectCoreAsync();
        _identity.Dispose();
    }
}
