using System.Buffers.Binary;
using System.Text;

namespace Amber.Core;

/// <summary>One entry of a zip's central directory: its name and the sizes the directory claims.</summary>
public readonly record struct ZipEntryInfo(string Name, long CompressedSize, long Size);

/// <summary>
/// Reads a zip's central directory one entry at a time, without building the list of every entry first (which is what
/// <see cref="System.IO.Compression.ZipArchive.Entries"/> does). The entry count and the directory's size come from the
/// end record (ZIP64 included) and are checked against limits before anything else is read; names are read one at a
/// time. Received files are untrusted, so this is the first look at any zip someone sent.
/// </summary>
public static class ZipDirectory
{
    private const uint EndSignature = 0x06054b50;
    private const uint Zip64EndSignature = 0x06064b50;
    private const uint Zip64LocatorSignature = 0x07064b50;
    private const uint EntrySignature = 0x02014b50;
    private const int EndSize = 22;
    private const int MaxComment = ushort.MaxValue;

    /// <summary>The entry count and directory size from the end record.</summary>
    public readonly record struct Summary(long Entries, long DirectorySize, long DirectoryOffset);

    /// <summary>Reads the end record. Throws <see cref="InvalidDataException"/> when the file is not a zip.</summary>
    public static Summary Read(Stream stream)
    {
        var length = stream.Length;
        if (length < EndSize) throw new InvalidDataException("Not a zip file.");
        var tailLength = (int)Math.Min(length, EndSize + MaxComment);
        var tail = new byte[tailLength];
        stream.Position = length - tailLength;
        stream.ReadExactly(tail);
        var end = -1;
        for (var i = tailLength - EndSize; i >= 0; i--)
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i)) == EndSignature) { end = i; break; }
        if (end < 0) throw new InvalidDataException("Not a zip file.");
        var record = tail.AsSpan(end);
        long entries = BinaryPrimitives.ReadUInt16LittleEndian(record[10..]);
        long size = BinaryPrimitives.ReadUInt32LittleEndian(record[12..]);
        long offset = BinaryPrimitives.ReadUInt32LittleEndian(record[16..]);
        var endPosition = length - tailLength + end;
        if (entries == ushort.MaxValue || size == uint.MaxValue || offset == uint.MaxValue)
        {
            // ZIP64: the locator sits just before the end record and points at the ZIP64 end record.
            if (endPosition < 20) throw new InvalidDataException("Broken ZIP64 end record.");
            var locator = new byte[20];
            stream.Position = endPosition - 20;
            stream.ReadExactly(locator);
            if (BinaryPrimitives.ReadUInt32LittleEndian(locator) != Zip64LocatorSignature) throw new InvalidDataException("Broken ZIP64 end record.");
            var at = BinaryPrimitives.ReadUInt64LittleEndian(locator.AsSpan(8));
            if (at > (ulong)(endPosition - 56)) throw new InvalidDataException("Broken ZIP64 end record.");
            var zip64 = new byte[56];
            stream.Position = (long)at;
            stream.ReadExactly(zip64);
            if (BinaryPrimitives.ReadUInt32LittleEndian(zip64) != Zip64EndSignature) throw new InvalidDataException("Broken ZIP64 end record.");
            var entries64 = BinaryPrimitives.ReadUInt64LittleEndian(zip64.AsSpan(32));
            var size64 = BinaryPrimitives.ReadUInt64LittleEndian(zip64.AsSpan(40));
            var offset64 = BinaryPrimitives.ReadUInt64LittleEndian(zip64.AsSpan(48));
            if (entries64 > long.MaxValue || size64 > long.MaxValue || offset64 > long.MaxValue) throw new InvalidDataException("Broken ZIP64 end record.");
            (entries, size, offset) = ((long)entries64, (long)size64, (long)offset64);
        }
        if (offset > length || size > length - offset) throw new InvalidDataException("The zip's directory is outside the file.");
        return new Summary(entries, size, offset);
    }

    /// <summary>
    /// Visits the entries in order until <paramref name="visit"/> returns false. Throws <see cref="InvalidDataException"/>
    /// when the zip has more than <paramref name="maxEntries"/> entries or a directory bigger than
    /// <paramref name="maxDirectoryBytes"/>, checked before the first entry is read.
    /// </summary>
    public static Summary Scan(string path, int maxEntries, long maxDirectoryBytes, Func<ZipEntryInfo, bool> visit, CancellationToken ct = default)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
        var summary = Read(file);
        if (summary.Entries > maxEntries) throw new InvalidDataException($"The zip has {summary.Entries} entries (at most {maxEntries}).");
        if (summary.DirectorySize > maxDirectoryBytes) throw new InvalidDataException($"The zip's directory is {summary.DirectorySize} bytes (at most {maxDirectoryBytes}).");
        file.Position = summary.DirectoryOffset;
        using var reader = new BufferedStream(file, 64 * 1024);
        var header = new byte[46];
        var name = new byte[ushort.MaxValue];
        var extra = new byte[ushort.MaxValue];
        long read = 0;
        for (long i = 0; i < summary.Entries; i++)
        {
            if ((i & 1023) == 0) ct.ThrowIfCancellationRequested();
            reader.ReadExactly(header);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != EntrySignature) throw new InvalidDataException("Broken zip directory entry.");
            var flags = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(8));
            long compressed = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(20));
            long size = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(24));
            int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28));
            int extraLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(30));
            int commentLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(32));
            read += 46 + nameLength + extraLength + commentLength;
            if (read > summary.DirectorySize) throw new InvalidDataException("The zip's directory is longer than it says.");
            reader.ReadExactly(name.AsSpan(0, nameLength));
            reader.ReadExactly(extra.AsSpan(0, extraLength));
            if (compressed == uint.MaxValue || size == uint.MaxValue) (size, compressed) = Zip64Sizes(extra.AsSpan(0, extraLength), size, compressed);
            Skip(reader, commentLength);
            var text = (flags & 0x800) != 0 ? Encoding.UTF8.GetString(name, 0, nameLength) : Encoding.Latin1.GetString(name, 0, nameLength);
            if (!visit(new ZipEntryInfo(text, compressed, size))) break;
        }
        return summary;
    }

    /// <summary>The 64-bit sizes from the ZIP64 extra field (uncompressed first, then compressed, each only when marked).</summary>
    private static (long Size, long Compressed) Zip64Sizes(ReadOnlySpan<byte> extra, long size, long compressed)
    {
        while (extra.Length >= 4)
        {
            var id = BinaryPrimitives.ReadUInt16LittleEndian(extra);
            var length = BinaryPrimitives.ReadUInt16LittleEndian(extra[2..]);
            if (length > extra.Length - 4) break;
            var data = extra.Slice(4, length);
            if (id == 1)
            {
                var at = 0;
                if (size == uint.MaxValue && data.Length >= at + 8) { size = ToLong(BinaryPrimitives.ReadUInt64LittleEndian(data[at..])); at += 8; }
                if (compressed == uint.MaxValue && data.Length >= at + 8) compressed = ToLong(BinaryPrimitives.ReadUInt64LittleEndian(data[at..]));
                return (size, compressed);
            }
            extra = extra[(4 + length)..];
        }
        return (size, compressed);
    }

    private static long ToLong(ulong value) => value > long.MaxValue ? throw new InvalidDataException("Entry size out of range.") : (long)value;

    private static void Skip(Stream stream, int count)
    {
        Span<byte> buffer = stackalloc byte[256];
        while (count > 0)
        {
            var n = Math.Min(count, buffer.Length);
            stream.ReadExactly(buffer[..n]);
            count -= n;
        }
    }
}
