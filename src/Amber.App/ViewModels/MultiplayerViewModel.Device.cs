using Amber.App.Services;
using Amber.Core.Localization;
using Amber.Core.Multiplayer.Public;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

/// <summary>
/// 联机页的设备指纹：默认关着。打开后可以随时生成新的指纹码发给别人（每个码都不一样，都指向这台电脑），
/// 别人在公共网络里输入码就能找到你开的房间；房主也可以要求敲门的人打开设备指纹，或者只让名单里的电脑进。
/// </summary>
public sealed partial class MultiplayerViewModel
{
    private DeviceFingerprint? _device;
    private bool _deviceLoaded;
    private string _deviceDir = AppSettings.DataDir;
    /// <summary>房主发布时勾了“可以用指纹码找到这个房间”（发布时定下，之后改勾选要重新发布）。</summary>
    private bool _searchPublished;

    /// <summary>本机的设备指纹（没打开时为 null）。</summary>
    internal DeviceFingerprint? Device
    {
        get
        {
            if (!_deviceLoaded)
            {
                _deviceLoaded = true;
                _device = DeviceFingerprint.Load(_deviceDir);
            }
            return _device;
        }
    }

    /// <summary>测试用：设备指纹放在别的目录（自检里同一个进程开房主和加入方）。</summary>
    internal void UseTestDevice(string directory)
    {
        _deviceDir = directory;
        _deviceLoaded = false;
        _device = null;
        OnDeviceChanged();
    }

    public bool DeviceEnabled => Device is not null;
    public string DeviceIdText => Device is { } d ? L.F("multiplayer.device.id", d.DeviceId) : "";

    [ObservableProperty] public partial string MyFingerprintCode { get; set; } = "";
    [ObservableProperty] public partial string FindCodeInput { get; set; } = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasFoundRoom))] public partial PublicRoomItem? FoundRoom { get; set; }
    public bool HasFoundRoom => FoundRoom is not null;
    [ObservableProperty] public partial bool IsFindingRoom { get; set; }

    /// <summary>房主：发布时让别人能用我的指纹码找到这个房间。</summary>
    [ObservableProperty] public partial bool AllowFingerprintSearch { get; set; }
    /// <summary>房主：敲门的人必须打开设备指纹。</summary>
    [ObservableProperty] public partial bool RequireDevice { get; set; }
    /// <summary>房主：只让这些电脑进（对方发来的指纹码，一行一个）。空的时候不限。</summary>
    [ObservableProperty] public partial string AllowedDevicesText { get; set; } = "";

    private volatile DevicePolicy _hostPolicy = DevicePolicy.None;

    /// <summary>敲门服务每次敲门时取一次（在后台线程上）：房主改了勾选马上生效。</summary>
    internal DevicePolicy HostDevicePolicy => _hostPolicy;

    partial void OnRequireDeviceChanged(bool value) => UpdateHostPolicy();
    partial void OnAllowedDevicesTextChanged(string value) => UpdateHostPolicy();

    /// <summary>房主的设备策略：自己勾的“要求设备指纹”，或者公共网络的配置要求（只会更严）。</summary>
    private void UpdateHostPolicy() =>
        _hostPolicy = new DevicePolicy(RequireDevice || NetworkPolicy.RequireDevice, DevicePolicy.ParseCodes(AllowedDevicesText));

    private void OnDeviceChanged()
    {
        MyFingerprintCode = "";
        OnPropertyChanged(nameof(DeviceEnabled));
        OnPropertyChanged(nameof(DeviceIdText));
    }

    [RelayCommand]
    private void EnableDevice()
    {
        try
        {
            _device = DeviceFingerprint.Enable(_deviceDir);
            _deviceLoaded = true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            SetStatus(StatusMessage.Failure("multiplayer.device.failed", e, ErrorText.Of(e)));
            return;
        }
        OnDeviceChanged();
        NewFingerprintCode();
        SetStatus(StatusMessage.Success("multiplayer.device.enabled"));
    }

    /// <summary>关掉：删掉本机的指纹，给出去的码全部作废。</summary>
    [RelayCommand]
    private void DisableDevice()
    {
        try { DeviceFingerprint.Disable(_deviceDir); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            SetStatus(StatusMessage.Failure("multiplayer.device.failed", e, ErrorText.Of(e)));
            return;
        }
        _device = null;
        _deviceLoaded = true;
        _searchPublished = false;
        _announcer?.Refresh();
        OnDeviceChanged();
        SetStatus(StatusMessage.Info("multiplayer.device.disabled"));
    }

    /// <summary>换新：以前给出去的码全部作废，房主那边看到的也是一台新设备。</summary>
    [RelayCommand]
    private void RotateDevice()
    {
        try
        {
            _device = DeviceFingerprint.Rotate(_deviceDir);
            _deviceLoaded = true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            SetStatus(StatusMessage.Failure("multiplayer.device.failed", e, ErrorText.Of(e)));
            return;
        }
        _announcer?.Refresh();
        OnDeviceChanged();
        NewFingerprintCode();
        SetStatus(StatusMessage.Success("multiplayer.device.rotated"));
    }

    [RelayCommand]
    private void NewFingerprintCode()
    {
        if (Device is { } device) MyFingerprintCode = device.NewCode();
    }

    [RelayCommand]
    private async Task CopyFingerprintCodeAsync()
    {
        if (MyFingerprintCode.Length > 0 && await SecretClipboard.CopyAsync(MyFingerprintCode))
            SetStatus(StatusMessage.Success("multiplayer.clipboard.copied_autoclear"));
    }

    /// <summary>按指纹码在公共网络里找房间。</summary>
    [RelayCommand]
    private async Task FindByFingerprintAsync()
    {
        FoundRoom = null;
        if (_public is not { } network)
        {
            SetStatus(StatusMessage.Warning("multiplayer.public.connect_first"));
            return;
        }
        var code = FindCodeInput.Trim();
        if (!FingerprintCode.TryParse(code, out _))
        {
            SetStatus(StatusMessage.Error("multiplayer.device.bad_code"));
            return;
        }
        IsFindingRoom = true;
        try
        {
            var room = await DirectoryClient(network).FindAsync(code, default);
            if (room is null)
            {
                SetStatus(StatusMessage.Info("multiplayer.device.not_found"));
                return;
            }
            FoundRoom = new PublicRoomItem(room);
            SetStatus(StatusMessage.Success("multiplayer.device.found", FoundRoom.Title));
        }
        catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or InvalidDataException or OperationCanceledException)
        {
            SetStatus(StatusMessage.Failure("multiplayer.public.list_failed", e, ErrorText.Of(e)));
        }
        finally
        {
            IsFindingRoom = false;
        }
    }

    [RelayCommand]
    private Task JoinFoundRoomAsync() => FoundRoom is { } room ? JoinPublicRoomAsync(room) : Task.CompletedTask;

    /// <summary>交给目录的指纹密钥：房主发布时勾了指纹查找、本机也打开了指纹才给。</summary>
    private byte[]? SearchKeyForAnnounce() => _searchPublished && Device is { } device ? device.SearchKey : null;
}
