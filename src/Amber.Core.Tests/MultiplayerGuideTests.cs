using System.ComponentModel;
using Amber.App.Services;
using Amber.App.ViewModels;
using Amber.Core.Localization;
using Amber.Core.Multiplayer;

namespace Amber.Core.Tests;

/// <summary>联机页引导（issue #23）：状态行随输入更新，贴错输入框的码转到对的地方。</summary>
public sealed class MultiplayerGuideTests
{
    private static (LauncherService Service, MultiplayerViewModel Vm, List<string?> Changed) Create()
    {
        var service = new LauncherService(new AppSettings { GameDirectory = TestDataDir.Path, PlayerName = "" });
        var vm = new MultiplayerViewModel(service);
        var changed = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        return (service, vm, changed);
    }

    [Fact]
    public void GameStatusFollowsManualPort() => UiTestThread.Run(() =>
    {
        var (service, vm, changed) = Create();
        using var _ = service;
        Assert.False(vm.HasHostGame);
        Assert.Equal(L.T("multiplayer.guide.game_missing"), vm.GameStatusText);

        vm.ManualPort = "25565";
        Assert.True(vm.HasHostGame);
        Assert.Equal(L.F("multiplayer.guide.port_ready", 25565), vm.GameStatusText);
        Assert.Contains(nameof(MultiplayerViewModel.GameStatusText), changed);
        Assert.Contains(nameof(MultiplayerViewModel.HasHostGame), changed);

        vm.ManualPort = "99999";
        Assert.False(vm.HasHostGame);
    });

    [Fact]
    public void NodeStatusFollowsNodeList() => UiTestThread.Run(() =>
    {
        var (service, vm, changed) = Create();
        using var _ = service;
        // 默认开着内置节点：开箱就有节点。
        Assert.True(vm.UseBuiltInNodes);
        Assert.True(vm.HasNodes);
        Assert.Equal(L.F("multiplayer.nodes.header_count", Amber.Core.Multiplayer.RelayNodes.BuiltIn.Count), vm.NodesHeaderText);
        Assert.Equal(Amber.Core.Multiplayer.RelayNodes.BuiltIn, service.MultiplayerPeers);

        vm.UseBuiltInNodes = false;
        Assert.False(vm.HasNodes);
        Assert.True(vm.NodesExpanded);
        Assert.Empty(service.MultiplayerPeers);
        Assert.Equal(L.T("multiplayer.nodes.header_empty"), vm.NodesHeaderText);

        vm.Nodes.Add(new NodeItem("tcp://example.com:11010"));
        Assert.True(vm.HasNodes);
        Assert.Equal(L.F("multiplayer.nodes.header_count", 1), vm.NodesHeaderText);
        Assert.Equal(L.F("multiplayer.guide.nodes_ready", 1), vm.NodesStatusText);
        Assert.Contains(nameof(MultiplayerViewModel.NodesStatusText), changed);
        Assert.Contains(nameof(MultiplayerViewModel.NodesHeaderText), changed);
    });

    [Fact]
    public void AdvancedHeaderListsWhatIsOn() => UiTestThread.Run(() =>
    {
        var (service, vm, changed) = Create();
        using var _ = service;
        Assert.Equal(L.T("multiplayer.advanced.title"), vm.AdvancedHeaderText);

        vm.FullLanMode = true;
        Assert.Contains(nameof(MultiplayerViewModel.AdvancedHeaderText), changed);
        if (vm.CanUseFullLan)
            Assert.Contains(L.T("multiplayer.advanced.item.full_lan"), vm.AdvancedHeaderText);
        else
        {
            Assert.False(vm.FullLanMode);
            Assert.Equal(L.T("multiplayer.advanced.title"), vm.AdvancedHeaderText);
        }

        changed.Clear();
        vm.AllowGameProxy = true;
        Assert.Contains(nameof(MultiplayerViewModel.AdvancedHeaderText), changed);
        Assert.Contains(L.T("multiplayer.advanced.item.proxy"), vm.AdvancedHeaderText);

        vm.FullLanMode = false;
        vm.AllowGameProxy = false;
        Assert.Equal(L.T("multiplayer.advanced.title"), vm.AdvancedHeaderText);
    });

    [Fact]
    public void NodeShareCodeInRoomBoxMovesToImportBoxWithoutImporting() => UiTestThread.Run(() =>
    {
        var (service, vm, _) = Create();
        using var __ = service;
        var code = RelayNodes.Encode(null, ["tcp://example.com:11010"]);
        vm.JoinCode = code;
        vm.JoinCommand.Execute(null);
        Assert.Equal("", vm.JoinCode);
        Assert.Equal(code, vm.ShareCodeText);
        Assert.Empty(vm.Nodes);
        Assert.True(vm.NodesExpanded);
        Assert.Equal(L.T("multiplayer.code.node_misplaced"), vm.Status);
        Assert.True(vm.IsIdle);
    });

    /// <summary>
    /// Removed direct codes in the join box: JAY2D / JAY2R carried both sides' public IPs, AMBDIRECT / AMBREPLY the whole room
    /// code. None of them is accepted; the status asks for a current invite.
    /// </summary>
    [Theory]
    [InlineData("JAY2D-eyJ2IjoxfQ")]
    [InlineData("JAY2R-eyJ2IjoxfQ")]
    [InlineData("AMBDIRECT1-eyJhIjoxfQ")]
    [InlineData("AMBREPLY1-eyJhIjoxfQ")]
    public void OldDirectCodeShowsTheUpdateHint(string code) => UiTestThread.Run(() =>
    {
        var (service, vm, _) = Create();
        using var __ = service;
        vm.JoinCode = code;
        vm.JoinCommand.Execute(null);
        Assert.Equal(L.T("multiplayer.direct.old_code"), vm.Status);
        Assert.True(vm.IsIdle);
        Assert.False(vm.IsKnocking);
    });

    [Fact]
    public void RoomCodeInRoomBoxIsNotRerouted() => UiTestThread.Run(() =>
    {
        var (service, vm, _) = Create();
        using var __ = service;
        vm.JoinCode = "AMB-K7Q2M9XD-4HZP8RW2N6TC-25565";
        vm.JoinCommand.Execute(null);
        // 组件没下载：照常停在原来的流程上，码留在输入框里。
        Assert.Equal("AMB-K7Q2M9XD-4HZP8RW2N6TC-25565", vm.JoinCode);
    });
}
