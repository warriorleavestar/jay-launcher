using Amber.Core.Launch;

namespace Amber.Core.Tests;

/// <summary>The account name is removed from exported reports wherever a path carries it, not only under the home folder.</summary>
public sealed class ReportRedactionTests
{
    [Fact]
    public void ShortProfileFolderIsRedacted()
    {
        var text = "TMP=C:\\Users\\ALICES~1\\AppData\\Local\\Temp\nTEMP=c:/users/alices~2/AppData/Local/Temp";
        var redacted = ErrorReport.Redact(text, "Alice Smith", "C:\\Users\\Alice Smith");
        Assert.DoesNotContain("ALICES", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("C:\\Users\\<user>\\AppData", redacted);
        Assert.Contains("c:/users/<user>/AppData", redacted);
    }

    [Theory]
    [InlineData("Jürgen Weber", "C:\\Users\\J_RGEN~1\\x")]
    [InlineData("Jürgen Weber", "C:\\Users\\JÜRGEN~1\\x")]
    [InlineData("a.b+c d", "C:\\Users\\AB_CD~1\\x")]
    public void ShortFormsWithReplacedCharactersAreRedacted(string name, string text) =>
        Assert.Equal("C:\\Users\\<user>\\x", ErrorReport.Redact(text, name, "C:\\Users\\" + name));

    [Fact]
    public void NameWithNothingShortIsLeftToTheOtherRules() => Assert.Null(ErrorReport.ShortNamePattern("..."));

    [Theory]
    [InlineData("D:\\alice\\Minecraft\\mods\\a.jar", "D:\\<user>\\Minecraft\\mods\\a.jar")]
    [InlineData("/run/media/alice/disk/game", "/run/media/<user>/disk/game")]
    [InlineData("/media/alice/disk", "/media/<user>/disk")]
    [InlineData("/mnt/alice/games", "/mnt/<user>/games")]
    public void AccountFolderUnderADriveOrMountIsRedacted(string path, string expected) =>
        Assert.Equal(expected, ErrorReport.Redact(path, "alice", "/home/alice"));

    [Fact]
    public void OrdinaryWordsAndOtherFoldersStay()
    {
        // A user called "java": the word and folders that merely contain it are left alone.
        const string text = "at java.lang.Thread.run(Thread.java:1583)\n/usr/lib/jvm/java-21/bin/java\nC:\\Program Files\\Java\\bin";
        Assert.Equal(text, ErrorReport.Redact(text, "java", "/home/java"));
    }
}
