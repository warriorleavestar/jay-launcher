using System.IO.Compression;
using Amber.Core.Content;

namespace Amber.Core.Tests;

/// <summary>The free-space check counts only the files an import really writes, not the ones it skips.</summary>
public sealed class PackRoomTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "amber-pack-room-" + Guid.NewGuid().ToString("N"));

    public PackRoomTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        ModpackService.FreeSpaceForTest.Value = null;
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static ZipArchive Pack(params (string Name, int Size)[] files)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, size) in files)
                using (var entry = zip.CreateEntry(name, CompressionLevel.NoCompression).Open())
                    entry.Write(new byte[size]);
        stream.Position = 0;
        return new ZipArchive(stream, ZipArchiveMode.Read);
    }

    [Fact]
    public void SkippedSharedFilesDontCountAgainstTheFreeSpace()
    {
        // 40 KB of shared assets the import skips and 1 KB of instance files, with 8 KB free: the import goes ahead.
        ModpackService.FreeSpaceForTest.Value = _ => 8 * 1024;
        var work = Path.Combine(_dir, "work");
        using var zip = Pack(("game/assets/objects/big.bin", 40 * 1024), ("game/options.txt", 1024));
        ModpackService.ExtractPrefix(zip, "game/", new ModpackService.PackTarget(work, work, "Pack"),
            skipWhere: relative => relative.StartsWith("assets/", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(work, "options.txt")));
        Assert.False(File.Exists(Path.Combine(work, "assets", "objects", "big.bin")));
    }

    [Fact]
    public void FilesThatAreWrittenStillHaveToFit()
    {
        ModpackService.FreeSpaceForTest.Value = _ => 8 * 1024;
        var work = Path.Combine(_dir, "work");
        using var zip = Pack(("game/assets/objects/big.bin", 1024), ("game/saves/world.dat", 40 * 1024));
        Assert.Throws<InvalidDataException>(() => ModpackService.ExtractPrefix(zip, "game/", new ModpackService.PackTarget(work, work, "Pack"),
            skipWhere: relative => relative.StartsWith("assets/", StringComparison.Ordinal)));
        Assert.False(File.Exists(Path.Combine(work, "saves", "world.dat")));
    }
}
