using System.Buffers.Binary;
using System.IO.Compression;
using Amber.Core.Game;
using Amber.Core.Multiplayer.Files;

namespace Amber.Core.Tests;

/// <summary>
/// Zips someone sent: telling whether one holds a world reads its list of entries a piece at a time with limits, and
/// importing a world checks counts, sizes and free space first, counts bytes while copying, and leaves nothing behind
/// (and the saves folder as it was) when it stops.
/// </summary>
public sealed class ZipLimitsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("jay-zip-limits-").FullName;
    private string Game => Path.Combine(_root, "game");
    private string Saves => Path.Combine(Game, "saves");

    public ZipLimitsTests()
    {
        // A world that is already there: no failed import may touch it.
        Directory.CreateDirectory(Path.Combine(Saves, "World"));
        File.WriteAllText(Path.Combine(Saves, "World", "level.dat"), "mine");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private string Zip(string name, IEnumerable<(string Name, int Size)> entries, CompressionLevel level = CompressionLevel.Fastest)
    {
        var path = Path.Combine(_root, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entry, size) in entries)
        {
            using var stream = zip.CreateEntry(entry, level).Open();
            if (size > 0) stream.Write(new byte[size]);
        }
        return path;
    }

    private void AssertSavesUntouched()
    {
        Assert.Equal(["World"], Directory.EnumerateFileSystemEntries(Saves).Select(Path.GetFileName));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(Saves, "World", "level.dat")));
        Assert.Empty(Directory.EnumerateDirectories(Game, ".jay-import-*", SearchOption.AllDirectories));
    }

    // ---- reading the list of entries ----

    [Fact]
    public void TooManyEntriesAreRefusedBeforeAnyIsRead()
    {
        var path = Zip("many.zip", Enumerable.Range(0, 1001).Select(i => ($"f{i}", 0)));
        var visited = 0;
        Assert.Throws<InvalidDataException>(() => ZipDirectory.Scan(path, 1000, long.MaxValue, _ => { visited++; return true; }));
        Assert.Equal(0, visited);
        Assert.Equal(1001, ZipDirectory.Scan(path, 1001, long.MaxValue, _ => true).Entries);
    }

    [Fact]
    public void AZip64ListIsReadThrough()
    {
        // More than 65535 entries: the counts only fit in the ZIP64 end record.
        const int count = ushort.MaxValue + 2;
        var path = Zip("zip64.zip", Enumerable.Range(0, count).Select(i => (i == count - 1 ? "w/level.dat" : $"{i}", 0)), CompressionLevel.NoCompression);
        var visited = 0;
        var summary = ZipDirectory.Scan(path, 100_000, long.MaxValue, _ => { visited++; return true; });
        Assert.Equal(count, summary.Entries);
        Assert.Equal(count, visited);
        Assert.True(FilePacker.HasWorld(path));
        Assert.Throws<InvalidDataException>(() => ZipDirectory.Scan(path, ushort.MaxValue, long.MaxValue, _ => true));
    }

    [Fact]
    public void LongNamesCountAgainstTheDirectoryLimit()
    {
        var longName = new string('n', 60_000);
        var path = Zip("long.zip", [(longName, 0), (longName + "2", 0), ("w/level.dat", 0)]);
        Assert.True(FilePacker.HasWorld(path));
        string? seen = null;
        ZipDirectory.Scan(path, 10, long.MaxValue, e => { seen ??= e.Name; return true; });
        Assert.Equal(longName, seen);
        Assert.Throws<InvalidDataException>(() => ZipDirectory.Scan(path, 10, 100_000, _ => true));
    }

    [Fact]
    public void ManyDuplicateNamesAreReadOneAtATime()
    {
        var path = Zip("dupes.zip", Enumerable.Range(0, 5000).Select(_ => ("same.txt", 1)).Append(("w/level.dat", 1)));
        Assert.True(FilePacker.HasWorld(path));
        Assert.False(FilePacker.HasWorld(Zip("dupes-only.zip", Enumerable.Range(0, 5000).Select(_ => ("same.txt", 1)))));
    }

    [Fact]
    public void ReadingTheListCanBeCancelled()
    {
        var path = Zip("cancel.zip", Enumerable.Range(0, 3000).Select(i => ($"f{i}", 0)));
        using var cts = new CancellationTokenSource();
        var visited = 0;
        Assert.ThrowsAny<OperationCanceledException>(() => ZipDirectory.Scan(path, 10_000, long.MaxValue, _ =>
        {
            if (++visited == 10) cts.Cancel();
            return true;
        }, cts.Token));
        Assert.InRange(visited, 10, 1024);
        Assert.Throws<InvalidDataException>(() => ZipDirectory.Read(new MemoryStream(new byte[100])));
    }

    // ---- importing a world ----

    private static WorldManager.ImportLimits Limits(long entry = 1 << 20, long total = 4 << 20, long? free = null) =>
        new(1000, 1 << 20, entry, total, 0) { FreeSpace = _ => free };

    [Fact]
    public void AHighRatioWorldIsRefusedByWhatItWouldUnpackTo()
    {
        // 20 MB of zeros packs into a few kilobytes.
        var path = Zip("bomb.zip", [("w/level.dat", 10), ("w/region/r.0.0.mca", 20 << 20)], CompressionLevel.SmallestSize);
        Assert.True(new FileInfo(path).Length < 200_000);
        Assert.Throws<InvalidDataException>(() => WorldManager.Import(path, Saves, Limits(entry: 64 << 20, total: 8 << 20)));
        AssertSavesUntouched();
    }

    [Fact]
    public void AnEntryOverItsLimitIsRefused()
    {
        var path = Zip("entry.zip", [("w/level.dat", 10), ("w/big", 2 << 20)]);
        var e = Assert.Throws<InvalidDataException>(() => WorldManager.Import(path, Saves, Limits(entry: 1 << 20)));
        Assert.Contains("w/big", e.Message);
        AssertSavesUntouched();
    }

    [Fact]
    public void AWorldOverTheTotalIsRefused()
    {
        var path = Zip("total.zip", [("w/level.dat", 10), ("w/a", 3 << 20), ("w/b", 3 << 20)]);
        Assert.Throws<InvalidDataException>(() => WorldManager.Import(path, Saves, Limits(entry: 4 << 20, total: 5 << 20)));
        AssertSavesUntouched();
    }

    [Fact]
    public void AnEntryThatUnpacksToMoreThanItsZipSaysNeverWritesMore()
    {
        var path = Zip("liar.zip", [("w/level.dat", 10), ("w/region", 1 << 20)], CompressionLevel.SmallestSize);
        // Make the zip's list claim the second entry is 100 bytes.
        var bytes = File.ReadAllBytes(path);
        var patched = 0;
        for (var i = 0; i + 46 < bytes.Length; i++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i)) != 0x02014b50) continue;
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i + 24)) != 1 << 20) continue;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i + 24), 100);
            patched++;
        }
        Assert.Equal(1, patched);
        File.WriteAllBytes(path, bytes);
        // Either refused, or nothing past what the list says comes out: never the whole megabyte.
        try
        {
            var folder = WorldManager.Import(path, Saves, Limits());
            Assert.InRange(new FileInfo(Path.Combine(Saves, folder, "region")).Length, 0, 100);
        }
        catch (InvalidDataException)
        {
            AssertSavesUntouched();
        }
        Assert.Empty(Directory.EnumerateDirectories(Game, ".jay-import-*", SearchOption.AllDirectories));
    }

    [Fact]
    public void NotEnoughFreeSpaceIsSaidBeforeAnythingIsWritten()
    {
        var path = Zip("space.zip", [("w/level.dat", 10), ("w/a", 100_000)]);
        Assert.Throws<IOException>(() => WorldManager.Import(path, Saves, Limits(free: 50_000)));
        AssertSavesUntouched();
        Assert.Equal("w", WorldManager.Import(path, Saves, Limits(free: 10_000_000)));
    }

    [Fact]
    public void ACancelledImportLeavesNothingBehind()
    {
        var path = Zip("cancel.zip", [("w/level.dat", 10), ("w/a", 100_000), ("w/b", 100_000)]);
        using var cts = new CancellationTokenSource();
        // Cancelled after the checks, once the staging folder is about to fill.
        var limits = new WorldManager.ImportLimits(1000, 1 << 20, 1 << 20, 4 << 20, 0) { FreeSpace = _ => { cts.Cancel(); return null; } };
        Assert.ThrowsAny<OperationCanceledException>(() => WorldManager.Import(path, Saves, limits, cts.Token));
        AssertSavesUntouched();
        Assert.ThrowsAny<OperationCanceledException>(() => WorldManager.Import(path, Saves, Limits(), cts.Token));
        AssertSavesUntouched();
    }

    [Fact]
    public void AWorldWithTheSameNameStillImportsBesideTheOriginal()
    {
        var path = Zip("same.zip", [("World/level.dat", 10), ("World/region/r.0.0.mca", 1000)]);
        Assert.Equal("World (2)", WorldManager.Import(path, Saves, Limits()));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(Saves, "World", "level.dat")));
        Assert.Empty(Directory.EnumerateDirectories(Game, ".jay-import-*", SearchOption.AllDirectories));
    }
}
