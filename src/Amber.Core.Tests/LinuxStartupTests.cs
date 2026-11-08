using Amber.App;

namespace Amber.Core.Tests;

public class LinuxStartupTests
{
    [Theory]
    [InlineData("zh_CN.UTF-8", "zh-Hans")]
    [InlineData("zh_SG", "zh-Hans")]
    [InlineData("zh_TW.UTF-8", "zh-Hant")]
    [InlineData("zh_HK.Big5", "zh-Hant")]
    [InlineData("zh_MO", "zh-Hant")]
    [InlineData("ja_JP.UTF-8", "ja")]
    [InlineData("en_US.UTF-8", "en")]
    [InlineData("de_DE@euro", "en")]
    [InlineData("C.UTF-8", "en")]
    [InlineData("POSIX", "en")]
    [InlineData("", "en")]
    [InlineData(null, "en")]
    public void LanguageFromLocale_MapsPosixLocales(string? locale, string expected) =>
        Assert.Equal(expected, LinuxStartup.LanguageFromLocale(locale));

    [Fact]
    public void IcuCandidates_CoverVersionedAndUnversionedNames()
    {
        var names = LinuxStartup.IcuCandidates().ToList();
        Assert.Contains("libicuuc.so.78", names);
        Assert.Contains("libicuuc.so.50", names);
        Assert.Contains("libicuuc.so.99", names);
        Assert.Equal("libicuuc.so", names[^1]);
    }

    [Fact]
    public void MissingLibraries_ListsOnlyTheOnesThatFailToLoad()
    {
        var missing = LinuxStartup.MissingLibraries(name => name != "libICE.so.6" && name != "libSM.so.6");
        Assert.Equal(["libICE.so.6", "libSM.so.6"], missing);
        Assert.Empty(LinuxStartup.MissingLibraries(_ => true));
    }

    [Fact]
    public void MissingLibrariesMessage_NamesLibraryAndPackagesInEachLanguage()
    {
        foreach (var language in new[] { "zh-Hans", "zh-Hant", "en", "ja" })
        {
            var text = LinuxStartup.MissingLibrariesMessage(["libICE.so.6"], language);
            Assert.Contains("libICE.so.6", text);
            Assert.Contains("sudo apt install libice6", text);
            Assert.Contains("sudo dnf install libICE", text);
            Assert.Contains("sudo pacman -S libice", text);
            Assert.DoesNotContain("{", text);
        }
        Assert.Contains("无法启动", LinuxStartup.MissingLibrariesMessage(["libICE.so.6"], "zh-Hans"));
        Assert.Contains("can't start", LinuxStartup.MissingLibrariesMessage(["libICE.so.6"], "en"));
    }

    [Fact]
    public void MissingLibrariesMessage_UnknownLibraryStillGivesInstallCommand()
    {
        var text = LinuxStartup.MissingLibrariesMessage(["libXi.so.6"], "en");
        Assert.Contains("libxi6", text);
        var unknown = LinuxStartup.MissingLibrariesMessage(["libfoo.so.1"], "en");
        Assert.Contains("libfoo.so.1", unknown);
        Assert.Contains("libx11-6 libice6 libsm6 libfontconfig1", unknown);
    }

    [Theory]
    [InlineData("Jay 启动器", true)]
    [InlineData("Jay 啟動器", true)]
    [InlineData("Jay ランチャー", true)]
    [InlineData("导出（ZIP）", true)]
    [InlineData("Jay launcher", false)]
    [InlineData("Jay", false)]
    [InlineData("Café – Ünïcode", false)]
    public void WindowTitleFallback_OnlyForCjkTitles(string title, bool expected) =>
        Assert.Equal(expected, LinuxFonts.NeedsFallback(title));

    [Theory]
    [InlineData("Unable to load shared library 'libICE.so.6' or one of its dependencies.", "libICE.so.6")]
    [InlineData("Unable to load shared library 'libXi.so.6' or one of its dependencies.\n/root/x: cannot open", "libXi.so.6")]
    [InlineData("something else", "something else")]
    public void ExtractLibraryName_ReadsTheQuotedName(string message, string expected) =>
        Assert.Equal(expected, LinuxStartup.ExtractLibraryName(message));
}
