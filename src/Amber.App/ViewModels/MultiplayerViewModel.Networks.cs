using Amber.App.Localization;
using Amber.App.Services;
using Amber.Core.Localization;
using Amber.Core.Multiplayer.Public;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;

namespace Amber.App.ViewModels;

/// <summary>公共网络下拉框的一项：内置的 Jay 公共网络，或者自己加的连接串网络。</summary>
public sealed record NetworkChoice(string Id, string Name, bool Reviewed, int NodeCount, string? Signer) : LocalizedRecord
{
    public bool IsBuiltIn => Id == "jay";
    public string Label => Reviewed ? Name : Name + " ⚠";
    public string Detail => IsBuiltIn ? L.T("multiplayer.network.builtin")
        : (Reviewed ? L.T("multiplayer.network.reviewed") : L.F("multiplayer.network.unreviewed", Signer ?? ""))
          + " · " + L.P("multiplayer.network.nodes", NodeCount, NodeCount);
    public override string ToString() => Label;
}

/// <summary>
/// 换公共网络：粘贴别人给的连接串（JAYNET1-...）加进来，在下拉框里切换。
/// Jay 注册表签的（GitHub 审核过）直接加；没审核的先提醒，确认后才加。
/// </summary>
public sealed partial class MultiplayerViewModel
{
    private bool _loadingNetworks;
    private string? _pendingNetworkText;

    public ObservableCollection<NetworkChoice> NetworkChoices { get; } = [];

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanRemoveNetwork))] public partial NetworkChoice? SelectedNetworkChoice { get; set; }
    [ObservableProperty] public partial string NetworkInput { get; set; } = "";
    [ObservableProperty] public partial bool NetworkConfirmOpen { get; set; }
    [ObservableProperty] public partial string NetworkConfirmText { get; set; } = "";
    public bool CanRemoveNetwork => SelectedNetworkChoice is { IsBuiltIn: false };

    internal void LoadNetworks()
    {
        _loadingNetworks = true;
        try
        {
            NetworkChoices.Clear();
            var jay = PublicNetworkProfile.Jay(_service.Settings.PublicNetworkNodes);
            NetworkChoices.Add(new NetworkChoice("jay", jay.Name, true, _service.PublicNetwork.IsBuiltIn ? _service.PublicNetwork.Nodes.Count : jay.Nodes.Count, null));
            foreach (var n in _service.Networks.Networks.Where(n => n.Info.Id != "jay"))
                NetworkChoices.Add(new NetworkChoice(n.Info.Id, n.Info.Name, n.Reviewed, n.Info.Nodes.Count, n.SignerFingerprint));
            var current = _service.PublicNetwork.Id;
            SelectedNetworkChoice = NetworkChoices.FirstOrDefault(c => c.Id == current) ?? NetworkChoices[0];
        }
        finally
        {
            _loadingNetworks = false;
        }
    }

    partial void OnSelectedNetworkChoiceChanged(NetworkChoice? value)
    {
        if (_loadingNetworks || value is null) return;
        var id = value.IsBuiltIn ? null : value.Id;
        if (_service.Settings.PublicNetworkId == id) return;
        // 换网络：先断开旧的（房主公开的房间也会撤下），再存设置；要用时重新点“连接”。
        if (IsPublicConnected || IsPublicConnecting) DisconnectPublic();
        _service.Settings.PublicNetworkId = id;
        _service.Settings.Save();
        NetworkChanged();
        SetStatus(StatusMessage.Success("multiplayer.network.switched", value.Name));
    }

    private void NetworkChanged()
    {
        OnPropertyChanged(nameof(HasPublicNodes));
        OnPropertyChanged(nameof(PublicStateText));
    }

    /// <summary>加连接串。</summary>
    [RelayCommand]
    private void AddNetwork() => AddNetworkText(NetworkInput, false);

    [RelayCommand]
    private void ConfirmAddNetwork()
    {
        NetworkConfirmOpen = false;
        if (_pendingNetworkText is { } text) AddNetworkText(text, true);
        _pendingNetworkText = null;
    }

    [RelayCommand]
    private void CancelAddNetwork()
    {
        NetworkConfirmOpen = false;
        _pendingNetworkText = null;
    }

    internal NetworkAddResult? AddNetworkText(string text, bool confirmed)
    {
        NetworkAddResult result;
        SignedNetwork network;
        try { result = _service.Networks.Add(text, confirmed, out network); }
        catch (InvalidDataException e)
        {
            SetStatus(new StatusMessage("common.raw", [e.Message], StatusKind.Error));
            return null;
        }
        switch (result)
        {
            case NetworkAddResult.NeedsConfirm:
                _pendingNetworkText = text;
                NetworkConfirmText = L.F("multiplayer.network.confirm", network.Info.Name, network.SignerFingerprint);
                NetworkConfirmOpen = true;
                return result;
            case NetworkAddResult.Conflict:
                SetStatus(StatusMessage.Warning("multiplayer.network.conflict", network.Info.Id));
                return result;
            case NetworkAddResult.Reserved:
                SetStatus(StatusMessage.Error("multiplayer.network.reserved"));
                return result;
            case NetworkAddResult.AlreadyHave:
                SetStatus(StatusMessage.Info("multiplayer.network.already", network.Info.Name));
                return result;
        }
        _service.Settings.PublicNetworks = _service.Networks.ToSaved();
        _service.Settings.Save();
        NetworkInput = "";
        var wasCurrent = _service.PublicNetwork.Id == network.Info.Id;
        LoadNetworks();
        // 新加的网络直接选上；更新的是正在用的网络就重新连（节点可能换了）。
        if (result == NetworkAddResult.Added) SelectedNetworkChoice = NetworkChoices.FirstOrDefault(c => c.Id == network.Info.Id) ?? SelectedNetworkChoice;
        else if (wasCurrent && IsPublicConnected) DisconnectPublic();
        NetworkChanged();
        SetStatus(StatusMessage.Success(result == NetworkAddResult.Added ? "multiplayer.network.added" : "multiplayer.network.updated", network.Info.Name));
        return result;
    }

    [RelayCommand]
    private void RemoveNetwork()
    {
        if (SelectedNetworkChoice is not { IsBuiltIn: false } choice) return;
        if (_service.PublicNetwork.Id == choice.Id && (IsPublicConnected || IsPublicConnecting)) DisconnectPublic();
        _service.Networks.Remove(choice.Id);
        _service.Settings.PublicNetworks = _service.Networks.ToSaved();
        if (_service.Settings.PublicNetworkId == choice.Id) _service.Settings.PublicNetworkId = null;
        _service.Settings.Save();
        LoadNetworks();
        NetworkChanged();
        SetStatus(StatusMessage.Success("multiplayer.network.removed", choice.Name));
    }

    /// <summary>复制正在用的网络的连接串（分享给朋友）。内置的 Jay 网络不用分享。</summary>
    [RelayCommand]
    private async Task CopyNetworkAsync()
    {
        if (SelectedNetworkChoice is not { IsBuiltIn: false } choice || _service.Networks.Find(choice.Id) is not { } network) return;
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime { MainWindow.Clipboard: { } clipboard })
            await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, network.Text);
        SetStatus(StatusMessage.Success("multiplayer.network.copied"));
    }
}
