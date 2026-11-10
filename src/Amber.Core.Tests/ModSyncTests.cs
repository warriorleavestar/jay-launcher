using Amber.Core.Multiplayer;

namespace Amber.Core.Tests;

/// <summary>Comparing mod lists, planning a sync, auto-picking an instance and reading a manifest someone sent.</summary>
public sealed class ModSyncTests
{
    private static string Hash(char c) => new(c, 40);

    private static ModManifestEntry Mod(string file, char hash, string? id = null, string? version = null) => new(file, Hash(hash), 1000, id, id, version);

    [Fact]
    public void SameFilesUnderOtherNamesMatch()
    {
        var mine = new ModManifest([Mod("a.jar", 'a'), Mod("b-renamed.jar", 'b')]);
        var host = new ModManifest([Mod("b.jar", 'b'), Mod("a.jar", 'a')]);
        var diff = ModSync.Compare(mine, host);
        Assert.True(diff.Identical);
        Assert.Equal(2, diff.Same);
        Assert.Equal(mine.Fingerprint, host.Fingerprint);
    }

    [Fact]
    public void FindsMissingDifferentAndExtra()
    {
        var mine = new ModManifest([
            Mod("sodium-fabric-0.5.3+mc1.20.1.jar", '1', "sodium", "0.5.3"),
            Mod("jei-1.20.1-fabric-15.2.0.27.jar", '2'),
            Mod("journeymap-1.20.1-5.9.18-fabric.jar", '3', "journeymap"),
            Mod("same.jar", 's'),
        ]);
        var host = new ModManifest([
            Mod("sodium-fabric-0.5.8+mc1.20.1.jar", '4', "sodium", "0.5.8"),
            Mod("jei-1.20.1-fabric-15.3.0.4.jar", '5'),
            Mod("create-1.20.1-0.5.1.jar", '6', "create"),
            Mod("same.jar", 's'),
        ]);
        var diff = ModSync.Compare(mine, host);
        Assert.Equal(["create"], diff.Missing.Select(m => m.ModId));
        Assert.Equal(2, diff.Different.Count);
        Assert.Contains(diff.Different, p => p.Mine.Version == "0.5.3" && p.Host.Version == "0.5.8");
        Assert.Equal(["journeymap"], diff.Extra.Select(m => m.ModId));
        Assert.Empty(diff.RiskyExtra);
        Assert.False(diff.Matches);
        Assert.Equal(1, diff.Same);
    }

    [Fact]
    public void ExtraClientOnlyModsStillMatch()
    {
        var mine = new ModManifest([Mod("a.jar", 'a'), Mod("xaero.jar", 'x', "xaerominimap")]);
        var host = new ModManifest([Mod("a.jar", 'a')]);
        var diff = ModSync.Compare(mine, host);
        Assert.True(diff.Matches);
        Assert.False(diff.Identical);
    }

    [Theory]
    [InlineData("sodium-fabric-0.5.8+mc1.20.1.jar", "sodium-fabric")]
    [InlineData("jei-1.20.1-forge-15.2.0.27.jar", "jei")]
    [InlineData("Create_0.5.1.jar.disabled", "create")]
    [InlineData("modmenu.jar", "modmenu")]
    public void StemsDropTheVersion(string file, string stem) => Assert.Equal(stem, ModSync.StemOf(file));

    [Fact]
    public void PlanUsesPublicFilesAndAsksTheHostForTheRest()
    {
        var mine = new ModManifest([Mod("jei-1.jar", '2'), Mod("server-only-extra.jar", '9', "someservermod"), Mod("map.jar", 'm', "journeymap")]);
        var host = new ModManifest([Mod("jei-2.jar", '5'), Mod("create-1.jar", '6', "create"), Mod("private-1.jar", '7')]);
        var diff = ModSync.Compare(mine, host);
        var plan = ModSync.Plan(diff, new HashSet<string> { Hash('5'), Hash('6') });
        Assert.Equal(2, plan.FromPublic);
        Assert.Equal(1, plan.FromHost);
        Assert.True(plan.IntoNewInstance);
        Assert.Contains(plan.Replaced, m => m.FileName == "jei-1.jar");
        Assert.Contains(plan.Replaced, m => m.FileName == "server-only-extra.jar");
        Assert.Equal(["map.jar"], plan.Kept.Select(m => m.FileName));

        var inPlace = ModSync.Plan(diff, new HashSet<string>(), intoNewInstance: false);
        Assert.Equal(3, inPlace.FromHost);
        Assert.Equal(2, inPlace.Kept.Count);
        Assert.Equal(["jei-1.jar"], inPlace.Replaced.Select(m => m.FileName));
    }

