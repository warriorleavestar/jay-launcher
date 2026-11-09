using Amber.App.Services;
using Amber.App.ViewModels;

namespace Amber.Core.Tests;

/// <summary>The first-run wizard (official design §6.1): who sees it, and who keeps the old default account.</summary>
public sealed class FirstRunTests
{
    [Theory]
    [InlineData(false, false, false, null, null, null, FirstRunDecision.Show)]
    [InlineData(true, false, false, null, null, null, FirstRunDecision.Hide)]
    [InlineData(false, true, false, null, null, null, FirstRunDecision.AlreadySetUp)]
    [InlineData(false, false, true, null, null, null, FirstRunDecision.AlreadySetUp)]
    [InlineData(false, false, false, "/r", "shell", null, FirstRunDecision.Hide)]
    [InlineData(false, false, false, "/r", "firstrun", null, FirstRunDecision.Show)]
    [InlineData(false, false, false, null, null, "/snap", FirstRunDecision.Hide)]
    public void OnlyABrandNewJayShowsTheWizard(bool done, bool account, bool instance, string? selfTest, string? scope, string? snapshot, FirstRunDecision expected) =>
        Assert.Equal(expected, FirstRunViewModel.Decide(done, account, instance, selfTest, scope, snapshot));

    [Theory]
    [InlineData(0, "Player", false, null, null, true)]   // an old settings file: keep its name as an offline account
    [InlineData(0, "Player", true, null, null, false)]   // brand new: the wizard asks
    [InlineData(1, "Player", false, null, null, false)]  // already has an account
    [InlineData(0, " ", false, null, null, false)]
    [InlineData(0, "Player", true, "/r", "library", true)]   // self-checks keep the default account
    [InlineData(0, "Player", true, "/r", "firstrun", false)]
    public void OldPlayerNameBecomesAnAccountOnlyForExistingSettings(int accounts, string name, bool fresh, string? selfTest, string? scope, bool expected) =>
        Assert.Equal(expected, LauncherService.MigratesPlayerName(accounts, name, fresh, selfTest, scope));
}
