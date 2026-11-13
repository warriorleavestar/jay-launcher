using Amber.Core.Content;
using Amber.Core.Game;
using Amber.Core.Multiplayer;

namespace Amber.Core.Tests;

/// <summary>
/// File names and archive paths that come from other people (a member sending a file, a modpack, a mod sync
/// list, a world backup) must never leave the folder they are meant for, and must not turn into something
/// Windows treats specially (device names, alternate data streams).
/// </summary>
public class HostileFileNameTests
{
    public static readonly string[] HostileNames =
    [
        "../../evil.jar", "..\\..\\evil.jar", "/etc/passwd", "C:\\Windows\\evil.dll", "C:evil.dll", "\\\\server\\share\\evil.jar",
        "a/../../b.jar", "....//....//evil.jar", "..", ".", "...", " .. ", "", " ", "\0evil.jar", "evil\0.jar",
        "file.jar:Zone.Identifier", "file.jar::$DATA", "CON", "con.jar", "NUL.txt", "COM1.zip", "lpt9.jar", "AUX",
        "photo\u202Egnp.exe", "zero\u200Bwidth.jar", "evil.jar.", "evil.jar   ", "%2e%2e%2fevil.jar",
        "\uFF0E\uFF0E\uFF0Fevil.jar", "..\u2215evil.jar", "\u2024\u2024/evil.jar", new string('a', 400) + ".jar",
        "mods/../../../evil.jar", "~/evil.jar", "$HOME/evil.jar", "a\nb.jar", "a\tb.jar", "<>|?*\".jar",
    ];

    private static readonly string[] DeviceStems = ["CON", "PRN", "AUX", "NUL", "COM1", "COM9", "LPT1", "LPT9"];

    public static TheoryData<string> Names() => new(HostileNames);

    private static void AssertBareSafeName(string result, string input)
    {
        Assert.False(string.IsNullOrWhiteSpace(result), $"empty name for {Show(input)}");
        Assert.DoesNotContain('/', result);
        Assert.DoesNotContain('\\', result);
        Assert.DoesNotContain(':', result);
        Assert.False(result.Any(char.IsControl), $"control character kept for {Show(input)}");
        Assert.NotEqual(".", result);
        Assert.NotEqual("..", result);
        Assert.False(result.EndsWith('.') || result.EndsWith(' '), $"trailing dot or space kept for {Show(input)}: {Show(result)}");
        var stem = result.Split('.')[0].Trim().ToUpperInvariant();
        Assert.False(DeviceStems.Contains(stem), $"Windows device name kept for {Show(input)}: {Show(result)}");
        var dir = Path.Combine(Path.GetTempPath(), "jay-name-check");
        var full = Path.GetFullPath(Path.Combine(dir, result));
        Assert.Equal(Path.GetFullPath(dir), Path.GetDirectoryName(full));
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void ReceivedFileNamesStayInTheDownloadsFolder(string name) =>
        AssertBareSafeName(PrivateMessenger.SafeName(name), name);

    [Theory]
    [MemberData(nameof(Names))]
    public void ModSyncFileNamesStayInTheModsFolder(string name) =>
        AssertBareSafeName(ModSyncRunner.SafeFileName(name), name);

    [Theory]
    [MemberData(nameof(Names))]
    public void DownloadedContentNamesStayInTheTargetFolder(string name)
    {
        string result;
        try { result = ContentInstaller.SafeFileName(name); }
        catch (InvalidDataException) { return; }
        AssertBareSafeName(result, name);
    }

    [Theory]
    [InlineData(400, "a")]
    [InlineData(150, "\u4E2D")]
    [InlineData(100, "\U0001F600")]
    public void LongNamesKeepTheirExtensionAndFitEveryFileSystem(int repeat, string unit)
    {
        var name = string.Concat(Enumerable.Repeat(unit, repeat)) + ".jar";
        foreach (var result in new[] { PrivateMessenger.SafeName(name), ModSyncRunner.SafeFileName(name), ContentInstaller.SafeFileName(name) })
        {
            Assert.EndsWith(".jar", result);
            Assert.True(result.Length <= FileNames.MaxLength);
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(result) <= FileNames.MaxUtf8Bytes);
            Assert.False(char.IsHighSurrogate(result[^5]), "a surrogate pair was split");
        }
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void ArchivePathsStayInsideTheTarget(string relative)
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "jay-archive-check"));
        string path;
        try { path = GameDirectory.Contained(root, relative); }
        catch (InvalidDataException) { return; }
        Assert.StartsWith(root + Path.DirectorySeparatorChar, path);
        Assert.DoesNotContain(':', path[root.Length..]);
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void ModpackPathsStayInsideTheInstance(string relative)
    {
        var work = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "jay-pack-check"));
        var target = new ModpackService.PackTarget(work, Path.Combine(work, "versions", "x"), "x");
        string? path;
        try { path = target.Resolve(relative); }
        catch (InvalidDataException) { return; }
        if (path is null) return;
        Assert.StartsWith(work + Path.DirectorySeparatorChar, path);
        Assert.DoesNotContain(':', path[work.Length..]);
    }

    private static string Show(string s) => "\"" + string.Concat(s.Take(40).Select(c => char.IsControl(c) || c > 0x7E ? $"\\u{(int)c:X4}" : c.ToString())) + "\"";
}
