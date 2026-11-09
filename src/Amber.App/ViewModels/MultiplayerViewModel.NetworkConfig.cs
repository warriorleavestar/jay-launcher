using Amber.App.Services;
using Amber.Core.Localization;
using Amber.Core.Multiplayer.Public;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;

namespace Amber.App.ViewModels;

/// <summary>
/// 公共网络的服务器配置：连接串里写了配置文件地址和 SHA-256 的网络，连之前先下载、对哈希，再按里面的公告、链接和安全策略来。
/// 策略只会更严：限制房间方式、要求设备指纹、关掉或限制传文件、要求最低 Jay 版本。界面主题（ui）见 MultiplayerViewModel.NetworkTheme.cs。
/// </summary>
public sealed partial class MultiplayerViewModel
{
    private NetworkConfig? _networkConfig;

    public ObservableCollection<NetworkLink> NetworkLinks { get; } = [];
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasNetworkNotice))] public partial string NetworkNotice { get; set; } = "";
    public bool HasNetworkNotice => NetworkNotice.Length > 0;
    public bool HasNetworkLinks => NetworkLinks.Count > 0;

    /// <summary>现在连着的公共网络的策略（没有配置文件就是默认的）。</summary>
    internal NetworkPolicy NetworkPolicy => _networkConfig?.Policy ?? NetworkPolicy.Default;

    /// <summary>自检用：网络配置文件的缓存目录。</summary>
    internal string NetworkConfigDir { get; set; } = Path.Combine(AppSettings.DataDir, "network-configs");

    /// <summary>这个 Jay 的版本（和最低版本比）。</summary>
    internal static Version AppVersion => typeof(MultiplayerViewModel).Assembly.GetName().Version ?? new Version(0, 0);

    /// <summary>连网络之前取配置；取不到、对不上哈希、版本太低都不连（返回 false，状态栏说明原因）。</summary>
    private async Task<bool> LoadNetworkConfigAsync(PublicNetworkProfile profile, CancellationToken ct)
    {
        ApplyNetworkConfig(null);
        if (profile.Config is not { } reference) return true;
        NetworkConfig config;
        try { config = await NetworkConfig.GetAsync(_service.Http, reference, profile.Id, NetworkConfigDir, ct); }
        catch (Exception e) when (e is InvalidDataException or HttpRequestException or IOException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            SetStatus(StatusMessage.Error("multiplayer.netconfig.failed", profile.Name, e.Message));
            return false;
        }
        if (config.Policy.MinVersion is { } min && AppVersion < min)
        {
            SetStatus(StatusMessage.Error("multiplayer.netconfig.too_old", profile.Name, min.ToString()));
            return false;
        }
        ApplyNetworkConfig(config);
        return true;
    }

    private void ApplyNetworkConfig(NetworkConfig? config)
    {
        _networkConfig = config;
        NetworkNotice = config?.Notice ?? "";
        NetworkLinks.Clear();
        foreach (var link in config?.Links ?? []) NetworkLinks.Add(link);
        OnPropertyChanged(nameof(HasNetworkLinks));
        ResetAccount();
        ApplyNetworkTheme(config);
        UpdateHostPolicy();
    }

    /// <summary>打开网络配置里的链接（只有 https，用系统浏览器打开）。</summary>
    [RelayCommand]
    private void OpenNetworkLink(NetworkLink? link)
    {
        if (link is null || !Uri.TryCreate(link.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }
}
