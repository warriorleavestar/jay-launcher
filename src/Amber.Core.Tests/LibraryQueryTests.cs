using Amber.Core.GameLibrary;

namespace Amber.Core.Tests;

public class LibraryQueryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    private static readonly LibraryEntry[] All =
    [
        new("fabric-1.20.1", "生存整合包", "1.20.1", "fabric", LastPlayed: T0.AddDays(-1)),
        new("sky", "Skyblock", "1.21.4", "neoforge", HasProblem: true, LastPlayed: T0.AddHours(-2)),
        new("1.12.2", "1.12.2", "1.12.2", null),
        new("tech", "Tech", "1.20.10", "forge", Pinned: true),
        new("run", "Running", "26.1", "fabric", IsRunning: true, LastPlayed: T0),
    ];

    [Fact]
    public void RecentSortPutsPinnedFirstThenLastPlayed() =>
        Assert.Equal(["tech", "run", "sky", "fabric-1.20.1", "1.12.2"], LibraryQuery.Apply(All, null, LibraryFilter.All, LibrarySort.Recent).Select(e => e.Id));

    [Fact]
    public void VersionSortIsNumeric() =>
        Assert.Equal(["tech", "run", "sky", "fabric-1.20.1", "1.12.2"], LibraryQuery.Apply(All, "", LibraryFilter.All, LibrarySort.Version).Select(e => e.Id));

    [Fact]
    public void NameSort() =>
        Assert.Equal("tech", LibraryQuery.Apply(All, null, LibraryFilter.All, LibrarySort.Name)[0].Id);

    [Theory]
    [InlineData(LibraryFilter.Pinned, "tech")]
    [InlineData(LibraryFilter.Problems, "sky")]
    [InlineData(LibraryFilter.Running, "run")]
    public void Filters(LibraryFilter filter, string only) =>
        Assert.Equal([only], LibraryQuery.Apply(All, null, filter, LibrarySort.Recent).Select(e => e.Id));

    [Theory]
    [InlineData("生存", "fabric-1.20.1")]
    [InlineData("NEOFORGE", "sky")]
    [InlineData("1.21", "sky")]
    [InlineData("fabric 26", "run")]
    public void SearchMatchesNameVersionLoaderAndAllTerms(string search, string only) =>
        Assert.Equal([only], LibraryQuery.Apply(All, search, LibraryFilter.All, LibrarySort.Recent).Select(e => e.Id));

    [Fact]
    public void ContinueWithPrefersRecentThenCurrentThenLastPlayed()
    {
        Assert.Equal("sky", LibraryQuery.ContinueWith(All, ["deleted", "sky"], "tech")?.Id);
        Assert.Equal("tech", LibraryQuery.ContinueWith(All, ["deleted"], "tech")?.Id);
        Assert.Equal("run", LibraryQuery.ContinueWith(All, null, null)?.Id);
        Assert.Equal("1.12.2", LibraryQuery.ContinueWith([All[2]], null, null)?.Id);
        Assert.Null(LibraryQuery.ContinueWith([], ["x"], "y"));
    }

    [Fact]
    public void TouchMovesToFrontAndCaps()
    {
        Assert.Equal(["b", "a", "c"], LibraryQuery.Touch(["a", "b", "c"], "b"));
        Assert.Equal(["n"], LibraryQuery.Touch(null, "n"));
        Assert.Equal(3, LibraryQuery.Touch(["a", "b", "c"], "d", max: 3).Count);
    }

    [Theory]
    [InlineData("1.20.10", "1.20.9", 1)]
    [InlineData("26.1", "1.21.4", 1)]
    [InlineData("1.21", "1.21.1", -1)]
    [InlineData("1.21.1", "1.21.1", 0)]
    [InlineData("1.21-pre1", "1.21", 1)]
    public void GameVersionsCompareNumerically(string a, string b, int sign) =>
        Assert.Equal(sign, Math.Sign(GameVersionComparer.Instance.Compare(a, b)));

    [Fact]
    public void CoverSlotIsStableAndSpread()
    {
        Assert.Equal(CoverArt.SlotFor("fabric-loader-0.16.9-1.20.1"), CoverArt.SlotFor("fabric-loader-0.16.9-1.20.1"));
        Assert.InRange(CoverArt.SlotFor(""), 0, CoverArt.Slots - 1);
        var slots = Enumerable.Range(0, 200).Select(i => CoverArt.SlotFor("instance-" + i)).Distinct().Count();
        Assert.Equal(CoverArt.Slots, slots);
        // Pinned value: changing the hash would recolor every user's library.
        Assert.Equal(5, CoverArt.SlotFor("1.20.1"));
    }

    [Theory]
    [InlineData("生存整合包", "生")]
    [InlineData("skyblock", "S")]
    [InlineData("  [1.20.1]", "1")]
    [InlineData("", "?")]
    public void Initial(string name, string expected) => Assert.Equal(expected, CoverArt.InitialOf(name));
}

[Collection("GlobalLanguage")]
public class AgoFormatTests
{
    [Fact]
    public void PlayTimeIsMinutesThenHours()
    {
        var before = Amber.Core.Localization.L.Language;
        Amber.Core.Localization.L.SetLanguage("en");
        try
        {
            Assert.Equal("45 minutes", Amber.Core.Localization.Fmt.PlayTime(TimeSpan.FromMinutes(45)));
            Assert.Equal("1 hour", Amber.Core.Localization.Fmt.PlayTime(TimeSpan.FromMinutes(70)));
            Assert.Equal("36 hours", Amber.Core.Localization.Fmt.PlayTime(TimeSpan.FromMinutes(2160)));
        }
        finally { Amber.Core.Localization.L.SetLanguage(before); }
    }

    [Fact]
    public void AgoBuckets()
    {
        var before = Amber.Core.Localization.L.Language;
        Amber.Core.Localization.L.SetLanguage("en");
        try
        {
            var now = new DateTimeOffset(2026, 11, 9, 15, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 11, 9, 15, 0, 0)));
            Assert.Equal("just now", Amber.Core.Localization.Fmt.Ago(now.AddSeconds(-20), now));
            Assert.Equal("5 min ago", Amber.Core.Localization.Fmt.Ago(now.AddMinutes(-5), now));
            Assert.Equal("1 hour ago", Amber.Core.Localization.Fmt.Ago(now.AddMinutes(-70), now));
            Assert.Equal("yesterday", Amber.Core.Localization.Fmt.Ago(now.AddDays(-1), now));
            Assert.Equal("3 days ago", Amber.Core.Localization.Fmt.Ago(now.AddDays(-3), now));
            Assert.DoesNotContain("ago", Amber.Core.Localization.Fmt.Ago(now.AddDays(-30), now));
        }
        finally
        {
            Amber.Core.Localization.L.SetLanguage(before);
        }
    }
}