    [Fact]
    public void PickerPrefersExactThenClosestThenOrder()
    {
        var host = new ModManifest([Mod("a.jar", 'a'), Mod("b.jar", 'b')]);
        var game = host.ToInviteGame("1.20.1", "fabric");
        var candidates = new[]
        {
            new InstanceCandidate("forge", "1.20.1", "forge", 2),
            new InstanceCandidate("far", "1.20.1", "fabric", 40),
            new InstanceCandidate("near", "1.20.1", "fabric", 3),
            new InstanceCandidate("near2", "1.20.1", "fabric", 1),
            new InstanceCandidate("old", "1.19.2", "fabric", 2, game.ModsFingerprint),
        };
        var pick = InstancePicker.Pick(game, candidates);
        Assert.Equal("near", pick!.Instance.Id);
        Assert.Equal(InstanceMatchKind.SameGame, pick.Kind);

        var exact = InstancePicker.Pick(game, [.. candidates, new InstanceCandidate("exact", "1.20.1", "fabric", 2, host.Fingerprint)]);
        Assert.Equal(("exact", InstanceMatchKind.Exact), (exact!.Instance.Id, exact.Kind));

        Assert.Null(InstancePicker.Pick(game with { McVersion = "1.21" }, candidates));
    }

    [Fact]
    public void VanillaMatchesOptiFine()
    {
        var game = ModManifest.Empty.ToInviteGame("1.20.1", null);
        Assert.Equal("of", InstancePicker.Pick(game, [new InstanceCandidate("of", "1.20.1", "optifine", 0)])!.Instance.Id);
    }

    [Fact]
    public void ManifestJsonRoundTripsAndRejectsBadInput()
    {
        var manifest = new ModManifest([Mod("a.jar", 'a', "alpha", "1.0")]);
        var back = ModManifest.FromJson(manifest.ToJson())!;
        Assert.Equal(manifest.Mods, back.Mods);

        Assert.Null(ModManifest.FromJson("not json"));
        Assert.Null(ModManifest.FromJson("""{"mods":[{"fileName":"a.jar","sha1":"xyz","size":1}]}"""));
        Assert.Null(ModManifest.FromJson("""{"mods":[{"fileName":"..","sha1":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","size":1}]}"""));
        var traversal = ModManifest.FromJson("""{"mods":[{"fileName":"../../evil.jar","sha1":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","size":1}]}""")!;
        Assert.Equal("evil.jar", traversal.Mods[0].FileName);
        Assert.Equal(Hash('a'), traversal.Mods[0].Sha1);
        var tooMany = new ModManifest([.. Enumerable.Range(0, ModManifest.MaxMods + 1).Select(i => Mod(i + ".jar", 'a'))]);
        Assert.Null(ModManifest.FromJson(tooMany.ToJson()));
    }

    [Fact]
    public void ReadsEnabledModsOfAFolder()
    {
        var dir = Directory.CreateTempSubdirectory("jay-manifest-").FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "one.jar"), [1, 2, 3]);
            File.WriteAllBytes(Path.Combine(dir, "two.jar.disabled"), [4]);
            var manifest = ModManifest.Read(dir);
            var entry = Assert.Single(manifest.Mods);
            Assert.Equal("one.jar", entry.FileName);
            Assert.Equal(3, entry.Size);
            Assert.Equal("7037807198c22a7d2b0807371d763779a84fdfcf", entry.Sha1);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
