using Amber.App.Services;
using Amber.App.ViewModels;
using Amber.Core.Localization;
using Amber.Core.Multiplayer.Public;

namespace Amber.Core.Tests;

/// <summary>
/// AMBER_TEST_REGISTRY_KEY on a test machine: a network signed only by a test key is added only after the
/// unreviewed-network confirmation, the "jay" network included, and the network list and panel show it as test-signed.
/// </summary>
public sealed class TestSignedNetworkTests
{
    private static readonly TrustSigner Trustee = TrustSigner.Generate(out _);
    private static readonly TrustSigner TestKey = TrustSigner.Generate(out _);

    private static NetworkInfo Jay() =>
        new("jay", "Jay", PublicNetworkProfile.JayNetworkName, "jay-public-v1", ["tcp://lab.example.org:11010"], 1);

    private static void WithService(Action<LauncherService, MultiplayerViewModel> body) => UiTestThread.Run(() =>
    {
        using var service = new LauncherService(new AppSettings { GameDirectory = TestDataDir.Path, PlayerName = "" })
        {
            TrustedNetworkKeys = [new TrustQuorum([Trustee.Id], 1)],
            TestNetworkKeys = new TrustQuorum([TestKey.Id], 1),
        };
        service.ReloadNetworks();
        var mp = new MultiplayerViewModel(service);
        mp.LoadNetworks();
        try { body(service, mp); }
        finally
        {
            service.Settings.PublicNetworks = [];
            service.Settings.PublicNetworkId = null;
            service.TrySaveSettings();
        }
    });

    [Fact]
    public void ATestSignedJayUpdateIsAskedAboutAndShownAsTestSigned() => WithService((service, mp) =>
    {
        var text = NetworkString.Create(Jay(), TestKey);
        Assert.Equal(NetworkAddResult.NeedsConfirm, mp.AddNetworkText(text, false));
        Assert.True(mp.NetworkConfirmOpen);
        Assert.Equal(L.F("multiplayer.network.confirm_test_jay", NetworkString.Parse(text, [new TrustQuorum([TestKey.Id], 1)]).SignerFingerprint),
            mp.NetworkConfirmText);
        Assert.Null(service.Networks.Find("jay"));
        Assert.DoesNotContain("tcp://lab.example.org:11010", service.PublicNetwork.Nodes);

        mp.ConfirmAddNetworkCommand.Execute(null);
        Assert.Contains("tcp://lab.example.org:11010", service.PublicNetwork.Nodes);
        var jay = Assert.Single(mp.NetworkChoices);
        Assert.True(jay.IsBuiltIn);
        Assert.True(jay.TestReviewed);
        Assert.Contains("🧪", jay.Label);
        Assert.StartsWith(L.T("multiplayer.network.builtin_test"), jay.Detail);

        // The network panel: the Jay row reads test-signed, not built in.
        var panel = new NetworkStatusViewModel(mp, _ => { });
        var row = Assert.Single(panel.Rows, r => r.Id == "jay");
        Assert.Equal(L.T("shell.network.trust.test"), row.TrustText);
    });

    [Fact]
    public void ATestSignedNetworkNeedsTheSameConfirmationAsAnUnreviewedOne() => WithService((service, mp) =>
    {
        var lab = new NetworkInfo("lab", "Lab", "jay-lab", "lab-v1", ["tcp://lab.example.org:11010"], 1);
        var text = NetworkString.Create(lab, TestKey);
        Assert.Equal(NetworkAddResult.NeedsConfirm, mp.AddNetworkText(text, false));
        Assert.Contains("Lab", mp.NetworkConfirmText);
        Assert.NotEqual(L.F("multiplayer.network.confirm", "Lab", NetworkString.Parse(text).SignerFingerprint), mp.NetworkConfirmText);
        Assert.DoesNotContain(mp.NetworkChoices, c => c.Id == "lab");

        mp.ConfirmAddNetworkCommand.Execute(null);
        var choice = Assert.Single(mp.NetworkChoices, c => c.Id == "lab");
        Assert.True(choice.TestReviewed);
        Assert.StartsWith(L.T("multiplayer.network.test_reviewed"), choice.Detail);

        // Signed by a trustee: added straight away and shown as reviewed.
        var club = new NetworkInfo("club", "Club", "jay-club", "club-v1", ["tcp://club.example.org:11010"], 1);
        Assert.Equal(NetworkAddResult.Added, mp.AddNetworkText(NetworkString.Create(club, Trustee), false));
        Assert.False(Assert.Single(mp.NetworkChoices, c => c.Id == "club").TestReviewed);
    });
}
