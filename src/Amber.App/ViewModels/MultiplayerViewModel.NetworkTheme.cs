using Amber.App.Services;
using Amber.Core.Localization;
using Amber.Core.Multiplayer.Public;
using Amber.Core.Themes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Text.Json;

namespace Amber.App.ViewModels;

/// <summary>
/// 网络配置里的界面主题（"ui" 字段，格式见 <see cref="NetworkTheme"/> 和 docs/themes.md）：连上网络后取主题，
/// 只在联机页用；scope 是 global 的要用户点头才换掉整个 Jay。断开、换网络就换回自己的主题。
/// 主题取不到或格式不对不影响联机，只在诊断里记一笔。
/// </summary>
public sealed partial class MultiplayerViewModel
{
    private int _networkThemeVersion;

    /// <summary>网络想把整个 Jay 换成它的主题，等用户决定。</summary>
    [ObservableProperty] public partial bool NetworkThemeAskGlobal { get; set; }

    /// <summary>自检用：网络主题的缓存目录。</summary>
    internal string NetworkThemeDir { get; set; } = Path.Combine(AppSettings.DataDir, "network-themes");

    /// <summary>现在用着的网络主题（测试和自检看）。</summary>
    internal ThemePack? NetworkThemePack { get; private set; }

    /// <summary>按网络配置换主题（没有配置或没有 ui 就换回自己的）。异步取，换网络后旧的结果丢掉。</summary>
    private void ApplyNetworkTheme(NetworkConfig? config)
    {
        var version = ++_networkThemeVersion;
        NetworkThemeAskGlobal = false;
        if (config?.Ui is not { } ui)
        {
            SetTheme(null, "network");
            return;
        }
        NetworkThemeSpec spec;
        try
        {
            using var doc = JsonDocument.Parse(ui.ToJsonString());
            spec = NetworkTheme.Parse(doc.RootElement);
        }
        catch (ThemePackException e)
        {
            SetTheme(null, "network");
            _diag.Add(L.T("multiplayer.public.diagnostics_category"), L.F("multiplayer.netconfig.theme_failed", e.Message));
            return;
        }
        // Network themes turned off, or started with --default-theme: nothing is downloaded, and the cache is cleared.
        // Turning the setting back on runs this again for the current network.
        if (!ThemeService.NetworkThemesWanted)
        {
            SetTheme(null, "network");
            var dir = NetworkThemeDir;
            if (ThemeService.SafeModeReason != "requested") _ = Task.Run(() => NetworkTheme.Prune(dir, null));
            return;
        }
        _ = LoadAsync();

        async Task LoadAsync()
        {
            ThemePack? pack = null;
            try
            {
                pack = await NetworkTheme.GetAsync(_service.Http, spec, NetworkThemeDir);
                // Keep only the theme of the network in use (none, when network themes were turned off during the download).
                NetworkTheme.Prune(NetworkThemeDir, ThemeService.NetworkThemesWanted ? spec.Sha256 : null);
            }
            catch (Exception e) when (e is ThemePackException or HttpRequestException or IOException or InvalidDataException or TaskCanceledException)
            {
                _diag.Add(L.T("multiplayer.public.diagnostics_category"), L.F("multiplayer.netconfig.theme_failed", e.Message));
            }
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (version != _networkThemeVersion) return;
                SetTheme(pack, spec.Scope);
                NetworkThemeAskGlobal = pack is not null && spec.Scope == "global" && ThemeService.AllowNetworkThemes;
            });
        }

        void SetTheme(ThemePack? pack, string scope)
        {
            NetworkThemePack = pack;
            ThemeService.SetNetworkTheme(pack, scope);
        }
    }

    /// <summary>The "use themes from multiplayer networks" setting changed: fetch or drop the current network's theme.</summary>
    internal void NetworkThemesAllowedChanged()
    {
        if (_networkConfig is not null) ApplyNetworkTheme(_networkConfig);
        // Turned off: clear the cache even when no network (or one without a theme) is in use right now.
        if (!ThemeService.NetworkThemesWanted && ThemeService.SafeModeReason != "requested")
        {
            var dir = NetworkThemeDir;
            _ = Task.Run(() => NetworkTheme.Prune(dir, null));
        }
    }

    [RelayCommand]
    private void AcceptNetworkThemeGlobal()
    {
        ThemeService.AcceptNetworkThemeGlobally();
        NetworkThemeAskGlobal = false;
    }

    /// <summary>不铺满：网络主题还是只在联机页用。</summary>
    [RelayCommand]
    private void DeclineNetworkThemeGlobal() => NetworkThemeAskGlobal = false;
}
