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

    [Theory]
    [InlineData(@"C:\Games\.minecraft", @"\\?\C:\Games\.minecraft")]
    [InlineData(@"\\nas\games\.minecraft", @"\\?\UNC\nas\games\.minecraft")]
    [InlineData(@"\\?\C:\Games", @"\\?\C:\Games")]
    [InlineData(@"\\?\UNC\nas\games", @"\\?\UNC\nas\games")]
    [InlineData(@"\\.\C:\Games", @"\\.\C:\Games")]
    public void WindowsPathsKeepTheirKind(string path, string expected) => Assert.Equal(expected, ContainedFiles.LongPath(path));

    [Fact]
    public void AnAlreadyLongGameFolderStillWorks()
    {
        if (!OperatingSystem.IsWindows()) return;
        BakePlatform.DisableLegacyForgeUpdateCheck(@"\\?\" + _work);
        Assert.Contains("B:Global=false", File.ReadAllText(Path.Combine(_work, "config", "forge.cfg")));
    }

    [Fact]
    public void AFifoInsteadOfTheConfigFileIsRefusedAtOnce()
    {
        if (OperatingSystem.IsWindows()) return;
        var config = Directory.CreateDirectory(Path.Combine(_work, "config")).FullName;
        var fifo = Path.Combine(config, "forge.cfg");
        using (var mk = System.Diagnostics.Process.Start("mkfifo", [fifo])) mk.WaitForExit();
        // No writer: opening it would wait forever; with one that never sends, reading would. Neither may happen.
        var noWriter = Task.Run(() => Assert.Throws<IOException>(() => BakePlatform.DisableLegacyForgeUpdateCheck(_work)));
        Assert.True(noWriter.Wait(TimeSpan.FromSeconds(5)), "the launch waited on a FIFO");
        using var writer = new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle(OpenWriterNonBlocking(fifo), true), FileAccess.Write);
        var silent = Task.Run(() => Assert.Throws<IOException>(() => BakePlatform.DisableLegacyForgeUpdateCheck(_work)));
        Assert.True(silent.Wait(TimeSpan.FromSeconds(5)), "the launch waited for data from a FIFO");
    }

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenRaw(string path, int flags);

    /// <summary>A write end that does not wait for a reader (O_RDWR on a FIFO never blocks on Linux and macOS).</summary>
    private static nint OpenWriterNonBlocking(string fifo) => OpenRaw(fifo, 2);

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
    public void AConfigFolderSwappedForALinkMeanwhileIsNeverWrittenThrough()
    {
        // The game swaps the folder for a link as fast as it can while Jay writes: a held level cannot be swapped, and
        // a link put there before Jay holds it is refused.
        var sentinel = Sentinel();
        var before = File.ReadAllBytes(sentinel);
        var outsideCfg = Path.Combine(_outside, "forge.cfg");
        File.Copy(sentinel, outsideCfg);
        var real = Directory.CreateDirectory(Path.Combine(_work, "config")).FullName;
        var aside = real + "-real";
        using var stop = new CancellationTokenSource();
        var swaps = 0;
        var swapper = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    if (Directory.Exists(aside))
                    {
                        if (Directory.Exists(real)) Directory.Delete(real, true);
                        Directory.Move(aside, real);
                    }
                    Directory.Move(real, aside);
                    FolderLink(real, _outside);
                    Directory.Delete(real);
                    Directory.Move(aside, real);
                    swaps++;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { }
            }
        });
        try
        {
            var until = DateTime.UtcNow.AddSeconds(2);
            while (DateTime.UtcNow < until)
            {
                try { BakePlatform.DisableLegacyForgeUpdateCheck(_work); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                // Room for the game: Jay holding the folder keeps it from being swapped, which would prove nothing.
                Thread.Sleep(1);
            }
        }
        finally
        {
            stop.Cancel();
            swapper.Wait(TimeSpan.FromSeconds(10));
        }
        Assert.True(swaps > 0);
        Assert.Equal(before, File.ReadAllBytes(sentinel));
        Assert.Equal(before, File.ReadAllBytes(outsideCfg));
        Assert.Equal(new[] { sentinel, outsideCfg }.Order(), Directory.GetFiles(_outside).Order());
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
