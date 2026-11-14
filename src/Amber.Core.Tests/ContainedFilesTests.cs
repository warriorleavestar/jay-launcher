using System.Text;
using Amber.Core.Bake;
using Amber.Core.Launch;
using Xunit;

namespace Amber.Core.Tests;

/// <summary>
/// Files and folders Jay writes inside an instance before a launch: a link the game left there (to another instance's
/// world, say) is refused, and nothing outside the instance is read, written or created.
/// </summary>
public sealed class ContainedFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amber-contained-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _work, _outside;

    public ContainedFilesTests()
    {
        _work = Directory.CreateDirectory(Path.Combine(_root, "versions", "f1122")).FullName;
        _outside = Directory.CreateDirectory(Path.Combine(_root, "versions", "other", "saves", "World")).FullName;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void FolderLink(string link, string target)
    {
        if (OperatingSystem.IsWindows()) WindowsJunction.Create(link, target);
        else Directory.CreateSymbolicLink(link, target);
    }

    /// <summary>A file symbolic link; false where this account may not make one (Windows without developer mode).</summary>
    private static bool FileLink(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    private string Sentinel()
    {
        var path = Path.Combine(_outside, "level.dat");
        File.WriteAllBytes(path, [0x1f, 0x8b, 0x08, 0x00, 0xff, 0xfe, 0x00, 0x42]);
        return path;
    }

    [Fact]
    public void AConfigFileIsCreatedAndUpdatedInPlace()
    {
        BakePlatform.DisableLegacyForgeUpdateCheck(_work);
        var cfg = Path.Combine(_work, "config", "forge.cfg");
        Assert.Contains("B:Global=false", File.ReadAllText(cfg));
        File.WriteAllText(cfg, "﻿general {\n    B:Global=true\n}\n");
        BakePlatform.DisableLegacyForgeUpdateCheck(_work);
        Assert.Equal("general {\n    B:Global=false\n}\n", File.ReadAllText(cfg, new UTF8Encoding(false)));
        Assert.Empty(Directory.GetFiles(_work, ".jay-write-*"));
    }

    [Fact]
    public void AConfigFileThatIsALinkIsNotWrittenThrough()
    {
        var sentinel = Sentinel();
        var before = File.ReadAllBytes(sentinel);
        Directory.CreateDirectory(Path.Combine(_work, "config"));
        var cfg = Path.Combine(_work, "config", "forge.cfg");
        if (!FileLink(cfg, sentinel)) return;
        Assert.Throws<IOException>(() => BakePlatform.DisableLegacyForgeUpdateCheck(_work));
        Assert.Equal(before, File.ReadAllBytes(sentinel));
    }

    [Fact]
    public void AConfigFolderThatIsALinkIsNotWrittenThrough()
    {
        var sentinel = Sentinel();
        var before = File.ReadAllBytes(sentinel);
        File.Copy(sentinel, Path.Combine(_outside, "forge.cfg"));
        FolderLink(Path.Combine(_work, "config"), _outside);
        Assert.Throws<IOException>(() => BakePlatform.DisableLegacyForgeUpdateCheck(_work));
        Assert.Throws<IOException>(() => BakePlatform.DisableForgeUpdateCheck(_work));
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(_outside, "forge.cfg")));
        Assert.Equal(before, File.ReadAllBytes(sentinel));
        Assert.False(File.Exists(Path.Combine(_outside, "fml.toml")));
    }

    [Fact]
    public void FoldersAreCreatedLevelByLevelAndALinkStopsThem()
    {
        ContainedFiles.CreateDirectory(_work, [".amber-ul", "legacy-1.12.2", "abc"]);
        Assert.True(Directory.Exists(Path.Combine(_work, ".amber-ul", "legacy-1.12.2", "abc")));

        FolderLink(Path.Combine(_work, ".amber-ul", "pivot"), _outside);
        Assert.True(ContainedFiles.HasLink(_work, [".amber-ul", "pivot", "new"]));
        Assert.False(ContainedFiles.HasLink(_work, [".amber-ul", "legacy-1.12.2", "abc"]));
        Assert.Throws<IOException>(() => ContainedFiles.CreateDirectory(_work, [".amber-ul", "pivot", "new"]));
        Assert.False(Directory.Exists(Path.Combine(_outside, "new")));
        Assert.Throws<ArgumentException>(() => ContainedFiles.CreateDirectory(_work, [".."]));
    }

    [Fact]
    public void ACacheFolderReachedThroughALinkIsDroppedAndNeverCreated()
    {
        var launch = new BakedLaunch("m", [], [], [], "s", "k");
        string? Check(string path) => BakeService.Checked(launch with { WritableCache = path }, _work).WritableCache;
        var state = Path.Combine(_work, BakeService.StateFolder);
        Directory.CreateDirectory(state);
        FolderLink(Path.Combine(state, "pivot"), _outside);
        var through = Path.Combine(state, "pivot", "new-directory");
        Assert.Null(Check(through));
        // A path written differently but inside comes back rebuilt from the instance folder.
        Assert.Equal(Path.Combine(state, "legacy-1.12.2", "k1"), Check(Path.Combine(state, "legacy-1.12.2", ".", "k1")));

        // The launch keeps its own check: a record that skipped Checked still cannot create the folder outside.
        Assert.Throws<IOException>(() => GameLauncher.CreateInside(_work, through));
        Assert.False(Directory.Exists(Path.Combine(_outside, "new-directory")));
        Assert.Throws<IOException>(() => GameLauncher.CreateInside(_work, Path.Combine(_root, "elsewhere")));
        Assert.False(Directory.Exists(Path.Combine(_root, "elsewhere")));
        GameLauncher.CreateInside(_work, Path.Combine(state, "legacy-1.12.2", "k1"));
        Assert.True(Directory.Exists(Path.Combine(state, "legacy-1.12.2", "k1")));
    }
}
