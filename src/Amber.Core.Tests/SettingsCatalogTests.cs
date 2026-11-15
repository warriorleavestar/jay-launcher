using Amber.Core.Localization;
using Amber.Core.Navigation;

namespace Amber.Core.Tests;

/// <summary>The settings groups and the field list that search and the command palette read (official design §6.20).</summary>
public sealed class SettingsCatalogTests
{
    [Fact]
    public void TenGroupsEachWithAtLeastOneSetting()
    {
        // The Tools group is gone: its things live in Storage, Game and About. Network came with redesign batch 7.
        Assert.Equal(10, SettingsCatalog.Groups.Count);
        Assert.Null(SettingsCatalog.Group("tools"));
        Assert.Equal(SettingsCatalog.Storage, SettingsCatalog.Find("Junk")?.Group);
        Assert.Equal(SettingsCatalog.Game, SettingsCatalog.Find("JavaDownload")?.Group);
        Assert.Equal(SettingsCatalog.Game, SettingsCatalog.Find("Memory")?.Group);
        Assert.Equal(SettingsCatalog.About, SettingsCatalog.Find("CheckUpdate")?.Group);
        Assert.Null(SettingsCatalog.Find("OpenTools"));
        Assert.Equal(SettingsCatalog.Groups.Count, SettingsCatalog.Groups.Select(g => g.Id).Distinct().Count());
        foreach (var group in SettingsCatalog.Groups)
            Assert.Contains(SettingsCatalog.Fields, f => f.Group == group.Id);
    }

    [Fact]
    public void WindowsOnlySettingsAreListedOnlyOnWindows()
    {
        // Strong sandbox has no control off Windows, so settings#StrongSandbox and the palette must not offer it there.
        Assert.True(SettingsCatalog.All.Single(f => f.Field == "StrongSandbox").WindowsOnly);
        Assert.Equal(OperatingSystem.IsWindows(), SettingsCatalog.Find("StrongSandbox") is not null);
        Assert.Equal(SettingsCatalog.All.Count(f => !f.WindowsOnly || OperatingSystem.IsWindows()), SettingsCatalog.Fields.Count);
    }

    [Fact]
    public void FieldsAreUniqueAndBelongToAGroup()
    {
        Assert.Equal(SettingsCatalog.Fields.Count, SettingsCatalog.Fields.Select(f => f.Field.ToLowerInvariant()).Distinct().Count());
        Assert.All(SettingsCatalog.Fields, f => Assert.NotNull(SettingsCatalog.Group(f.Group)));
    }

    [Theory]
    [InlineData("sidebar", "Sidebar", "appearance")]
    [InlineData("SidebarBox", "Sidebar", "appearance")]
    [InlineData("gamedirectory", "GameDirectory", "storage")]
    [InlineData("NoTrace", "NoTrace", "privacy")]
    [InlineData("TransportBox", "Transport", "multiplayer")]
    [InlineData("Breaker", "Breaker", "multiplayer")]   // emergency disconnect moved from Privacy (batch 7)
    [InlineData("StunBox", "Stun", "network")]
    [InlineData("RelayGroup", "RelayGroup", "network")]
    public void FindsAFieldByFragmentOrControlName(string fragment, string field, string group)
    {
        var found = SettingsCatalog.Find(fragment);
        Assert.NotNull(found);
        Assert.Equal(field, found.Field);
        Assert.Equal(group, found.Group);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nothing-here")]
    public void UnknownFragmentIsNull(string? fragment) => Assert.Null(SettingsCatalog.Find(fragment));

    [Fact]
    public void EveryKeyIsInTheSourceLanguage()
    {
        var en = L.Load(L.Source);
        var keys = SettingsCatalog.Groups.SelectMany(g => new[] { g.TitleKey, g.SummaryKey, g.NoteKey })
            .Concat(SettingsCatalog.All.SelectMany(f => new[] { f.LabelKey, f.HintKey }).OfType<string>());
        Assert.All(keys, k => Assert.True(en.ContainsKey(k), k));
    }
}
